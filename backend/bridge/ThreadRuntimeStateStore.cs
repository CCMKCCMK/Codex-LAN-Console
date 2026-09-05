using System.Collections.Concurrent;
using System.Text.Json;

namespace CodexLanBridge;

public sealed record ThreadRuntimeSnapshot(
    string ThreadId,
    string Phase,
    bool? IsRunning,
    string? ActiveTurnId,
    string[] ActiveFlags,
    string? LastOutcome,
    string Source,
    bool CanControl,
    DateTimeOffset ObservedAt,
    long Generation,
    bool Stale)
{
    public DateTimeOffset? FreshUntil { get; init; }
}

/// <summary>
/// Keeps live task state separate from persisted thread history. The bridge
/// app-server is authoritative for tasks it owns; rollout observations are a
/// best-effort view of tasks currently owned by Codex Desktop.
/// </summary>
public sealed class ThreadRuntimeStateStore
{
    // A task_started record is not a lease. If its rollout stops changing, the
    // owning desktop process may have exited without writing task_complete.
    private static readonly TimeSpan ExternalRunningLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ExternalWaitingLifetime = TimeSpan.FromHours(2);
    // File activity alone is only a short lease. It is used when a very large
    // Desktop rollout no longer exposes task_started inside the bounded
    // lifecycle scan, and only while thread/turns/list confirms the latest turn
    // is still in progress.
    private static readonly TimeSpan InferredExternalRunningLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, RuntimeEvidence> _appServer = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RuntimeEvidence> _rollout = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _rolloutActivity = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RuntimeEvidence> _history = new(StringComparer.Ordinal);
    private long _generation;

    public long BeginGeneration()
    {
        var generation = Interlocked.Increment(ref _generation);
        _appServer.Clear();
        return generation;
    }

    public void InvalidateGeneration(long generation)
    {
        if (generation == Volatile.Read(ref _generation)) _appServer.Clear();
    }

    public void ForgetAppServerThread(string threadId, long generation)
    {
        if (IsCurrentGeneration(generation)) _appServer.TryRemove(threadId, out _);
    }

    public void ObserveAppServerStatus(string threadId, JsonElement status, long generation, DateTimeOffset? observedAt = null)
    {
        if (!IsCurrentGeneration(generation) || status.ValueKind != JsonValueKind.Object ||
            !TryText(status, "type", out var type)) return;
        if (type.Equals("notLoaded", StringComparison.Ordinal))
        {
            // notLoaded is an explicit loss of visibility in this app-server
            // generation. Do not keep an earlier idle/running/canControl snapshot.
            ForgetAppServerThread(threadId, generation);
            return;
        }

        var flags = type.Equals("active", StringComparison.Ordinal)
            ? ReadFlags(status)
            : Array.Empty<string>();
        var phase = type switch
        {
            "active" => PhaseFor(flags),
            "idle" => "idle",
            "systemError" => "error",
            _ => "unknown"
        };
        UpdateAppServer(threadId, generation, current => new RuntimeEvidence(
            phase,
            phase is "running" or "waitingInput" or "waitingApproval" or "waitingAction" ? true : phase is "idle" or "error" ? false : null,
            phase.StartsWith("waiting", StringComparison.Ordinal) || phase == "running" ? current?.ActiveTurnId : null,
            flags,
            phase == "error" ? "failed" : current?.LastOutcome,
            observedAt ?? DateTimeOffset.UtcNow,
            generation));
    }

    /// <summary>
    /// Records a status read from the persisted thread index. An indexed
    /// "active" value is not proof that this app-server owns a live turn: it can
    /// survive a crashed Desktop process indefinitely.
    /// </summary>
    public void ObserveHistoricalStatus(string threadId, JsonElement status, DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || status.ValueKind != JsonValueKind.Object ||
            !TryText(status, "type", out var type)) return;
        var at = observedAt ?? DateTimeOffset.UtcNow;
        var evidence = type switch
        {
            "idle" => new RuntimeEvidence("idle", false, null, Array.Empty<string>(), null, at, 0),
            "systemError" => new RuntimeEvidence("error", false, null, Array.Empty<string>(), "failed", at, 0),
            "active" or "notLoaded" => new RuntimeEvidence(
                "unknown", null, null, Array.Empty<string>(), null, at, 0),
            _ => null
        };
        if (evidence is null) return;
        _history.AddOrUpdate(threadId,
            evidence,
            (_, current) =>
            {
                // A latest-turn result is stronger than the denormalized thread
                // status and carries a useful completion outcome. notLoaded is
                // only a loss of visibility, while a newer indexed active bit
                // must replace an older terminal result with unknown state.
                if (type.Equals("notLoaded", StringComparison.Ordinal) &&
                    current.IsRunning == false && current.ActiveTurnId is not null) return current;
                return at < current.ObservedAt ? current : evidence;
            });
    }

    public void ObserveTurnStarted(string threadId, string turnId, long generation, DateTimeOffset? observedAt = null) =>
        UpdateAppServer(threadId, generation, current => new RuntimeEvidence(
            "running", true, turnId, Array.Empty<string>(), current?.LastOutcome,
            observedAt ?? DateTimeOffset.UtcNow, generation));

    public void ObserveTurnCompleted(
        string threadId,
        string turnId,
        string status,
        long generation,
        DateTimeOffset? observedAt = null)
    {
        var normalized = status.Trim().ToLowerInvariant();
        var failed = normalized == "failed";
        var at = observedAt ?? DateTimeOffset.UtcNow;
        var outcome = normalized switch
        {
            "completed" => "completed",
            "interrupted" => "interrupted",
            "failed" => "failed",
            _ => normalized
        };
        UpdateAppServer(threadId, generation, _ => new RuntimeEvidence(
            failed ? "error" : "idle", false, turnId, Array.Empty<string>(), outcome,
            at, generation));
        // A direct app-server completion event is live protocol evidence, not a
        // denormalized persisted status. End the matching external rollout so it
        // cannot outrank this authoritative terminal event in Get().
        EndMatchingRollout(threadId, turnId, outcome, at);
    }

    /// <summary>
    /// Reconciles best-effort live observations with the latest persisted turn.
    /// A timestamped terminal turn ends an orphaned rollout even when Desktop did
    /// not append its final lifecycle event. An untimestamped terminal record is
    /// weaker than a fresh, matching Desktop rollout because the persisted index
    /// can transiently report interrupted while that turn is still producing data.
    /// </summary>
    public void ObservePersistedTurn(
        string threadId,
        string turnId,
        string status,
        DateTimeOffset? completedAt = null,
        DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId) ||
            string.IsNullOrWhiteSpace(status)) return;
        var normalized = status.Trim().Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        if (normalized is "inprogress" or "running")
        {
            var progressAt = observedAt ?? DateTimeOffset.UtcNow;
            var progressEvidence = new RuntimeEvidence(
                "unknown", null, turnId, Array.Empty<string>(), null, progressAt, 0);
            _history.AddOrUpdate(threadId, progressEvidence, (_, current) =>
                progressAt >= current.ObservedAt ? progressEvidence : current);
            return;
        }
        if (normalized is not ("completed" or "failed" or "interrupted" or "cancelled" or "canceled")) return;

        var outcome = normalized switch
        {
            "cancelled" or "canceled" => "interrupted",
            _ => normalized
        };
        // Fetch time is not event time. A terminal record without completedAt
        // must not end the same turn while fresh Desktop rollout evidence still
        // says it is running. A direct app-server turn/completed notification is
        // handled by ObserveTurnCompleted and remains authoritative.
        if (!completedAt.HasValue && HasFreshMatchingRollout(threadId, turnId)) return;

        var at = completedAt ?? DateTimeOffset.MinValue;
        var evidence = new RuntimeEvidence(
            outcome == "failed" ? "error" : "idle",
            false,
            turnId,
            Array.Empty<string>(),
            outcome,
            at,
            0);
        EndMatchingRollout(threadId, turnId, outcome, completedAt);
        _history.AddOrUpdate(threadId, evidence, (_, current) =>
            current.ActiveTurnId == turnId || completedAt.HasValue && at >= current.ObservedAt
                ? evidence
                : current);
    }

    public void ObserveLatestPersistedTurn(
        string threadId,
        JsonElement response,
        DateTimeOffset? observedAt = null)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("data", out var turns) ||
            turns.ValueKind != JsonValueKind.Array) return;
        foreach (var turn in turns.EnumerateArray())
        {
            if (!TryText(turn, "id", out var turnId) || !TryText(turn, "status", out var status)) return;
            ObservePersistedTurn(threadId, turnId, status, UnixTimestamp(turn, "completedAt"), observedAt);
            return;
        }
    }

    public void ObservePending(
        string threadId,
        string? turnId,
        bool userInput,
        long generation,
        DateTimeOffset? observedAt = null)
    {
        UpdateAppServer(threadId, generation, current =>
        {
            var flag = userInput ? "waitingOnUserInput" : "waitingOnApproval";
            var flags = (current?.ActiveFlags ?? Array.Empty<string>())
                .Append(flag)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new RuntimeEvidence(
                PhaseFor(flags), true, string.IsNullOrWhiteSpace(turnId) ? current?.ActiveTurnId : turnId,
                flags, current?.LastOutcome, observedAt ?? DateTimeOffset.UtcNow, generation);
        });
    }

    public void ResolvePending(string threadId, bool userInput, long generation, DateTimeOffset? observedAt = null)
    {
        UpdateAppServer(threadId, generation, current =>
        {
            if (current is null || current.IsRunning != true) return current;
            var flag = userInput ? "waitingOnUserInput" : "waitingOnApproval";
            var flags = current.ActiveFlags.Where(value => !value.Equals(flag, StringComparison.Ordinal)).ToArray();
            return current with
            {
                Phase = flags.Length == 0 ? "running" : PhaseFor(flags),
                ActiveFlags = flags,
                ObservedAt = observedAt ?? DateTimeOffset.UtcNow
            };
        });
    }

    public void ObserveRolloutLifecycle(
        string threadId,
        string eventType,
        string? turnId,
        DateTimeOffset? observedAt = null)
    {
        var at = observedAt ?? DateTimeOffset.UtcNow;
        ObserveRolloutActivity(threadId, at);
        _rollout.AddOrUpdate(threadId,
            _ => RolloutEvidence(eventType, turnId, null, at),
            (_, current) => at < current.ObservedAt ? current : RolloutEvidence(eventType, turnId, current, at));
    }

    public void ObserveRolloutWaiting(
        string threadId,
        string? turnId,
        bool waiting,
        DateTimeOffset? observedAt = null)
    {
        var at = observedAt ?? DateTimeOffset.UtcNow;
        ObserveRolloutActivity(threadId, at);
        _rollout.AddOrUpdate(threadId,
            _ => waiting
                ? new RuntimeEvidence("waitingInput", true, turnId, ["waitingOnUserInput"], null, at, 0)
                : new RuntimeEvidence("running", true, turnId, Array.Empty<string>(), null, at, 0),
            (_, current) =>
            {
                if (at < current.ObservedAt || current.IsRunning == false) return current;
                return current with
                {
                    Phase = waiting ? "waitingInput" : "running",
                    IsRunning = true,
                    ActiveTurnId = string.IsNullOrWhiteSpace(turnId) ? current.ActiveTurnId : turnId,
                    ActiveFlags = waiting ? ["waitingOnUserInput"] : Array.Empty<string>(),
                    ObservedAt = at
                };
            });
    }

    public void ObserveRolloutActivity(string threadId, DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        var at = observedAt ?? DateTimeOffset.UtcNow;
        _rolloutActivity.AddOrUpdate(threadId, at, (_, current) => at > current ? at : current);
    }

    public ThreadRuntimeSnapshot? Get(string threadId)
    {
        _appServer.TryGetValue(threadId, out var appServer);
        if (appServer is not null && appServer.Generation != Volatile.Read(ref _generation))
            appServer = null;
        _rollout.TryGetValue(threadId, out var rollout);
        _history.TryGetValue(threadId, out var history);
        if (rollout is null &&
            history is { IsRunning: null, ActiveTurnId.Length: > 0 } &&
            _rolloutActivity.TryGetValue(threadId, out var activityAt) &&
            DateTimeOffset.UtcNow <= activityAt + InferredExternalRunningLifetime)
        {
            rollout = new RuntimeEvidence(
                "running",
                true,
                history.ActiveTurnId,
                Array.Empty<string>(),
                null,
                activityAt,
                0,
                InferredFromActivity: true);
        }
        RuntimeEvidence? selected;
        var source = "appServer";
        var canControl = true;
        var rolloutIsFreshAndActive = rollout?.IsRunning == true && IsFreshRollout(threadId, rollout);
        var appServerHasKnownTurn = appServer?.ActiveTurnId is { Length: > 0 };
        var matchingAppServerTurn = appServerHasKnownTurn &&
                                    rollout?.ActiveTurnId is { Length: > 0 } &&
                                    string.Equals(appServer!.ActiveTurnId, rollout.ActiveTurnId, StringComparison.Ordinal);
        var appServerOwnsRunningTurn = appServerHasKnownTurn && appServer!.IsRunning == true;

        // The rollout originator describes who created the thread, not who owns
        // every later turn. A Desktop-created thread can be resumed by this
        // app-server, so a matching current-generation turn/started observation
        // is authoritative and remains controllable from the phone. A direct
        // turn/completed observation for the same turn also wins over a delayed
        // task_started tail record, preventing a completed bridge turn from
        // being resurrected as an externally-owned active turn.
        if (matchingAppServerTurn || appServerOwnsRunningTurn && !rolloutIsFreshAndActive)
        {
            selected = appServer;
        }
        else if (rolloutIsFreshAndActive || appServer is null || rollout is not null && rollout.ObservedAt > appServer.ObservedAt)
        {
            selected = rollout;
            source = "rollout";
            canControl = rollout?.IsRunning != true;
        }
        else selected = appServer;

        var selectedIsFreshRunningRollout = source == "rollout" &&
                                            selected?.IsRunning == true &&
                                            rolloutIsFreshAndActive;
        var historyMatchesSelected = selected is not null &&
                                     history?.ActiveTurnId == selected.ActiveTurnId;
        if (history?.IsRunning == false &&
            (selected is null ||
             history.ObservedAt >= selected.ObservedAt ||
             historyMatchesSelected && !selectedIsFreshRunningRollout))
        {
            selected = history;
            source = "history";
            canControl = true;
        }
        else if (history is { IsRunning: null } && selected?.IsRunning != true &&
                 (selected is null || history.ObservedAt >= selected.ObservedAt))
        {
            selected = history;
            source = "history";
            canControl = true;
        }
        else if (selected is null && history is not null)
        {
            selected = history;
            source = "history";
            canControl = true;
        }
        if (selected is null) return null;

        var stale = source == "rollout" && selected.IsRunning == true &&
                    !IsFreshRollout(threadId, selected);
        var snapshotObservedAt = source == "rollout" && selected.IsRunning == true
            ? RolloutFreshnessAt(threadId, selected)
            : selected.ObservedAt;
        var freshUntil = source == "rollout" && selected.IsRunning == true
            ? RolloutFreshUntil(threadId, selected)
            : (DateTimeOffset?)null;
        if (stale)
            return new ThreadRuntimeSnapshot(
                threadId, "unknown", null, null, Array.Empty<string>(), selected.LastOutcome,
                source, true, snapshotObservedAt, selected.Generation, true)
            { FreshUntil = freshUntil };
        return new ThreadRuntimeSnapshot(
            threadId, selected.Phase, selected.IsRunning,
            selected.IsRunning == true ? selected.ActiveTurnId : null,
            selected.ActiveFlags, selected.LastOutcome, source, canControl,
            snapshotObservedAt, selected.Generation, false)
        { FreshUntil = freshUntil };
    }

    public bool IsExternallyOwnedActive(string threadId) =>
        Get(threadId) is { Source: "rollout", IsRunning: true, CanControl: false, Stale: false };

    public bool IsCurrentBridgeOwnedTurn(string threadId, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId) ||
            !_appServer.TryGetValue(threadId, out var evidence) ||
            evidence.Generation != Volatile.Read(ref _generation) ||
            evidence.IsRunning != true)
            return false;
        return string.Equals(evidence.ActiveTurnId, turnId, StringComparison.Ordinal);
    }

    public IReadOnlyDictionary<string, ThreadRuntimeSnapshot> Snapshot()
    {
        var ids = _appServer.Keys.Concat(_rollout.Keys).Concat(_history.Keys).Distinct(StringComparer.Ordinal);
        return ids.Select(Get)
            .Where(value => value is not null)
            .ToDictionary(value => value!.ThreadId, value => value!, StringComparer.Ordinal);
    }

    private bool IsCurrentGeneration(long generation) => generation == Volatile.Read(ref _generation);

    private void EndMatchingRollout(
        string threadId,
        string turnId,
        string outcome,
        DateTimeOffset? completedAt)
    {
        while (_rollout.TryGetValue(threadId, out var current))
        {
            if (!string.Equals(current.ActiveTurnId, turnId, StringComparison.Ordinal)) return;
            var terminal = new RuntimeEvidence(
                outcome == "failed" ? "error" : "idle",
                false,
                turnId,
                Array.Empty<string>(),
                outcome,
                completedAt ?? current.ObservedAt,
                0);
            if (_rollout.TryUpdate(threadId, terminal, current)) return;
        }
    }

    private DateTimeOffset RolloutFreshnessAt(string threadId, RuntimeEvidence evidence)
    {
        var freshnessAt = evidence.ObservedAt;
        if (_rolloutActivity.TryGetValue(threadId, out var activity) && activity > freshnessAt)
            freshnessAt = activity;
        return freshnessAt;
    }

    private DateTimeOffset RolloutFreshUntil(string threadId, RuntimeEvidence evidence)
    {
        var lifetime = evidence.InferredFromActivity
            ? InferredExternalRunningLifetime
            : evidence.Phase.StartsWith("waiting", StringComparison.Ordinal)
            ? ExternalWaitingLifetime
            : ExternalRunningLifetime;
        return RolloutFreshnessAt(threadId, evidence) + lifetime;
    }

    private bool IsFreshRollout(string threadId, RuntimeEvidence evidence) =>
        DateTimeOffset.UtcNow <= RolloutFreshUntil(threadId, evidence);

    private bool HasFreshMatchingRollout(string threadId, string turnId) =>
        _rollout.TryGetValue(threadId, out var rollout) &&
        rollout.IsRunning == true &&
        string.Equals(rollout.ActiveTurnId, turnId, StringComparison.Ordinal) &&
        IsFreshRollout(threadId, rollout);

    private void UpdateAppServer(
        string threadId,
        long generation,
        Func<RuntimeEvidence?, RuntimeEvidence?> update)
    {
        if (!IsCurrentGeneration(generation) || string.IsNullOrWhiteSpace(threadId)) return;
        while (true)
        {
            if (!IsCurrentGeneration(generation)) return;
            _appServer.TryGetValue(threadId, out var current);
            var next = update(current);
            if (next is null) return;
            if (current is null)
            {
                if (_appServer.TryAdd(threadId, next)) return;
            }
            else if (_appServer.TryUpdate(threadId, next, current)) return;
        }
    }

    private static RuntimeEvidence RolloutEvidence(
        string eventType,
        string? turnId,
        RuntimeEvidence? current,
        DateTimeOffset observedAt) => eventType switch
    {
        "task_started" => new RuntimeEvidence(
            "running", true, turnId, Array.Empty<string>(), current?.LastOutcome, observedAt, 0),
        "task_complete" => new RuntimeEvidence(
            "idle", false, null, Array.Empty<string>(), "completed", observedAt, 0),
        "turn_aborted" => new RuntimeEvidence(
            "idle", false, null, Array.Empty<string>(), "interrupted", observedAt, 0),
        _ => current ?? new RuntimeEvidence(
            "unknown", null, null, Array.Empty<string>(), null, observedAt, 0)
    };

    private static string PhaseFor(IReadOnlyCollection<string> flags)
    {
        var input = flags.Contains("waitingOnUserInput", StringComparer.Ordinal);
        var approval = flags.Contains("waitingOnApproval", StringComparer.Ordinal);
        if (input && approval) return "waitingAction";
        if (input) return "waitingInput";
        if (approval) return "waitingApproval";
        return "running";
    }

    private static string[] ReadFlags(JsonElement status)
    {
        if (!status.TryGetProperty("activeFlags", out var flags) || flags.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return flags.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => value is "waitingOnApproval" or "waitingOnUserInput")
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryText(JsonElement element, string property, out string value)
    {
        value = "";
        if (!element.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString() ?? "";
        return value.Length > 0;
    }

    private static DateTimeOffset? UnixTimestamp(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.Number ||
            !timestamp.TryGetInt64(out var seconds) ||
            seconds <= 0)
            return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private sealed record RuntimeEvidence(
        string Phase,
        bool? IsRunning,
        string? ActiveTurnId,
        string[] ActiveFlags,
        string? LastOutcome,
        DateTimeOffset ObservedAt,
        long Generation,
        bool InferredFromActivity = false);
}
