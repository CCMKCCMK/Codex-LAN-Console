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

public sealed class CodexAppServer : BackgroundService
{
    // App-server messages are newline-delimited JSON. A malformed or legacy request can
    // otherwise materialize an entire multi-gigabyte rollout in this bridge process.
    // Mobile responses are deliberately compact, so 32 MiB is a generous safety ceiling.
    public const long MaximumAppServerMessageBytes = 32L * 1024 * 1024;

    private readonly NotificationStore _notifications;
    private readonly ThreadRuntimeStateStore _runtimeStates;
    private readonly ApprovalSettingsStore _approvalSettings;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _calls = new();
    private readonly ConcurrentDictionary<string, byte> _loadedThreads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _threadLoadLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExecutionPermissions> _threadExecutionPermissions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _threadWorkingDirectories = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, PendingRequest> Pending { get; } = new();
    private Process? _process;
    private StreamWriter? _input;
    private long _nextId;
    private long _generation;
    private volatile bool _isReady;
    public bool IsReady => _isReady;

    public CodexAppServer(
        NotificationStore notifications,
        ThreadRuntimeStateStore runtimeStates,
        ApprovalSettingsStore approvalSettings)
    {
        _notifications = notifications;
        _runtimeStates = runtimeStates;
        _approvalSettings = approvalSettings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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

    private async Task RunOnce(CancellationToken ct)
    {
        var generation = _runtimeStates.BeginGeneration();
        Volatile.Write(ref _generation, generation);
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
                    Console.Error.WriteLine($"[codex] {error}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
        }, ct);

        try
        {
            var reader = Task.Run(() => ReadLoop(process, ct), ct);
            await CallAsync("initialize", new
            {
                clientInfo = new { name = "codex-lan-console", title = "Codex LAN Console", version = "1.6.0" },
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
            Console.WriteLine($"Connected to Codex app-server: {exe}");
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

            var failure = new InvalidOperationException("Codex app-server disconnected.");
            foreach (var call in _calls)
                if (_calls.TryRemove(call.Key, out var completion)) completion.TrySetException(failure);
            Pending.Clear();
            _requestIds.Clear();
            _loadedThreads.Clear();
            _threadExecutionPermissions.Clear();
            _threadWorkingDirectories.Clear();
            try { if (!process.HasExited) process.Kill(true); } catch { }
            process.Dispose();
            if (ReferenceEquals(_process, process)) _process = null;
        }
    }

    private async Task ReadLoop(Process process, CancellationToken ct)
    {
        var reader = PipeReader.Create(
            process.StandardOutput.BaseStream,
            new StreamPipeReaderOptions(
                bufferSize: 64 * 1024,
                minimumReadSize: 4 * 1024,
                leaveOpen: true));
        try
        {
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                var read = await reader.ReadAsync(ct);
                var remaining = read.Buffer;
                while (remaining.PositionOf((byte)'\n') is { } newline)
                {
                    var line = remaining.Slice(0, newline);
                    remaining = remaining.Slice(remaining.GetPosition(1, newline));
                    ThrowIfMessageTooLarge(line.Length);
                    if (line.Length > 0) await ProcessMessageAsync(TrimCarriageReturn(line));
                }

                ThrowIfMessageTooLarge(remaining.Length);
                if (read.IsCompleted && remaining.Length > 0)
                {
                    await ProcessMessageAsync(TrimCarriageReturn(remaining));
                    remaining = remaining.Slice(remaining.End);
                }
                reader.AdvanceTo(remaining.Start, remaining.End);
                if (read.IsCompleted) break;
            }
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private async Task ProcessMessageAsync(ReadOnlySequence<byte> line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var id) && root.TryGetProperty("result", out var result) && id.ValueKind == JsonValueKind.Number)
            {
                if (_calls.TryRemove(id.GetInt64(), out var tcs)) tcs.TrySetResult(result.Clone());
            }
            else if (root.TryGetProperty("id", out id) && root.TryGetProperty("error", out var error) && id.ValueKind == JsonValueKind.Number)
            {
                if (_calls.TryRemove(id.GetInt64(), out var tcs)) tcs.TrySetException(new CodexRpcException(error.Clone()));
            }
            else if (root.TryGetProperty("id", out id) && root.TryGetProperty("method", out var method))
            {
                var key = Guid.NewGuid().ToString("N");
                var p = root.TryGetProperty("params", out var param) ? param.Clone() : JsonSerializer.SerializeToElement(new { });
                var pending = new PendingRequest(key, method.GetString() ?? "request", p, DateTimeOffset.UtcNow);
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
        catch (JsonException ex) { Console.Error.WriteLine($"Invalid app-server message: {ex.Message}"); }
    }

    public static void ThrowIfMessageTooLarge(long length)
    {
        if (length > MaximumAppServerMessageBytes)
            throw new AppServerMessageTooLargeException(length, MaximumAppServerMessageBytes);
    }

    private static ReadOnlySequence<byte> TrimCarriageReturn(ReadOnlySequence<byte> line)
    {
        if (line.Length == 0) return line;
        var last = line.Slice(line.Length - 1, 1).FirstSpan[0];
        return last == (byte)'\r' ? line.Slice(0, line.Length - 1) : line;
    }

    private readonly ConcurrentDictionary<string, JsonElement> _requestIds = new();

    private void HandleNotification(string method, JsonElement parameters)
    {
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
            DateTimeOffset? completedAt = null;
            if (completedTurn.TryGetProperty("completedAt", out var timestamp) &&
                timestamp.TryGetInt64(out var seconds) && seconds > 0)
            {
                try { completedAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { }
            }
            _runtimeStates.ObserveTurnCompleted(
                completedThreadId,
                completedTurnId,
                completedStatus,
                generation,
                completedAt);
            _notifications.PublishTurnOutcome(completedThreadId, completedTurnId, completedStatus, completedAt);
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
            _loadedThreads.TryRemove(closedThreadId, out _);
            _threadExecutionPermissions.TryRemove(closedThreadId, out _);
            _threadWorkingDirectories.TryRemove(closedThreadId, out _);
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
        if (!element.TryGetProperty(propertyName, out var timestamp) || !timestamp.TryGetInt64(out var seconds) || seconds <= 0)
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
        _loadedThreads[threadId] = 0;
        if (TryGetString(thread, "cwd", out var cwd))
            _threadWorkingDirectories[threadId] = cwd;
        if (thread.TryGetProperty("status", out var status))
            _runtimeStates.ObserveAppServerStatus(threadId, status, Volatile.Read(ref _generation));
    }

    public void ObserveThreadList(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var threads) || threads.ValueKind != JsonValueKind.Array) return;
        var generation = Volatile.Read(ref _generation);
        foreach (var thread in threads.EnumerateArray())
        {
            if (!TryGetString(thread, "id", out var threadId) || !thread.TryGetProperty("status", out var status)) continue;
            if (TryGetString(thread, "cwd", out var cwd))
                _threadWorkingDirectories[threadId] = cwd;
            _runtimeStates.ObserveAppServerStatus(threadId, status, generation);
        }
    }

    public async Task EnsureThreadLoadedAsync(string threadId, CancellationToken cancellationToken, bool force = false)
    {
        if (!force && _loadedThreads.ContainsKey(threadId)) return;
        var gate = _threadLoadLocks.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && _loadedThreads.ContainsKey(threadId)) return;
            var result = await CallAsync("thread/resume", new { threadId, excludeTurns = true }, cancellationToken);
            MarkThreadLoaded(result);
            _loadedThreads[threadId] = 0;
        }
        finally { gate.Release(); }
    }

    public async Task<JsonElement> SendUserInputAsync(
        string threadId,
        IReadOnlyCollection<object> input,
        string? clientUserMessageId,
        string? expectedTurnId,
        ExecutionPermissions executionPermissions,
        CancellationToken cancellationToken)
    {
        if (input.Count == 0) throw new ArgumentException("At least one message or attachment is required.");
        await EnsureThreadLoadedAsync(threadId, cancellationToken);
        var clientId = string.IsNullOrWhiteSpace(clientUserMessageId) ? Guid.NewGuid().ToString() : clientUserMessageId;

        if (!string.IsNullOrWhiteSpace(expectedTurnId))
        {
            try { return await SteerAsync(threadId, expectedTurnId, input, clientId, cancellationToken); }
            catch (CodexRpcException ex) when (ex.IsNoActiveTurn || ex.IsExpectedTurnMismatch)
            {
                var activeTurnId = await GetActiveTurnIdAsync(threadId, cancellationToken);
                if (!string.IsNullOrWhiteSpace(activeTurnId))
                    return await SteerAsync(threadId, activeTurnId, input, clientId, cancellationToken);
                return await StartTurnAsync(threadId, input, clientId, executionPermissions, cancellationToken);
            }
            catch (CodexRpcException ex) when (ex.IsThreadNotFound)
            {
                await EnsureThreadLoadedAsync(threadId, cancellationToken, force: true);
                return await StartTurnAsync(threadId, input, clientId, executionPermissions, cancellationToken);
            }
        }

        try { return await StartTurnAsync(threadId, input, clientId, executionPermissions, cancellationToken); }
        catch (CodexRpcException ex) when (ex.IsThreadNotFound)
        {
            await EnsureThreadLoadedAsync(threadId, cancellationToken, force: true);
            return await StartTurnAsync(threadId, input, clientId, executionPermissions, cancellationToken);
        }
        catch (CodexRpcException ex) when (ex.IsActiveTurnConflict)
        {
            var activeTurnId = await GetActiveTurnIdAsync(threadId, cancellationToken);
            if (string.IsNullOrWhiteSpace(activeTurnId)) throw;
            return await SteerAsync(threadId, activeTurnId, input, clientId, cancellationToken);
        }
    }

    public async Task<JsonElement> InterruptCurrentTurnAsync(string threadId, CancellationToken cancellationToken)
    {
        await EnsureThreadLoadedAsync(threadId, cancellationToken);
        var activeTurnId = await GetActiveTurnIdAsync(threadId, cancellationToken);
        if (string.IsNullOrWhiteSpace(activeTurnId))
            return JsonSerializer.SerializeToElement(new { interrupted = false, reason = "noActiveTurn" });
        try
        {
            return await CallAsync("turn/interrupt", new { threadId, turnId = activeTurnId }, cancellationToken);
        }
        catch (CodexRpcException ex) when (ex.IsNoActiveTurn)
        {
            return JsonSerializer.SerializeToElement(new { interrupted = false, reason = "alreadyFinished" });
        }
    }

    public async Task<string?> GetActiveTurnIdAsync(string threadId, CancellationToken cancellationToken)
    {
        var live = _runtimeStates.Get(threadId);
        if (live is { CanControl: true, IsRunning: true, ActiveTurnId.Length: > 0 }) return live.ActiveTurnId;
        var result = await CallAsync("thread/turns/list", new
        {
            threadId,
            limit = 1,
            sortDirection = "desc",
            itemsView = "notLoaded"
        }, cancellationToken);
        if (!result.TryGetProperty("data", out var turns) || turns.ValueKind != JsonValueKind.Array) return null;
        foreach (var turn in turns.EnumerateArray())
            if (TryGetString(turn, "id", out var turnId) && TryGetString(turn, "status", out var status) &&
                status.Equals("inProgress", StringComparison.Ordinal)) return turnId;
        return null;
    }

    private async Task<JsonElement> StartTurnAsync(
        string threadId,
        IReadOnlyCollection<object> input,
        string clientUserMessageId,
        ExecutionPermissions executionPermissions,
        CancellationToken cancellationToken)
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
                result = await CallAsync("turn/start", new
                {
                    threadId,
                    input,
                    clientUserMessageId,
                    permissions = executionPermissions.Permissions,
                    approvalPolicy = executionPermissions.ApprovalPolicy,
                    approvalsReviewer = executionPermissions.ApprovalsReviewer
                }, cancellationToken);
            }
            catch (CodexRpcException ex) when (IsPermissionsFieldUnsupported(ex))
            {
                var cwd = await GetThreadCwdAsync(threadId, cancellationToken);
                result = await CallAsync("turn/start", new
                {
                    threadId,
                    input,
                    clientUserMessageId,
                    sandboxPolicy = executionPermissions.LegacyTurnSandboxPolicy(cwd),
                    approvalPolicy = executionPermissions.ApprovalPolicy,
                    approvalsReviewer = executionPermissions.ApprovalsReviewer
                }, cancellationToken);
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
        return result;
    }

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

    private Task<JsonElement> SteerAsync(
        string threadId,
        string expectedTurnId,
        IReadOnlyCollection<object> input,
        string clientUserMessageId,
        CancellationToken cancellationToken) =>
        CallAsync("turn/steer", new { threadId, expectedTurnId, input, clientUserMessageId }, cancellationToken);

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
        request.Method.Equals("currentTime/read", StringComparison.Ordinal);

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
        _calls[id] = tcs;
        try
        {
            await SendAsync(new { id, method, @params = parameters ?? new { } }, ct);
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

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private async Task SendAsync(object message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_input is null) throw new InvalidOperationException("Codex app-server is unavailable.");
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

    public CodexRpcException(JsonElement error) : base(ReadMessage(error))
    {
        Error = error;
        Code = error.ValueKind == JsonValueKind.Object &&
               error.TryGetProperty("code", out var code) &&
               code.TryGetInt32(out var value)
            ? value
            : 0;
    }

    public bool IsThreadNotFound => Contains("thread not found") || Contains("unknown thread") || Contains("no rollout found");
    public bool IsNoActiveTurn => Contains("no active turn") || Contains("turn is not active") || Contains("turn not found");
    public bool IsExpectedTurnMismatch => Contains("expected turn") || Contains("turn id mismatch") || Contains("does not match");
    public bool IsActiveTurnConflict =>
        Contains("turn already in progress") || Contains("active turn") || Contains("thread is busy") || Contains("already running");
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
