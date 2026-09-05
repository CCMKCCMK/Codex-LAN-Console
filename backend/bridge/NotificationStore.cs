using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexLanBridge;

public sealed record NotificationEvent(
    long Id,
    string Type,
    string? ThreadId,
    string? TurnId,
    string? PendingKey,
    string Title,
    string Body,
    bool RequiresAction,
    DateTimeOffset CreatedAt);

public sealed record NotificationPage(
    IReadOnlyList<NotificationEvent> Events,
    long NextCursor,
    long CurrentCursor,
    bool HasMore,
    bool CursorExpired,
    DateTimeOffset ServerTime);

public sealed class NotificationStore
{
    public const int MaximumEvents = 500;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private List<PersistedNotification> _events = new();
    private HashSet<string> _dedupeHashes = new(StringComparer.Ordinal);
    private long _nextId = 1;
    private TaskCompletionSource<long> _changed = NewSignal();

    public NotificationStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLanConsole");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "notification-events.json");
        Load();
    }

    public long CurrentCursor
    {
        get { lock (_gate) return _nextId - 1; }
    }

    public NotificationEvent? Publish(
        string dedupeKey,
        string type,
        string? threadId,
        string? turnId,
        string? pendingKey,
        string title,
        string body,
        bool requiresAction,
        DateTimeOffset? createdAt = null) =>
        PublishCore(dedupeKey, type, threadId, turnId, pendingKey, title, body, requiresAction, createdAt, null);

    private NotificationEvent? PublishCore(
        string dedupeKey,
        string type,
        string? threadId,
        string? turnId,
        string? pendingKey,
        string title,
        string body,
        bool requiresAction,
        DateTimeOffset? createdAt,
        Func<NotificationEvent, bool>? suppressIf)
    {
        var hash = HashDedupeKey(dedupeKey);
        TaskCompletionSource<long>? signal;
        PersistedNotification persisted;
        lock (_gate)
        {
            Prune(DateTimeOffset.UtcNow);
            if (_dedupeHashes.Contains(hash)) return null;
            if (suppressIf is not null && _events.Any(item => suppressIf(item.Event))) return null;
            var item = new NotificationEvent(
                _nextId++,
                type,
                NullIfBlank(threadId),
                NullIfBlank(turnId),
                NullIfBlank(pendingKey),
                title,
                body,
                requiresAction,
                createdAt ?? DateTimeOffset.UtcNow);
            persisted = new PersistedNotification(item, hash);
            _events.Add(persisted);
            _dedupeHashes.Add(hash);
            Prune(DateTimeOffset.UtcNow);
            Save();
            signal = _changed;
            _changed = NewSignal();
        }
        signal.TrySetResult(persisted.Event.Id);
        return persisted.Event;
    }

    public NotificationEvent? PublishTurnOutcome(
        string threadId,
        string turnId,
        string status,
        DateTimeOffset? createdAt = null)
    {
        var normalized = status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "completed" => Publish(
                $"turn:{threadId}:{turnId}:completed", "task_completed", threadId, turnId, null,
                "Codex 任务已完成", "打开 Codex Console 查看结果。", false, createdAt),
            "failed" => Publish(
                $"turn:{threadId}:{turnId}:failed", "task_failed", threadId, turnId, null,
                "Codex 任务需要处理", "任务未能完成，请打开应用查看。", false, createdAt),
            "interrupted" => Publish(
                $"turn:{threadId}:{turnId}:interrupted", "task_stopped", threadId, turnId, null,
                "Codex 任务已停止", "任务运行已中断。", false, createdAt),
            _ => null
        };
    }

    public NotificationEvent? PublishTurnRecovering(
        string threadId,
        string turnId,
        int attempt,
        int maximumAttempts,
        DateTimeOffset? createdAt = null) =>
        Publish(
            $"turn:{threadId}:{turnId}:recovering:{attempt}",
            "task_recovering",
            threadId,
            turnId,
            null,
            "Codex 网络中断，正在自动续接",
            $"任务会从当前进度继续（第 {attempt}/{maximumAttempts} 次）。",
            false,
            createdAt);

    public NotificationEvent? PublishDesktopInputRequired(
        string threadId,
        string callId,
        DateTimeOffset? createdAt = null) =>
        Publish(
            $"desktop-input:{threadId}:{callId}",
            "input_required",
            threadId,
            null,
            null,
            "Codex 正在等待回复",
            "这个轮次由另一个 Codex 进程持有；可在手机新建任务继续处理。",
            false,
            createdAt);

    public NotificationEvent? PublishThreadFailure(string threadId, long updatedAt)
    {
        var transitionAt = TimestampOrNow(updatedAt);
        return PublishCore(
            $"thread:{threadId}:systemError:{updatedAt}",
            "task_failed",
            threadId,
            null,
            null,
            "Codex 任务需要处理",
            "任务出现系统错误，请打开应用查看。",
            false,
            transitionAt,
            existing => IsRecentTerminal(existing, threadId, transitionAt));
    }

    public NotificationEvent? PublishThreadStateCompletion(string threadId, long updatedAt)
    {
        var transitionAt = TimestampOrNow(updatedAt);
        return PublishCore(
            $"thread:{threadId}:idle:{updatedAt}",
            "task_completed",
            threadId,
            null,
            null,
            "Codex 任务已完成",
            "打开 Codex Console 查看结果。",
            false,
            transitionAt,
            existing => IsRecentTerminal(existing, threadId, transitionAt));
    }

    public NotificationEvent? PublishPending(PendingRequest request)
    {
        var threadId = Text(request.Params, "threadId");
        var turnId = Text(request.Params, "turnId");
        var itemId = Text(request.Params, "itemId");
        var approvalId = Text(request.Params, "approvalId");
        var parametersHash = HashDedupeKey(request.Params.GetRawText());
        var identity = string.Join(':', new[] { request.Method, threadId, turnId, itemId, approvalId, parametersHash }
            .Select(value => value ?? ""));

        var (type, title, body) = ClassifyPending(request);
        return Publish(
            $"pending:{identity}",
            type,
            threadId,
            turnId,
            request.Key,
            title,
            body,
            true,
            request.CreatedAt);
    }

    public async Task WaitForChangeAfterAsync(long cursor, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero) return;
        Task<long> signal;
        lock (_gate)
        {
            var current = _nextId - 1;
            if (current != cursor) return;
            signal = _changed.Task;
        }
        try { await signal.WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException) { }
    }

    public NotificationPage Read(long? after, int limit, IReadOnlySet<string> activePendingKeys)
    {
        lock (_gate)
        {
            var changed = Prune(DateTimeOffset.UtcNow);
            if (changed) Save();

            var current = _nextId - 1;
            var pendingSnapshot = !after.HasValue || after.Value < 0;
            var cursor = !after.HasValue
                ? 0
                : after.Value == long.MinValue
                    ? 0
                    : pendingSnapshot
                        ? Math.Abs(after.Value)
                        : after.Value;
            if (cursor > current)
                return new NotificationPage(Array.Empty<NotificationEvent>(), current, current, false, true, DateTimeOffset.UtcNow);
            var oldest = _events.Count == 0 ? _nextId : _events[0].Event.Id;
            var expired = after.HasValue && cursor > 0 && cursor < oldest - 1;
            var results = new List<NotificationEvent>(limit);
            var considered = cursor;
            foreach (var persisted in _events)
            {
                var item = persisted.Event;
                if (item.Id <= cursor) continue;
                if (results.Count >= limit) break;
                considered = item.Id;
                var activeAction = item.RequiresAction &&
                                   item.PendingKey is { Length: > 0 } pendingKey &&
                                   activePendingKeys.Contains(pendingKey);
                if (pendingSnapshot && !activeAction) continue;
                if (item.RequiresAction && !activeAction) continue;
                results.Add(item);
            }

            if (pendingSnapshot)
            {
                var hasMoreActions = _events.Any(persisted =>
                    persisted.Event.Id > considered &&
                    persisted.Event.RequiresAction &&
                    persisted.Event.PendingKey is { Length: > 0 } pendingKey &&
                    activePendingKeys.Contains(pendingKey));
                if (hasMoreActions)
                    return new NotificationPage(results, -considered, current, true, expired, DateTimeOffset.UtcNow);
                return new NotificationPage(results, current, current, false, expired, DateTimeOffset.UtcNow);
            }

            if (considered < current && results.Count < limit)
                considered = current;
            var hasMore = _events.Any(item => item.Event.Id > considered);
            return new NotificationPage(results, considered, current, hasMore, expired, DateTimeOffset.UtcNow);
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
            {
                try
                {
                    var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_path), _json);
                    if (state is not null)
                    {
                        _nextId = Math.Max(1, state.NextId);
                        _events = state.Events?
                            .Where(item => item.Event.Id > 0 && !string.IsNullOrWhiteSpace(item.DedupeHash))
                            .OrderBy(item => item.Event.Id)
                            .ToList() ?? new List<PersistedNotification>();
                        if (_events.Count > 0) _nextId = Math.Max(_nextId, _events[^1].Event.Id + 1);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Notification history could not be loaded: {ex.Message}");
                    _events = new List<PersistedNotification>();
                }
            }
            Prune(DateTimeOffset.UtcNow);
            _dedupeHashes = _events.Select(item => item.DedupeHash).ToHashSet(StringComparer.Ordinal);
            Save();
        }
    }

    private bool Prune(DateTimeOffset now)
    {
        var cutoff = now - MaximumAge;
        var before = _events.Count;
        _events.RemoveAll(item => item.Event.CreatedAt < cutoff);
        if (_events.Count > MaximumEvents)
            _events.RemoveRange(0, _events.Count - MaximumEvents);
        if (_events.Count == before) return false;
        _dedupeHashes = _events.Select(item => item.DedupeHash).ToHashSet(StringComparer.Ordinal);
        return true;
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        var state = new PersistedState(_nextId, _events);
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, _json));
        File.Move(temporary, _path, overwrite: true);
    }

    private static (string Type, string Title, string Body) ClassifyPending(PendingRequest request)
    {
        if (CodexAppServer.IsUserInputRequest(request))
            return ("input_required", "Codex 等待你的回复", "需要你提供信息后才能继续。");
        if (CodexAppServer.IsUserApprovalRequest(request) ||
            request.Method.EndsWith("/requestApproval", StringComparison.Ordinal))
            return ("approval_required", "Codex 等待批准", "需要你确认一项操作。");
        if (ElicitationProtocol.IsElicitationRequest(request))
            return ("decision_required", "Codex 等待你的决定", "需要你选择后才能继续。");
        return ("action_required", "Codex 等待处理", "任务需要你打开应用继续处理。");
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? NullIfBlank(value.GetString())
            : null;

    private static string HashDedupeKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsRecentTerminal(NotificationEvent existing, string threadId, DateTimeOffset transitionAt) =>
        existing.ThreadId?.Equals(threadId, StringComparison.Ordinal) == true &&
        existing.Type is "task_completed" or "task_failed" or "task_stopped" &&
        (existing.CreatedAt - transitionAt).Duration() <= TimeSpan.FromMinutes(2);

    private static DateTimeOffset TimestampOrNow(long unixSeconds)
    {
        if (unixSeconds > 0)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
            catch (ArgumentOutOfRangeException) { }
        }
        return DateTimeOffset.UtcNow;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static TaskCompletionSource<long> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record PersistedNotification(NotificationEvent Event, string DedupeHash);
    private sealed record PersistedState(long NextId, List<PersistedNotification>? Events);
}
