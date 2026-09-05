using System.Text.Json;

namespace CodexLanBridge;

public sealed class NotificationMonitor : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumTurnReadInterval = TimeSpan.FromSeconds(30);
    private const int ThreadListLimit = 50;
    private const int MaximumReadsPerScan = 2;

    private readonly CodexAppServer _codex;
    private readonly NotificationStore _notifications;
    private readonly ThreadRuntimeStateStore _runtimeStates;
    private readonly Dictionary<string, ThreadObservation> _threads = new(StringComparer.Ordinal);
    private bool _hasBaseline;
    private int _turnListCapability;
    private long _lastSuccessfulScanAt;

    public NotificationMonitor(
        CodexAppServer codex,
        NotificationStore notifications,
        ThreadRuntimeStateStore runtimeStates)
    {
        _codex = codex;
        _notifications = notifications;
        _runtimeStates = runtimeStates;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_codex.IsReady) await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Notification reconciliation failed: {ex.Message}");
            }

            try { await Task.Delay(ScanInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var response = await _codex.CallAsync("thread/list", new
        {
            limit = ThreadListLimit,
            sortKey = "updated_at",
            sortDirection = "desc"
        }, cancellationToken);
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;

        var initialScan = !_hasBaseline;
        var now = DateTimeOffset.UtcNow;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var thread in data.EnumerateArray())
        {
            var threadId = Text(thread, "id");
            if (threadId is null) continue;
            present.Add(threadId);
            var updatedAt = Integer(thread, "updatedAt");
            var status = ThreadStatus(thread);
            if (thread.TryGetProperty("status", out var statusElement))
            {
                DateTimeOffset? updated = null;
                if (updatedAt > 0)
                {
                    try { updated = DateTimeOffset.FromUnixTimeSeconds(updatedAt); }
                    catch (ArgumentOutOfRangeException) { }
                }
                _runtimeStates.ObserveHistoricalStatus(threadId, statusElement, updated);
            }

            if (!_threads.TryGetValue(threadId, out var observation))
            {
                var activeAtBaseline = initialScan && status.Equals("active", StringComparison.Ordinal);
                observation = new ThreadObservation
                {
                    UpdatedAt = updatedAt,
                    Status = status,
                    LastReadStatus = initialScan && !activeAtBaseline ? status : null,
                    LastReconciledUpdatedAt = initialScan ? updatedAt : _lastSuccessfulScanAt,
                    NeedsRead = !initialScan || activeAtBaseline,
                    SuppressNextTerminal = activeAtBaseline
                };
                _threads[threadId] = observation;
                continue;
            }

            var previousStatus = observation.Status;
            var changed = observation.UpdatedAt != updatedAt ||
                          !previousStatus.Equals(status, StringComparison.Ordinal);
            if (!initialScan && previousStatus.Equals("active", StringComparison.Ordinal) &&
                !status.Equals("active", StringComparison.Ordinal))
                observation.TerminalStatusTransition = status;
            if (changed && !(previousStatus.Equals("active", StringComparison.Ordinal) &&
                             status.Equals("active", StringComparison.Ordinal)))
                observation.NeedsRead = true;
            observation.UpdatedAt = updatedAt;
            observation.Status = status;
        }

        foreach (var staleId in _threads.Keys.Where(id => !present.Contains(id)).ToArray())
            _threads.Remove(staleId);

        var candidates = _threads
            .Where(pair => pair.Value.NeedsRead &&
                           (Volatile.Read(ref _turnListCapability) < 0 ||
                            pair.Value.TerminalStatusTransition is not null ||
                            pair.Value.Status.Equals("systemError", StringComparison.Ordinal) ||
                            now - pair.Value.LastTurnReadAt >= MinimumTurnReadInterval))
            .OrderByDescending(pair => pair.Value.Status.Equals("active", StringComparison.Ordinal))
            .ThenByDescending(pair => pair.Value.UpdatedAt)
            .Take(MaximumReadsPerScan)
            .ToArray();

        foreach (var candidate in candidates)
        {
            try { await ReconcileThreadAsync(candidate.Key, candidate.Value, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Notification reconciliation skipped task {candidate.Key}: {ex.Message}");
            }
        }

        _hasBaseline = true;
        _lastSuccessfulScanAt = now.ToUnixTimeSeconds();
    }

    private async Task ReconcileThreadAsync(
        string threadId,
        ThreadObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            observation.LastTurnReadAt = DateTimeOffset.UtcNow;
            if (Volatile.Read(ref _turnListCapability) < 0)
            {
                ReconcileFromThreadStatus(threadId, observation);
                return;
            }

            JsonElement response;
            try
            {
                response = await _codex.CallAsync(
                    "thread/turns/list",
                    new
                    {
                        threadId,
                        limit = 5,
                        sortDirection = "desc",
                        itemsView = "notLoaded"
                    },
                    cancellationToken);
                Volatile.Write(ref _turnListCapability, 1);
            }
            catch (CodexRpcException ex) when (ex.Code == -32601)
            {
                if (Interlocked.Exchange(ref _turnListCapability, -1) >= 0)
                    Console.Error.WriteLine("This Codex app-server does not support lightweight turn listing; notification monitoring will use thread status only.");
                ReconcileFromThreadStatus(threadId, observation);
                return;
            }

            var latestTurns = LatestTurns(response);
            _runtimeStates.ObserveLatestPersistedTurn(threadId, response);
            var suppressExistingTerminal = observation.SuppressNextTerminal;
            var statusChangedSinceRead = !observation.Status.Equals(observation.LastReadStatus, StringComparison.Ordinal);
            foreach (var turn in SelectNewTerminalOutcomes(
                         latestTurns,
                         observation.LastReconciledUpdatedAt,
                         observation.SeenOutcomes,
                         suppressExistingTerminal))
            {
                _notifications.PublishTurnOutcome(threadId, turn.Id, turn.Status, turn.CompletedAt);
            }
            foreach (var turn in latestTurns.Where(turn => IsTerminal(turn.Status)))
            {
                var outcomeKey = $"{turn.Id}:{turn.Status}";
                observation.RememberOutcome(outcomeKey);
            }
            var latest = latestTurns.FirstOrDefault();
            if (latest is not null)
            {
                observation.LastTurnId = latest.Id;
                observation.LastTurnStatus = latest.Status;
            }
            if (!suppressExistingTerminal &&
                statusChangedSinceRead &&
                observation.Status.Equals("systemError", StringComparison.Ordinal) &&
                (latest is null || !latest.Status.Equals("failed", StringComparison.Ordinal)))
            {
                _notifications.PublishThreadFailure(threadId, observation.UpdatedAt);
            }
            observation.LastReconciledUpdatedAt = Math.Max(observation.LastReconciledUpdatedAt, observation.UpdatedAt);
            observation.LastReadStatus = observation.Status;
            observation.SuppressNextTerminal = false;
            observation.TerminalStatusTransition = null;
            observation.NeedsRead = false;
        }
        catch (CodexRpcException ex) when (ex.IsThreadNotFound)
        {
            observation.NeedsRead = false;
        }
        catch
        {
            observation.NeedsRead = true;
            throw;
        }
    }

    private void ReconcileFromThreadStatus(string threadId, ThreadObservation observation)
    {
        if (!observation.SuppressNextTerminal)
        {
            if (observation.Status.Equals("systemError", StringComparison.Ordinal))
                _notifications.PublishThreadFailure(threadId, observation.UpdatedAt);
            else if (observation.TerminalStatusTransition is "idle")
                _notifications.PublishThreadStateCompletion(threadId, observation.UpdatedAt);
        }
        observation.LastReadStatus = observation.Status;
        observation.LastReconciledUpdatedAt = Math.Max(observation.LastReconciledUpdatedAt, observation.UpdatedAt);
        observation.SuppressNextTerminal = false;
        observation.TerminalStatusTransition = null;
        observation.NeedsRead = false;
    }

    private static IReadOnlyList<TurnObservation> LatestTurns(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("data", out var turns) ||
            turns.ValueKind != JsonValueKind.Array) return Array.Empty<TurnObservation>();
        var output = new List<TurnObservation>(5);
        foreach (var turn in turns.EnumerateArray())
        {
            var id = Text(turn, "id");
            var status = Text(turn, "status");
            if (id is null || status is null) continue;
            DateTimeOffset? completedAt = null;
            var completedSeconds = Integer(turn, "completedAt");
            if (completedSeconds > 0)
            {
                try { completedAt = DateTimeOffset.FromUnixTimeSeconds(completedSeconds); }
                catch (ArgumentOutOfRangeException) { }
            }
            output.Add(new TurnObservation(id, status, completedSeconds > 0 ? completedSeconds : null, completedAt));
            if (output.Count == 5) break;
        }
        return output;
    }

    private static IReadOnlyList<TurnObservation> SelectNewTerminalOutcomes(
        IReadOnlyList<TurnObservation> turns,
        long completedAfter,
        IReadOnlySet<string> seenOutcomes,
        bool suppressExistingTerminal)
    {
        if (suppressExistingTerminal) return Array.Empty<TurnObservation>();
        return turns
            .Where(turn =>
                IsTerminal(turn.Status) &&
                turn.CompletedAtUnixSeconds is { } seconds &&
                seconds > completedAfter &&
                !seenOutcomes.Contains($"{turn.Id}:{turn.Status}"))
            .OrderBy(turn => turn.CompletedAtUnixSeconds)
            .ToArray();
    }

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "interrupted";

    private static string ThreadStatus(JsonElement thread)
    {
        if (!thread.TryGetProperty("status", out var status)) return "unknown";
        if (status.ValueKind == JsonValueKind.String) return status.GetString() ?? "unknown";
        return Text(status, "type") ?? "unknown";
    }

    private static long Integer(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result)
            ? result
            : 0;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private sealed class ThreadObservation
    {
        public long UpdatedAt { get; set; }
        public string Status { get; set; } = "unknown";
        public string? LastTurnId { get; set; }
        public string? LastTurnStatus { get; set; }
        public string? LastReadStatus { get; set; }
        public string? TerminalStatusTransition { get; set; }
        public DateTimeOffset LastTurnReadAt { get; set; } = DateTimeOffset.MinValue;
        public long LastReconciledUpdatedAt { get; set; }
        public bool SuppressNextTerminal { get; set; }
        public bool NeedsRead { get; set; }
        public HashSet<string> SeenOutcomes { get; } = new(StringComparer.Ordinal);
        private Queue<string> SeenOutcomeOrder { get; } = new();

        public void RememberOutcome(string key)
        {
            if (!SeenOutcomes.Add(key)) return;
            SeenOutcomeOrder.Enqueue(key);
            while (SeenOutcomeOrder.Count > 16)
                SeenOutcomes.Remove(SeenOutcomeOrder.Dequeue());
        }
    }

    private sealed record TurnObservation(
        string Id,
        string Status,
        long? CompletedAtUnixSeconds,
        DateTimeOffset? CompletedAt);
}
