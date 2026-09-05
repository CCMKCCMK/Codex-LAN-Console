using System.Text.Json;

namespace CodexLanBridge;

public sealed record BridgeTurnRecoverySnapshot(
    string ThreadId,
    string RootTurnId,
    string CurrentTurnId,
    string Status,
    int Attempt,
    int MaximumAttempts,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    string Message);

public sealed record BridgeTurnRecoveryRetry(
    string ThreadId,
    string FailedTurnId,
    string RootTurnId,
    int Attempt,
    DateTimeOffset NotBefore,
    string ClientUserMessageId,
    ExecutionPermissions Permissions,
    string ErrorMessage);

/// <summary>
/// Persists only the small amount of state needed to safely continue a turn
/// submitted through Codex LAN Console after an upstream response stream
/// disconnect. The original user input is deliberately not retained or
/// replayed: a recovery turn receives a fixed continuation instruction and
/// must reconcile the current filesystem/external state first.
/// </summary>
public sealed class BridgeTurnRecoveryStore
{
    public const int MaximumAutomaticAttempts = 2;
    public static readonly TimeSpan MaximumRecoveryAge = TimeSpan.FromHours(2);

    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<int, TimeSpan> _retryDelay;
    private readonly Dictionary<string, PersistedRecovery> _records = new(StringComparer.Ordinal);

    public BridgeTurnRecoveryStore(string? storageDirectory = null, Func<int, TimeSpan>? retryDelay = null)
    {
        storageDirectory ??= System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLanConsole");
        Directory.CreateDirectory(storageDirectory);
        _path = System.IO.Path.Combine(storageDirectory, "turn-recovery.json");
        _retryDelay = retryDelay ?? (attempt => attempt switch
        {
            1 => TimeSpan.FromSeconds(4),
            _ => TimeSpan.FromSeconds(15)
        });
        Load();
    }

    public string Path => _path;

    public void TrackStarted(
        string threadId,
        string turnId,
        ExecutionPermissions permissions,
        DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId)) return;
        var now = observedAt ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _records[threadId] = new PersistedRecovery(
                SchemaVersion,
                threadId,
                turnId,
                turnId,
                "running",
                0,
                MaximumAutomaticAttempts,
                permissions,
                now,
                null,
                null,
                null);
            SaveLocked();
        }
    }

    public BridgeTurnRecoveryRetry? ObserveCompleted(
        string threadId,
        JsonElement turn,
        DateTimeOffset? observedAt = null)
    {
        if (!TryReadTurn(turn, out var turnId, out var status, out var errorMessage, out var errorKind))
            return null;

        var now = observedAt ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_records.TryGetValue(threadId, out var record) ||
                !record.CurrentTurnId.Equals(turnId, StringComparison.Ordinal)) return null;

            if (status is "completed" or "interrupted")
            {
                _records.Remove(threadId);
                SaveLocked();
                return null;
            }

            if (!status.Equals("failed", StringComparison.OrdinalIgnoreCase)) return null;

            if (!IsRetryableDisconnect(errorKind, errorMessage))
            {
                _records[threadId] = record with
                {
                    Status = "notRetryable",
                    UpdatedAt = now,
                    NextAttemptAt = null,
                    LastError = TrimError(errorMessage)
                };
                SaveLocked();
                return null;
            }

            if (record.Attempt >= record.MaximumAttempts)
            {
                _records[threadId] = record with
                {
                    Status = "retryExhausted",
                    UpdatedAt = now,
                    NextAttemptAt = null,
                    LastError = TrimError(errorMessage)
                };
                SaveLocked();
                return null;
            }

            var attempt = record.Attempt + 1;
            var notBefore = now + _retryDelay(attempt);
            var clientId = Guid.NewGuid().ToString();
            _records[threadId] = record with
            {
                Status = "waitingToContinue",
                UpdatedAt = now,
                NextAttemptAt = notBefore,
                LastError = TrimError(errorMessage),
                PendingClientUserMessageId = clientId
            };
            SaveLocked();
            return new BridgeTurnRecoveryRetry(
                threadId,
                turnId,
                record.RootTurnId,
                attempt,
                notBefore,
                clientId,
                record.Permissions,
                TrimError(errorMessage) ?? "The response stream disconnected.");
        }
    }

    public bool TryBeginAttempt(BridgeTurnRecoveryRetry retry, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_records.TryGetValue(retry.ThreadId, out var record) ||
                !record.CurrentTurnId.Equals(retry.FailedTurnId, StringComparison.Ordinal) ||
                !record.Status.Equals("waitingToContinue", StringComparison.Ordinal) ||
                record.Attempt + 1 != retry.Attempt ||
                !string.Equals(record.PendingClientUserMessageId, retry.ClientUserMessageId, StringComparison.Ordinal) ||
                now < retry.NotBefore)
                return false;

            _records[retry.ThreadId] = record with
            {
                Status = "startingContinuation",
                Attempt = retry.Attempt,
                UpdatedAt = now,
                NextAttemptAt = null
            };
            SaveLocked();
            return true;
        }
    }

    public bool MarkAttemptStarted(
        BridgeTurnRecoveryRetry retry,
        string turnId,
        DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(turnId)) return false;
        var now = observedAt ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_records.TryGetValue(retry.ThreadId, out var record) ||
                !record.CurrentTurnId.Equals(retry.FailedTurnId, StringComparison.Ordinal) ||
                !record.Status.Equals("startingContinuation", StringComparison.Ordinal) ||
                record.Attempt != retry.Attempt ||
                !string.Equals(record.PendingClientUserMessageId, retry.ClientUserMessageId, StringComparison.Ordinal))
                return false;

            _records[retry.ThreadId] = record with
            {
                CurrentTurnId = turnId,
                Status = "continuationRunning",
                UpdatedAt = now,
                PendingClientUserMessageId = null,
                NextAttemptAt = null
            };
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// A turn/start request that was written but did not return an acknowledgement
    /// is intentionally not resent. clientUserMessageId is correlation metadata,
    /// not a protocol-level idempotency key.
    /// </summary>
    public void MarkDispatchUncertain(BridgeTurnRecoveryRetry retry, string? error, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_records.TryGetValue(retry.ThreadId, out var record) ||
                record.Attempt != retry.Attempt ||
                !record.Status.Equals("startingContinuation", StringComparison.Ordinal)) return;
            _records[retry.ThreadId] = record with
            {
                Status = "ownershipUncertain",
                UpdatedAt = now,
                NextAttemptAt = null,
                LastError = TrimError(error) ?? record.LastError
            };
            SaveLocked();
        }
    }

    public void MarkRunningAfterRestart(string threadId, string turnId, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_records.TryGetValue(threadId, out var record) ||
                !record.CurrentTurnId.Equals(turnId, StringComparison.Ordinal)) return;
            _records[threadId] = record with
            {
                Status = record.Attempt == 0 ? "running" : "continuationRunning",
                UpdatedAt = now,
                NextAttemptAt = null,
                PendingClientUserMessageId = null
            };
            SaveLocked();
        }
    }

    public void MarkOwnershipUncertain(
        string threadId,
        string expectedTurnId,
        string? detail = null,
        DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_records.TryGetValue(threadId, out var record) ||
                !record.CurrentTurnId.Equals(expectedTurnId, StringComparison.Ordinal)) return;
            _records[threadId] = record with
            {
                Status = "ownershipUncertain",
                UpdatedAt = now,
                NextAttemptAt = null,
                LastError = TrimError(detail) ?? record.LastError
            };
            SaveLocked();
        }
    }

    public void CancelByUser(string threadId)
    {
        lock (_gate)
        {
            if (!_records.Remove(threadId)) return;
            SaveLocked();
        }
    }

    public void CancelPendingRetryByUser(string threadId)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(threadId, out var record) ||
                record.Status is not ("waitingToContinue" or "retryExhausted" or "notRetryable" or "ownershipUncertain"))
                return;
            _records.Remove(threadId);
            SaveLocked();
        }
    }

    /// <summary>
    /// Removes a recovery record after the persisted conversation proves that a
    /// different turn has superseded it. Ordinary page reads only discard
    /// terminal or not-yet-dispatched records; startup reconciliation may also
    /// discard an active record when the expected id still matches the record
    /// captured before the app-server was queried.
    /// </summary>
    public bool DiscardIfSupersededByTurn(
        string threadId,
        string latestTurnId,
        string? expectedCurrentTurnId = null,
        bool includeActive = false)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(latestTurnId)) return false;
        lock (_gate)
        {
            if (!_records.TryGetValue(threadId, out var record) ||
                (!string.IsNullOrWhiteSpace(expectedCurrentTurnId) &&
                 !record.CurrentTurnId.Equals(expectedCurrentTurnId, StringComparison.Ordinal)) ||
                record.CurrentTurnId.Equals(latestTurnId, StringComparison.Ordinal) ||
                (!includeActive &&
                 record.Status is not ("waitingToContinue" or "retryExhausted" or "notRetryable" or "ownershipUncertain")))
                return false;

            _records.Remove(threadId);
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Keeps an already scheduled automatic continuation alive until a manual
    /// user dispatch is acknowledged. If preflight or dispatch fails, the
    /// original recovery remains eligible to run. The expected turn check also
    /// prevents a very short newly-started turn from having its own recovery
    /// removed after it completes before the acknowledgement is processed.
    /// </summary>
    public async Task<T> ReplacePendingRetryAfterAcknowledgedDispatchAsync<T>(
        string threadId,
        Func<Task<T>> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        string? expectedCurrentTurnId;
        lock (_gate)
        {
            expectedCurrentTurnId = _records.TryGetValue(threadId, out var record) &&
                                    record.Status is "waitingToContinue" or "retryExhausted" or "notRetryable" or "ownershipUncertain"
                ? record.CurrentTurnId
                : null;
        }

        var result = await dispatch().ConfigureAwait(false);
        if (expectedCurrentTurnId is not null)
            CancelPendingRetryByUser(threadId, expectedCurrentTurnId);
        return result;
    }

    private void CancelPendingRetryByUser(string threadId, string expectedCurrentTurnId)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(threadId, out var record) ||
                !record.CurrentTurnId.Equals(expectedCurrentTurnId, StringComparison.Ordinal) ||
                record.Status is not ("waitingToContinue" or "retryExhausted" or "notRetryable" or "ownershipUncertain"))
                return;
            _records.Remove(threadId);
            SaveLocked();
        }
    }

    public IReadOnlyList<BridgeTurnRecoverySnapshot> Snapshot(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            PruneLocked(at);
            return _records.Values
                .OrderByDescending(record => record.UpdatedAt)
                .Select(ToSnapshot)
                .ToArray();
        }
    }

    public BridgeTurnRecoverySnapshot? SnapshotFor(string threadId, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            PruneLocked(at);
            return _records.TryGetValue(threadId, out var record) ? ToSnapshot(record) : null;
        }
    }

    internal IReadOnlyList<BridgeTurnRecoverySnapshot> StartupCandidates(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            PruneLocked(at);
            return _records.Values
                .Where(record => record.Status is "running" or "continuationRunning" or "waitingToContinue")
                .Select(ToSnapshot)
                .ToArray();
        }
    }

    internal ExecutionPermissions? PermissionsFor(string threadId)
    {
        lock (_gate)
            return _records.TryGetValue(threadId, out var record) ? record.Permissions : null;
    }

    public bool IsTrackingTurn(string threadId, string turnId)
    {
        lock (_gate)
            return _records.TryGetValue(threadId, out var record) &&
                   record.CurrentTurnId.Equals(turnId, StringComparison.Ordinal) &&
                   record.Status is "running" or "continuationRunning";
    }

    public static bool IsRetryableDisconnect(string? errorKind, string? message)
    {
        if (errorKind is "responseStreamDisconnected" or "responseStreamConnectionFailed") return true;
        if (errorKind is not (null or "" or "other")) return false;
        return !string.IsNullOrWhiteSpace(message) &&
               (message.Contains("stream disconnected before completion", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("response stream disconnected", StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryReadTurn(
        JsonElement turn,
        out string turnId,
        out string status,
        out string? errorMessage,
        out string? errorKind)
    {
        turnId = "";
        status = "";
        errorMessage = null;
        errorKind = null;
        if (turn.ValueKind != JsonValueKind.Object ||
            !TryText(turn, "id", out turnId) ||
            !TryText(turn, "status", out status)) return false;
        status = status.Trim().Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "inprogress" or "running" => "inProgress",
            "cancelled" or "canceled" => "interrupted",
            var value => value
        };
        if (!turn.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return true;
        if (TryText(error, "message", out var message)) errorMessage = message;
        if (!error.TryGetProperty("codexErrorInfo", out var info)) return true;
        if (info.ValueKind == JsonValueKind.String) errorKind = info.GetString();
        else if (info.ValueKind == JsonValueKind.Object)
            errorKind = info.EnumerateObject().Select(property => property.Name).FirstOrDefault();
        return true;
    }

    private BridgeTurnRecoverySnapshot ToSnapshot(PersistedRecovery record) => new(
        record.ThreadId,
        record.RootTurnId,
        record.CurrentTurnId,
        record.Status,
        record.Attempt,
        record.MaximumAttempts,
        record.UpdatedAt,
        record.NextAttemptAt,
        record.LastError,
        record.Status switch
        {
            "running" => "任务正在电脑端运行",
            "waitingToContinue" => $"网络连接中断，正在准备自动续接（第 {record.Attempt + 1}/{record.MaximumAttempts} 次）",
            "startingContinuation" => $"正在自动续接任务（第 {record.Attempt}/{record.MaximumAttempts} 次）",
            "continuationRunning" => $"任务已自动续接，正在继续运行（第 {record.Attempt}/{record.MaximumAttempts} 次）",
            "retryExhausted" => "网络连续中断，自动续接次数已用完，请手动继续",
            "notRetryable" => "任务因非网络中断错误停止，未自动重试",
            "ownershipUncertain" => "电脑端未能确认续接请求是否已启动，为避免重复操作已停止自动重试",
            _ => "任务恢复状态未知"
        });

    private void PruneLocked(DateTimeOffset now)
    {
        var stale = _records
            .Where(pair => now - pair.Value.UpdatedAt > MaximumRecoveryAge)
            .Select(pair => pair.Key)
            .ToArray();
        if (stale.Length == 0) return;
        foreach (var threadId in stale) _records.Remove(threadId);
        SaveLocked();
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var document = JsonSerializer.Deserialize<PersistedDocument>(File.ReadAllText(_path));
                if (document is null || document.SchemaVersion != SchemaVersion) return;
                foreach (var record in document.Records ?? Array.Empty<PersistedRecovery>())
                {
                    if (record.SchemaVersion != SchemaVersion ||
                        string.IsNullOrWhiteSpace(record.ThreadId) ||
                        string.IsNullOrWhiteSpace(record.CurrentTurnId) ||
                        record.Attempt < 0 ||
                        record.Attempt > MaximumAutomaticAttempts) continue;
                    _records[record.ThreadId] = record;
                }
                PruneLocked(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not load turn recovery state: {ex.Message}");
                _records.Clear();
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new PersistedDocument(SchemaVersion, _records.Values.ToArray()),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex)
        {
            // Recovery is a best-effort safety feature. Failure to persist must
            // never crash the bridge or the active Codex turn.
            Console.Error.WriteLine($"Could not save turn recovery state: {ex.Message}");
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

    private static string? TrimError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var normalized = error.Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private sealed record PersistedDocument(int SchemaVersion, PersistedRecovery[]? Records);

    private sealed record PersistedRecovery(
        int SchemaVersion,
        string ThreadId,
        string RootTurnId,
        string CurrentTurnId,
        string Status,
        int Attempt,
        int MaximumAttempts,
        ExecutionPermissions Permissions,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? NextAttemptAt,
        string? LastError,
        string? PendingClientUserMessageId);
}
