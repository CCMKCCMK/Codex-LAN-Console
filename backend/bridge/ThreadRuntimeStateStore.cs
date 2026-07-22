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
    bool Stale);

/// <summary>
/// Keeps live task state separate from persisted thread history. The bridge
/// app-server is authoritative for tasks it owns; rollout observations are a
/// best-effort view of tasks currently owned by Codex Desktop.
/// </summary>
public sealed class ThreadRuntimeStateStore
{
    private static readonly TimeSpan ExternalActiveLifetime = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, RuntimeEvidence> _appServer = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RuntimeEvidence> _rollout = new(StringComparer.Ordinal);
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
        var outcome = normalized switch
        {
            "completed" => "completed",
            "interrupted" => "interrupted",
            "failed" => "failed",
            _ => normalized
        };
        UpdateAppServer(threadId, generation, _ => new RuntimeEvidence(
            failed ? "error" : "idle", false, null, Array.Empty<string>(), outcome,
            observedAt ?? DateTimeOffset.UtcNow, generation));
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

    public ThreadRuntimeSnapshot? Get(string threadId)
    {
        _appServer.TryGetValue(threadId, out var appServer);
        _rollout.TryGetValue(threadId, out var rollout);
        RuntimeEvidence? selected;
        var source = "appServer";
        var canControl = true;
        var rolloutIsFreshAndActive = rollout?.IsRunning == true &&
                                      DateTimeOffset.UtcNow - rollout.ObservedAt <= ExternalActiveLifetime;
        if (rolloutIsFreshAndActive || appServer is null || rollout is not null && rollout.ObservedAt > appServer.ObservedAt)
        {
            selected = rollout;
            source = "rollout";
            canControl = false;
        }
        else selected = appServer;
        if (selected is null) return null;

        var stale = source == "rollout" && selected.IsRunning == true &&
                    DateTimeOffset.UtcNow - selected.ObservedAt > ExternalActiveLifetime;
        if (stale)
            return new ThreadRuntimeSnapshot(
                threadId, "unknown", null, null, Array.Empty<string>(), selected.LastOutcome,
                source, false, selected.ObservedAt, selected.Generation, true);
        return new ThreadRuntimeSnapshot(
            threadId, selected.Phase, selected.IsRunning, selected.ActiveTurnId,
            selected.ActiveFlags, selected.LastOutcome, source, canControl,
            selected.ObservedAt, selected.Generation, false);
    }

    public bool IsExternallyOwnedActive(string threadId) =>
        Get(threadId) is { Source: "rollout", IsRunning: true, CanControl: false, Stale: false };

    public IReadOnlyDictionary<string, ThreadRuntimeSnapshot> Snapshot()
    {
        var ids = _appServer.Keys.Concat(_rollout.Keys).Distinct(StringComparer.Ordinal);
        return ids.Select(Get)
            .Where(value => value is not null)
            .ToDictionary(value => value!.ThreadId, value => value!, StringComparer.Ordinal);
    }

    private bool IsCurrentGeneration(long generation) => generation == Volatile.Read(ref _generation);

    private void UpdateAppServer(
        string threadId,
        long generation,
        Func<RuntimeEvidence?, RuntimeEvidence?> update)
    {
        if (!IsCurrentGeneration(generation) || string.IsNullOrWhiteSpace(threadId)) return;
        while (true)
        {
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

    private sealed record RuntimeEvidence(
        string Phase,
        bool? IsRunning,
        string? ActiveTurnId,
        string[] ActiveFlags,
        string? LastOutcome,
        DateTimeOffset ObservedAt,
        long Generation);
}
