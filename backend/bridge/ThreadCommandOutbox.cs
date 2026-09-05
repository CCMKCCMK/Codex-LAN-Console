using System.Text.Json;

namespace CodexLanBridge;

public static class ThreadCommandStatus
{
    public const string Queued = "queued";
    public const string Dispatching = "dispatching";
    public const string Delivered = "delivered";
    public const string DispatchUncertain = "dispatchUncertain";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string status) => status is Delivered or Failed or Cancelled;
}

public sealed record ThreadCommandReceipt(
    string Id,
    string Status,
    string Message,
    string ThreadId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Attempt,
    string? AcceptedTurnId,
    string? LastError,
    CodexCommandOptions? Options = null);

internal sealed record ThreadCommandDispatch(
    string Id,
    string ThreadId,
    string ClientUserMessageId,
    string? ExpectedTurnId,
    JsonElement Input,
    ExecutionPermissions Permissions,
    CodexCommandOptions? Options,
    int Attempt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DispatchStartedAt,
    DateTimeOffset? RequestWrittenAt);

/// <summary>
/// A durable, per-thread command outbox. HTTP handlers persist here before any
/// app-server request is attempted. A record in dispatchUncertain is never
/// replayed automatically: it must first be reconciled using protocol/history
/// evidence that contains the original clientUserMessageId.
/// </summary>
public sealed class ThreadCommandOutboxStore
{
    private const int SchemaVersion = 1;
    private const int MaximumRecords = 500;
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromDays(7);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, PersistedThreadCommand> _records = new(StringComparer.Ordinal);

    public ThreadCommandOutboxStore(string? storageDirectory = null, Func<DateTimeOffset>? clock = null)
    {
        storageDirectory ??= System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLanConsole");
        Directory.CreateDirectory(storageDirectory);
        _path = System.IO.Path.Combine(storageDirectory, "command-outbox.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Load();
    }

    public string Path => _path;

    public ThreadCommandReceipt Enqueue(
        string threadId,
        JsonElement input,
        string? clientUserMessageId,
        string? expectedTurnId,
        ExecutionPermissions permissions,
        CodexCommandOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0)
            throw new ArgumentException("At least one message or attachment is required.", nameof(input));

        var clientId = string.IsNullOrWhiteSpace(clientUserMessageId)
            ? Guid.NewGuid().ToString()
            : clientUserMessageId.Trim();
        if (clientId.Length > 200)
            throw new ArgumentException("The client message identifier is too long.", nameof(clientUserMessageId));

        lock (_gate)
        {
            // A retried HTTP request must resolve to the same receipt even after
            // delivery. clientUserMessageId is not app-server idempotency, so the
            // outbox is the layer that gives it safe idempotent semantics.
            var existing = _records.Values.FirstOrDefault(record =>
                record.ThreadId.Equals(threadId, StringComparison.Ordinal) &&
                record.ClientUserMessageId.Equals(clientId, StringComparison.Ordinal));
            if (existing is not null) return Receipt(existing);

            var now = _clock();
            var record = new PersistedThreadCommand(
                SchemaVersion,
                Guid.NewGuid().ToString("N"),
                threadId,
                clientId,
                string.IsNullOrWhiteSpace(expectedTurnId) ? null : expectedTurnId.Trim(),
                input.Clone(),
                permissions,
                ThreadCommandStatus.Queued,
                0,
                now,
                now,
                now,
                null,
                null,
                null,
                null,
                null,
                options?.HasOverrides == true ? options : null);
            _records[record.Id] = record;
            PruneLocked(now);
            SaveLocked();
            return Receipt(record);
        }
    }

    public IReadOnlyList<ThreadCommandReceipt> Snapshot(string? threadId = null, int limit = 50)
    {
        lock (_gate)
        {
            return _records.Values
                .Where(record => string.IsNullOrWhiteSpace(threadId) ||
                                 record.ThreadId.Equals(threadId, StringComparison.Ordinal))
                .OrderBy(record => record.CreatedAt)
                .TakeLast(Math.Clamp(limit, 1, 100))
                .Select(Receipt)
                .ToArray();
        }
    }

    public ThreadCommandReceipt? Find(string threadId, string receiptId)
    {
        lock (_gate)
            return _records.TryGetValue(receiptId, out var record) &&
                   record.ThreadId.Equals(threadId, StringComparison.Ordinal)
                ? Receipt(record)
                : null;
    }

    internal bool WasAcceptedByBridge(string threadId, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId)) return false;
        lock (_gate)
            return _records.Values.Any(record =>
                record.ThreadId.Equals(threadId, StringComparison.Ordinal) &&
                record.Status == ThreadCommandStatus.Delivered &&
                string.Equals(record.AcceptedTurnId, turnId, StringComparison.Ordinal));
    }

    internal IReadOnlyList<ThreadCommandDispatch> DispatchCandidates(DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? _clock();
        lock (_gate)
        {
            var result = new List<ThreadCommandDispatch>();
            foreach (var group in _records.Values.OrderBy(record => record.CreatedAt).GroupBy(record => record.ThreadId))
            {
                // Preserve ordering. An uncertain earlier command blocks every
                // later command in the same task until it is reconciled/cancelled.
                var first = group.FirstOrDefault(record => !ThreadCommandStatus.IsTerminal(record.Status));
                if (first is null || first.Status != ThreadCommandStatus.Queued || first.NextAttemptAt > now) continue;
                result.Add(Dispatch(first));
            }
            return result;
        }
    }

    internal IReadOnlyList<ThreadCommandDispatch> UncertainCandidates(DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? _clock();
        lock (_gate)
            return _records.Values
                .Where(record => record.Status == ThreadCommandStatus.DispatchUncertain &&
                                 (!record.NextAttemptAt.HasValue || record.NextAttemptAt <= now))
                .OrderBy(record => record.UpdatedAt)
                .Select(Dispatch)
                .ToArray();
    }

    internal bool TryBeginDispatch(string receiptId, out ThreadCommandDispatch dispatch)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(receiptId, out var record) || record.Status != ThreadCommandStatus.Queued)
            {
                dispatch = default!;
                return false;
            }
            var now = _clock();
            if (record.NextAttemptAt > now)
            {
                dispatch = default!;
                return false;
            }
            record = record with
            {
                Status = ThreadCommandStatus.Dispatching,
                Attempt = record.Attempt + 1,
                UpdatedAt = now,
                DispatchStartedAt = now,
                RequestWrittenAt = null,
                NextAttemptAt = null,
                LastError = null
            };
            _records[receiptId] = record;
            SaveLocked();
            dispatch = Dispatch(record);
            return true;
        }
    }

    internal void DeferQueued(string receiptId, TimeSpan delay, string? reason = null)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(receiptId, out var record) || record.Status != ThreadCommandStatus.Queued) return;
            var now = _clock();
            _records[receiptId] = record with
            {
                UpdatedAt = now,
                NextAttemptAt = now + delay,
                LastError = TrimError(reason)
            };
            SaveLocked();
        }
    }

    internal void ObserveRequestWritten(
        string threadId,
        string clientUserMessageId,
        DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(clientUserMessageId)) return;
        lock (_gate)
        {
            var record = _records.Values.FirstOrDefault(candidate =>
                candidate.ThreadId.Equals(threadId, StringComparison.Ordinal) &&
                candidate.Status == ThreadCommandStatus.Dispatching &&
                candidate.ClientUserMessageId.Equals(clientUserMessageId, StringComparison.Ordinal));
            if (record is null) return;
            var now = observedAt ?? _clock();
            _records[record.Id] = record with { UpdatedAt = now, RequestWrittenAt = now };
            SaveLocked();
        }
    }

    internal void ObserveTurnStarted(string threadId, string turnId, DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId)) return;
        lock (_gate)
        {
            // RequestWrittenAt prevents a turn emitted while thread/resume is
            // still doing preflight from being mistaken for this new command.
            var record = _records.Values
                .Where(candidate => candidate.ThreadId.Equals(threadId, StringComparison.Ordinal) &&
                                    candidate.Status == ThreadCommandStatus.Dispatching &&
                                    candidate.RequestWrittenAt.HasValue)
                .OrderBy(candidate => candidate.RequestWrittenAt)
                .FirstOrDefault();
            if (record is null) return;
            // A bare turn/started is only progress evidence. The app-server can
            // allocate/start a turn and still lose the phone's user message
            // before it is durably appended. Delivery requires either the RPC
            // acknowledgement or an observed matching clientUserMessageId.
            var now = observedAt ?? _clock();
            _records[record.Id] = record with
            {
                UpdatedAt = now,
                AcceptedTurnId = turnId
            };
            SaveLocked();
        }
    }

    internal void ObserveClientMessage(
        string? threadId,
        string clientUserMessageId,
        string? turnId = null,
        DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(clientUserMessageId)) return;
        lock (_gate)
        {
            var matches = _records.Values.Where(candidate =>
                    (string.IsNullOrWhiteSpace(threadId) ||
                     candidate.ThreadId.Equals(threadId, StringComparison.Ordinal)) &&
                    candidate.ClientUserMessageId.Equals(clientUserMessageId, StringComparison.Ordinal) &&
                    candidate.Status is ThreadCommandStatus.Dispatching or ThreadCommandStatus.DispatchUncertain)
                .Take(2)
                .ToArray();
            // Without a task id, only a globally unique correlation is safe.
            if (matches.Length != 1) return;
            var record = matches[0];
            MarkDeliveredLocked(record, turnId, observedAt ?? _clock());
            SaveLocked();
        }
    }

    internal void MarkDelivered(string receiptId, string? acceptedTurnId = null, DateTimeOffset? observedAt = null)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(receiptId, out var record) ||
                record.Status is not (ThreadCommandStatus.Dispatching or ThreadCommandStatus.DispatchUncertain)) return;
            MarkDeliveredLocked(record, acceptedTurnId, observedAt ?? _clock());
            SaveLocked();
        }
    }

    internal void MarkDispatchUncertain(string receiptId, string? error, DateTimeOffset? observedAt = null)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(receiptId, out var record) || record.Status != ThreadCommandStatus.Dispatching) return;
            var now = observedAt ?? _clock();
            _records[receiptId] = record with
            {
                Status = ThreadCommandStatus.DispatchUncertain,
                UpdatedAt = now,
                NextAttemptAt = now,
                LastError = TrimError(error) ?? "The app-server disconnected before acknowledging the command."
            };
            SaveLocked();
        }
    }

    internal void MarkTransportFailure(string receiptId, string? error, DateTimeOffset? observedAt = null)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(receiptId, out var record) ||
                record.Status != ThreadCommandStatus.Dispatching) return;
            var now = observedAt ?? _clock();
            var written = record.RequestWrittenAt.HasValue;
            _records[receiptId] = record with
            {
                Status = written ? ThreadCommandStatus.DispatchUncertain : ThreadCommandStatus.Queued,
                UpdatedAt = now,
                NextAttemptAt = now,
                LastError = TrimError(error) ?? (written
                    ? "The app-server disconnected before acknowledging the command."
                    : "The command was not written and will retry safely.")
            };
            SaveLocked();
        }
    }

    internal void MarkAllDispatchingUncertain(string? error = null, DateTimeOffset? observedAt = null)
    {
        lock (_gate)
        {
            var now = observedAt ?? _clock();
            var changed = false;
            foreach (var record in _records.Values.Where(candidate => candidate.Status == ThreadCommandStatus.Dispatching).ToArray())
            {
                var written = record.RequestWrittenAt.HasValue;
                _records[record.Id] = record with
                {
                    Status = written ? ThreadCommandStatus.DispatchUncertain : ThreadCommandStatus.Queued,
                    UpdatedAt = now,
                    NextAttemptAt = now,
                    LastError = TrimError(error) ?? (written
                        ? "The app-server disconnected before acknowledging the command."
                        : "The app-server disconnected before the command was written; it will retry safely.")
                };
                changed = true;
            }
            if (changed) SaveLocked();
        }
    }

    internal void MarkFailed(string receiptId, string? error, DateTimeOffset? observedAt = null)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(receiptId, out var record) || record.Status != ThreadCommandStatus.Dispatching) return;
            var now = observedAt ?? _clock();
            _records[receiptId] = record with
            {
                Status = ThreadCommandStatus.Failed,
                UpdatedAt = now,
                NextAttemptAt = null,
                LastError = TrimError(error) ?? "The computer rejected the command."
            };
            SaveLocked();
        }
    }

    internal void RequeueBusy(
        string receiptId,
        TimeSpan delay,
        string? reason = null,
        DateTimeOffset? observedAt = null)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(receiptId, out var record) ||
                record.Status != ThreadCommandStatus.Dispatching) return;
            var now = observedAt ?? _clock();
            _records[receiptId] = record with
            {
                // The app-server explicitly rejected this request before it
                // could become a second writer.  It is therefore safe to retry
                // the exact durable command after the active writer releases
                // the task; this is not a user/policy rejection.
                Status = ThreadCommandStatus.Queued,
                UpdatedAt = now,
                NextAttemptAt = now + delay,
                RequestWrittenAt = null,
                AcceptedTurnId = null,
                LastError = TrimError(reason) ?? "The task is busy; the command will be sent automatically when it is available."
            };
            SaveLocked();
        }
    }

    internal void DeferUncertain(string receiptId, TimeSpan delay, string? error = null)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(receiptId, out var record) ||
                record.Status != ThreadCommandStatus.DispatchUncertain) return;
            var now = _clock();
            _records[receiptId] = record with
            {
                UpdatedAt = now,
                NextAttemptAt = now + delay,
                LastError = TrimError(error) ?? record.LastError
            };
            SaveLocked();
        }
    }

    public bool Cancel(string threadId, string receiptId)
    {
        lock (_gate)
        {
            // Once dispatch begins the command may already have crossed the
            // app-server boundary. Calling that state "cancelled" would be a
            // dangerous lie, so only never-dispatched queued work is cancellable.
            if (!_records.TryGetValue(receiptId, out var record) ||
                !record.ThreadId.Equals(threadId, StringComparison.Ordinal) ||
                record.Status != ThreadCommandStatus.Queued) return false;
            var now = _clock();
            _records[receiptId] = record with
            {
                Status = ThreadCommandStatus.Cancelled,
                UpdatedAt = now,
                NextAttemptAt = null,
                LastError = null
            };
            SaveLocked();
            return true;
        }
    }

    internal void ReconcileHistory(string threadId, JsonElement newestFirstTurnPage)
    {
        if (newestFirstTurnPage.ValueKind != JsonValueKind.Object ||
            !newestFirstTurnPage.TryGetProperty("data", out var turns) ||
            turns.ValueKind != JsonValueKind.Array) return;

        lock (_gate)
        {
            var uncertain = _records.Values
                .Where(record => record.ThreadId.Equals(threadId, StringComparison.Ordinal) &&
                                 record.Status == ThreadCommandStatus.DispatchUncertain)
                .ToArray();
            var changed = false;
            foreach (var record in uncertain)
            {
                foreach (var turn in turns.EnumerateArray())
                {
                    if (!ContainsClientMessageId(turn, record.ClientUserMessageId)) continue;
                    var turnId = Text(turn, "id");
                    MarkDeliveredLocked(record, turnId, _clock());
                    changed = true;
                    break;
                }
            }
            if (changed) SaveLocked();
        }
    }

    internal static bool CanDispatch(ThreadRuntimeSnapshot? state) =>
        state is null || state.Stale || state.IsRunning != true || state.CanControl;

    private void MarkDeliveredLocked(PersistedThreadCommand record, string? acceptedTurnId, DateTimeOffset now)
    {
        // Delivery is monotonic. A later disconnect cannot regress it.
        if (ThreadCommandStatus.IsTerminal(record.Status)) return;
        _records[record.Id] = record with
        {
            Status = ThreadCommandStatus.Delivered,
            UpdatedAt = now,
            DeliveredAt = now,
            AcceptedTurnId = string.IsNullOrWhiteSpace(acceptedTurnId) ? record.AcceptedTurnId : acceptedTurnId,
            NextAttemptAt = null,
            LastError = null
        };
    }

    private void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return;
            try
            {
                var document = JsonSerializer.Deserialize<OutboxDocument>(
                    File.ReadAllText(_path),
                    JsonOptions);
                if (document?.SchemaVersion != SchemaVersion || document.Commands is null) return;
                var now = _clock();
                var changed = false;
                foreach (var record in document.Commands)
                {
                    if (!Valid(record)) continue;
                    var loaded = record;
                    // A process restart destroys the in-memory RPC waiter. Only
                    // a command whose turn request reached the pipe is uncertain;
                    // preflight-only work is safe to retry.
                    if (loaded.Status == ThreadCommandStatus.Dispatching)
                    {
                        var written = loaded.RequestWrittenAt.HasValue;
                        loaded = loaded with
                        {
                            Status = written ? ThreadCommandStatus.DispatchUncertain : ThreadCommandStatus.Queued,
                            UpdatedAt = now,
                            NextAttemptAt = now,
                            LastError = written
                                ? "The Bridge restarted before the command acknowledgement was recorded."
                                : "The Bridge restarted before the command was written; it will retry safely."
                        };
                        changed = true;
                    }
                    _records[loaded.Id] = loaded;
                }
                PruneLocked(now);
                if (changed) SaveLocked();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"Could not load command outbox: {ex.Message}");
            }
        }
    }

    private void SaveLocked()
    {
        var document = new OutboxDocument(SchemaVersion, _records.Values.OrderBy(record => record.CreatedAt).ToArray());
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, _path, true);
    }

    private void PruneLocked(DateTimeOffset now)
    {
        foreach (var record in _records.Values.Where(record =>
                     ThreadCommandStatus.IsTerminal(record.Status) &&
                     record.UpdatedAt < now - TerminalRetention).ToArray())
            _records.Remove(record.Id);

        foreach (var record in _records.Values
                     .Where(record => ThreadCommandStatus.IsTerminal(record.Status))
                     .OrderBy(record => record.UpdatedAt)
                     .Take(Math.Max(0, _records.Count - MaximumRecords))
                     .ToArray())
            _records.Remove(record.Id);
    }

    private static bool Valid(PersistedThreadCommand record) =>
        record.SchemaVersion == SchemaVersion &&
        !string.IsNullOrWhiteSpace(record.Id) &&
        !string.IsNullOrWhiteSpace(record.ThreadId) &&
        !string.IsNullOrWhiteSpace(record.ClientUserMessageId) &&
        record.Input.ValueKind == JsonValueKind.Array &&
        record.Permissions is not null &&
        record.Status is ThreadCommandStatus.Queued or ThreadCommandStatus.Dispatching or
            ThreadCommandStatus.Delivered or ThreadCommandStatus.DispatchUncertain or
            ThreadCommandStatus.Failed or ThreadCommandStatus.Cancelled;

    private static ThreadCommandDispatch Dispatch(PersistedThreadCommand record) => new(
        record.Id,
        record.ThreadId,
        record.ClientUserMessageId,
        record.ExpectedTurnId,
        record.Input.Clone(),
        record.Permissions,
        record.Options,
        record.Attempt,
        record.CreatedAt,
        record.DispatchStartedAt,
        record.RequestWrittenAt);

    private static ThreadCommandReceipt Receipt(PersistedThreadCommand record) => new(
        record.Id,
        record.Status,
        StatusMessage(record.Status),
        record.ThreadId,
        record.CreatedAt,
        record.UpdatedAt,
        record.Attempt,
        record.AcceptedTurnId,
        record.LastError,
        record.Options);

    private static string StatusMessage(string status) => status switch
    {
        ThreadCommandStatus.Queued => "指令已安全保存，等待电脑端接收。",
        ThreadCommandStatus.Dispatching => "电脑端正在接收这条指令。",
        ThreadCommandStatus.Delivered => "电脑端已接收这条指令。",
        ThreadCommandStatus.DispatchUncertain => "连接在确认前中断；系统正在核对，且不会重复发送。",
        ThreadCommandStatus.Failed => "这条指令未能在电脑端启动。",
        ThreadCommandStatus.Cancelled => "这条指令已取消。",
        _ => "指令状态未知。"
    };

    private static string? TrimError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= 1000 ? value : value[..1000];
    }

    private static string? Text(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ContainsClientMessageId(JsonElement element, string expected)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var isUserMessage = Text(element, "type")?.Equals("userMessage", StringComparison.OrdinalIgnoreCase) == true;
                foreach (var property in element.EnumerateObject())
                {
                    if (isUserMessage &&
                        (property.Name.Equals("clientId", StringComparison.OrdinalIgnoreCase) ||
                         property.Name.Equals("clientUserMessageId", StringComparison.OrdinalIgnoreCase)) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        string.Equals(property.Value.GetString(), expected, StringComparison.Ordinal)) return true;
                    if (ContainsClientMessageId(property.Value, expected)) return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsClientMessageId(item, expected)) return true;
                break;
        }
        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed record OutboxDocument(int SchemaVersion, PersistedThreadCommand[] Commands);

    private sealed record PersistedThreadCommand(
        int SchemaVersion,
        string Id,
        string ThreadId,
        string ClientUserMessageId,
        string? ExpectedTurnId,
        JsonElement Input,
        ExecutionPermissions Permissions,
        string Status,
        int Attempt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? NextAttemptAt,
        DateTimeOffset? DispatchStartedAt,
        DateTimeOffset? RequestWrittenAt,
        DateTimeOffset? DeliveredAt,
        string? AcceptedTurnId,
        string? LastError,
        CodexCommandOptions? Options = null);
}

/// <summary>
/// Dispatches only the oldest unresolved command per task. Fresh Desktop-owned
/// turns are left alone; terminal/stale external state wakes the queued command.
/// </summary>
public sealed class ThreadCommandOutboxDispatcher : BackgroundService
{
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconcileRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(1);
    private readonly ThreadCommandOutboxStore _outbox;
    private readonly ThreadRuntimeStateStore _runtimeStates;
    private readonly CodexAppServer _codex;
    private readonly SemaphoreSlim _signal = new(0, 1);

    public ThreadCommandOutboxDispatcher(
        ThreadCommandOutboxStore outbox,
        ThreadRuntimeStateStore runtimeStates,
        CodexAppServer codex)
    {
        _outbox = outbox;
        _runtimeStates = runtimeStates;
        _codex = codex;
    }

    public void Wake()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _codex.AppServerNotification += ObserveNotification;
        _codex.AppServerRequestWritten += ObserveRequestWritten;
        _codex.AppServerDisconnected += ObserveDisconnected;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _codex.AppServerNotification -= ObserveNotification;
        _codex.AppServerRequestWritten -= ObserveRequestWritten;
        _codex.AppServerDisconnected -= ObserveDisconnected;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // On restart, let the rollout monitor establish Desktop ownership before
        // considering persisted queued commands. This avoids a startup race in
        // which a live Desktop turn has not reached ThreadRuntimeStateStore yet.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileUncertainAsync(stoppingToken);
                foreach (var candidate in _outbox.DispatchCandidates())
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var state = _runtimeStates.Get(candidate.ThreadId);
                    // Codex cannot change model or reasoning effort in the
                    // middle of turn/steer. Keep this command queued until it
                    // can create a fresh turn/start carrying the override.
                    if (candidate.Options?.HasOverrides == true && state is { Stale: false, IsRunning: true })
                    {
                        _outbox.DeferQueued(
                            candidate.Id,
                            BusyRetryDelay,
                            "The selected model will be applied when the active turn finishes.");
                        continue;
                    }
                    if (!_codex.IsReady)
                    {
                        _outbox.DeferQueued(
                            candidate.Id,
                            BusyRetryDelay,
                            "Codex app-server is reconnecting.");
                        continue;
                    }
                    if (!ThreadCommandOutboxStore.CanDispatch(state))
                    {
                        // A turn accepted by this Bridge can leave a fresh
                        // task_started tail behind when the app-server transport
                        // disconnects immediately after acknowledging turn/start.
                        // If the newly connected app-server confirms that exact
                        // turn is no longer in progress, the rollout is orphaned
                        // bridge evidence rather than another Desktop owner. It is
                        // then safe to dispatch the next durable command. Never use
                        // this escape hatch for an unknown/external turn.
                        var bridgeAcceptedOrphan = state is
                            { Source: "rollout", IsRunning: true, ActiveTurnId.Length: > 0 } &&
                            _outbox.WasAcceptedByBridge(candidate.ThreadId, state.ActiveTurnId) &&
                            string.IsNullOrWhiteSpace(await _codex.GetActiveTurnIdAsync(
                                candidate.ThreadId, stoppingToken));
                        if (!bridgeAcceptedOrphan)
                        {
                            _outbox.DeferQueued(
                                candidate.Id,
                                BusyRetryDelay,
                                "Another Codex client owns the active turn.");
                            continue;
                        }
                    }
                    if (!_outbox.TryBeginDispatch(candidate.Id, out var dispatch)) continue;
                    await DispatchAsync(dispatch, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"Command outbox loop failed safely: {ex.Message}"); }

            try
            {
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                delay.CancelAfter(IdlePollDelay);
                await _signal.WaitAsync(delay.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task DispatchAsync(ThreadCommandDispatch dispatch, CancellationToken stoppingToken)
    {
        try
        {
            var input = dispatch.Input.EnumerateArray().Select(item => (object)item.Clone()).ToArray();
            var result = await _codex.SendUserInputAsync(
                dispatch.ThreadId,
                input,
                dispatch.ClientUserMessageId,
                dispatch.ExpectedTurnId,
                dispatch.Permissions,
                stoppingToken,
                dispatch.Options);
            _outbox.MarkDelivered(dispatch.Id, AcceptedTurnId(result));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _outbox.MarkTransportFailure(dispatch.Id, "The Bridge stopped before acknowledging the command.");
        }
        catch (CodexRpcException ex) when (ex.IsActiveTurnConflict)
        {
            // Runtime ownership can change between the preflight state check
            // and turn/start.  An explicit active-writer conflict means the
            // message was not accepted, so retain it in the durable queue.
            _outbox.RequeueBusy(dispatch.Id, BusyRetryDelay, ex.Message);
        }
        catch (CodexRpcException ex)
        {
            // An RPC error is an acknowledged rejection, so replay is unnecessary.
            _codex.RecordCommandError("commandRpcRejected", dispatch.Id, dispatch.ThreadId, ex);
            _outbox.MarkFailed(dispatch.Id, ex.Message);
        }
        catch (ArgumentException ex)
        {
            _codex.RecordCommandError("commandInvalid", dispatch.Id, dispatch.ThreadId, ex);
            _outbox.MarkFailed(dispatch.Id, ex.Message);
        }
        catch (CodexTurnBusyException ex)
        {
            // A turn began after the runtime precheck. No turn/start carrying
            // this command was written, so it is safe to retry after that turn.
            _outbox.MarkTransportFailure(dispatch.Id, ex.Message);
            _outbox.DeferQueued(dispatch.Id, BusyRetryDelay, ex.Message);
        }
        catch (Exception ex)
        {
            // Preflight failures are safe to retry. Once the actual turn request
            // crossed the pipe, preserve uncertainty and reconcile instead of
            // blindly replaying the user's instruction.
            _codex.RecordCommandError("commandTransportFailure", dispatch.Id, dispatch.ThreadId, ex);
            _outbox.MarkTransportFailure(dispatch.Id, ex.Message);
            _outbox.DeferQueued(dispatch.Id, BusyRetryDelay, ex.Message);
        }
    }

    private async Task ReconcileUncertainAsync(CancellationToken cancellationToken)
    {
        if (!_codex.IsReady) return;
        foreach (var group in _outbox.UncertainCandidates().GroupBy(candidate => candidate.ThreadId))
        {
            try
            {
                var page = await _codex.CallAsync("thread/turns/list", new
                {
                    threadId = group.Key,
                    limit = 8,
                    sortDirection = "desc",
                    itemsView = "summary"
                }, cancellationToken);
                _outbox.ReconcileHistory(group.Key, page);
                foreach (var candidate in group)
                    if (_outbox.Find(candidate.ThreadId, candidate.Id)?.Status == ThreadCommandStatus.DispatchUncertain)
                        _outbox.DeferUncertain(candidate.Id, ReconcileRetryDelay);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                foreach (var candidate in group)
                    _outbox.DeferUncertain(candidate.Id, ReconcileRetryDelay, ex.Message);
                Console.Error.WriteLine($"Could not reconcile uncertain command for {group.Key}: {ex.Message}");
            }
        }
    }

    private void ObserveRequestWritten(string method, JsonElement parameters)
    {
        if (method is not ("turn/start" or "turn/steer") ||
            !TryText(parameters, "threadId", out var threadId) ||
            !TryText(parameters, "clientUserMessageId", out var clientId)) return;
        _outbox.ObserveRequestWritten(threadId, clientId);
    }

    private void ObserveNotification(string method, JsonElement parameters)
    {
        if (method.Equals("turn/started", StringComparison.Ordinal) &&
            TryText(parameters, "threadId", out var threadId) &&
            parameters.TryGetProperty("turn", out var turn) &&
            TryText(turn, "id", out var turnId))
        {
            _outbox.ObserveTurnStarted(threadId, turnId);
            Wake();
        }
        var notificationThreadId = TryText(parameters, "threadId", out var currentThreadId)
            ? currentThreadId
            : null;
        foreach (var clientId in ClientMessageIds(parameters))
            _outbox.ObserveClientMessage(
                notificationThreadId,
                clientId,
                TryText(parameters, "turnId", out var id) ? id : null);

        if (method.Equals("turn/completed", StringComparison.Ordinal)) Wake();
    }

    private void ObserveDisconnected()
    {
        _outbox.MarkAllDispatchingUncertain();
        Wake();
    }

    private static string? AcceptedTurnId(JsonElement result)
    {
        if (TryText(result, "turnId", out var direct)) return direct;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("turn", out var turn) &&
            TryText(turn, "id", out var nested)) return nested;
        return null;
    }

    private static IEnumerable<string> ClientMessageIds(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var isUserMessage = TryText(element, "type", out var type) &&
                                type.Equals("userMessage", StringComparison.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (isUserMessage &&
                    (property.Name.Equals("clientId", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Equals("clientUserMessageId", StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    yield return property.Value.GetString()!;
                foreach (var value in ClientMessageIds(property.Value)) yield return value;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var value in ClientMessageIds(item)) yield return value;
        }
    }

    private static bool TryText(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? "";
        return value.Length > 0;
    }
}
