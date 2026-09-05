using System.Collections.Concurrent;

namespace CodexLanBridge;

public sealed record ThreadAccessLeaseSnapshot(
    string ThreadId,
    bool Loaded,
    bool TurnActive,
    bool ReleaseInProgress,
    long Revision,
    DateTimeOffset LastTouchedAt,
    DateTimeOffset IdleReleaseAt);

/// <summary>
/// Tracks only the app-server subscription owned by this Bridge process. Reading
/// persisted task data does not create an entry; an entry is created when an
/// interactive operation resumes or starts a thread.
/// </summary>
internal sealed class ThreadAccessLeaseTracker
{
    private sealed class Lease
    {
        public object Gate { get; } = new();
        public bool Loaded { get; set; }
        public bool TurnActive { get; set; }
        public bool AwaitingFirstTurn { get; set; }
        public DateTimeOffset? FirstTurnStartedAt { get; set; }
        public bool ReleaseInProgress { get; set; }
        public long Revision { get; set; }
        public DateTimeOffset LastTouchedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;

    public ThreadAccessLeaseTracker(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool IsLoaded(string threadId) =>
        _leases.TryGetValue(threadId, out var lease) && ReadLoaded(lease);

    public bool IsAwaitingFirstTurn(string threadId)
    {
        if (!_leases.TryGetValue(threadId, out var lease)) return false;
        lock (lease.Gate) return lease.Loaded && lease.AwaitingFirstTurn;
    }

    public bool IsStartingFirstTurn(string threadId)
    {
        if (!_leases.TryGetValue(threadId, out var lease)) return false;
        lock (lease.Gate) return lease.Loaded && (lease.AwaitingFirstTurn ||
            lease.FirstTurnStartedAt is { } started && _utcNow() - started < TimeSpan.FromSeconds(5));
    }

    public long MarkLoaded(string threadId)
    {
        var lease = Get(threadId);
        lock (lease.Gate)
        {
            lease.Loaded = true;
            lease.LastTouchedAt = _utcNow();
            return ++lease.Revision;
        }
    }

    public long Touch(string threadId)
    {
        var lease = Get(threadId);
        lock (lease.Gate)
        {
            lease.LastTouchedAt = _utcNow();
            return ++lease.Revision;
        }
    }

    public void MarkAwaitingFirstTurn(string threadId)
    {
        var lease = Get(threadId);
        lock (lease.Gate)
        {
            // Before turn/start there is no rollout to resume. Unsubscribing
            // this inexpensive empty shell would make the returned id unusable.
            lease.AwaitingFirstTurn = true;
            lease.LastTouchedAt = _utcNow();
            ++lease.Revision;
        }
    }

    public long MarkTurnStarted(string threadId)
    {
        var lease = Get(threadId);
        lock (lease.Gate)
        {
            lease.Loaded = true;
            lease.TurnActive = true;
            if (lease.AwaitingFirstTurn) lease.FirstTurnStartedAt = _utcNow();
            lease.AwaitingFirstTurn = false;
            lease.LastTouchedAt = _utcNow();
            return ++lease.Revision;
        }
    }

    public long MarkTurnCompleted(string threadId)
    {
        var lease = Get(threadId);
        lock (lease.Gate)
        {
            lease.TurnActive = false;
            lease.AwaitingFirstTurn = false;
            lease.LastTouchedAt = _utcNow();
            return ++lease.Revision;
        }
    }

    public bool TryBeginRelease(
        string threadId,
        long? expectedRevision,
        TimeSpan minimumIdle,
        out long releaseRevision)
    {
        releaseRevision = 0;
        if (!_leases.TryGetValue(threadId, out var lease)) return false;
        lock (lease.Gate)
        {
            if (!lease.Loaded || lease.TurnActive || lease.AwaitingFirstTurn || lease.ReleaseInProgress) return false;
            if (expectedRevision.HasValue && lease.Revision != expectedRevision.Value) return false;
            if (_utcNow() - lease.LastTouchedAt < minimumIdle) return false;
            lease.ReleaseInProgress = true;
            releaseRevision = lease.Revision;
            return true;
        }
    }

    public void FinishRelease(string threadId, long releaseRevision, bool released)
    {
        if (!_leases.TryGetValue(threadId, out var lease)) return;
        lock (lease.Gate)
        {
            lease.ReleaseInProgress = false;
            if (!released) return;

            // thread/unsubscribe is authoritative. Even if an observer touched
            // the lease while the RPC was in flight, the next writer must resume
            // it instead of assuming that access is still held.
            lease.Loaded = false;
            lease.TurnActive = false;
            lease.LastTouchedAt = _utcNow();
            lease.Revision = Math.Max(lease.Revision, releaseRevision) + 1;
        }
    }

    public void Forget(string threadId) => _leases.TryRemove(threadId, out _);

    public void Clear() => _leases.Clear();

    public IReadOnlyList<ThreadAccessLeaseSnapshot> Snapshot(TimeSpan idleReleaseAfter)
    {
        var result = new List<ThreadAccessLeaseSnapshot>(_leases.Count);
        foreach (var item in _leases)
        {
            lock (item.Value.Gate)
            {
                if (!item.Value.Loaded) continue;
                result.Add(new ThreadAccessLeaseSnapshot(
                    item.Key,
                    item.Value.Loaded,
                    item.Value.TurnActive,
                    item.Value.ReleaseInProgress,
                    item.Value.Revision,
                    item.Value.LastTouchedAt,
                    item.Value.LastTouchedAt + idleReleaseAfter));
            }
        }
        return result.OrderBy(snapshot => snapshot.LastTouchedAt).ToArray();
    }

    private Lease Get(string threadId) => _leases.GetOrAdd(
        threadId,
        _ => new Lease { LastTouchedAt = _utcNow() });

    private static bool ReadLoaded(Lease lease)
    {
        lock (lease.Gate) return lease.Loaded;
    }
}
