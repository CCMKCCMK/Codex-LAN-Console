using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexLanBridge;

/// <summary>
/// Watches Codex Desktop rollout tails for request_user_input calls that belong to
/// a different app-server process. This is notification-only: the desktop process
/// still owns and resolves the request.
/// </summary>
public sealed class ExternalRolloutMonitor : BackgroundService
{
    private const int RecentFileLimit = 8;
    private const int MaximumTrackedFiles = 24;
    private const int MaximumSignalsPerPass = 64;
    private const int TailBytes = 2 * 1024 * 1024;
    private const int MaximumBytesPerPass = 4 * 1024 * 1024;
    private const int ReadBufferBytes = 64 * 1024;
    private const int MaximumLineBytes = 256 * 1024;
    private const int OwnerPrefixBytes = 64 * 1024;
    private const int MaximumCallsPerBatch = 64;
    private const int LifecycleScanBlockBytes = 256 * 1024;
    private const int LifecycleLineWindowBytes = 64 * 1024;
    private const int LifecycleLookbackBytes = 64 * 1024 * 1024;
    private const int LifecyclePatternOverlapBytes = 32;
    private static ReadOnlySpan<byte> TaskStartedPattern => "\"type\":\"task_started\""u8;
    private static ReadOnlySpan<byte> TaskCompletePattern => "\"type\":\"task_complete\""u8;
    private static ReadOnlySpan<byte> TurnAbortedPattern => "\"type\":\"turn_aborted\""u8;
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecentFileAge = TimeSpan.FromDays(2);

    private readonly NotificationStore _notifications;
    private readonly ThreadRuntimeStateStore _runtimeStates;
    private readonly ThreadLiveEventStore _liveEvents;
    private readonly string _sessionsRoot;
    private readonly Channel<string> _signals = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly Dictionary<string, FileTailState> _states = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;

    public ExternalRolloutMonitor(
        NotificationStore notifications,
        ThreadRuntimeStateStore runtimeStates,
        ThreadLiveEventStore liveEvents)
    {
        _notifications = notifications;
        _runtimeStates = runtimeStates;
        _liveEvents = liveEvents;
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _sessionsRoot = Path.GetFullPath(Path.Combine(codexHome, "sessions"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            EnsureWatcher();
            try { await EstablishBaselineAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { Console.Error.WriteLine($"Desktop notification baseline was skipped: {ex.Message}"); }
            var nextRecovery = DateTimeOffset.UtcNow + RecoveryInterval;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var untilRecovery = nextRecovery - DateTimeOffset.UtcNow;
                    if (untilRecovery <= TimeSpan.Zero)
                    {
                        EnsureWatcher();
                        await RecoverAsync(stoppingToken);
                        nextRecovery = DateTimeOffset.UtcNow + RecoveryInterval;
                        continue;
                    }

                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    wait.CancelAfter(untilRecovery);
                    try
                    {
                        var path = await _signals.Reader.ReadAsync(wait.Token);
                        await ProcessChangedFileAsync(path, stoppingToken);
                        var processed = 1;
                        while (processed < MaximumSignalsPerPass && _signals.Reader.TryRead(out path))
                        {
                            await ProcessChangedFileAsync(path, stoppingToken);
                            processed++;
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Recovery deadline reached.
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Desktop notification monitoring will retry: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            DisposeWatcher();
        }
    }

    private async Task EstablishBaselineAsync(CancellationToken cancellationToken)
    {
        foreach (var file in FindRecentFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessNewFileAsync(file.FullName, baseline: true, cancellationToken);
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var path in _states.Keys.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessChangedFileAsync(path, cancellationToken);
        }

        foreach (var file in FindRecentFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_states.ContainsKey(file.FullName))
                await ProcessNewFileAsync(file.FullName, baseline: true, cancellationToken);
        }
    }

    private async Task ProcessChangedFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsRolloutPath(path)) return;
        if (!_states.TryGetValue(path, out var state))
        {
            await ProcessNewFileAsync(path, baseline: true, cancellationToken);
            return;
        }

        state.LastTouched = DateTimeOffset.UtcNow;
        var currentLength = SafeLength(path);
        if (currentLength < 0)
        {
            _states.Remove(path);
            return;
        }
        if (currentLength < state.Offset)
        {
            // A truncate/replace can change ownership as well as contents.
            _states.Remove(path);
            await ProcessNewFileAsync(path, baseline: false, cancellationToken);
            return;
        }
        if (!state.IsDesktopOwned) return;
        var previousOffset = state.Offset;
        var batch = await ReadTailAsync(path, state, null, cancellationToken);
        if (state.Offset > previousOffset) state.LastActivityAt = SafeLastWriteTime(path) ?? state.LastActivityAt;
        PublishUnresolved(state, batch);
        PublishLive(state, batch);
        PublishRuntime(state);
        if (batch.HasUnreadBytes) _signals.Writer.TryWrite(path);
    }

    private async Task ProcessNewFileAsync(string path, bool baseline, CancellationToken cancellationToken)
    {
        if (!IsRolloutPath(path) || !File.Exists(path)) return;
        var threadId = ThreadIdFromPath(path);
        if (threadId is null) return;
        var owner = ReadOriginator(path);
        if (owner is null) return; // A later Changed event or recovery pass will retry it.

        TrimTrackedFilesIfNeeded();
        var length = SafeLength(path);
        if (length < 0) return;
        if (!IsDesktopOriginator(owner))
        {
            // Remember bridge/CLI-owned rollouts so their frequent writes do not
            // repeatedly reopen and inspect the metadata prefix.
            _states[path] = new FileTailState(threadId, length, false, false);
            return;
        }
        var start = baseline || length > TailBytes ? Math.Max(0, length - TailBytes) : 0;
        var state = new FileTailState(threadId, start, start > 0, true);
        state.LastActivityAt = SafeLastWriteTime(path);
        _states[path] = state;
        if (baseline && start > 0)
            await SeedLatestLifecycleAsync(path, state, length, cancellationToken);
        var batch = await ReadTailAsync(path, state, length, cancellationToken);
        PublishUnresolved(state, batch);
        PublishLive(state, batch);
        PublishRuntime(state);
        if (batch.HasUnreadBytes) _signals.Writer.TryWrite(path);
    }

    private async Task<ScanBatch> ReadTailAsync(
        string path,
        FileTailState state,
        long? stopAt,
        CancellationToken cancellationToken)
    {
        var batch = new ScanBatch();
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stopAt.HasValue ? Math.Min(stopAt.Value, stream.Length) : stream.Length;
            if (length < state.Offset)
            {
                state.Reset();
                length = stream.Length;
            }
            if (state.Offset >= length) return batch;

            stream.Position = state.Offset;
            var remainingBudget = Math.Min(MaximumBytesPerPass, length - state.Offset);
            var rented = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
            try
            {
                while (remainingBudget > 0)
                {
                    var wanted = (int)Math.Min(rented.Length, remainingBudget);
                    var read = await stream.ReadAsync(rented.AsMemory(0, wanted), cancellationToken);
                    if (read <= 0) break;
                    ConsumeBytes(state, rented.AsSpan(0, read), batch);
                    state.Offset += read;
                    remainingBudget -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            batch.HasUnreadBytes = state.Offset < length;
        }
        catch (FileNotFoundException) { _states.Remove(path); }
        catch (DirectoryNotFoundException) { _states.Remove(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return batch;
    }

    private static async Task SeedLatestLifecycleAsync(
        string path,
        FileTailState state,
        long length,
        CancellationToken cancellationToken)
    {
        var floor = Math.Max(0, length - LifecycleLookbackBytes);
        var cursor = length;
        var rented = ArrayPool<byte>.Shared.Rent(LifecycleScanBlockBytes);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                LifecycleScanBlockBytes,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            while (cursor > floor)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = Math.Max(floor, cursor - LifecycleScanBlockBytes);
                var wanted = checked((int)(cursor - start));
                stream.Position = start;
                var read = 0;
                while (read < wanted)
                {
                    var count = await stream.ReadAsync(rented.AsMemory(read, wanted - read), cancellationToken);
                    if (count <= 0) break;
                    read += count;
                }
                if (read <= 0) break;

                var relative = LatestLifecyclePatternOffset(rented.AsSpan(0, read));
                if (relative >= 0 &&
                    await TryReadLifecycleLineAsync(stream, state, start + relative, length, cancellationToken))
                    return;

                if (start == floor) break;
                cursor = Math.Min(cursor - 1, start + LifecyclePatternOverlapBytes);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int LatestLifecyclePatternOffset(ReadOnlySpan<byte> bytes)
    {
        var started = bytes.LastIndexOf(TaskStartedPattern);
        var completed = bytes.LastIndexOf(TaskCompletePattern);
        var aborted = bytes.LastIndexOf(TurnAbortedPattern);
        return Math.Max(started, Math.Max(completed, aborted));
    }

    private static async Task<bool> TryReadLifecycleLineAsync(
        FileStream stream,
        FileTailState state,
        long markerOffset,
        long length,
        CancellationToken cancellationToken)
    {
        var start = Math.Max(0, markerOffset - LifecycleLineWindowBytes);
        var end = Math.Min(length, markerOffset + LifecycleLineWindowBytes);
        var wanted = checked((int)(end - start));
        var rented = ArrayPool<byte>.Shared.Rent(wanted);
        try
        {
            stream.Position = start;
            var read = 0;
            while (read < wanted)
            {
                var count = await stream.ReadAsync(rented.AsMemory(read, wanted - read), cancellationToken);
                if (count <= 0) break;
                read += count;
            }
            var marker = checked((int)(markerOffset - start));
            if (marker >= read) return false;
            var before = rented.AsSpan(0, marker).LastIndexOf((byte)'\n');
            var lineStart = before < 0 ? 0 : before + 1;
            var after = rented.AsSpan(marker, read - marker).IndexOf((byte)'\n');
            var lineEnd = after < 0 ? read : marker + after;
            if (lineEnd <= lineStart || lineEnd - lineStart > MaximumLineBytes) return false;
            var previousType = state.LifecycleType;
            var previousAt = state.RuntimeObservedAt;
            InspectLine(state, rented.AsSpan(lineStart, lineEnd - lineStart), new ScanBatch());
            return state.LifecycleType != previousType || state.RuntimeObservedAt != previousAt;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ConsumeBytes(FileTailState state, ReadOnlySpan<byte> bytes, ScanBatch batch)
    {
        var position = 0;
        while (position < bytes.Length)
        {
            var newline = bytes[position..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                AppendPartial(state, bytes[position..]);
                return;
            }

            newline += position;
            if (state.DiscardUntilNewline)
            {
                state.DiscardUntilNewline = false;
                state.PartialLine.Clear();
            }
            else
            {
                AppendPartial(state, bytes[position..newline]);
                if (state.DiscardUntilNewline)
                    state.DiscardUntilNewline = false; // The oversized line ends here.
                else
                    InspectLine(state, state.PartialLine.WrittenSpan, batch);
                state.PartialLine.Clear();
            }
            position = newline + 1;
        }
    }

    private static void AppendPartial(FileTailState state, ReadOnlySpan<byte> fragment)
    {
        if (state.DiscardUntilNewline || fragment.IsEmpty) return;
        if (state.PartialLine.WrittenCount + fragment.Length > MaximumLineBytes)
        {
            state.PartialLine.Clear();
            state.DiscardUntilNewline = true;
            return;
        }
        state.PartialLine.Write(fragment);
    }

    private static void InspectLine(FileTailState state, ReadOnlySpan<byte> line, ScanBatch batch)
    {
        if (line.Length == 0) return;
        if (line[^1] == (byte)'\r') line = line[..^1];
        if (line.IndexOf("\"type\":\"response_item\""u8) < 0 &&
            line.IndexOf("\"type\":\"turn_context\""u8) < 0 &&
            line.IndexOf("mcp_tool_call_end"u8) < 0 &&
            line.IndexOf("patch_apply_end"u8) < 0 &&
            line.IndexOf("task_started"u8) < 0 &&
            line.IndexOf("task_complete"u8) < 0 &&
            line.IndexOf("turn_aborted"u8) < 0) return;

        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            var observedAt = ParseTimestamp(root);
            if (StringPropertyEquals(root, "type", "turn_context") &&
                root.TryGetProperty("payload", out var turnContext) &&
                turnContext.ValueKind == JsonValueKind.Object &&
                Text(turnContext, "turn_id") is { } contextTurnId)
            {
                state.AssumeActiveTurn(contextTurnId, observedAt);
                batch.RememberTurn(contextTurnId, "running", observedAt);
                return;
            }
            if (StringPropertyEquals(root, "type", "event_msg") &&
                root.TryGetProperty("payload", out var eventPayload) &&
                eventPayload.ValueKind == JsonValueKind.Object)
            {
                var eventType = Text(eventPayload, "type");
                if (eventType is "task_started" or "task_complete" or "turn_aborted")
                {
                    var turnId = Text(eventPayload, "turn_id") ?? state.ActiveTurnId;
                    if (!string.IsNullOrWhiteSpace(turnId)) batch.RememberTurn(turnId, eventType, observedAt);
                    state.ObserveLifecycle(eventType, turnId, observedAt);
                }
                else if (eventType == "patch_apply_end" &&
                         ProjectPatchItem(eventPayload, observedAt) is { } patchItem &&
                         (Text(eventPayload, "turn_id") ?? state.ActiveTurnId) is { } patchTurnId)
                {
                    state.AssumeActiveTurn(patchTurnId, observedAt);
                    batch.RememberItem(patchTurnId, patchItem, observedAt);
                }
                else if (eventType == "mcp_tool_call_end" &&
                         ProjectMcpItem(eventPayload, observedAt) is { } mcpItem &&
                         state.ActiveTurnId is { } mcpTurnId)
                    batch.RememberItem(mcpTurnId, mcpItem, observedAt);
                return;
            }
            if (!StringPropertyEquals(root, "type", "response_item") ||
                !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object) return;

            if (ResponseTurnId(payload) is { } responseTurnId)
                state.AssumeActiveTurn(responseTurnId, observedAt);
            var payloadType = Text(payload, "type") ?? "";
            if (payloadType == "reasoning")
            {
                if (state.ActiveTurnId is { } reasoningTurnId)
                {
                    foreach (var completed in state.CompleteProcesses(observedAt))
                        batch.RememberItem(reasoningTurnId, completed, observedAt);
                    if (ProjectReasoningItem(payload, observedAt) is { } reasoningItem)
                        batch.RememberItem(reasoningTurnId, reasoningItem, observedAt);
                }
                return;
            }
            if (payloadType is "message" or "agent_message")
            {
                if (state.ActiveTurnId is { } messageTurnId)
                {
                    foreach (var completed in state.CompleteProcesses(observedAt))
                        batch.RememberItem(messageTurnId, completed, observedAt);
                    if (ProjectAgentItem(payload, observedAt) is { } messageItem)
                        batch.RememberItem(messageTurnId, messageItem, observedAt);
                }
                return;
            }

            var callId = Text(payload, "call_id");
            if (callId is null || callId.Length > 256) return;
            if (payloadType == "function_call" && StringPropertyEquals(payload, "name", "request_user_input"))
            {
                var createdAt = ParseTimestamp(root);
                batch.RememberCall(callId, createdAt);
                state.RememberCall(callId, createdAt);
            }
            else if (payloadType is "custom_tool_call" or "function_call")
            {
                var name = Text(payload, "name") ?? "tool";
                var process = state.RememberProcess(callId, ProcessType(name), name, observedAt);
                if (state.ActiveTurnId is { } processTurnId)
                    batch.RememberItem(processTurnId, process, observedAt);
            }
            else if (payloadType is "custom_tool_call_output" or "function_call_output")
            {
                batch.Resolve(callId);
                state.Resolve(callId, observedAt);
                if (state.ActiveTurnId is { } completedTurnId)
                    batch.RememberItem(completedTurnId, state.CompleteProcess(callId, observedAt), observedAt);
            }
        }
        catch (JsonException) { }
    }

    private static JsonElement? ProjectReasoningItem(JsonElement payload, DateTimeOffset? observedAt)
    {
        if (!payload.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Array) return null;
        var parts = summary.EnumerateArray()
            .Take(8)
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : Text(value, "text"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => BoundText(value!, 2_048))
            .ToArray();
        if (parts.Length == 0) return null;
        return JsonSerializer.SerializeToElement(new
        {
            type = "reasoning",
            id = Text(payload, "id") ?? $"external-reasoning-{Guid.NewGuid():N}",
            summary = parts,
            createdAt = UnixSeconds(observedAt)
        });
    }

    private static JsonElement? ProjectAgentItem(JsonElement payload, DateTimeOffset? observedAt)
    {
        if (Text(payload, "role") is { } role && !role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = new List<string>();
        if (Text(payload, "message") is { } message) parts.Add(message);
        if (payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray().Take(32))
                if (part.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(part.GetString()))
                    parts.Add(part.GetString()!);
                else if (part.ValueKind == JsonValueKind.Object && Text(part, "text") is { } text)
                    parts.Add(text);
        }
        var complete = string.Join('\n', parts).Trim();
        if (complete.Length == 0) return null;
        return JsonSerializer.SerializeToElement(new
        {
            type = "agentMessage",
            id = Text(payload, "id") ?? $"external-agent-{Guid.NewGuid():N}",
            text = BoundText(complete, 256 * 1024),
            phase = Text(payload, "phase"),
            createdAt = UnixSeconds(observedAt)
        });
    }

    private static JsonElement? ProjectPatchItem(JsonElement payload, DateTimeOffset? observedAt)
    {
        var callId = Text(payload, "call_id");
        if (string.IsNullOrWhiteSpace(callId)) return null;
        var changes = new List<object>();
        if (payload.TryGetProperty("changes", out var source))
        {
            if (source.ValueKind == JsonValueKind.Object)
            {
                foreach (var change in source.EnumerateObject().Take(12))
                    changes.Add(new
                    {
                        path = BoundText(change.Name, 2_048),
                        kind = change.Value.ValueKind == JsonValueKind.String
                            ? change.Value.GetString()
                            : Text(change.Value, "kind") ?? Text(change.Value, "type")
                    });
            }
            else if (source.ValueKind == JsonValueKind.Array)
            {
                foreach (var change in source.EnumerateArray().Take(12))
                    if (Text(change, "path") is { } path)
                        changes.Add(new { path = BoundText(path, 2_048), kind = Text(change, "kind") ?? Text(change, "type") });
            }
        }
        return JsonSerializer.SerializeToElement(new
        {
            type = "fileChange",
            id = callId,
            callId,
            status = Text(payload, "status") ?? (payload.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False ? "failed" : "completed"),
            changes = changes.ToArray(),
            createdAt = UnixSeconds(observedAt),
            updatedAt = UnixSeconds(observedAt)
        });
    }

    private static JsonElement? ProjectMcpItem(JsonElement payload, DateTimeOffset? observedAt)
    {
        var callId = Text(payload, "call_id");
        if (string.IsNullOrWhiteSpace(callId) ||
            !payload.TryGetProperty("invocation", out var invocation) ||
            invocation.ValueKind != JsonValueKind.Object) return null;
        var failed = payload.TryGetProperty("result", out var result) &&
                     result.ValueKind == JsonValueKind.Object &&
                     (result.TryGetProperty("Err", out _) || result.TryGetProperty("error", out _));
        return JsonSerializer.SerializeToElement(new
        {
            type = "mcpToolCall",
            id = callId,
            callId,
            status = failed ? "failed" : "completed",
            server = Text(invocation, "server"),
            tool = Text(invocation, "tool"),
            createdAt = UnixSeconds(observedAt),
            updatedAt = UnixSeconds(observedAt)
        });
    }

    private static string ProcessType(string name) => name switch
    {
        "exec" or "shell_command" => "commandExecution",
        "apply_patch" => "fileChange",
        "spawn_agent" or "followup_task" or "send_message" or "wait_agent" or "wait" or "list_agents" => "collabAgentToolCall",
        "view_image" or "imagegen" => "imageView",
        "web" or "web__run" or "search" => "webSearch",
        _ when name.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("computer", StringComparison.OrdinalIgnoreCase) => "computerToolCall",
        _ => "dynamicToolCall"
    };

    private static JsonElement ProcessItem(
        string callId,
        string type,
        string name,
        string status,
        DateTimeOffset? createdAt,
        DateTimeOffset? updatedAt = null) =>
        JsonSerializer.SerializeToElement(new
        {
            type,
            id = callId,
            callId,
            status,
            name = BoundText(name, 128),
            createdAt = UnixSeconds(createdAt),
            updatedAt = UnixSeconds(updatedAt)
        });

    private static string BoundText(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static long? UnixSeconds(DateTimeOffset? value) => value?.ToUnixTimeSeconds();

    internal static string? ResponseTurnId(JsonElement payload)
    {
        if (Text(payload, "turn_id") is { } direct) return direct;
        return payload.TryGetProperty("internal_chat_message_metadata_passthrough", out var metadata) &&
               metadata.ValueKind == JsonValueKind.Object
            ? Text(metadata, "turn_id")
            : null;
    }

    private void PublishUnresolved(FileTailState state, ScanBatch batch)
    {
        // A rollout keeps the originator from thread creation. If this bridge
        // later resumes that Desktop-created thread, its own turn must not be
        // announced as an external Desktop request.
        if (_runtimeStates.IsCurrentBridgeOwnedTurn(state.ThreadId, state.ActiveTurnId)) return;
        foreach (var call in batch.UnresolvedCalls)
            _notifications.PublishDesktopInputRequired(state.ThreadId, call.Key, call.Value);
    }

    private void PublishLive(FileTailState state, ScanBatch batch)
    {
        // The rollout belongs to another Codex process, but its append-only tail
        // is still the most accurate source for the small progress rows shown on
        // the phone. Only compact projections created below enter the live store.
        foreach (var activity in batch.LiveEvents)
        {
            if (activity.Item is { } item)
                _liveEvents.ObserveExternalItem(state.ThreadId, activity.TurnId, item, activity.ObservedAt);
            else
                _liveEvents.ObserveExternalTurn(
                    state.ThreadId, activity.TurnId, activity.Status ?? "inProgress", activity.ObservedAt);
        }
    }

    private void PublishRuntime(FileTailState state)
    {
        if (state.LifecycleType is { } lifecycle)
            _runtimeStates.ObserveRolloutLifecycle(
                state.ThreadId,
                lifecycle,
                state.ActiveTurnId,
                state.RuntimeObservedAt);
        if (state.LifecycleType == "task_started" || state.UnresolvedCalls.Count > 0)
            _runtimeStates.ObserveRolloutWaiting(
                state.ThreadId,
                state.ActiveTurnId,
                state.UnresolvedCalls.Count > 0,
                state.RuntimeObservedAt);
        if (state.LastActivityAt is { } activityAt)
            _runtimeStates.ObserveRolloutActivity(state.ThreadId, activityAt);
    }

    private IReadOnlyList<FileInfo> FindRecentFiles()
    {
        if (!Directory.Exists(_sessionsRoot)) return Array.Empty<FileInfo>();
        var cutoff = DateTime.UtcNow - RecentFileAge;
        var newest = new PriorityQueue<FileInfo, long>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    var file = new FileInfo(path);
                    if (file.LastWriteTimeUtc < cutoff) continue;
                    newest.Enqueue(file, file.LastWriteTimeUtc.Ticks);
                    if (newest.Count > RecentFileLimit) newest.Dequeue();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return newest.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
    }

    private void EnsureWatcher()
    {
        if (_watcher is not null || !Directory.Exists(_sessionsRoot)) return;
        try
        {
            var watcher = new FileSystemWatcher(_sessionsRoot, "rollout-*.jsonl")
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 16 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;
            _watcher = watcher;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => _signals.Writer.TryWrite(args.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs args) => _signals.Writer.TryWrite(args.FullPath);
    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        Console.Error.WriteLine($"Desktop notification watcher will recover automatically: {args.GetException().Message}");
        DisposeWatcher();
    }

    private void DisposeWatcher()
    {
        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher is null) return;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    private void TrimTrackedFilesIfNeeded()
    {
        while (_states.Count >= MaximumTrackedFiles)
        {
            var oldest = _states.MinBy(item => item.Value.LastTouched);
            if (oldest.Key is null) break;
            _states.Remove(oldest.Key);
        }
    }

    private bool IsRolloutPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var rootPrefix = _sessionsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileName(fullPath).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) &&
                   Path.GetExtension(fullPath).Equals(".jsonl", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? ThreadIdFromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length < 36) return null;
        var candidate = name[^36..];
        return Guid.TryParseExact(candidate, "D", out var id) ? id.ToString("D") : null;
    }

    private static string? ReadOriginator(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var rented = ArrayPool<byte>.Shared.Rent(OwnerPrefixBytes);
            try
            {
                var read = stream.Read(rented, 0, OwnerPrefixBytes);
                var reader = new Utf8JsonReader(rented.AsSpan(0, read), isFinalBlock: false, state: default);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("originator")) continue;
                    if (reader.Read() && reader.TokenType == JsonTokenType.String) return reader.GetString();
                    return null;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    private static bool IsDesktopOriginator(string originator) =>
        originator.Equals("Codex Desktop", StringComparison.OrdinalIgnoreCase) ||
        originator.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
        originator.Contains("Desktop", StringComparison.OrdinalIgnoreCase);

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { return -1; }
    }

    private static DateTimeOffset? SafeLastWriteTime(string path)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { return null; }
    }

    private static bool StringPropertyEquals(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.ValueEquals(expected);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static DateTimeOffset? ParseTimestamp(JsonElement root) =>
        Text(root, "timestamp") is { } value && DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : null;

    private sealed class FileTailState
    {
        private readonly Dictionary<string, ExternalProcessCall> _processCalls = new(StringComparer.Ordinal);
        private readonly Queue<string> _processOrder = new();

        public FileTailState(string threadId, long offset, bool discardUntilNewline, bool isDesktopOwned)
        {
            ThreadId = threadId;
            Offset = offset;
            DiscardUntilNewline = discardUntilNewline;
            IsDesktopOwned = isDesktopOwned;
        }

        public string ThreadId { get; }
        public bool IsDesktopOwned { get; }
        public long Offset { get; set; }
        public bool DiscardUntilNewline { get; set; }
        public ArrayBufferWriter<byte> PartialLine { get; } = new();
        public DateTimeOffset LastTouched { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastActivityAt { get; set; }
        public Dictionary<string, DateTimeOffset?> UnresolvedCalls { get; } = new(StringComparer.Ordinal);
        public Queue<string> CallOrder { get; } = new();
        public string? LifecycleType { get; private set; }
        public string? ActiveTurnId { get; private set; }
        public DateTimeOffset? RuntimeObservedAt { get; private set; }

        public void ObserveLifecycle(string eventType, string? turnId, DateTimeOffset? observedAt)
        {
            LifecycleType = eventType;
            RuntimeObservedAt = observedAt ?? DateTimeOffset.UtcNow;
            if (eventType == "task_started")
            {
                ActiveTurnId = string.IsNullOrWhiteSpace(turnId) ? ActiveTurnId : turnId;
                UnresolvedCalls.Clear();
                CallOrder.Clear();
                _processCalls.Clear();
                _processOrder.Clear();
            }
            else
            {
                ActiveTurnId = null;
                UnresolvedCalls.Clear();
                CallOrder.Clear();
                _processCalls.Clear();
                _processOrder.Clear();
            }
        }

        public void AssumeActiveTurn(string turnId, DateTimeOffset? observedAt)
        {
            if (string.IsNullOrWhiteSpace(turnId)) return;
            if (!string.Equals(ActiveTurnId, turnId, StringComparison.Ordinal))
            {
                _processCalls.Clear();
                _processOrder.Clear();
            }
            ActiveTurnId = turnId;
            LifecycleType = "task_started";
            RuntimeObservedAt = observedAt ?? DateTimeOffset.UtcNow;
        }

        public JsonElement RememberProcess(
            string callId,
            string type,
            string name,
            DateTimeOffset? createdAt)
        {
            if (!_processCalls.ContainsKey(callId))
            {
                _processCalls[callId] = new ExternalProcessCall(type, name, createdAt);
                _processOrder.Enqueue(callId);
                while (_processOrder.Count > MaximumCallsPerBatch)
                    _processCalls.Remove(_processOrder.Dequeue());
            }
            var process = _processCalls[callId];
            return ProcessItem(callId, type, name, "inProgress", process.CreatedAt);
        }

        public JsonElement CompleteProcess(string callId, DateTimeOffset? completedAt)
        {
            var process = _processCalls.TryGetValue(callId, out var tracked)
                ? tracked
                : new ExternalProcessCall("dynamicToolCall", "tool", completedAt);
            _processCalls.Remove(callId);
            return ProcessItem(
                callId, process.Type, process.Name, "completed", process.CreatedAt, completedAt);
        }

        public IReadOnlyList<JsonElement> CompleteProcesses(DateTimeOffset? completedAt)
        {
            if (_processCalls.Count == 0) return Array.Empty<JsonElement>();
            var completed = _processOrder
                .Where(_processCalls.ContainsKey)
                .Select(callId =>
                {
                    var process = _processCalls[callId];
                    return ProcessItem(
                        callId, process.Type, process.Name, "completed", process.CreatedAt, completedAt);
                })
                .ToArray();
            _processCalls.Clear();
            _processOrder.Clear();
            return completed;
        }

        public void RememberCall(string callId, DateTimeOffset? createdAt)
        {
            if (!UnresolvedCalls.TryAdd(callId, createdAt)) return;
            CallOrder.Enqueue(callId);
            RuntimeObservedAt = createdAt ?? DateTimeOffset.UtcNow;
            while (CallOrder.Count > MaximumCallsPerBatch)
                UnresolvedCalls.Remove(CallOrder.Dequeue());
        }

        public void Resolve(string callId, DateTimeOffset? observedAt)
        {
            UnresolvedCalls.Remove(callId);
            RuntimeObservedAt = observedAt ?? DateTimeOffset.UtcNow;
        }

        public void Reset()
        {
            Offset = 0;
            DiscardUntilNewline = false;
            PartialLine.Clear();
            UnresolvedCalls.Clear();
            CallOrder.Clear();
            _processCalls.Clear();
            _processOrder.Clear();
            LifecycleType = null;
            ActiveTurnId = null;
            RuntimeObservedAt = null;
        }
    }

    private sealed class ScanBatch
    {
        public Dictionary<string, DateTimeOffset?> UnresolvedCalls { get; } = new(StringComparer.Ordinal);
        public Queue<string> CallOrder { get; } = new();
        public List<ExternalLiveEvent> LiveEvents { get; } = new();
        public bool HasUnreadBytes { get; set; }

        public void RememberCall(string callId, DateTimeOffset? createdAt)
        {
            if (UnresolvedCalls.ContainsKey(callId)) return;
            UnresolvedCalls[callId] = createdAt;
            CallOrder.Enqueue(callId);
            while (CallOrder.Count > MaximumCallsPerBatch)
                UnresolvedCalls.Remove(CallOrder.Dequeue());
        }

        public void Resolve(string callId) => UnresolvedCalls.Remove(callId);

        public void RememberTurn(string turnId, string status, DateTimeOffset? observedAt) =>
            RememberLive(new ExternalLiveEvent(turnId, status, null, observedAt));

        public void RememberItem(string turnId, JsonElement item, DateTimeOffset? observedAt) =>
            RememberLive(new ExternalLiveEvent(turnId, null, item.Clone(), observedAt));

        private void RememberLive(ExternalLiveEvent activity)
        {
            LiveEvents.Add(activity);
            while (LiveEvents.Count(IsDiscardableProcessEvent) > MaximumProcessItemsPerLiveBatch)
            {
                var index = LiveEvents.FindIndex(IsDiscardableProcessEvent);
                if (index < 0) break;
                LiveEvents.RemoveAt(index);
            }
        }

        private static bool IsDiscardableProcessEvent(ExternalLiveEvent activity)
        {
            if (activity.Item is not { } item || item.ValueKind != JsonValueKind.Object) return false;
            var type = Text(item, "type");
            return type is not "userMessage" and not "agentMessage";
        }
    }

    private const int MaximumProcessItemsPerLiveBatch = 256;
    private sealed record ExternalProcessCall(string Type, string Name, DateTimeOffset? CreatedAt);
    private sealed record ExternalLiveEvent(
        string TurnId,
        string? Status,
        JsonElement? Item,
        DateTimeOffset? ObservedAt);
}
