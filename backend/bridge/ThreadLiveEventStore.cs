using System.Text;
using System.Text.Json;

namespace CodexLanBridge;

/// <summary>
/// Keeps the small, currently streaming portion of a conversation in memory.
/// Persisted turn summaries can lag behind app-server notifications until a turn
/// finishes; this store lets the phone render every message while it is produced.
/// </summary>
public sealed class ThreadLiveEventStore
{
    private const int MaximumTurnsPerThread = 8;
    private const int MaximumItemsPerTurn = 256;
    private const int MaximumAgentCharacters = 256 * 1024;
    private const int MaximumProcessCharacters = 64 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, LiveThread> _threads = new(StringComparer.Ordinal);
    private long _generation;
    // A timestamp-based seed keeps revisions monotonic across Bridge restarts, so a
    // reconnecting phone never waits for a new process to count up to an old value.
    private long _revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000;

    public void BeginGeneration(long generation)
    {
        lock (_gate)
        {
            _generation = generation;
            _revision++;
            foreach (var thread in _threads.Values)
            {
                // App-server events from the disconnected generation are no longer
                // authoritative. Desktop rollout projections are independent of that
                // connection and must survive, otherwise a reconnect makes already
                // visible progress disappear from the phone.
                thread.RetainExternalTurns();
                thread.Signal(_revision);
            }
            foreach (var empty in _threads.Where(entry => entry.Value.IsEmpty).Select(entry => entry.Key).ToArray())
                _threads.Remove(empty);
        }
    }

    public void Observe(string method, JsonElement parameters, long generation)
    {
        if (parameters.ValueKind != JsonValueKind.Object || generation != Volatile.Read(ref _generation)) return;
        if (!TryString(parameters, "threadId", out var threadId)) return;

        lock (_gate)
        {
            if (generation != _generation) return;
            var thread = GetThread(threadId);
            var changed = method switch
            {
                "turn/started" => ObserveTurn(thread, parameters, completed: false),
                "turn/completed" => ObserveTurn(thread, parameters, completed: true),
                "item/started" or "item/completed" => ObserveItem(thread, parameters),
                "item/agentMessage/delta" => ObserveAgentDelta(thread, parameters),
                "item/reasoning/summaryPartAdded" => ObserveReasoningPart(thread, parameters),
                "item/reasoning/summaryTextDelta" => ObserveReasoningSummaryDelta(thread, parameters),
                "item/reasoning/textDelta" => ObserveReasoningTextDelta(thread, parameters),
                _ => false
            };
            if (changed)
            {
                _revision++;
                thread.Signal(_revision);
            }
        }
    }

    public void ObserveExternalTurn(
        string threadId,
        string turnId,
        string status,
        DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId)) return;
        lock (_gate)
        {
            var thread = GetThread(threadId);
            var turn = thread.GetOrAdd(turnId);
            turn.MarkExternal();
            turn.SetStatus(status, observedAt);
            thread.Trim();
            _revision++;
            thread.Signal(_revision);
        }
    }

    public void ObserveExternalItem(
        string threadId,
        string turnId,
        JsonElement item,
        DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId) ||
            item.ValueKind != JsonValueKind.Object) return;
        lock (_gate)
        {
            var thread = GetThread(threadId);
            var turn = thread.GetOrAdd(turnId);
            turn.MarkExternal();
            turn.Upsert(item, observedAt);
            thread.Trim();
            _revision++;
            thread.Signal(_revision);
        }
    }

    public LiveThreadSnapshot Snapshot(string threadId)
    {
        lock (_gate)
        {
            if (!_threads.TryGetValue(threadId, out var thread))
                return new LiveThreadSnapshot(_revision, Array.Empty<JsonElement>());
            return new LiveThreadSnapshot(
                _revision,
                thread.Order.Select(id => thread.Turns[id].Snapshot()).ToArray());
        }
    }

    public async Task<LiveThreadSnapshot> WaitForChangeAsync(
        string threadId,
        long afterRevision,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task<long> waiter;
        lock (_gate)
        {
            if (_revision > afterRevision) return SnapshotUnsafe(threadId);
            waiter = GetThread(threadId).Waiter;
        }
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try { await waiter.WaitAsync(timeoutSource.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        // Agent text arrives token by token. A short coalescing window keeps the phone
        // smooth while still feeling instantaneous.
        await Task.Delay(180, cancellationToken);
        lock (_gate) return SnapshotUnsafe(threadId);
    }

    private LiveThreadSnapshot SnapshotUnsafe(string threadId)
    {
        if (!_threads.TryGetValue(threadId, out var thread))
            return new LiveThreadSnapshot(_revision, Array.Empty<JsonElement>());
        return new LiveThreadSnapshot(
            _revision,
            thread.Order.Select(id => thread.Turns[id].Snapshot()).ToArray());
    }

    private LiveThread GetThread(string threadId)
    {
        if (_threads.TryGetValue(threadId, out var thread)) return thread;
        thread = new LiveThread();
        _threads[threadId] = thread;
        return thread;
    }

    private static bool ObserveTurn(LiveThread thread, JsonElement parameters, bool completed)
    {
        if (!parameters.TryGetProperty("turn", out var turn) ||
            turn.ValueKind != JsonValueKind.Object ||
            !TryString(turn, "id", out var turnId)) return false;
        var live = thread.GetOrAdd(turnId);
        live.ReplaceTurn(turn, completed);
        thread.Trim();
        return true;
    }

    private static bool ObserveItem(LiveThread thread, JsonElement parameters)
    {
        if (!TryString(parameters, "turnId", out var turnId) ||
            !parameters.TryGetProperty("item", out var item) ||
            item.ValueKind != JsonValueKind.Object) return false;
        thread.GetOrAdd(turnId).Upsert(item);
        thread.Trim();
        return true;
    }

    private static bool ObserveAgentDelta(LiveThread thread, JsonElement parameters)
    {
        if (!TryIds(parameters, out var turnId, out var itemId) ||
            !TryString(parameters, "delta", out var delta)) return false;
        thread.GetOrAdd(turnId).GetOrAdd(itemId, "agentMessage").AppendAgent(delta);
        return true;
    }

    private static bool ObserveReasoningPart(LiveThread thread, JsonElement parameters)
    {
        if (!TryIds(parameters, out var turnId, out var itemId) ||
            !TryInt(parameters, "summaryIndex", out var index)) return false;
        thread.GetOrAdd(turnId).GetOrAdd(itemId, "reasoning").EnsureSummary(index);
        return true;
    }

    private static bool ObserveReasoningSummaryDelta(LiveThread thread, JsonElement parameters)
    {
        if (!TryIds(parameters, out var turnId, out var itemId) ||
            !TryInt(parameters, "summaryIndex", out var index) ||
            !TryString(parameters, "delta", out var delta)) return false;
        thread.GetOrAdd(turnId).GetOrAdd(itemId, "reasoning").AppendSummary(index, delta);
        return true;
    }

    private static bool ObserveReasoningTextDelta(LiveThread thread, JsonElement parameters)
    {
        if (!TryIds(parameters, out var turnId, out var itemId) ||
            !TryInt(parameters, "contentIndex", out var index) ||
            !TryString(parameters, "delta", out var delta)) return false;
        thread.GetOrAdd(turnId).GetOrAdd(itemId, "reasoning").AppendContent(index, delta);
        return true;
    }

    private static bool TryIds(JsonElement parameters, out string turnId, out string itemId)
    {
        turnId = "";
        itemId = "";
        return TryString(parameters, "turnId", out turnId) &&
               TryString(parameters, "itemId", out itemId);
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = property.GetString() ?? "");
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out value) && value >= 0;
    }

    private sealed class LiveThread
    {
        private TaskCompletionSource<long> _change = NewChangeSource();
        public List<string> Order { get; } = new();
        public Dictionary<string, LiveTurn> Turns { get; } = new(StringComparer.Ordinal);
        public Task<long> Waiter => _change.Task;
        public bool IsEmpty => Order.Count == 0;

        public void Signal(long revision)
        {
            var previous = _change;
            _change = NewChangeSource();
            previous.TrySetResult(revision);
        }

        private static TaskCompletionSource<long> NewChangeSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LiveTurn GetOrAdd(string id)
        {
            if (Turns.TryGetValue(id, out var turn)) return turn;
            turn = new LiveTurn(id);
            Turns[id] = turn;
            Order.Add(id);
            return turn;
        }

        public void Trim()
        {
            while (Order.Count > MaximumTurnsPerThread)
            {
                var removable = Order.FirstOrDefault(id => Turns[id].Completed) ?? Order[0];
                Order.Remove(removable);
                Turns.Remove(removable);
            }
        }

        public void RetainExternalTurns()
        {
            foreach (var id in Order.Where(id => !Turns[id].IsExternal).ToArray())
            {
                Order.Remove(id);
                Turns.Remove(id);
            }
        }
    }

    private sealed class LiveTurn
    {
        private readonly List<string> _order = new();
        private readonly Dictionary<string, LiveItem> _items = new(StringComparer.Ordinal);
        private string _status = "inProgress";
        private JsonElement? _error;
        private long? _startedAt;
        private long? _completedAt;
        private long? _durationMs;

        public LiveTurn(string id) => Id = id;
        public string Id { get; }
        public bool Completed => _status is not "inProgress";
        public bool IsExternal { get; private set; }

        public void MarkExternal() => IsExternal = true;

        public void SetStatus(string status, DateTimeOffset? observedAt = null)
        {
            _status = status switch
            {
                "task_complete" => "completed",
                "turn_aborted" => "interrupted",
                "task_started" or "running" => "inProgress",
                _ => string.IsNullOrWhiteSpace(status) ? _status : status
            };
            var seconds = observedAt?.ToUnixTimeSeconds();
            if (_status == "inProgress")
            {
                _startedAt ??= seconds;
            }
            else
            {
                _completedAt = seconds ?? _completedAt;
                if (_startedAt.HasValue && _completedAt.HasValue)
                    _durationMs = Math.Max(0, (_completedAt.Value - _startedAt.Value) * 1_000);
            }
        }

        public void ReplaceTurn(JsonElement turn, bool completed)
        {
            if (turn.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
                _status = status.GetString() ?? _status;
            else if (completed) _status = "completed";
            _error = CloneProperty(turn, "error");
            _startedAt = LongProperty(turn, "startedAt") ?? _startedAt;
            _completedAt = LongProperty(turn, "completedAt") ?? _completedAt;
            _durationMs = LongProperty(turn, "durationMs") ?? _durationMs;
            if (turn.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray()) Upsert(item);
        }

        public void Upsert(JsonElement item, DateTimeOffset? observedAt = null)
        {
            if (item.ValueKind != JsonValueKind.Object || !TryString(item, "id", out var id)) return;
            if (_items.TryGetValue(id, out var existing)) existing.Replace(item, observedAt);
            else if (TryMakeRoom(TryString(item, "type", out var type) ? type : "unknown"))
            {
                _items[id] = new LiveItem(item, observedAt);
                _order.Add(id);
            }
        }

        public LiveItem GetOrAdd(string id, string type)
        {
            if (_items.TryGetValue(id, out var item)) return item;
            item = new LiveItem(id, type, DateTimeOffset.UtcNow);
            if (TryMakeRoom(type))
            {
                _items[id] = item;
                _order.Add(id);
            }
            return item;
        }

        private bool TryMakeRoom(string incomingType)
        {
            if (_order.Count < MaximumItemsPerTurn) return true;
            var disposable = _order.FirstOrDefault(id => !_items[id].IsMessage);
            if (!string.IsNullOrEmpty(disposable))
            {
                _order.Remove(disposable);
                _items.Remove(disposable);
                return true;
            }
            // User and assistant messages are never discarded, even in an unusually
            // long turn. Process items may be summarized once the bounded cache is full.
            return incomingType is "userMessage" or "agentMessage";
        }

        public JsonElement Snapshot() => JsonSerializer.SerializeToElement(new
        {
            id = Id,
            items = _order.Where(_items.ContainsKey).Select(id => _items[id].Snapshot()).ToArray(),
            itemsView = IsExternal ? "liveTail" : "full",
            status = _status,
            error = _error,
            startedAt = _startedAt,
            completedAt = _completedAt,
            durationMs = _durationMs
        });

        private static JsonElement? CloneProperty(JsonElement element, string name) =>
            element.TryGetProperty(name, out var property) ? property.Clone() : null;

        private static long? LongProperty(JsonElement element, string name) =>
            element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value) ? value : null;
    }

    private sealed class LiveItem
    {
        private JsonElement? _raw;
        private string _type;
        private string _agentText = "";
        private string? _phase;
        private JsonElement? _memoryCitation;
        private long? _createdAt;
        private long? _updatedAt;
        private readonly List<StringBuilder> _summary = new();
        private readonly List<StringBuilder> _content = new();

        public LiveItem(JsonElement item, DateTimeOffset? observedAt = null)
        {
            Id = TryString(item, "id", out var id) ? id : Guid.NewGuid().ToString("N");
            _type = TryString(item, "type", out var type) ? type : "unknown";
            Replace(item, observedAt);
        }

        public LiveItem(string id, string type, DateTimeOffset? observedAt = null)
        {
            Id = id;
            _type = type;
            _createdAt = (observedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            _updatedAt = _createdAt;
        }

        public string Id { get; }
        public bool IsMessage => _type is "userMessage" or "agentMessage";

        public void Replace(JsonElement item, DateTimeOffset? observedAt = null)
        {
            _raw = item.Clone();
            var observedSeconds = (observedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            _createdAt ??= Timestamp(item, "createdAt") ?? observedSeconds;
            _updatedAt = Timestamp(item, "updatedAt") ?? observedSeconds;
            if (TryString(item, "type", out var type)) _type = type;
            if (_type == "agentMessage" && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                var complete = text.GetString() ?? "";
                if (complete.Length >= _agentText.Length) _agentText = Bound(complete, MaximumAgentCharacters);
            }
            if (_type == "agentMessage")
            {
                if (item.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String)
                    _phase = phase.GetString();
                if (item.TryGetProperty("memoryCitation", out var citation)) _memoryCitation = citation.Clone();
            }
            if (_type == "reasoning")
            {
                ImportStrings(item, "summary", _summary);
                ImportStrings(item, "content", _content);
            }
        }

        public void AppendAgent(string delta)
        {
            _agentText = Append(_agentText, delta, MaximumAgentCharacters);
            _updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        public void EnsureSummary(int index) => Ensure(_summary, index);
        public void AppendSummary(int index, string delta) => Append(_summary, index, delta);
        public void AppendContent(int index, string delta) => Append(_content, index, delta);

        public JsonElement Snapshot()
        {
            if (_type == "agentMessage")
                return JsonSerializer.SerializeToElement(new
                {
                    type = _type,
                    id = Id,
                    text = _agentText,
                    phase = _phase,
                    memoryCitation = _memoryCitation,
                    createdAt = _createdAt,
                    updatedAt = _updatedAt
                });
            if (_type == "reasoning")
                return JsonSerializer.SerializeToElement(new
                {
                    type = _type,
                    id = Id,
                    summary = _summary.Select(value => value.ToString()).ToArray(),
                    content = _content.Select(value => value.ToString()).ToArray(),
                    createdAt = _createdAt,
                    updatedAt = _updatedAt
                });
            if (_raw is not { } raw)
                return JsonSerializer.SerializeToElement(new
                {
                    type = _type, id = Id, createdAt = _createdAt, updatedAt = _updatedAt
                });
            var properties = raw.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            if (!properties.ContainsKey("createdAt"))
                properties["createdAt"] = JsonSerializer.SerializeToElement(_createdAt);
            properties["updatedAt"] = JsonSerializer.SerializeToElement(_updatedAt);
            return JsonSerializer.SerializeToElement(properties);
        }

        private static long? Timestamp(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var seconds)
                ? seconds
                : null;

        private static void ImportStrings(JsonElement item, string name, List<StringBuilder> destination)
        {
            if (!item.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return;
            var incoming = values.EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "")
                .ToArray();
            for (var index = 0; index < incoming.Length; index++)
            {
                Ensure(destination, index);
                if (incoming[index].Length >= destination[index].Length)
                {
                    destination[index].Clear();
                    destination[index].Append(Bound(incoming[index], MaximumProcessCharacters));
                }
            }
        }

        private static void Append(List<StringBuilder> values, int index, string delta)
        {
            Ensure(values, index);
            var remaining = MaximumProcessCharacters - values[index].Length;
            if (remaining > 0) values[index].Append(delta.AsSpan(0, Math.Min(delta.Length, remaining)));
        }

        private static void Ensure(List<StringBuilder> values, int index)
        {
            while (values.Count <= Math.Min(index, 63)) values.Add(new StringBuilder());
        }

        private static string Append(string current, string delta, int maximum) =>
            current.Length >= maximum ? current : current + delta[..Math.Min(delta.Length, maximum - current.Length)];

        private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    }
}

public sealed record LiveThreadSnapshot(long Revision, IReadOnlyList<JsonElement> Turns);
