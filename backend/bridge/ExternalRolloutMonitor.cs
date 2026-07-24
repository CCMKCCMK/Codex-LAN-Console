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
    private readonly string _sessionsRoot;
    private readonly Channel<string> _signals = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly Dictionary<string, FileTailState> _states = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;

    public ExternalRolloutMonitor(NotificationStore notifications, ThreadRuntimeStateStore runtimeStates)
    {
        _notifications = notifications;
        _runtimeStates = runtimeStates;
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
        PublishUnresolved(state.ThreadId, batch);
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
        PublishUnresolved(threadId, batch);
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
        if (line.IndexOf("request_user_input"u8) < 0 &&
            line.IndexOf("function_call_output"u8) < 0 &&
            line.IndexOf("task_started"u8) < 0 &&
            line.IndexOf("task_complete"u8) < 0 &&
            line.IndexOf("turn_aborted"u8) < 0) return;

        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (StringPropertyEquals(root, "type", "event_msg") &&
                root.TryGetProperty("payload", out var lifecycle) &&
                lifecycle.ValueKind == JsonValueKind.Object)
            {
                var eventType = Text(lifecycle, "type");
                if (eventType is "task_started" or "task_complete" or "turn_aborted")
                {
                    state.ObserveLifecycle(eventType, Text(lifecycle, "turn_id"), ParseTimestamp(root));
                }
                return;
            }
            if (!StringPropertyEquals(root, "type", "response_item") ||
                !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object) return;

            var callId = Text(payload, "call_id");
            if (callId is null || callId.Length > 256) return;
            if (StringPropertyEquals(payload, "type", "function_call") &&
                StringPropertyEquals(payload, "name", "request_user_input"))
            {
                var createdAt = ParseTimestamp(root);
                batch.RememberCall(callId, createdAt);
                state.RememberCall(callId, createdAt);
            }
            else if (StringPropertyEquals(payload, "type", "function_call_output"))
            {
                batch.Resolve(callId);
                state.Resolve(callId, ParseTimestamp(root));
            }
        }
        catch (JsonException) { }
    }

    private void PublishUnresolved(string threadId, ScanBatch batch)
    {
        foreach (var call in batch.UnresolvedCalls)
            _notifications.PublishDesktopInputRequired(threadId, call.Key, call.Value);
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
            }
            else
            {
                ActiveTurnId = null;
                UnresolvedCalls.Clear();
                CallOrder.Clear();
            }
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
            LifecycleType = null;
            ActiveTurnId = null;
            RuntimeObservedAt = null;
        }
    }

    private sealed class ScanBatch
    {
        public Dictionary<string, DateTimeOffset?> UnresolvedCalls { get; } = new(StringComparer.Ordinal);
        public Queue<string> CallOrder { get; } = new();
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
    }
}
