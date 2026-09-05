using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace CodexLanBridge;

public sealed record PendingRequest(string Key, string Method, JsonElement Params, DateTimeOffset CreatedAt);
public sealed record ApprovalBatchResult(
    int Pending,
    int Supported,
    int Approved,
    int AlreadyResolved,
    int Unsupported,
    int Failed)
{
    public static ApprovalBatchResult Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

internal sealed record PendingAppServerCall(
    TaskCompletionSource<JsonElement> Completion,
    string Method,
    long Generation,
    string? ThreadId,
    string? TurnId);

public sealed class CodexAppServer : BackgroundService
{
    // App-server messages are newline-delimited JSON. A malformed or legacy request can
    // otherwise materialize an entire multi-gigabyte rollout in this bridge process.
    // Mobile responses are deliberately compact, so 32 MiB is a generous safety ceiling.
    public const long MaximumAppServerMessageBytes = 32L * 1024 * 1024;

    private readonly NotificationStore _notifications;
    private readonly ThreadRuntimeStateStore _runtimeStates;
    private readonly ApprovalSettingsStore _approvalSettings;
    private readonly BridgeTurnRecoveryStore _turnRecovery;
    private readonly ThreadLiveEventStore _liveEvents;
    private readonly AppServerDiagnosticLog _transportDiagnostics;
    private readonly ConcurrentDictionary<long, PendingAppServerCall> _calls = new();
    private readonly ThreadAccessLeaseTracker _threadAccess = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _threadLoadLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _threadTurnLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExecutionPermissions> _threadExecutionPermissions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _threadWorkingDirectories = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, PendingRequest> Pending { get; } = new();
    private Process? _process;
    private StreamWriter? _input;
    private long _nextId;
    private long _generation;
    private volatile bool _isReady;
    private int _threadUnsubscribeUnsupported;
    private CancellationToken _stoppingToken;
    private readonly TimeSpan _completedTurnReleaseDelay;
    private readonly TimeSpan _idleThreadAccessTimeout;
    private readonly TimeSpan _threadAccessSweepInterval;
    public bool IsReady => _isReady;
    public event Action<string, JsonElement>? AppServerNotification;
    // These acknowledgement boundaries are deliberately payload-free on the
    // persistent side. Consumers may retain only their own correlation id.
    public event Action<string, JsonElement>? AppServerRequestWritten;
    public event Action? AppServerDisconnected;

    public CodexAppServer(
        NotificationStore notifications,
        ThreadRuntimeStateStore runtimeStates,
        ApprovalSettingsStore approvalSettings,
        BridgeTurnRecoveryStore turnRecovery,
        ThreadLiveEventStore liveEvents,
        AppServerDiagnosticLog transportDiagnostics,
        IConfiguration configuration)
    {
        _notifications = notifications;
        _runtimeStates = runtimeStates;
        _approvalSettings = approvalSettings;
        _turnRecovery = turnRecovery;
        _liveEvents = liveEvents;
        _transportDiagnostics = transportDiagnostics;
        _completedTurnReleaseDelay = ConfiguredDuration(
            configuration,
            "ThreadAccess:CompletedTurnReleaseSeconds",
            fallbackSeconds: 5,
            minimumSeconds: 1,
            maximumSeconds: 60);
        _idleThreadAccessTimeout = ConfiguredDuration(
            configuration,
            "ThreadAccess:IdleReleaseSeconds",
            fallbackSeconds: 120,
            minimumSeconds: 30,
            maximumSeconds: 3600);
        _threadAccessSweepInterval = ConfiguredDuration(
            configuration,
            "ThreadAccess:SweepSeconds",
            fallbackSeconds: 15,
            minimumSeconds: 5,
            maximumSeconds: 300);
    }

    private static TimeSpan ConfiguredDuration(
        IConfiguration configuration,
        string key,
        int fallbackSeconds,
        int minimumSeconds,
        int maximumSeconds)
    {
        var seconds = configuration.GetValue<int?>(key) ?? fallbackSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, minimumSeconds, maximumSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var accessCleanup = ReleaseIdleThreadAccessLoopAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnce(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex) { Console.Error.WriteLine($"Codex app-server stopped: {ex.Message}"); }
                _isReady = false;
                if (!stoppingToken.IsCancellationRequested) await Task.Delay(2500, stoppingToken);
            }
        }
        finally
        {
            try { await accessCleanup; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        var generation = _runtimeStates.BeginGeneration();
        Volatile.Write(ref _generation, generation);
        _liveEvents.BeginGeneration(generation);
        var exe = FindCodex();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo(exe, "app-server")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            CreateNoWindow = true
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start codex app-server.");
        var input = process.StandardInput;
        var stderrTail = new AppServerStderrTail();
        _process = process;
        _input = input;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && !process.HasExited)
                {
                    var error = await process.StandardError.ReadLineAsync(ct);
                    if (error is null) break;
                    stderrTail.Observe(error);
                    Console.Error.WriteLine($"[codex] {error}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
        }, ct);

        try
        {
            var reader = Task.Run(() => ReadLoop(process, generation, stderrTail, ct), ct);
            await CallAsync("initialize", new
            {
                clientInfo = new
                {
                    name = "codex-lan-console",
                    title = "Codex LAN Console",
                    version = typeof(CodexAppServer).Assembly.GetName().Version?.ToString(3) ?? "1.7.3"
                },
                capabilities = new
                {
                    experimentalApi = true,
                    // The mobile client renders standard MCP forms and has a JSON
                    // fallback for OpenAI extended forms, so these requests can stay
                    // on the same bridge-owned app-server connection.
                    mcpServerOpenaiFormElicitation = true
                }
            }, ct);
            await SendAsync(new { method = "initialized" }, ct);
            _isReady = true;
            Volatile.Write(ref _threadUnsubscribeUnsupported, 0);
            Console.WriteLine($"Connected to Codex app-server: {exe}");
            _ = RecoverPersistedTurnsAsync(ct);
            await reader;
        }
        finally
        {
            _isReady = false;
            _runtimeStates.InvalidateGeneration(generation);
            await _writeLock.WaitAsync(CancellationToken.None);
            try
            {
                if (ReferenceEquals(_input, input)) _input = null;
            }
            finally { _writeLock.Release(); }

            var (processExited, exitCode) = ReadProcessExit(process);
            var disconnectedCallCount = 0;
            foreach (var call in _calls)
            {
                if (!_calls.TryRemove(call.Key, out var pendingCall)) continue;
                disconnectedCallCount++;
                pendingCall.Completion.TrySetException(new AppServerDisconnectedException(
                    call.Key,
                    pendingCall.Method,
                    pendingCall.ThreadId,
                    pendingCall.TurnId,
                    pendingCall.Generation,
                    process.Id,
                    processExited,
                    exitCode));
                if (!ct.IsCancellationRequested)
                {
                    _transportDiagnostics.Write(new AppServerDiagnosticEntry(
                        "requestDisconnected",
                        pendingCall.Generation,
                        process.Id,
                        processExited,
                        exitCode,
                        stderrTail.Snapshot(),
                        null,
                        pendingCall.Method,
                        call.Key,
                        pendingCall.ThreadId,
                        pendingCall.TurnId));
                }
            }
            if (!ct.IsCancellationRequested && disconnectedCallCount == 0)
            {
                _transportDiagnostics.Write(new AppServerDiagnosticEntry(
                    "transportDisconnected",
                    generation,
                    process.Id,
                    processExited,
                    exitCode,
                    stderrTail.Snapshot(),
                    null,
                    null,
                    null,
                    null,
                    null));
            }
            PublishDisconnected();
            Pending.Clear();
            _requestIds.Clear();
            _threadAccess.Clear();
            _threadExecutionPermissions.Clear();
            _threadWorkingDirectories.Clear();
            try { if (!process.HasExited) process.Kill(true); } catch { }
            process.Dispose();
            if (ReferenceEquals(_process, process)) _process = null;
        }
    }

    private async Task ReadLoop(
        Process process,
        long generation,
        AppServerStderrTail stderrTail,
        CancellationToken ct)
    {
        await AppServerNdjsonReader.ReadAsync(
            process.StandardOutput.BaseStream,
            MaximumAppServerMessageBytes,
            ProcessMessageAsync,
            async oversized =>
            {
                await HandleOversizedMessageAsync(
                    process,
                    generation,
                    stderrTail,
                    oversized,
                    ct);
            },
            ct);
    }

    private async Task ProcessMessageAsync(ReadOnlySequence<byte> line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var id) && root.TryGetProperty("result", out var result) && id.ValueKind == JsonValueKind.Number)
            {
                if (_calls.TryRemove(id.GetInt64(), out var call)) call.Completion.TrySetResult(result.Clone());
            }
            else if (root.TryGetProperty("id", out id) && root.TryGetProperty("error", out var error) && id.ValueKind == JsonValueKind.Number)
            {
                if (_calls.TryRemove(id.GetInt64(), out var call))
                {
                    var rpcError = new CodexRpcException(error.Clone(), call.Method, id.GetInt64());
                    if (!rpcError.IsUnmaterializedThread &&
                        !(rpcError.IsHistoryInitializing && IsThreadStarting(call.ThreadId)))
                        RecordCommandError("rpcRejected", null, call.ThreadId, rpcError);
                    call.Completion.TrySetException(rpcError);
                }
            }
            else if (root.TryGetProperty("id", out id) && root.TryGetProperty("method", out var method))
            {
                var key = Guid.NewGuid().ToString("N");
                var p = root.TryGetProperty("params", out var param) ? param.Clone() : JsonSerializer.SerializeToElement(new { });
                var pending = new PendingRequest(key, method.GetString() ?? "request", p, DateTimeOffset.UtcNow);
                if (pending.Method == "item/tool/call")
                {
                    // A resumed Desktop thread can retain client-owned tools.
                    // Their executors do not live in this app-server process.
                    // Return a valid tool failure, not a JSON-RPC method error
                    // or an approval that falsely claims the tool was executed.
                    await SendRawResponse(id.Clone(), DynamicToolProtocol.Unavailable(p));
                    RecordCommandError("dynamicToolExecutorUnavailable", null,
                        TryGetString(p, "threadId", out var toolThreadId) ? toolThreadId : null,
                        new CodexRpcException(JsonSerializer.SerializeToElement(new
                        {
                            code = -32601,
                            message = "A client-owned dynamic tool has no executor in this Bridge. No action was performed."
                        }), pending.Method));
                    return;
                }
                if (!IsSupportedServerRequest(pending))
                {
                    await SendRawError(
                        id.Clone(),
                        -32601,
                        $"Codex LAN Console does not implement server request '{pending.Method}'.");
                    Console.Error.WriteLine($"Rejected unsupported app-server request: {pending.Method}");
                    return;
                }
                Pending[key] = pending;
                _requestIds[key] = id.Clone();
                ObservePendingState(pending);
                var autoApproval = await TryAutoResolveAsync(pending);
                if (ApprovalProtocol.ShouldPublishPendingNotification(
                        autoApproval,
                        Pending.ContainsKey(pending.Key)))
                {
                    _notifications.PublishPending(pending);
                }
            }
            else if (root.TryGetProperty("method", out method) && method.ValueKind == JsonValueKind.String)
            {
                var p = root.TryGetProperty("params", out var param) ? param : default;
                HandleNotification(method.GetString() ?? "", p);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex) { Console.Error.WriteLine($"Invalid app-server message: {ex.Message}"); }
        catch (Exception ex)
        {
            // Protocol observers render and persist auxiliary state. A bug in one
            // observer must never tear down the stdio reader and thereby abort the
            // actual Codex turn that is already running in the child process.
            Console.Error.WriteLine($"App-server message observer failed safely: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleOversizedMessageAsync(
        Process process,
        long generation,
        AppServerStderrTail stderrTail,
        AppServerOversizedMessage oversized,
        CancellationToken cancellationToken)
    {
        PendingAppServerCall? call = null;
        if (oversized.ServerMethod is null && oversized.NumericId is { } responseId)
            _calls.TryRemove(responseId, out call);

        var (processExited, exitCode) = ReadProcessExit(process);
        var method = call?.Method ?? oversized.ServerMethod;
        var threadId = call?.ThreadId;
        var turnId = call?.TurnId;
        _transportDiagnostics.Write(new AppServerDiagnosticEntry(
            "oversizedMessageDiscarded",
            generation,
            process.Id,
            processExited,
            exitCode,
            stderrTail.Snapshot(),
            oversized.ActualBytes,
            method,
            oversized.NumericId,
            threadId,
            turnId,
            oversized.EndedWithNewline));

        if (call is not null)
        {
            call.Completion.TrySetException(new AppServerMessageTooLargeException(
                oversized.ActualBytes,
                MaximumAppServerMessageBytes,
                oversized.NumericId,
                call.Method,
                call.ThreadId,
                call.TurnId,
                call.Generation,
                process.Id));
        }
        else if (oversized.ServerMethod is not null && oversized.NumericId is { } serverRequestId)
        {
            // Do not leave Codex waiting for a server-request response merely
            // because its parameters exceeded the bridge's bounded input. This
            // error itself is tiny and keeps the protocol synchronized.
            try
            {
                await SendAsync(new
                {
                    id = serverRequestId,
                    error = new
                    {
                        code = -32001,
                        message = "The app-server request exceeded the bridge safety limit."
                    }
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Console.Error.WriteLine($"Could not reject oversized app-server request: {ex.Message}");
            }
        }

        Console.Error.WriteLine(
            $"Discarded oversized Codex app-server message without restarting the command channel: " +
            $"bytes={oversized.ActualBytes}, method={method ?? "unknown"}, request={oversized.NumericId?.ToString() ?? "unknown"}.");
    }

    private static (bool Exited, int? ExitCode) ReadProcessExit(Process process)
    {
        try
        {
            if (!process.HasExited) return (false, null);
            return (true, process.ExitCode);
        }
        catch (InvalidOperationException) { return (false, null); }
    }

    private void PublishDisconnected()
    {
        try { AppServerDisconnected?.Invoke(); }
        catch (Exception ex)
        {
            // A persistence observer must not prevent app-server cleanup/restart.
            Console.Error.WriteLine($"App-server disconnect observer failed: {ex.Message}");
        }
    }

    public static void ThrowIfMessageTooLarge(long length)
    {
        if (length > MaximumAppServerMessageBytes)
            throw new AppServerMessageTooLargeException(length, MaximumAppServerMessageBytes);
    }

    private readonly ConcurrentDictionary<string, JsonElement> _requestIds = new();

    private void HandleNotification(string method, JsonElement parameters)
    {
        try { _liveEvents.Observe(method, parameters, Volatile.Read(ref _generation)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Live event observer failed safely for {method}: {ex.GetType().Name}: {ex.Message}");
        }
        var publishedParameters = parameters.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new { })
            : parameters.Clone();
        var notification = AppServerNotification;
        if (notification is not null)
        {
            foreach (var subscriber in notification.GetInvocationList())
            {
                try { ((Action<string, JsonElement>)subscriber)(method, publishedParameters); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"App-server notification subscriber failed safely for {method}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        var generation = Volatile.Read(ref _generation);
        if (method.Equals("thread/status/changed", StringComparison.Ordinal) &&
            TryGetString(parameters, "threadId", out var statusThreadId) &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("status", out var threadStatus))
        {
            _runtimeStates.ObserveAppServerStatus(statusThreadId, threadStatus, generation);
            return;
        }
        if (method.Equals("turn/started", StringComparison.Ordinal) &&
            TryGetString(parameters, "threadId", out var startedThreadId) &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("turn", out var startedTurn) &&
            TryGetString(startedTurn, "id", out var startedTurnId))
        {
            _threadAccess.MarkTurnStarted(startedThreadId);
            _runtimeStates.ObserveTurnStarted(
                startedThreadId,
                startedTurnId,
                generation,
                UnixTimestamp(startedTurn, "startedAt"));
            return;
        }
        if (method.Equals("turn/completed", StringComparison.Ordinal) &&
            TryGetString(parameters, "threadId", out var completedThreadId) &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("turn", out var completedTurn) &&
            TryGetString(completedTurn, "id", out var completedTurnId) &&
            TryGetString(completedTurn, "status", out var completedStatus))
        {
            var completedAt = UnixTimestamp(completedTurn, "completedAt");
            _runtimeStates.ObserveTurnCompleted(
                completedThreadId,
                completedTurnId,
                completedStatus,
                generation,
                completedAt);
            var retry = _turnRecovery.ObserveCompleted(completedThreadId, completedTurn, completedAt);
            var completedAccessRevision = _threadAccess.MarkTurnCompleted(completedThreadId);
            if (retry is not null)
            {
                _notifications.PublishTurnRecovering(
                    completedThreadId,
                    completedTurnId,
                    retry.Attempt,
                    BridgeTurnRecoveryStore.MaximumAutomaticAttempts,
                    completedAt);
                _ = ContinueAfterDisconnectAsync(retry, _stoppingToken);
            }
            else
            {
                _notifications.PublishTurnOutcome(completedThreadId, completedTurnId, completedStatus, completedAt);
                ScheduleThreadAccessRelease(completedThreadId, completedAccessRevision);
            }
            return;
        }
        if (method.Equals("thread/started", StringComparison.Ordinal) &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("thread", out var thread))
        {
            MarkThreadLoaded(thread);
            return;
        }
        if ((method.Equals("thread/closed", StringComparison.Ordinal) || method.Equals("thread/deleted", StringComparison.Ordinal)) &&
            TryGetString(parameters, "threadId", out var closedThreadId))
        {
            _threadAccess.Forget(closedThreadId);
            _threadExecutionPermissions.TryRemove(closedThreadId, out _);
            _threadWorkingDirectories.TryRemove(closedThreadId, out _);
            _turnRecovery.CancelByUser(closedThreadId);
            _runtimeStates.ForgetAppServerThread(closedThreadId, generation);
            return;
        }
        if (method.Equals("serverRequest/resolved", StringComparison.Ordinal) &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("requestId", out var requestId))
        {
            foreach (var request in _requestIds)
            {
                if (!JsonIdsEqual(request.Value, requestId) || !_requestIds.TryRemove(request.Key, out _)) continue;
                if (Pending.TryRemove(request.Key, out var resolved)) ObserveResolvedPendingState(resolved);
                break;
            }
        }
    }

    private void ObservePendingState(PendingRequest request)
    {
        if (!TryGetString(request.Params, "threadId", out var threadId)) return;
        TryGetString(request.Params, "turnId", out var turnId);
        _runtimeStates.ObservePending(
            threadId,
            turnId,
            IsUserInputLikeRequest(request),
            Volatile.Read(ref _generation),
            request.CreatedAt);
    }

    private void ObserveResolvedPendingState(PendingRequest request)
    {
        if (!TryGetString(request.Params, "threadId", out var threadId)) return;
        _runtimeStates.ResolvePending(
            threadId,
            IsUserInputLikeRequest(request),
            Volatile.Read(ref _generation));
    }

    private static DateTimeOffset? UnixTimestamp(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.Number ||
            !timestamp.TryGetInt64(out var seconds) ||
            seconds <= 0)
            return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? "";
        return value.Length > 0;
    }

    private static bool JsonIdsEqual(JsonElement left, JsonElement right) =>
        left.ValueKind == right.ValueKind && left.GetRawText().Equals(right.GetRawText(), StringComparison.Ordinal);

    public void MarkThreadLoaded(JsonElement result)
    {
        var thread = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("thread", out var nested) ? nested : result;
        if (!TryGetString(thread, "id", out var threadId)) return;
        _threadAccess.MarkLoaded(threadId);
        if (TryGetString(thread, "cwd", out var cwd))
            _threadWorkingDirectories[threadId] = cwd;
        if (thread.TryGetProperty("status", out var status))
            _runtimeStates.ObserveAppServerStatus(threadId, status, Volatile.Read(ref _generation));
    }

    public void ObserveThreadList(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var threads) || threads.ValueKind != JsonValueKind.Array) return;
        foreach (var thread in threads.EnumerateArray())
        {
            if (!TryGetString(thread, "id", out var threadId) || !thread.TryGetProperty("status", out var status)) continue;
            if (TryGetString(thread, "cwd", out var cwd))
                _threadWorkingDirectories[threadId] = cwd;
            // thread/list is backed by persisted state. Its active bit can be
            // orphaned after another client exits, so it must not establish
            // bridge ownership or refresh a live-state lease.
            _runtimeStates.ObserveHistoricalStatus(threadId, status, UnixTimestamp(thread, "updatedAt"));
        }
    }

    public async Task EnsureThreadLoadedAsync(string threadId, CancellationToken cancellationToken, bool force = false)
    {
        if (!force && _threadAccess.IsLoaded(threadId))
        {
            _threadAccess.Touch(threadId);
            return;
        }
        var gate = _threadLoadLocks.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && _threadAccess.IsLoaded(threadId))
            {
                _threadAccess.Touch(threadId);
                return;
            }
            var result = await CallAsync("thread/resume", new { threadId, excludeTurns = true }, cancellationToken);
            MarkThreadLoaded(result);
        }
        finally { gate.Release(); }
    }

    public bool HasThreadAccess(string threadId) => _threadAccess.IsLoaded(threadId);

    public bool IsThreadStarting(string? threadId) => !string.IsNullOrWhiteSpace(threadId) &&
        (_threadAccess.IsStartingFirstTurn(threadId) || _calls.Values.Any(call =>
            call.Method == "turn/start" && call.ThreadId == threadId));

    public IReadOnlyList<ThreadAccessLeaseSnapshot> AccessSnapshot() =>
        _threadAccess.Snapshot(_idleThreadAccessTimeout);

    /// <summary>
    /// Marks a short non-turn interaction as complete. A newer interaction or a
    /// newly started turn changes the revision and cancels this scheduled release.
    /// </summary>
    public void ScheduleThreadAccessRelease(string threadId)
    {
        if (!_threadAccess.IsLoaded(threadId)) return;
        ScheduleThreadAccessRelease(threadId, _threadAccess.Touch(threadId));
    }

    private void ScheduleThreadAccessRelease(string threadId, long expectedRevision) =>
        _ = ReleaseThreadAccessAfterDelayAsync(
            threadId,
            expectedRevision,
            Volatile.Read(ref _generation),
            _stoppingToken);

    private async Task ReleaseThreadAccessAfterDelayAsync(
        string threadId,
        long expectedRevision,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_completedTurnReleaseDelay, cancellationToken);
            if (expectedGeneration != Volatile.Read(ref _generation)) return;
            await TryReleaseThreadAccessAsync(
                threadId,
                expectedRevision,
                _completedTurnReleaseDelay,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Scheduled task-access release for {threadId} failed safely: {ex.Message}");
        }
    }

    private async Task ReleaseIdleThreadAccessLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_threadAccessSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!IsReady || Volatile.Read(ref _threadUnsubscribeUnsupported) != 0) continue;
                foreach (var lease in _threadAccess.Snapshot(_idleThreadAccessTimeout))
                {
                    await TryReleaseThreadAccessAsync(
                        lease.ThreadId,
                        lease.Revision,
                        _idleThreadAccessTimeout,
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task<bool> TryReleaseThreadAccessAsync(
        string threadId,
        long? expectedRevision,
        TimeSpan minimumIdle,
        CancellationToken cancellationToken)
    {
        if (!IsReady || Volatile.Read(ref _threadUnsubscribeUnsupported) != 0) return false;
        var loadGate = _threadLoadLocks.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
        await loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_threadTurnLocks.TryGetValue(threadId, out var turnGate) && turnGate.CurrentCount == 0)
                return false;
            if (Pending.Values.Any(request =>
                    TryGetString(request.Params, "threadId", out var pendingThreadId) &&
                    pendingThreadId.Equals(threadId, StringComparison.Ordinal)))
                return false;
            if (_calls.Values.Any(call => threadId.Equals(call.ThreadId, StringComparison.Ordinal)))
                return false;
            if (!_threadAccess.TryBeginRelease(
                    threadId,
                    expectedRevision,
                    minimumIdle,
                    out var releaseRevision))
                return false;

            var released = false;
            try
            {
                var result = await CallAsync("thread/unsubscribe", new { threadId }, cancellationToken);
                released = TryGetString(result, "status", out var status) &&
                           status is "notLoaded" or "notSubscribed" or "unsubscribed";
                if (!released)
                    Console.Error.WriteLine(
                        $"Codex returned an unknown thread/unsubscribe result for {threadId}: {result.GetRawText()}");
            }
            catch (CodexRpcException ex) when (ex.Code == -32601)
            {
                if (Interlocked.Exchange(ref _threadUnsubscribeUnsupported, 1) == 0)
                    Console.Error.WriteLine(
                        "The installed Codex app-server does not support thread/unsubscribe; automatic access release is disabled until Codex is updated.");
            }
            finally
            {
                _threadAccess.FinishRelease(threadId, releaseRevision, released);
                if (released) _threadExecutionPermissions.TryRemove(threadId, out _);
            }
            return released;
        }
        finally { loadGate.Release(); }
    }

    public async Task<JsonElement> SendUserInputAsync(
        string threadId,
        IReadOnlyCollection<object> input,
        string? clientUserMessageId,
        string? expectedTurnId,
        ExecutionPermissions executionPermissions,
        CancellationToken cancellationToken,
        CodexCommandOptions? turnOptions = null)
    {
        if (input.Count == 0) throw new ArgumentException("At least one message or attachment is required.");
        var gate = _threadTurnLocks.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await _turnRecovery.ReplacePendingRetryAfterAcknowledgedDispatchAsync(threadId, async () =>
            {
                await EnsureThreadLoadedAsync(threadId, cancellationToken);
                var clientId = string.IsNullOrWhiteSpace(clientUserMessageId) ? Guid.NewGuid().ToString() : clientUserMessageId;

                if (turnOptions?.HasOverrides == true)
                {
                    // The official turn/steer request has no model or effort
                    // fields. Never pretend that a selector changed an already
                    // running turn: wait and create a fresh turn/start instead.
                    var activeTurnId = await GetActiveTurnIdAsync(threadId, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(activeTurnId))
                        throw new CodexTurnBusyException(
                            "The selected model will be applied when the active turn finishes.");
                    return await StartTurnAsync(
                        threadId,
                        input,
                        clientId,
                        executionPermissions,
                        CancellationToken.None,
                        turnOptions: turnOptions);
                }

                if (!string.IsNullOrWhiteSpace(expectedTurnId))
                {
                    try { return await SteerAsync(threadId, expectedTurnId, input, clientId, executionPermissions, CancellationToken.None); }
                    catch (CodexRpcException ex) when (ex.IsNoActiveTurn || ex.IsExpectedTurnMismatch)
                    {
                        var activeTurnId = await GetActiveTurnIdAsync(threadId, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(activeTurnId))
                            return await SteerAsync(threadId, activeTurnId, input, clientId, executionPermissions, CancellationToken.None);
                        return await StartTurnAsync(
                            threadId, input, clientId, executionPermissions, CancellationToken.None);
                    }
                    catch (CodexRpcException ex) when (ex.IsThreadNotFound)
                    {
                        await EnsureThreadLoadedAsync(threadId, cancellationToken, force: true);
                        var resumedTurnId = await GetActiveTurnIdAsync(threadId, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(resumedTurnId))
                            return await SteerAsync(
                                threadId, resumedTurnId, input, clientId, executionPermissions, CancellationToken.None);
                        return await StartTurnAsync(
                            threadId, input, clientId, executionPermissions, CancellationToken.None);
                    }
                }

                // Serialize per thread and prefer steering a proven active turn.
                // Some app-server versions accept a second turn/start instead of
                // returning an active-turn conflict, so exception-only fallback is
                // not sufficient protection against parallel duplicate work.
                var currentTurnId = await GetActiveTurnIdAsync(threadId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(currentTurnId))
                    return await SteerAsync(threadId, currentTurnId, input, clientId, executionPermissions, CancellationToken.None);

                try
                {
                    return await StartTurnAsync(
                        threadId, input, clientId, executionPermissions, CancellationToken.None);
                }
                catch (CodexRpcException ex) when (ex.IsThreadNotFound)
                {
                    await EnsureThreadLoadedAsync(threadId, cancellationToken, force: true);
                    var resumedTurnId = await GetActiveTurnIdAsync(threadId, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(resumedTurnId))
                        return await SteerAsync(
                            threadId, resumedTurnId, input, clientId, executionPermissions, CancellationToken.None);
                    return await StartTurnAsync(
                        threadId, input, clientId, executionPermissions, CancellationToken.None);
                }
                catch (CodexRpcException ex) when (ex.IsActiveTurnConflict)
                {
                    var activeTurnId = await GetActiveTurnIdAsync(threadId, cancellationToken);
                    if (string.IsNullOrWhiteSpace(activeTurnId)) throw;
                    return await SteerAsync(threadId, activeTurnId, input, clientId, executionPermissions, CancellationToken.None);
                }
            });
        }
        catch
        {
            // A rejected interaction must not leave a successfully resumed but
            // otherwise idle thread subscribed forever.
            ScheduleThreadAccessRelease(threadId);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<JsonElement> InterruptCurrentTurnAsync(string threadId, CancellationToken cancellationToken)
    {
        // A user stop is authoritative even if the failed turn is currently in
        // its backoff window and no active app-server turn exists yet.
        _turnRecovery.CancelByUser(threadId);
        await EnsureThreadLoadedAsync(threadId, cancellationToken);
        var activeTurnId = await GetActiveTurnIdAsync(threadId, cancellationToken);
        if (string.IsNullOrWhiteSpace(activeTurnId))
        {
            ScheduleThreadAccessRelease(threadId);
            return JsonSerializer.SerializeToElement(new { interrupted = false, reason = "noActiveTurn" });
        }
        try
        {
            return await CallAsync("turn/interrupt", new { threadId, turnId = activeTurnId }, cancellationToken);
        }
        catch (CodexRpcException ex) when (ex.IsNoActiveTurn)
        {
            ScheduleThreadAccessRelease(threadId);
            return JsonSerializer.SerializeToElement(new { interrupted = false, reason = "alreadyFinished" });
        }
    }

    public async Task<string?> GetActiveTurnIdAsync(string threadId, CancellationToken cancellationToken)
    {
        var live = _runtimeStates.Get(threadId);
        if (live is { CanControl: true, IsRunning: true, ActiveTurnId.Length: > 0 })
        {
            _threadAccess.MarkTurnStarted(threadId);
            return live.ActiveTurnId;
        }
        JsonElement result;
        try
        {
            result = await CallAsync("thread/turns/list", new
            {
                threadId,
                limit = 1,
                sortDirection = "desc",
                itemsView = "notLoaded"
            }, cancellationToken);
        }
        catch (CodexRpcException ex) when (ex.IsUnmaterializedThread)
        {
            // Current app-server persists a new thread only after its first
            // user message. An absent history here means no active turn, not
            // that turn/start was rejected. Never swallow a missing old thread.
            return null;
        }
        _runtimeStates.ObserveLatestPersistedTurn(threadId, result);
        if (!result.TryGetProperty("data", out var turns) || turns.ValueKind != JsonValueKind.Array) return null;
        foreach (var turn in turns.EnumerateArray())
            if (TryGetString(turn, "id", out var turnId) && TryGetString(turn, "status", out var status) &&
                status.Equals("inProgress", StringComparison.Ordinal))
            {
                _threadAccess.MarkTurnStarted(threadId);
                return turnId;
            }
        return null;
    }

    private async Task<JsonElement> StartTurnAsync(
        string threadId,
        IReadOnlyCollection<object> input,
        string clientUserMessageId,
        ExecutionPermissions executionPermissions,
        CancellationToken cancellationToken,
        BridgeTurnRecoveryRetry? recovery = null,
        CodexCommandOptions? turnOptions = null)
    {
        executionPermissions = EffectiveExecutionPermissions(executionPermissions);
        var hadPreviousPermissions = _threadExecutionPermissions.TryGetValue(threadId, out var previousPermissions);
        // Install the profile before turn/start so an app-server implementation
        // that streams an immediate host request cannot race the acknowledgement.
        // If turn/start is rejected (for example because an older turn is active),
        // the previous profile is restored and turn/steer keeps the old semantics.
        _threadExecutionPermissions[threadId] = executionPermissions;
        JsonElement result;
        try
        {
            try
            {
                result = await CallAsync(
                    "turn/start",
                    BuildTurnStartParameters(
                        threadId,
                        input,
                        clientUserMessageId,
                        executionPermissions,
                        turnOptions),
                    cancellationToken);
            }
            catch (CodexRpcException ex) when (IsPermissionsFieldUnsupported(ex))
            {
                var cwd = await GetThreadCwdAsync(threadId, cancellationToken);
                result = await CallAsync(
                    "turn/start",
                    BuildTurnStartParameters(
                        threadId,
                        input,
                        clientUserMessageId,
                        executionPermissions,
                        turnOptions,
                        legacyCwd: cwd),
                    cancellationToken);
            }
        }
        catch
        {
            if (hadPreviousPermissions && previousPermissions is not null)
                _threadExecutionPermissions[threadId] = previousPermissions;
            else
                _threadExecutionPermissions.TryRemove(threadId, out _);
            throw;
        }
        var turn = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("turn", out var nestedTurn)
            ? nestedTurn
            : result;
        if (TryGetString(turn, "id", out var turnId))
        {
            _threadAccess.MarkTurnStarted(threadId);
            _runtimeStates.ObserveTurnStarted(
                threadId,
                turnId,
                Volatile.Read(ref _generation),
                UnixTimestamp(turn, "startedAt"));
            if (recovery is null)
                _turnRecovery.TrackStarted(threadId, turnId, executionPermissions);
            else
                _turnRecovery.MarkAttemptStarted(recovery, turnId);

            // A very short failed turn can complete before the turn/start RPC
            // acknowledgement reaches this process. Reconcile the returned
            // terminal state so recovery is not lost to notification ordering.
            if (BridgeTurnRecoveryStore.TryReadTurn(
                    turn,
                    out _,
                    out var acknowledgedStatus,
                    out _,
                    out _) &&
                acknowledgedStatus is "completed" or "failed" or "interrupted")
            {
                var next = _turnRecovery.ObserveCompleted(threadId, turn, UnixTimestamp(turn, "completedAt"));
                var completedAccessRevision = _threadAccess.MarkTurnCompleted(threadId);
                if (next is not null)
                    _ = ContinueAfterDisconnectAsync(next, _stoppingToken);
                else
                    ScheduleThreadAccessRelease(threadId, completedAccessRevision);
            }
        }
        return result;
    }

    internal static JsonElement BuildTurnStartParameters(
        string threadId,
        IReadOnlyCollection<object> input,
        string clientUserMessageId,
        ExecutionPermissions executionPermissions,
        CodexCommandOptions? turnOptions,
        string? legacyCwd = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["input"] = input,
            ["clientUserMessageId"] = clientUserMessageId,
            ["approvalPolicy"] = executionPermissions.ApprovalPolicy,
            ["approvalsReviewer"] = executionPermissions.ApprovalsReviewer
        };
        if (legacyCwd is null)
            parameters["permissions"] = executionPermissions.Permissions;
        else
            parameters["sandboxPolicy"] = executionPermissions.LegacyTurnSandboxPolicy(legacyCwd);
        if (!string.IsNullOrWhiteSpace(turnOptions?.Model))
            parameters["model"] = turnOptions.Model;
        if (!string.IsNullOrWhiteSpace(turnOptions?.ReasoningEffort))
            parameters["effort"] = turnOptions.ReasoningEffort;
        return JsonSerializer.SerializeToElement(parameters);
    }

    public IReadOnlyList<BridgeTurnRecoverySnapshot> RecoverySnapshot() => _turnRecovery.Snapshot();

    public BridgeTurnRecoverySnapshot? RecoverySnapshotFor(string threadId) =>
        _turnRecovery.SnapshotFor(threadId);

    public BridgeTurnRecoverySnapshot? ReconcileRecoveryWithLatestPersistedTurn(
        string threadId,
        JsonElement newestFirstTurnPage)
    {
        if (newestFirstTurnPage.ValueKind == JsonValueKind.Object &&
            newestFirstTurnPage.TryGetProperty("data", out var turns) &&
            turns.ValueKind == JsonValueKind.Array &&
            turns.GetArrayLength() > 0 &&
            BridgeTurnRecoveryStore.TryReadTurn(turns[0], out var latestTurnId, out _, out _, out _))
        {
            _turnRecovery.DiscardIfSupersededByTurn(threadId, latestTurnId);
        }
        return _turnRecovery.SnapshotFor(threadId);
    }

    private async Task ContinueAfterDisconnectAsync(
        BridgeTurnRecoveryRetry retry,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = retry.NotBefore - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);

            var gate = _threadTurnLocks.GetOrAdd(retry.ThreadId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                await EnsureThreadLoadedAsync(retry.ThreadId, cancellationToken);
                var activeTurnId = await GetActiveTurnIdAsync(retry.ThreadId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(activeTurnId))
                {
                    _threadAccess.MarkTurnStarted(retry.ThreadId);
                    _turnRecovery.MarkOwnershipUncertain(
                        retry.ThreadId,
                        retry.FailedTurnId,
                        "Another turn became active before automatic continuation.");
                    return;
                }
                if (!_turnRecovery.TryBeginAttempt(retry)) return;

                var input = new object[]
                {
                    new
                    {
                        type = "text",
                        text = AutomaticContinuationInstruction,
                        text_elements = Array.Empty<object>()
                    }
                };
                try
                {
                    // Once turn/start is dispatched, a phone request no longer
                    // owns its lifetime. Wait for the acknowledgement even if
                    // the mobile browser disconnects; blind resubmission is not
                    // safe because clientUserMessageId is only correlation data.
                    await StartTurnAsync(
                        retry.ThreadId,
                        input,
                        retry.ClientUserMessageId,
                        retry.Permissions,
                        CancellationToken.None,
                        retry);
                }
                catch (Exception ex)
                {
                    _turnRecovery.MarkDispatchUncertain(retry, ex.Message);
                    Console.Error.WriteLine(
                        $"Automatic turn continuation for {retry.ThreadId} was not acknowledged: {ex.Message}");
                }
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _turnRecovery.MarkOwnershipUncertain(retry.ThreadId, retry.FailedTurnId, ex.Message);
            Console.Error.WriteLine($"Automatic turn continuation failed safely: {ex.Message}");
        }
    }

    private async Task RecoverPersistedTurnsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let initialize/initialized settle before resuming threads. This
            // path is intentionally conservative: any mismatched latest turn
            // loses automatic ownership instead of being retried.
            await Task.Delay(250, cancellationToken);
            foreach (var candidate in _turnRecovery.StartupCandidates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gate = _threadTurnLocks.GetOrAdd(candidate.ThreadId, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken);
                try
                {
                    await EnsureThreadLoadedAsync(candidate.ThreadId, cancellationToken);
                    var page = await CallAsync("thread/turns/list", new
                    {
                        threadId = candidate.ThreadId,
                        limit = 1,
                        sortDirection = "desc",
                        itemsView = "summary"
                    }, cancellationToken);
                    if (!page.TryGetProperty("data", out var turns) ||
                        turns.ValueKind != JsonValueKind.Array ||
                        turns.GetArrayLength() == 0 ||
                        !BridgeTurnRecoveryStore.TryReadTurn(
                            turns[0],
                            out var latestTurnId,
                            out var latestStatus,
                            out _,
                            out _))
                    {
                        _turnRecovery.MarkOwnershipUncertain(
                            candidate.ThreadId,
                            candidate.CurrentTurnId,
                            "The latest turn could not be reconciled after bridge restart.");
                        continue;
                    }
                    if (!latestTurnId.Equals(candidate.CurrentTurnId, StringComparison.Ordinal))
                    {
                        _turnRecovery.DiscardIfSupersededByTurn(
                            candidate.ThreadId,
                            latestTurnId,
                            candidate.CurrentTurnId,
                            includeActive: true);
                        continue;
                    }

                    if (latestStatus.Equals("inProgress", StringComparison.Ordinal))
                    {
                        _threadAccess.MarkTurnStarted(candidate.ThreadId);
                        _turnRecovery.MarkRunningAfterRestart(candidate.ThreadId, latestTurnId);
                        _runtimeStates.ObserveTurnStarted(
                            candidate.ThreadId,
                            latestTurnId,
                            Volatile.Read(ref _generation));
                        continue;
                    }

                    var retry = _turnRecovery.ObserveCompleted(candidate.ThreadId, turns[0]);
                    var completedAccessRevision = _threadAccess.MarkTurnCompleted(candidate.ThreadId);
                    if (retry is not null)
                        _ = ContinueAfterDisconnectAsync(retry, cancellationToken);
                    else
                        ScheduleThreadAccessRelease(candidate.ThreadId, completedAccessRevision);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _turnRecovery.MarkOwnershipUncertain(
                        candidate.ThreadId,
                        candidate.CurrentTurnId,
                        ex.Message);
                    Console.Error.WriteLine(
                        $"Persisted turn recovery for {candidate.ThreadId} was skipped safely: {ex.Message}");
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Persisted turn recovery stopped safely: {ex.Message}");
        }
    }

    private const string AutomaticContinuationInstruction =
        "The previous turn ended because its response stream disconnected before completion. " +
        "Continue the existing user task from the current filesystem and external state. " +
        "First inspect what has already completed. Do not repeat submissions, messages, payments, uploads, " +
        "deletions, or other irreversible side effects that may already have succeeded. " +
        "Finish the remaining work and report the final result normally.";

    public async Task<JsonElement> StartThreadAsync(
        string cwd,
        ExecutionPermissions executionPermissions,
        CancellationToken cancellationToken)
    {
        executionPermissions = EffectiveExecutionPermissions(executionPermissions);
        JsonElement result;
        try
        {
            result = await CallAsync("thread/start", new
            {
                cwd,
                permissions = executionPermissions.Permissions,
                approvalPolicy = executionPermissions.ApprovalPolicy,
                approvalsReviewer = executionPermissions.ApprovalsReviewer
            }, cancellationToken);
        }
        catch (CodexRpcException ex) when (IsPermissionsFieldUnsupported(ex))
        {
            result = await CallAsync("thread/start", new
            {
                cwd,
                sandbox = executionPermissions.LegacySandbox,
                approvalPolicy = executionPermissions.ApprovalPolicy,
                approvalsReviewer = executionPermissions.ApprovalsReviewer
            }, cancellationToken);
        }
        MarkThreadLoaded(result);
        var thread = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("thread", out var nested)
            ? nested
            : result;
        if (TryGetString(thread, "id", out var threadId))
        {
            _threadAccess.MarkAwaitingFirstTurn(threadId);
            _threadExecutionPermissions[threadId] = executionPermissions;
            _threadWorkingDirectories[threadId] = cwd;
        }
        return result;
    }

    public bool TryGetKnownThreadCwd(string threadId, out string cwd) =>
        _threadWorkingDirectories.TryGetValue(threadId, out cwd!);

    private ExecutionPermissions EffectiveExecutionPermissions(ExecutionPermissions requested)
        => requested.RouteApprovalsToBridge(_approvalSettings.Get().AutoApproveAll);

    public async Task<JsonElement> ListPermissionProfilesAsync(string cwd, CancellationToken cancellationToken) =>
        await CallAsync("permissionProfile/list", new { cwd, limit = 100 }, cancellationToken);

    private async Task<string> GetThreadCwdAsync(string threadId, CancellationToken cancellationToken)
    {
        if (TryGetKnownThreadCwd(threadId, out var knownCwd)) return knownCwd;
        var result = await CallAsync("thread/read", new { threadId, includeTurns = false }, cancellationToken);
        var thread = result.TryGetProperty("thread", out var nested) ? nested : result;
        if (TryGetString(thread, "cwd", out var cwd))
        {
            _threadWorkingDirectories[threadId] = cwd;
            return cwd;
        }
        return Environment.CurrentDirectory;
    }

    private static bool IsPermissionsFieldUnsupported(CodexRpcException exception)
    {
        if (exception.Code != -32602 || exception.IsPolicyRestricted ||
            !exception.Message.Contains("permissions", StringComparison.OrdinalIgnoreCase)) return false;

        // Only retry when an older app-server explicitly rejects the shape of the
        // request. A disallowed permission profile is a policy decision and must
        // never be converted into the less expressive legacy sandbox request.
        return exception.Message.Contains("unknown field", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("unrecognized field", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("unexpected field", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("unsupported field", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("unknown parameter", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("unrecognized parameter", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("unsupported parameter", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonElement> SteerAsync(
        string threadId,
        string expectedTurnId,
        IReadOnlyCollection<object> input,
        string clientUserMessageId,
        ExecutionPermissions executionPermissions,
        CancellationToken cancellationToken)
    {
        var result = await CallAsync(
            "turn/steer",
            new { threadId, expectedTurnId, input, clientUserMessageId },
            cancellationToken);
        var acceptedTurnId = TryGetString(result, "turnId", out var returnedTurnId)
            ? returnedTurnId
            : expectedTurnId;
        _threadAccess.MarkTurnStarted(threadId);
        _runtimeStates.ObserveTurnStarted(
            threadId,
            acceptedTurnId,
            Volatile.Read(ref _generation));
        if (!_turnRecovery.IsTrackingTurn(threadId, acceptedTurnId))
            _turnRecovery.TrackStarted(
                threadId,
                acceptedTurnId,
                EffectiveExecutionPermissions(executionPermissions));
        return result;
    }

    public async Task<bool> ResolvePendingAsync(string key, string decision)
    {
        if (!Pending.TryGetValue(key, out var request) || !IsApprovalRequest(request)) return false;
        var result = ApprovalProtocol.BuildResult(request, decision);
        if (!Pending.TryRemove(key, out var removed)) return false;
        if (!_requestIds.TryRemove(key, out var id))
        {
            Pending.TryAdd(key, removed);
            return false;
        }
        try
        {
            await SendRawResponse(id, result);
            ObserveResolvedPendingState(removed);
        }
        catch
        {
            if (IsReady)
            {
                _requestIds.TryAdd(key, id);
                Pending.TryAdd(key, removed);
            }
            throw;
        }
        return true;
    }

    public async Task<ApprovalBatchResult> ResolveAllApprovalsAsync(
        string decision,
        bool recordAsAutomatic = false)
    {
        if (decision is not ("accept" or "acceptForSession"))
            throw new ArgumentException("Bulk approval only supports accept or acceptForSession.");

        var allPending = Pending.Values.ToArray();
        var pending = allPending
            .Where(IsUserApprovalRequest)
            .OrderBy(request => request.CreatedAt)
            .ToArray();
        var approved = 0;
        var alreadyResolved = 0;
        var failed = 0;
        foreach (var request in pending)
        {
            try
            {
                var resolved = IsApprovalRequest(request)
                    ? await ResolvePendingAsync(request.Key, decision)
                    : await ResolveSystemRequestAsync(
                        request.Key,
                        ElicitationProtocol.BuildToolApproval(
                            request,
                            decision == "acceptForSession" &&
                            ElicitationProtocol.AdvertisedPersistence(request).Contains("session", StringComparer.Ordinal)
                                ? "session"
                                : null));
                if (resolved) approved++;
                else alreadyResolved++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"Could not approve pending request {request.Key}: {ex.Message}");
            }
        }
        if (recordAsAutomatic && approved > 0)
        {
            try { _approvalSettings.RecordAutoApprovals(approved); }
            catch (Exception ex) { Console.Error.WriteLine($"Could not persist automatic approval statistics: {ex.Message}"); }
        }
        return new ApprovalBatchResult(
            allPending.Length,
            pending.Length,
            approved,
            alreadyResolved,
            allPending.Length - pending.Length,
            failed);
    }

    private async Task<AutoApprovalDisposition> TryAutoResolveAsync(PendingRequest request)
    {
        if (request.Method.Equals("currentTime/read", StringComparison.Ordinal))
        {
            try
            {
                return await ResolveSystemRequestAsync(
                    request.Key,
                    JsonSerializer.SerializeToElement(new
                    {
                        currentTimeAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }))
                    ? AutoApprovalDisposition.Approved
                    : AutoApprovalDisposition.NoLongerPending;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Automatic current-time response failed: {ex.Message}");
                return Pending.ContainsKey(request.Key)
                    ? AutoApprovalDisposition.Failed
                    : AutoApprovalDisposition.NoLongerPending;
            }
        }

        if (!IsUserApprovalRequest(request) || !ShouldAutoApprove(request))
            return AutoApprovalDisposition.NotAttempted;
        try
        {
            var resolved = IsApprovalRequest(request)
                ? await ResolvePendingAsync(request.Key, "accept")
                : await ResolveSystemRequestAsync(request.Key, ElicitationProtocol.BuildAutomaticApproval(request));
            if (!resolved)
                return AutoApprovalDisposition.NoLongerPending;
            try { _approvalSettings.RecordAutoApprovals(1); }
            catch (Exception ex) { Console.Error.WriteLine($"Could not persist automatic approval statistics: {ex.Message}"); }
            return AutoApprovalDisposition.Approved;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Automatic approval failed for {request.Method}: {ex.Message}");
            return Pending.ContainsKey(request.Key)
                ? AutoApprovalDisposition.Failed
                : AutoApprovalDisposition.NoLongerPending;
        }
    }

    private bool ShouldAutoApprove(PendingRequest request)
    {
        if (_approvalSettings.Get().AutoApproveAll) return true;
        return TryGetString(request.Params, "threadId", out var threadId) &&
               _threadExecutionPermissions.TryGetValue(threadId, out var permissions) &&
               permissions.IsUnrestrictedAutonomy;
    }

    public async Task<bool> ResolveUserInputAsync(string key, IReadOnlyDictionary<string, UserInputAnswer> answers)
    {
        if (!Pending.TryGetValue(key, out var request) || !IsUserInputRequest(request)) return false;
        if (!TryBuildAnswers(request.Params, answers, out var payload)) return false;
        if (!Pending.TryRemove(key, out var removed)) return false;
        if (!_requestIds.TryRemove(key, out var id))
        {
            Pending.TryAdd(key, removed);
            return false;
        }
        try
        {
            await SendRawResponse(id, new { answers = payload });
            ObserveResolvedPendingState(removed);
        }
        catch
        {
            if (IsReady)
            {
                _requestIds.TryAdd(key, id);
                Pending.TryAdd(key, removed);
            }
            throw;
        }
        return true;
    }

    public async Task<bool> ResolveMcpElicitationAsync(
        string key,
        string action,
        JsonElement? content,
        string? persistence)
    {
        if (!Pending.TryGetValue(key, out var request) || !ElicitationProtocol.IsElicitationRequest(request))
            return false;
        var result = ElicitationProtocol.BuildResult(request, action, content, persistence);
        return await ResolveSystemRequestAsync(key, result);
    }

    private async Task<bool> ResolveSystemRequestAsync(string key, JsonElement result)
    {
        if (!Pending.TryRemove(key, out var removed)) return false;
        if (!_requestIds.TryRemove(key, out var id))
        {
            Pending.TryAdd(key, removed);
            return false;
        }
        try
        {
            await SendRawResponse(id, result);
            ObserveResolvedPendingState(removed);
        }
        catch
        {
            if (IsReady)
            {
                _requestIds.TryAdd(key, id);
                Pending.TryAdd(key, removed);
            }
            throw;
        }
        return true;
    }

    public static bool IsUserInputRequest(PendingRequest request) =>
        request.Method.Contains("requestUserInput", StringComparison.OrdinalIgnoreCase);

    public static bool IsUserInputLikeRequest(PendingRequest request) =>
        IsUserInputRequest(request) ||
        ElicitationProtocol.IsElicitationRequest(request);

    public static bool IsApprovalRequest(PendingRequest request) => ApprovalProtocol.IsApprovalRequest(request);

    public static bool IsUserApprovalRequest(PendingRequest request) =>
        IsApprovalRequest(request) || ElicitationProtocol.IsToolApproval(request);

    public static bool IsSupportedServerRequest(PendingRequest request) =>
        IsApprovalRequest(request) ||
        IsUserInputRequest(request) ||
        ElicitationProtocol.IsElicitationRequest(request) ||
        request.Method.Equals("currentTime/read", StringComparison.Ordinal) ||
        request.Method.Equals("item/tool/call", StringComparison.Ordinal);

    internal void RecordCommandError(string reason, string? commandId, string? threadId, Exception exception)
    {
        try
        {
            var rpc = exception as CodexRpcException;
            _transportDiagnostics.Write(new AppServerDiagnosticEntry(
                reason, Volatile.Read(ref _generation), _process?.Id ?? 0,
                false, null, "", null, rpc?.Method, rpc?.RequestId, threadId, null,
                CommandId: commandId, RpcCode: rpc?.Code, ErrorMessage: exception.Message));
        }
        catch { /* A diagnostic failure must not prevent recording the receipt. */ }
    }

    private static bool TryBuildAnswers(
        JsonElement parameters,
        IReadOnlyDictionary<string, UserInputAnswer> answers,
        out Dictionary<string, object> payload)
    {
        payload = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!parameters.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array) return false;
        foreach (var question in questions.EnumerateArray())
        {
            if (!question.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String) return false;
            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id) || !answers.TryGetValue(id, out var answer) ||
                answer.Answers is null || answer.Answers.All(string.IsNullOrWhiteSpace)) return false;
            payload[id] = new { answers = answer.Answers.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() };
        }
        return true;
    }

    private async Task SendRawResponse(JsonElement id, object result) => await SendAsync(new { id, result }, CancellationToken.None);

    private async Task SendRawError(JsonElement id, int code, string message) =>
        await SendAsync(new { id, error = new { code, message } }, CancellationToken.None);

    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct = default)
    {
        for (var i = 0; i < 40 && (_input is null || (!method.Equals("initialize", StringComparison.Ordinal) && !IsReady)); i++)
            await Task.Delay(100, ct);
        if (_input is null || (!method.Equals("initialize", StringComparison.Ordinal) && !IsReady))
            throw new InvalidOperationException("Codex app-server is unavailable.");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (threadId, turnId) = ReadCallIdentity(parameters);
        _calls[id] = new PendingAppServerCall(
            tcs,
            LimitProtocolIdentifier(method),
            Volatile.Read(ref _generation),
            threadId,
            turnId);
        try
        {
            var requestParameters = parameters ?? new { };
            // Mark the durable outbox at the actual pipe-write boundary, after
            // the write lock and live input have been verified. From this point
            // WriteLine/Flush can fail after sending some bytes, so replay must
            // be conservative. Disconnect publication waits for the same lock.
            await SendAsync(
                new { id, method, @params = requestParameters },
                ct,
                () => PublishRequestWritten(method, requestParameters));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var registration = timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token));
            return await tcs.Task;
        }
        finally
        {
            _calls.TryRemove(id, out _);
        }
    }

    private void PublishRequestWritten(string method, object requestParameters)
    {
        var observer = AppServerRequestWritten;
        if (observer is null) return;
        try
        {
            var element = requestParameters is JsonElement json
                ? json.Clone()
                : JsonSerializer.SerializeToElement(requestParameters);
            observer(method, element);
        }
        catch (Exception ex)
        {
            // Never convert an observer/storage problem into a false dispatch
            // failure; the actual JSON-RPC write still proceeds.
            Console.Error.WriteLine($"App-server write observer failed: {ex.Message}");
        }
    }

    private static (string? ThreadId, string? TurnId) ReadCallIdentity(object? parameters)
    {
        if (parameters is null) return (null, null);
        if (parameters is JsonElement json)
            return (
                ReadSafeJsonIdentifier(json, "threadId"),
                ReadSafeJsonIdentifier(json, "turnId") ?? ReadSafeJsonIdentifier(json, "expectedTurnId"));

        var type = parameters.GetType();
        return (
            ReadSafeObjectIdentifier(parameters, type, "threadId"),
            ReadSafeObjectIdentifier(parameters, type, "turnId") ??
            ReadSafeObjectIdentifier(parameters, type, "expectedTurnId"));
    }

    private static string? ReadSafeObjectIdentifier(object value, Type type, string propertyName)
    {
        try
        {
            var property = type.GetProperties()
                .FirstOrDefault(candidate => candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            return property?.GetValue(value) is string identifier
                ? LimitProtocolIdentifier(identifier)
                : null;
        }
        catch (Exception) { return null; }
    }

    private static string? ReadSafeJsonIdentifier(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? LimitProtocolIdentifier(property.GetString())
            : null;

    private static string LimitProtocolIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var safe = new string(value.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '/' or ':' or '.' or '_' or '-').ToArray());
        if (safe.Length == 0) return "unknown";
        return safe.Length <= 256 ? safe : safe[..256];
    }

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private async Task SendAsync(object message, CancellationToken ct, Action? beforeWriteAttempt = null)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_input is null) throw new InvalidOperationException("Codex app-server is unavailable.");
            beforeWriteAttempt?.Invoke();
            await _input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
            await _input.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    private static string FindCodex()
    {
        var env = Environment.GetEnvironmentVariable("CODEX_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".codex", "plugins", ".plugin-appserver", "codex.exe"),
            Path.Combine(home, ".codex", ".sandbox-bin", "codex.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? "codex";
    }

    public override void Dispose()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        _process?.Dispose();
        base.Dispose();
    }
}

public sealed record UserInputAnswer(string[] Answers);

public sealed class CodexRpcException : Exception
{
    public int Code { get; }
    public JsonElement Error { get; }
    public string? Method { get; }
    public long? RequestId { get; }

    public CodexRpcException(JsonElement error, string? method = null, long? requestId = null) : base(ReadMessage(error))
    {
        Error = error;
        Method = method;
        RequestId = requestId;
        Code = error.ValueKind == JsonValueKind.Object &&
               error.TryGetProperty("code", out var code) &&
               code.TryGetInt32(out var value)
            ? value
            : 0;
    }

    public bool IsThreadNotFound => Contains("thread not found") || Contains("unknown thread") || Contains("no rollout found");
    public bool IsUnmaterializedThread => Contains("not materialized") ||
        Contains("before first user message") || (Contains("rollout") && Contains("empty"));
    public bool IsHistoryInitializing => Code == -32601 && Contains("paginated_threads is not supported yet");
    public bool IsNoActiveTurn => Contains("no active turn") || Contains("turn is not active") || Contains("turn not found");
    public bool IsExpectedTurnMismatch => Contains("expected turn") || Contains("turn id mismatch") || Contains("does not match");
    public bool IsActiveTurnConflict =>
        Contains("turn already in progress") || Contains("active turn") || Contains("thread is busy") ||
        Contains("already running") || Contains("active writer") || Contains("already has a writer");
    public bool IsPolicyRestricted =>
        Contains("not allowed") || Contains("managed requirement") || Contains("policy restriction") ||
        Contains("permission profile") && Contains("allowed");

    public int SuggestedHttpStatus
    {
        get
        {
            if (IsThreadNotFound) return StatusCodes.Status404NotFound;
            if (IsPolicyRestricted) return StatusCodes.Status403Forbidden;
            if (IsNoActiveTurn || IsExpectedTurnMismatch || IsActiveTurnConflict) return StatusCodes.Status409Conflict;
            if (Code == -32601) return StatusCodes.Status501NotImplemented;
            if (Code is -32600 or -32602) return StatusCodes.Status400BadRequest;
            if (Contains("rate limit") || Contains("too many requests")) return StatusCodes.Status429TooManyRequests;
            return StatusCodes.Status502BadGateway;
        }
    }

    private bool Contains(string value) => Message.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string ReadMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(message.GetString())) return message.GetString()!;
        return "Codex app-server returned an error.";
    }
}
