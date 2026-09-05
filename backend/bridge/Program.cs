using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexLanBridge;
using CodexLanBridge.Commute;
using Microsoft.AspNetCore.Http.Features;

var bridgeDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "CodexLanConsole");
if (args.Length == 1 && args[0].Equals("configure-administrator-code", StringComparison.OrdinalIgnoreCase))
{
    var administratorCode = Console.In.ReadLine()?.Trim() ?? "";
    var storage = PairingStoragePolicy.ResolveCurrent();
    PairingStoragePolicy.Prepare(storage);
    var persistentCode = new PersistentAdministratorCode(storage.AdministratorCodeFile);
    persistentCode.Configure(administratorCode, storage.AdministratorMode
        ? file => PairingStoragePolicy.ProtectSecretFile(storage, file)
        : null);
    return;
}
var manualStopFile = Path.Combine(bridgeDataDirectory, "manual-stop.flag");
if (OperatingSystem.IsWindows() && File.Exists(manualStopFile)) return;

Mutex? singleInstanceMutex = null;
if (OperatingSystem.IsWindows())
{
    singleInstanceMutex = new Mutex(true, @"Local\CodexLanConsole.Bridge", out var ownsMutex);
    if (!ownsMutex)
    {
        singleInstanceMutex.Dispose();
        return;
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var configuredBridgeUrls = Environment.GetEnvironmentVariable("CODEX_LAN_URLS");
var bridgeBinding = BridgeBindingPolicy.ResolveCurrent(
    WindowsProcessElevation.Current.Active,
    configuredBridgeUrls);
builder.WebHost.UseUrls(bridgeBinding.UrlSetting);
builder.Services.AddSingleton<PairingService>();
builder.Services.AddSingleton<ApiErrorLog>();
builder.Services.AddSingleton<AppServerDiagnosticLog>();
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddSingleton<ThreadRuntimeStateStore>();
builder.Services.AddSingleton<ApprovalSettingsStore>();
builder.Services.AddSingleton<BridgeTurnRecoveryStore>();
builder.Services.AddSingleton<ThreadCommandOutboxStore>();
builder.Services.AddSingleton<ThreadLiveEventStore>();
builder.Services.AddSingleton<CodexAppServer>();
builder.Services.AddSingleton<CodexModelCatalog>();
builder.Services.AddSingleton<ThreadCommandOutboxDispatcher>();
builder.Services.AddSingleton<ProjectScanner>();
builder.Services.AddSingleton<LocalPortRelayService>();
builder.Services.AddSingleton<FileTransferService>();
builder.Services.AddSingleton<ConsoleLaunchAuditService>();
builder.Services.AddSingleton<QuotaMonitorService>();
builder.Services.AddSingleton<CpuGuardService>();
builder.Services.AddSingleton<LocalControlTokenStore>();
builder.Services.AddSingleton<WindowsQuotaWidgetSettingsStore>();
builder.Services.AddSingleton<ChromeBootstrapService>();
builder.Services.AddSingleton<CommuteStore>();
builder.Services.AddSingleton<CommutePlanner>();
builder.Services.AddSingleton<ScooterStore>();
builder.Services.AddSingleton<ScooterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScooterService>());
builder.Services.AddSingleton<CommuteReminderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CommuteReminderService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CodexAppServer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ThreadCommandOutboxDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalPortRelayService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<FileTransferService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConsoleLaunchAuditService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<QuotaMonitorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CpuGuardService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChromeBootstrapService>());
builder.Services.AddHostedService<WindowsQuotaWidgetHostedService>();
builder.Services.AddHostedService<NotificationMonitor>();
builder.Services.AddHostedService<ExternalRolloutMonitor>();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = FileTransferService.MaximumRequestBytes;
    options.ValueLengthLimit = 64 * 1024;
});

var app = builder.Build();
_ = app.Services.GetRequiredService<LocalControlTokenStore>();
app.Use(async (context, next) =>
{
    var suppliedRequestId = context.Request.Headers["X-Request-ID"].ToString().Trim();
    var requestId = suppliedRequestId.Length is > 0 and <= 64 &&
                    suppliedRequestId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
        ? suppliedRequestId
        : Guid.NewGuid().ToString("N");
    context.TraceIdentifier = requestId;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Request-ID"] = requestId;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    });
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        context.RequestServices.GetRequiredService<ApiErrorLog>().Write(
            requestId,
            context.Request.Method,
            context.Request.Path.Value ?? "",
            ex);
        var (status, kind, message, code) = ex switch
        {
            CodexRpcException rpc when rpc.IsPolicyRestricted =>
                (StatusCodes.Status403Forbidden, "policyRestricted", rpc.Message, (int?)rpc.Code),
            CodexRpcException rpc => (rpc.SuggestedHttpStatus, "codexRpc", rpc.Message, (int?)rpc.Code),
            FileNotFoundException => (StatusCodes.Status404NotFound, "fileNotFound", ex.Message, null),
            DirectoryNotFoundException => (StatusCodes.Status404NotFound, "directoryNotFound", ex.Message, null),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "forbidden", ex.Message, null),
            ArgumentException => (StatusCodes.Status400BadRequest, "invalidRequest", ex.Message, null),
            InvalidDataException => (StatusCodes.Status400BadRequest, "invalidRequest", ex.Message, null),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "invalidRequest", ex.Message, null),
            TaskCanceledException => (StatusCodes.Status504GatewayTimeout, "timeout", "Codex did not respond in time.", null),
            TimeoutException => (StatusCodes.Status504GatewayTimeout, "timeout", ex.Message, null),
            InvalidOperationException when ex.Message.Contains("app-server", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status503ServiceUnavailable, "codexUnavailable", ex.Message, null),
            _ => (StatusCodes.Status500InternalServerError, "serverError", "The bridge could not complete this request.", null)
        };
        Console.Error.WriteLine($"API {context.Request.Method} {context.Request.Path} failed: {ex}");
        context.Response.Clear();
        context.Response.StatusCode = status;
        var detail = ex is CodexRpcException or ArgumentException or InvalidDataException or BadHttpRequestException
            ? ex.Message
            : null;
        await context.Response.WriteAsJsonAsync(new { error = message, kind, code, requestId, detail });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    var authorizedLocalCpu = context.Request.Path.StartsWithSegments("/api/local/cpu") &&
        CpuGuardApi.IsAuthorizedLocalControl(
            context,
            context.RequestServices.GetRequiredService<LocalControlTokenStore>());
    if (!context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/api/health") ||
        context.Request.Path.StartsWithSegments("/api/pair") ||
        authorizedLocalCpu)
    {
        await next();
        return;
    }

    var auth = context.Request.Headers.Authorization.ToString();
    var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? auth[7..].Trim()
        : context.Request.Cookies[PairingService.SessionCookieName] ?? "";
    if (!context.RequestServices.GetRequiredService<PairingService>().Validate(token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Pair this device first." });
        return;
    }
    await next();
});

app.MapCommute();
app.MapScooter();

app.MapGet("/api/health", (PairingService pairing, CodexAppServer codex) => new
{
    ok = true,
    name = "Codex LAN Console",
    version = typeof(CodexAppServer).Assembly.GetName().Version?.ToString(3) ?? "1.7.7",
    paired = pairing.HasDevices,
    pairingOpen = pairing.IsPairingOpen,
    codex = codex.IsReady,
    machine = Environment.MachineName,
    time = DateTimeOffset.Now
});

app.MapGet("/api/quota", (QuotaMonitorService quota, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(quota.GetSnapshot());
});

app.MapGet("/api/cpu", (CpuGuardService cpu, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(cpu.GetSnapshot());
});

app.MapGet("/api/browser/status", (ChromeBootstrapService browser, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(browser.GetSnapshot());
});

app.MapPost("/api/browser/start", async (
    ChromeBootstrapService browser,
    IHostApplicationLifetime applicationLifetime) =>
{
    var snapshot = await browser.EnsureStartedAsync(
        "手机端远程办事",
        applicationLifetime.ApplicationStopping);
    // A failed browser wake-up is an actionable state, not a failed phone-to-PC
    // transport. Returning the snapshot lets the mobile UI explain and retry it.
    return Results.Ok(snapshot);
});

app.MapPost("/api/browser/settings", (
    BrowserSettingsRequest request,
    ChromeBootstrapService browser) =>
    Results.Ok(browser.SetAutoStart(request.AutoStartWithBridge)));

app.MapGet("/api/local/cpu", (CpuGuardService cpu, LocalControlTokenStore localControl, HttpContext context) =>
{
    if (!CpuGuardApi.IsAuthorizedLocalControl(context, localControl)) return Results.NotFound();
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(cpu.GetSnapshot());
});

app.MapGet("/api/local/cpu/status", (CpuGuardService cpu, LocalControlTokenStore localControl, HttpContext context) =>
{
    if (!CpuGuardApi.IsAuthorizedLocalControl(context, localControl)) return Results.NotFound();
    context.Response.Headers.CacheControl = "no-store";
    return Results.Text(CpuGuardApi.FormatStatus(cpu.GetSnapshot()), "text/plain; charset=utf-8");
});

app.MapPost("/api/local/cpu/mode/{requestedMode}", (string requestedMode, CpuGuardService cpu, LocalControlTokenStore localControl, HttpContext context) =>
{
    if (!CpuGuardApi.IsAuthorizedLocalControl(context, localControl)) return Results.NotFound();
    if (!Enum.TryParse<CpuGuardMode>(requestedMode, true, out var mode) || !Enum.IsDefined(mode))
        return Results.BadRequest(new { error = "Mode must be Off, Monitor, or AutoGuard." });
    cpu.SetMode(mode);
    return Results.Text($"CPU guard mode: {mode}", "text/plain; charset=utf-8");
});

app.MapPost("/api/local/cpu/repair", async (CpuGuardService cpu, LocalControlTokenStore localControl, HttpContext context) =>
{
    if (!CpuGuardApi.IsAuthorizedLocalControl(context, localControl)) return Results.NotFound();
    var result = await cpu.RepairNowAsync(context.RequestAborted);
    var details = result.Changes.Concat(result.Errors).ToArray();
    var text = details.Length == 0
        ? result.Message
        : result.Message + Environment.NewLine + string.Join(Environment.NewLine, details.Select(value => "- " + value));
    return Results.Text(text, "text/plain; charset=utf-8", statusCode: result.Applied ? 200 : 409);
});

app.MapPost("/api/local/cpu/baseline", async (CpuGuardService cpu, LocalControlTokenStore localControl, HttpContext context) =>
{
    if (!CpuGuardApi.IsAuthorizedLocalControl(context, localControl)) return Results.NotFound();
    var result = await cpu.CaptureCurrentBaselineAsync(context.RequestAborted);
    var details = result.Changes.Concat(result.Errors).ToArray();
    var text = details.Length == 0
        ? result.Message
        : result.Message + Environment.NewLine + string.Join(Environment.NewLine, details.Select(value => "- " + value));
    return Results.Text(text, "text/plain; charset=utf-8", statusCode: result.Applied ? 200 : 409);
});

app.MapPost("/api/pair", async (HttpContext context, PairingService pairing) =>
{
    var request = await context.Request.ReadFromJsonAsync<PairRequest>();
    if (request is null) return Results.BadRequest(new { error = "Pairing details are required." });
    var clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var result = pairing.TryPair(
        request.Code ?? "",
        request.DeviceName ?? "Android",
        clientKey,
        out var token,
        out var retryAfterSeconds);
    if (result == PairingAttemptResult.RateLimited)
    {
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        return Results.Json(new { error = "Too many pairing attempts. Try again shortly." }, statusCode: 429);
    }
    if (result == PairingAttemptResult.PairingClosed)
        return Results.Json(new
        {
            error = "Administrator Mode pairing is closed or its code expired. Open a time-limited pairing window from the local Windows manager.",
            kind = "pairingClosed"
        }, statusCode: StatusCodes.Status403Forbidden);
    if (result != PairingAttemptResult.Success)
        return Results.Json(new { error = "Invalid or expired pairing code." }, statusCode: 401);
    context.Response.Cookies.Append(PairingService.SessionCookieName, token, SessionCookie.Options(context));
    return Results.Ok(new { token, machine = Environment.MachineName });
});

app.MapPost("/api/session", (HttpContext context) =>
{
    var auth = context.Request.Headers.Authorization.ToString();
    var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? auth[7..].Trim()
        : context.Request.Cookies[PairingService.SessionCookieName] ?? "";
    context.Response.Cookies.Append(PairingService.SessionCookieName, token, SessionCookie.Options(context));
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/notifications/events", async (
    long? after,
    int? limit,
    int? wait,
    HttpContext context,
    NotificationStore notifications,
    CodexAppServer codex) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var pageSize = Math.Clamp(limit ?? 50, 1, 100);
    var waitSeconds = Math.Clamp(wait ?? 0, 0, 30);
    if (after.HasValue && after.Value >= 0 && waitSeconds > 0)
        await notifications.WaitForChangeAfterAsync(
            after.Value,
            TimeSpan.FromSeconds(waitSeconds),
            context.RequestAborted);
    var activePendingKeys = codex.Pending.Keys.ToHashSet(StringComparer.Ordinal);
    return Results.Ok(notifications.Read(after, pageSize, activePendingKeys));
});

app.MapGet("/api/summary", async (
    CodexAppServer codex,
    ProjectScanner projects,
    ThreadRuntimeStateStore runtimeStates,
    ThreadCommandOutboxStore commandOutbox,
    ChromeBootstrapService browser,
    CancellationToken cancellationToken) =>
{
    var threads = await codex.CallAsync(
        "thread/list",
        new { limit = 30, sortKey = "updated_at", sortDirection = "desc", useStateDbOnly = true },
        cancellationToken);
    codex.ObserveThreadList(threads);
    return Results.Ok(new
    {
        machine = Environment.MachineName,
        codexReady = codex.IsReady,
        administratorMode = WindowsProcessElevation.Current,
        browser = browser.GetSnapshot(),
        threads,
        projects = projects.Scan().Take(12).ToArray(),
        processes = ProcessApi.List().Take(20).ToArray(),
        pending = codex.Pending.Values.OrderByDescending(x => x.CreatedAt).ToArray(),
        commandOutbox = commandOutbox.Snapshot(limit: 50),
        turnRecovery = codex.RecoverySnapshot(),
        threadAccess = codex.AccessSnapshot(),
        runtimeStates = runtimeStates.Snapshot()
    });
});

app.MapGet("/api/threads", async (int? limit, CodexAppServer codex, CancellationToken cancellationToken) =>
    Results.Ok(await codex.CallAsync(
        "thread/list",
        new
        {
            limit = Math.Clamp(limit ?? 50, 1, 100),
            sortKey = "updated_at",
            sortDirection = "desc",
            useStateDbOnly = true
        },
        cancellationToken)));

app.MapGet("/api/threads/{id}", async (
    string id,
    string? cursor,
    bool? paged,
    int? before,
    int? limit,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    ThreadLiveEventStore liveEvents,
    ThreadCommandOutboxStore commandOutbox,
    CancellationToken cancellationToken) =>
{
    var pageSize = Math.Clamp(limit ?? 6, 1, 20);
    if (cursor?.Length > 4096) throw new ArgumentException("The turn cursor is too long.");

    // Full-history reads can materialize multi-gigabyte rollout files. Never fall back to
    // a full-history RPC; cached clients must refresh to the cursor-paginated UI.
    if (paged != true && cursor is null)
    {
        return Results.Json(new
        {
            error = "This cached client uses unsafe full-history loading. Refresh or update Codex LAN Console.",
            kind = "cursorPaginationRequired"
        }, statusCode: StatusCodes.Status426UpgradeRequired);
    }

    JsonElement metadata;
    try
    {
        metadata = await codex.CallAsync(
            "thread/read",
            new { threadId = id, includeTurns = false },
            cancellationToken);
    }
    catch (CodexRpcException ex) when (ApiHelpers.IsUnmaterializedThread(ex) ||
        ex.IsHistoryInitializing && codex.IsThreadStarting(id))
    {
        codex.TryGetKnownThreadCwd(id, out var knownCwd);
        metadata = JsonSerializer.SerializeToElement(new
        {
            thread = new
            {
                id,
                cwd = knownCwd,
                status = new { type = "notLoaded" }
            }
        });
    }
    try
    {
        var turnPage = await codex.CallAsync(
            "thread/turns/list",
            new
            {
                threadId = id,
                cursor,
                limit = pageSize,
                sortDirection = "desc",
                itemsView = "summary"
            },
            cancellationToken);
        runtimeStates.ObserveLatestPersistedTurn(id, turnPage);
        var recovery = codex.ReconcileRecoveryWithLatestPersistedTurn(id, turnPage);
        JsonElement recentItemsPage = default;
        var recentItemsTurnId = string.IsNullOrWhiteSpace(cursor) ? ApiHelpers.LatestTurnId(turnPage) : null;
        if (!string.IsNullOrWhiteSpace(recentItemsTurnId))
        {
            try
            {
                recentItemsPage = await codex.CallAsync(
                    "thread/items/list",
                    new
                    {
                        threadId = id,
                        turnId = recentItemsTurnId,
                        limit = 64,
                        sortDirection = "desc"
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is CodexRpcException or IOException)
            {
                // Very large, legacy, or temporarily unavailable item history must not make
                // the entire conversation unreadable. The summary page remains a
                // safe fallback and the next visible-page poll will try again.
            }
        }
        return Results.Ok(ApiHelpers.PagedThread(
            metadata,
            turnPage,
            id,
            runtimeStates.Get(id),
            string.IsNullOrWhiteSpace(cursor) ? liveEvents.Snapshot(id) : null,
            recentItemsPage,
            recentItemsTurnId,
            recovery,
            commandOutbox.Snapshot(id, 20)));
    }
    catch (CodexRpcException ex) when (ApiHelpers.IsUnmaterializedThread(ex) ||
        ex.IsHistoryInitializing && codex.IsThreadStarting(id))
    {
        return Results.Ok(ApiHelpers.PagedThread(
            metadata,
            default,
            id,
            runtimeStates.Get(id),
            string.IsNullOrWhiteSpace(cursor) ? liveEvents.Snapshot(id) : null,
            recoveryState: codex.RecoverySnapshotFor(id),
            commandReceipts: commandOutbox.Snapshot(id, 20)));
    }
    catch (CodexRpcException ex) when (ex.Code == -32601)
    {
        return Results.Json(new
        {
            error = "The installed Codex app-server does not support safe turn pagination. Update Codex before opening task history remotely.",
            kind = "codexUpgradeRequired"
        }, statusCode: StatusCodes.Status501NotImplemented);
    }
});

app.MapGet("/api/threads/{id}/live", async (
    string id,
    long? after,
    int? waitMs,
    ThreadLiveEventStore liveEvents,
    CancellationToken cancellationToken) =>
{
    var revision = Math.Max(0, after ?? 0);
    var wait = TimeSpan.FromMilliseconds(Math.Clamp(waitMs ?? 25_000, 250, 25_000));
    var snapshot = await liveEvents.WaitForChangeAsync(id, revision, wait, cancellationToken);
    return Results.Ok(new { revision = snapshot.Revision, turns = snapshot.Turns });
});

app.MapGet("/api/permissions", async (
    string? cwd,
    CodexAppServer codex,
    CancellationToken cancellationToken) =>
{
    var workspace = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd;
    try
    {
        return Results.Ok(await codex.ListPermissionProfilesAsync(workspace, cancellationToken));
    }
    catch (CodexRpcException ex) when (ex.Code == -32601)
    {
        return Results.Ok(new
        {
            data = new[]
            {
                new { id = ":read-only", allowed = true, description = "Read files without modifying them." },
                new { id = ":workspace", allowed = true, description = "Modify files inside the current workspace." },
                new { id = ":danger-full-access", allowed = true, description = "Access the full computer without sandbox restrictions." }
            },
            legacy = true
        });
    }
});

app.MapGet("/api/models", async (
    bool? forceRefresh,
    CodexModelCatalog models,
    CancellationToken cancellationToken) =>
    Results.Ok(new
    {
        data = await models.ListAsync(forceRefresh == true, cancellationToken)
    }));

app.MapPost("/api/threads", async (
    ThreadCreate request,
    CodexAppServer codex,
    IHostApplicationLifetime applicationLifetime) =>
{
    // After ASP.NET has accepted the command, the bridge owns its lifetime.
    // A phone/WebView disconnect must not cancel an app-server dispatch.
    var operationToken = applicationLifetime.ApplicationStopping;
    var workspace = string.IsNullOrWhiteSpace(request.Cwd)
        ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        : request.Cwd;
    var permissions = ExecutionPermissions.Parse(request.Permissions, request.ApprovalPolicy, request.ApprovalsReviewer);
    return Results.Ok(await codex.StartThreadAsync(workspace, permissions, operationToken));
});

app.MapPost("/api/threads/{id}/messages", async (
    string id,
    MessageRequest request,
    CodexAppServer codex,
    CodexModelCatalog models,
    ThreadCommandOutboxStore commandOutbox,
    ThreadCommandOutboxDispatcher commandDispatcher,
    FileTransferService files,
    ChromeBootstrapService browser,
    IHostApplicationLifetime applicationLifetime) =>
{
    // Persist before dispatch. The mobile HTTP connection is only the producer;
    // disconnecting it cannot cancel or lose an accepted computer command.
    var operationToken = applicationLifetime.ApplicationStopping;
    var browserBootstrap = request.BrowserRequired == true
        ? await browser.EnsureStartedAsync("浏览器任务开始", operationToken)
        : null;
    var input = new List<object>();
    if (!string.IsNullOrWhiteSpace(request.Text))
        input.Add(new { type = "text", text = request.Text, text_elements = Array.Empty<object>() });
    input.AddRange(files.BuildCodexInputs(id, request.AttachmentIds));
    input.AddRange(await ApiHelpers.BuildSkillInputsAsync(codex, id, request.Skills, operationToken));
    if (input.Count == 0) return Results.BadRequest(new { error = "Message and attachments are empty." });
    var permissions = ExecutionPermissions.Parse(request.Permissions, request.ApprovalPolicy, request.ApprovalsReviewer);
    var options = await models.NormalizeAsync(request.Model, request.ReasoningEffort, operationToken);
    var receipt = commandOutbox.Enqueue(
        id,
        JsonSerializer.SerializeToElement(input),
        request.ClientUserMessageId,
        null,
        permissions,
        options);
    commandDispatcher.Wake();
    return Results.Accepted($"/api/threads/{Uri.EscapeDataString(id)}/commands/{receipt.Id}", new
    {
        queued = receipt.Status == ThreadCommandStatus.Queued,
        receipt,
        browser = browserBootstrap
    });
});

app.MapPost("/api/threads/{id}/steer", async (
    string id,
    SteerRequest request,
    CodexAppServer codex,
    CodexModelCatalog models,
    ThreadCommandOutboxStore commandOutbox,
    ThreadCommandOutboxDispatcher commandDispatcher,
    FileTransferService files,
    ChromeBootstrapService browser,
    IHostApplicationLifetime applicationLifetime) =>
{
    // A stale or Desktop-owned turn is not an HTTP conflict. The durable outbox
    // waits until ownership is safe, then reconciles the expected turn id.
    var operationToken = applicationLifetime.ApplicationStopping;
    var browserBootstrap = request.BrowserRequired == true
        ? await browser.EnsureStartedAsync("浏览器任务续跑", operationToken)
        : null;
    var input = new List<object>();
    if (!string.IsNullOrWhiteSpace(request.Text))
        input.Add(new { type = "text", text = request.Text, text_elements = Array.Empty<object>() });
    input.AddRange(files.BuildCodexInputs(id, request.AttachmentIds));
    input.AddRange(await ApiHelpers.BuildSkillInputsAsync(codex, id, request.Skills, operationToken));
    if (input.Count == 0) return Results.BadRequest(new { error = "Message and attachments are empty." });
    var permissions = ExecutionPermissions.Parse(request.Permissions, request.ApprovalPolicy, request.ApprovalsReviewer);
    var options = await models.NormalizeAsync(request.Model, request.ReasoningEffort, operationToken);
    var receipt = commandOutbox.Enqueue(
        id,
        JsonSerializer.SerializeToElement(input),
        request.ClientUserMessageId,
        request.TurnId,
        permissions,
        options);
    commandDispatcher.Wake();
    return Results.Accepted($"/api/threads/{Uri.EscapeDataString(id)}/commands/{receipt.Id}", new
    {
        queued = receipt.Status == ThreadCommandStatus.Queued,
        receipt,
        browser = browserBootstrap
    });
});

app.MapGet("/api/threads/{id}/commands", (
    string id,
    int? limit,
    ThreadCommandOutboxStore commandOutbox) =>
    Results.Ok(new { commands = commandOutbox.Snapshot(id, Math.Clamp(limit ?? 20, 1, 100)) }));

app.MapGet("/api/threads/{id}/commands/{receiptId}", (
    string id,
    string receiptId,
    ThreadCommandOutboxStore commandOutbox) =>
    commandOutbox.Find(id, receiptId) is { } receipt
        ? Results.Ok(new { queued = receipt.Status == ThreadCommandStatus.Queued, receipt })
        : Results.NotFound(new { error = "The command receipt was not found." }));

app.MapDelete("/api/threads/{id}/commands/{receiptId}", (
    string id,
    string receiptId,
    ThreadCommandOutboxStore commandOutbox,
    ThreadCommandOutboxDispatcher commandDispatcher) =>
{
    if (!commandOutbox.Cancel(id, receiptId))
        return Results.Conflict(new { error = "This command was not found or can no longer be cancelled." });
    commandDispatcher.Wake();
    return Results.Ok(new { receipt = commandOutbox.Find(id, receiptId) });
});

app.MapPost("/api/threads/{id}/interrupt", async (
    string id,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    CancellationToken cancellationToken) =>
{
    if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } conflict) return conflict;
    return Results.Ok(await codex.InterruptCurrentTurnAsync(id, cancellationToken));
});

app.MapGet("/api/approvals", (CodexAppServer codex) =>
    codex.Pending.Values.Where(CodexAppServer.IsUserApprovalRequest).OrderByDescending(x => x.CreatedAt));
app.MapPost("/api/approvals/{key}", async (string key, ApprovalDecision request, CodexAppServer codex) =>
{
    if (request.Decision is not ("accept" or "acceptForSession" or "decline" or "cancel"))
        return Results.BadRequest(new { error = "Invalid decision." });
    if (!codex.Pending.TryGetValue(key, out var pending)) return Results.NotFound();
    if (ElicitationProtocol.IsToolApproval(pending))
    {
        try
        {
            var accepting = request.Decision is "accept" or "acceptForSession";
            var persistence = request.Decision == "acceptForSession" &&
                              ElicitationProtocol.AdvertisedPersistence(pending).Contains("session", StringComparer.Ordinal)
                ? "session"
                : null;
            if (accepting)
                return await codex.ResolveMcpElicitationAsync(
                        key,
                        "accept",
                        ElicitationProtocol.BuildToolApproval(pending, persistence).GetProperty("content"),
                        persistence)
                    ? Results.Ok()
                    : Results.NotFound();
            return await codex.ResolveMcpElicitationAsync(key, request.Decision, null, null)
                ? Results.Ok()
                : Results.NotFound();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
    return await codex.ResolvePendingAsync(key, request.Decision) ? Results.Ok() : Results.NotFound();
});

app.MapPost("/api/approvals/approve-all", async (ApprovalBatchDecision request, CodexAppServer codex) =>
{
    if (request.Decision is not ("accept" or "acceptForSession"))
        return Results.BadRequest(new { error = "Bulk approval only supports accept or acceptForSession." });
    return Results.Ok(await codex.ResolveAllApprovalsAsync(request.Decision));
});

app.MapGet("/api/approval-settings", (ApprovalSettingsStore settings) => settings.Get());
app.MapPost("/api/approval-settings", async (
    ApprovalSettingsUpdate request,
    ApprovalSettingsStore settings,
    CodexAppServer codex) =>
{
    if (request.AutoApproveAll &&
        !string.Equals(request.Confirmation, ApprovalSettingsStore.EnableConfirmation, StringComparison.Ordinal))
    {
        return Results.BadRequest(new
        {
            error = $"Enabling automatic approval requires confirmation: {ApprovalSettingsStore.EnableConfirmation}."
        });
    }

    settings.SetAutoApproveAll(request.AutoApproveAll);
    var approvedNow = request.AutoApproveAll
        ? await codex.ResolveAllApprovalsAsync("accept", recordAsAutomatic: true)
        : ApprovalBatchResult.Empty;
    return Results.Ok(new { settings = settings.Get(), approvedNow });
});

app.MapPost("/api/pending/{key}/answers", async (string key, UserInputResponse request, CodexAppServer codex) =>
{
    if (!codex.Pending.TryGetValue(key, out var pending)) return Results.NotFound(new { error = "This question has already been handled." });
    if (!CodexAppServer.IsUserInputRequest(pending)) return Results.BadRequest(new { error = "This request is an approval, not a question." });
    if (request.Answers is null || request.Answers.Count == 0) return Results.BadRequest(new { error = "Please answer every question." });
    return await codex.ResolveUserInputAsync(key, request.Answers)
        ? Results.Ok()
        : Results.BadRequest(new { error = "Please answer every question before sending." });
});

app.MapPost("/api/pending/{key}/elicitation", async (
    string key,
    ElicitationResponse request,
    CodexAppServer codex) =>
{
    if (!codex.Pending.TryGetValue(key, out var pending))
        return Results.NotFound(new { error = "This request has already been handled." });
    if (!ElicitationProtocol.IsElicitationRequest(pending))
        return Results.BadRequest(new { error = "This pending item is not an MCP form." });
    if (request.Action is not ("accept" or "decline" or "cancel"))
        return Results.BadRequest(new { error = "Invalid elicitation action." });
    try
    {
        return await codex.ResolveMcpElicitationAsync(
                key,
                request.Action,
                request.Content,
                request.Persistence)
            ? Results.Ok()
            : Results.NotFound(new { error = "This request has already been handled." });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/files/upload", async (HttpContext context, FileTransferService files) =>
{
    if (context.Request.ContentLength is > FileTransferService.MaximumRequestBytes)
        return Results.BadRequest(new { error = $"One upload request must be no larger than {FileTransferService.MaximumRequestBytes / 1024 / 1024} MiB." });
    var threadId = context.Request.Query["threadId"].ToString();
    if (string.IsNullOrWhiteSpace(threadId)) return Results.BadRequest(new { error = "A task id is required for uploads." });
    if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "Use multipart/form-data to upload files." });
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    return Results.Ok(new { files = await files.StoreUploadsAsync(form.Files, threadId, context.RequestAborted) });
});

app.MapPost("/api/files/register", async (
    ExistingFileRequest request,
    CodexAppServer codex,
    FileTransferService files,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ThreadId)) return Results.BadRequest(new { error = "A task id is required." });
    var cwd = await ApiHelpers.GetThreadCwdAsync(codex, request.ThreadId, cancellationToken);
    try
    {
        return Results.Ok(files.RegisterExisting(request.Path, cwd, request.ThreadId));
    }
    catch (Exception original) when (original is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
    {
        var referenced = await ThreadArtifactResolver.ResolveAsync(
            codex,
            request.ThreadId,
            request.Path,
            cancellationToken);
        if (referenced is not null)
            return Results.Ok(await files.StoreThreadDeliveryAsync(referenced, request.ThreadId, cancellationToken));

        return original switch
        {
            UnauthorizedAccessException => Results.Json(
                new { error = "The file is outside the task workspace and was not referenced as a task delivery." },
                statusCode: StatusCodes.Status403Forbidden),
            DirectoryNotFoundException => Results.NotFound(new
            {
                error = "The task workspace no longer exists and this file reference could not be resolved."
            }),
            _ => Results.NotFound(new
            {
                error = "The file reference could not be resolved from this task. The source file may still exist elsewhere."
            })
        };
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/files", (string? threadId, FileTransferService files) => Results.Ok(new { files = files.List(threadId) }));

app.MapGet("/api/files/{id}/download", (string id, HttpContext context, FileTransferService files) =>
{
    var file = files.ResolveDownload(id);
    context.Response.Headers.CacheControl = "no-store";
    return Results.File(file.Path, file.ContentType, file.Descriptor.Name, enableRangeProcessing: true);
});

IResult ViewLeasedFile(string id, string? subpath, HttpContext context, FileTransferService files)
{
    var file = files.ResolveView(id, subpath);
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Content-Security-Policy"] =
        "sandbox allow-scripts allow-forms allow-modals allow-downloads; default-src 'self' data: blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    return Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
}

app.MapGet("/api/files/{id}/view", ViewLeasedFile);
app.MapGet("/api/files/{id}/view/{**subpath}", ViewLeasedFile);
app.MapDelete("/api/files/{id}", (string id, FileTransferService files) => files.Delete(id) ? Results.Ok() : Results.NotFound());

app.MapGet("/api/skills", async (string? cwd, bool? forceReload, CodexAppServer codex, CancellationToken cancellationToken) =>
    Results.Ok(await codex.CallAsync("skills/list", new
    {
        cwds = new[] { string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd },
        forceReload = forceReload ?? false
    }, cancellationToken)));

app.MapGet("/api/tools", async (
    string? threadId,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    CancellationToken cancellationToken) =>
    Results.Ok(await ApiHelpers.GetToolsAsync(codex, threadId, runtimeStates, cancellationToken)));

app.MapGet("/api/threads/{id}/goal", async (
    string id,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    CancellationToken cancellationToken) =>
{
    // Goals are live app-server state. Do not resume a released task merely to
    // render a read-only panel; the next mutating action will acquire access.
    if (!codex.HasThreadAccess(id))
        return Results.Ok(new { goal = (object?)null, access = "released" });
    return Results.Ok(await codex.CallAsync("thread/goal/get", new { threadId = id }, cancellationToken));
});

app.MapPut("/api/threads/{id}/goal", async (
    string id,
    GoalUpdate request,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    CancellationToken cancellationToken) =>
{
    if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } conflict) return conflict;
    ApiHelpers.ValidateGoal(request);
    await codex.EnsureThreadLoadedAsync(id, cancellationToken);
    try
    {
        return Results.Ok(await codex.CallAsync("thread/goal/set", new
        {
            threadId = id,
            objective = request.Objective,
            status = request.Status,
            tokenBudget = request.TokenBudget
        }, cancellationToken));
    }
    finally { codex.ScheduleThreadAccessRelease(id); }
});

app.MapDelete("/api/threads/{id}/goal", async (
    string id,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    CancellationToken cancellationToken) =>
{
    if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } conflict) return conflict;
    await codex.EnsureThreadLoadedAsync(id, cancellationToken);
    try
    {
        return Results.Ok(await codex.CallAsync("thread/goal/clear", new { threadId = id }, cancellationToken));
    }
    finally { codex.ScheduleThreadAccessRelease(id); }
});

app.MapPost("/api/threads/{id}/compact", async (
    string id,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    CancellationToken cancellationToken) =>
{
    if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } conflict) return conflict;
    await codex.EnsureThreadLoadedAsync(id, cancellationToken);
    try
    {
        return Results.Ok(await codex.CallAsync("thread/compact/start", new { threadId = id }, cancellationToken));
    }
    finally { codex.ScheduleThreadAccessRelease(id); }
});

app.MapGet("/api/commands", () => Results.Ok(ApiHelpers.CommandCatalog));
app.MapPost("/api/threads/{id}/commands", async (
    string id,
    CommandRequest request,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    CancellationToken cancellationToken) =>
{
    var command = (request.Command ?? "").Trim().TrimStart('/').ToLowerInvariant();
    if (command.Length == 0) return Results.BadRequest(new { error = "A command is required." });
    switch (command)
    {
        case "status":
            return Results.Ok(new
            {
                thread = await codex.CallAsync("thread/read", new { threadId = id, includeTurns = false }, cancellationToken),
                runtimeState = runtimeStates.Get(id)
            });
        case "skills":
        {
            var cwd = await ApiHelpers.GetThreadCwdAsync(codex, id, cancellationToken);
            return Results.Ok(await codex.CallAsync("skills/list", new { cwds = new[] { cwd }, forceReload = false }, cancellationToken));
        }
        case "tools":
        case "mcp":
            return Results.Ok(await ApiHelpers.GetToolsAsync(codex, id, runtimeStates, cancellationToken));
        case "compact":
            if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } compactConflict) return compactConflict;
            await codex.EnsureThreadLoadedAsync(id, cancellationToken);
            try
            {
                return Results.Ok(await codex.CallAsync("thread/compact/start", new { threadId = id }, cancellationToken));
            }
            finally { codex.ScheduleThreadAccessRelease(id); }
        case "goal":
        case "go":
        {
            var arguments = request.Arguments?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(request.Objective) && string.IsNullOrWhiteSpace(request.Status) && request.TokenBudget is null && arguments.Length == 0)
            {
                if (!codex.HasThreadAccess(id))
                    return Results.Ok(new { goal = (object?)null, access = "released" });
                return Results.Ok(await codex.CallAsync("thread/goal/get", new { threadId = id }, cancellationToken));
            }
            if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } goalConflict) return goalConflict;
            await codex.EnsureThreadLoadedAsync(id, cancellationToken);
            if (arguments.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Results.Ok(await codex.CallAsync("thread/goal/clear", new { threadId = id }, cancellationToken));
                }
                finally { codex.ScheduleThreadAccessRelease(id); }
            }
            var status = request.Status;
            var objective = request.Objective;
            if (string.IsNullOrWhiteSpace(status) && string.IsNullOrWhiteSpace(objective))
            {
                status = arguments.ToLowerInvariant() switch
                {
                    "pause" or "paused" => "paused",
                    "resume" or "active" => "active",
                    "complete" or "completed" => "complete",
                    _ => null
                };
                if (status is null) objective = arguments;
            }
            var update = new GoalUpdate(objective, status, request.TokenBudget);
            ApiHelpers.ValidateGoal(update);
            try
            {
                return Results.Ok(await codex.CallAsync("thread/goal/set", new
                {
                    threadId = id,
                    objective = update.Objective,
                    status = update.Status,
                    tokenBudget = update.TokenBudget
                }, cancellationToken));
            }
            finally { codex.ScheduleThreadAccessRelease(id); }
        }
        default:
            return Results.BadRequest(new { error = $"Unknown command: /{command}" });
    }
});

app.MapPost("/api/local-links/resolve", async (HttpContext context, LocalLinkRequest request, LocalPortRelayService relay) =>
{
    if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var target) || !LocalPortRelayService.IsLocalDevelopmentUrl(target))
        return Results.BadRequest(new { error = "Only absolute localhost or 127.0.0.1 HTTP links can be mapped." });
    var listenAddress = context.Connection.LocalIpAddress;
    var clientAddress = context.Connection.RemoteIpAddress;
    if (listenAddress is null || clientAddress is null) return Results.BadRequest(new { error = "The active network interface could not be identified." });
    if (listenAddress.IsIPv4MappedToIPv6) listenAddress = listenAddress.MapToIPv4();
    if (clientAddress.IsIPv4MappedToIPv6) clientAddress = clientAddress.MapToIPv4();
    try
    {
        return Results.Ok(await relay.ResolveAsync(
            target,
            listenAddress,
            clientAddress,
            listenAddress.ToString(),
            context.Connection.LocalPort,
            context.RequestAborted));
    }
    catch (NotSupportedException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (SocketException ex) { return Results.Json(new { error = $"The relay port could not be opened: {ex.Message}" }, statusCode: 502); }
    catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});

app.MapGet("/api/projects", (ProjectScanner projects) => projects.Scan());
app.MapGet("/api/processes", () => ProcessApi.List());
app.MapGet("/api/diagnostics/console-launches", (int? limit, ConsoleLaunchAuditService audit) =>
{
    var snapshot = audit.Snapshot(Math.Clamp(limit ?? 100, 1, 256));
    return Results.Ok(new
    {
        supported = snapshot.IsSupported,
        capturing = snapshot.IsRunning,
        status = snapshot.Status,
        generatedAt = snapshot.GeneratedAt,
        events = snapshot.Events.Select(item =>
        {
            var chain = new List<ConsoleLaunchAuditProcess>();
            foreach (var process in item.ParentChain.Reverse().Append(item.CommandProcess))
            {
                if (chain.All(existing => existing.ProcessId != process.ProcessId)) chain.Add(process);
            }
            if (chain.Count == 0 || chain[^1].ProcessId != item.WindowProcess.ProcessId)
                chain.Add(item.WindowProcess);
            return new
            {
                id = item.Id.ToString(),
                firstSeenAt = item.FirstObservedAt,
                lastSeenAt = item.ObservedAt,
                count = item.RepeatCount,
                intervalSeconds = item.IntervalMilliseconds is null ? (double?)null : Math.Round(item.IntervalMilliseconds.Value / 1000, 2),
                averageIntervalSeconds = item.AverageIntervalMilliseconds is null ? (double?)null : Math.Round(item.AverageIntervalMilliseconds.Value / 1000, 2),
                item.Classification,
                item.Explanation,
                item.WindowTitle,
                item.WindowClass,
                windowProcess = ApiHelpers.ConsoleAuditProcess(item.WindowProcess),
                commandProcess = ApiHelpers.ConsoleAuditProcess(item.CommandProcess),
                sourceProcess = item.CandidateLaunchingProcess is null ? null : ApiHelpers.ConsoleAuditProcess(item.CandidateLaunchingProcess),
                chain = chain.Select(ApiHelpers.ConsoleAuditProcess).ToArray(),
                item.ParentChainComplete,
                commandLine = ApiHelpers.RedactCommandLine(item.CommandLine),
                executablePath = item.ExecutablePath
            };
        }).ToArray()
    });
});
app.MapPost("/api/diagnostics/console-audit/capture", async (
    ConsoleAuditCaptureRequest request,
    ConsoleLaunchAuditService audit,
    CancellationToken cancellationToken) =>
{
    if (!audit.IsSupported) return Results.BadRequest(new { error = "Console launch auditing is supported on Windows only." });
    if (request.Enabled) await audit.StartAsync(cancellationToken);
    else await audit.StopAsync(cancellationToken);
    return Results.Ok(new { supported = audit.IsSupported, capturing = audit.IsRunning });
});
app.MapPost("/api/diagnostics/console-audit/clear", (
    ConsoleAuditClearRequest request,
    ConsoleLaunchAuditService audit) =>
{
    if (!string.Equals(request.Confirmation, "CLEAR AUDIT", StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Type CLEAR AUDIT to confirm." });
    audit.Clear();
    return Results.Ok(new { cleared = true });
});
app.MapPost("/api/processes/{pid:int}/stop", (
    int pid,
    StopProcessRequest request,
    ConsoleLaunchAuditService audit) =>
{
    if (request.Confirmation != $"STOP {pid}") return Results.BadRequest(new { error = $"Type STOP {pid} to confirm." });
    if (pid == Environment.ProcessId) return Results.BadRequest(new { error = "The bridge cannot stop itself." });
    try
    {
        using var process = Process.GetProcessById(pid);
        string? path = null;
        DateTimeOffset? startedAt = null;
        try { path = process.MainModule?.FileName; } catch { }
        try { startedAt = process.StartTime; } catch { }
        if (!ProcessApi.IsAllowed(process.ProcessName) &&
            !audit.MatchesObservedSource(pid, process.ProcessName, path, startedAt))
            return Results.BadRequest(new { error = "This process is neither managed nor a currently observed popup source." });
        process.Kill(entireProcessTree: true);
        return Results.Ok();
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapFallbackToFile("index.html");

var pairing = app.Services.GetRequiredService<PairingService>();
if (bridgeBinding.AdministratorMode)
{
    foreach (var url in bridgeBinding.Urls) Console.WriteLine($"Codex LAN Console: {url}");
}
else
{
    Console.WriteLine($"Codex LAN Console: {LocalAddress.GetConsoleUrl(configuredBridgeUrls)}");
}
if (pairing.IsTemporaryPairingOpen)
    Console.WriteLine($"Pairing code: {pairing.Code} (expires {pairing.CodeExpiresAt:O}, or immediately after successful pairing)");
else if (pairing.HasPersistentAdministratorCode)
    Console.WriteLine("Persistent administrator sign-in: enabled (the code is never printed or stored in plaintext)");
else
    Console.WriteLine("Pairing: closed (use the local Windows manager to add another Administrator device)");
Console.WriteLine($"Pairing details: {pairing.PairingFile}");
app.Run();
GC.KeepAlive(singleInstanceMutex);

record PairRequest(string? Code, string? DeviceName);
record BrowserSettingsRequest(bool AutoStartWithBridge);
record ThreadCreate(
    string? Cwd,
    string? Permissions,
    string? ApprovalPolicy,
    string? ApprovalsReviewer);
record MessageRequest(
    string? Text,
    string? ClientUserMessageId,
    string[]? AttachmentIds,
    SkillReference[]? Skills,
    string? Permissions,
    string? ApprovalPolicy,
    string? ApprovalsReviewer,
    string? Model,
    string? ReasoningEffort,
    bool? BrowserRequired);
record SteerRequest(
    string? TurnId,
    string? Text,
    string? ClientUserMessageId,
    string[]? AttachmentIds,
    SkillReference[]? Skills,
    string? Permissions,
    string? ApprovalPolicy,
    string? ApprovalsReviewer,
    string? Model,
    string? ReasoningEffort,
    bool? BrowserRequired);
record SkillReference(string Name, string? Path);
record ExistingFileRequest(string ThreadId, string Path);
record GoalUpdate(string? Objective, string? Status, long? TokenBudget);
record CommandRequest(string? Command, string? Arguments, string? Objective, string? Status, long? TokenBudget);
record ApprovalDecision(string Decision);
record ApprovalBatchDecision(string Decision);
record ApprovalSettingsUpdate(bool AutoApproveAll, string? Confirmation);
record UserInputResponse(Dictionary<string, UserInputAnswer> Answers);
record ElicitationResponse(string Action, JsonElement? Content, string? Persistence);
record LocalLinkRequest(string Url);
record StopProcessRequest(string Confirmation);
record ConsoleAuditCaptureRequest(bool Enabled);
record ConsoleAuditClearRequest(string Confirmation);
static class CpuGuardApi
{
    public static bool IsLoopbackRequest(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    public static bool IsAuthorizedLocalControl(HttpContext context, LocalControlTokenStore tokens) =>
        IsLoopbackRequest(context) && tokens.Validate(context.Request.Headers[LocalControlTokenStore.HeaderName].ToString());

    public static string FormatStatus(CpuHealthSnapshot snapshot)
    {
        static string Number(double? value, string suffix) => value.HasValue ? $"{value.Value:F0}{suffix}" : "n/a";
        var telemetry = snapshot.Telemetry;
        return string.Join(Environment.NewLine,
            $"CPU guard: {snapshot.Mode} / {snapshot.State}",
            $"P-core: load {Number(telemetry?.PerformanceCoreLoadPercent, "%")}, " +
            $"actual {Number(telemetry?.PerformanceCoreFrequencyMhz, " MHz")}, " +
            $"performance {Number(telemetry?.PerformanceCorePerformancePercent, "%")}",
            $"Power: {(telemetry is null ? "n/a" : telemetry.OnAcPower ? "AC" : "battery")}",
            $"Summary: {snapshot.Summary}");
    }
}

static class ApiHelpers
{
    private static readonly Regex ConsoleAuditSecret = new(
        "(?ix)(authorization\\s*[:=]\\s*(?:bearer\\s+)?|(?:token|api[-_]?key|password|passwd|secret)\\s*[:=]\\s*)[^\\s\\\"']+|\\b(?:ghp_|github_pat_|sk-)[A-Za-z0-9_-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<string> GoalStatuses = new(StringComparer.Ordinal)
    {
        "active", "paused", "blocked", "usageLimited", "budgetLimited", "complete"
    };

    public static readonly object[] CommandCatalog =
    {
        new { command = "/status", description = "Show the current task state." },
        new { command = "/skills", description = "List skills available in this task." },
        new { command = "/tools", description = "List connected tools and accessible apps." },
        new { command = "/goal", description = "Read or update the task goal." },
        new { command = "/compact", description = "Compact older context for this task." }
    };

    public static object ConsoleAuditProcess(ConsoleLaunchAuditProcess process) => new
    {
        processId = process.ProcessId,
        parentProcessId = process.ParentProcessId,
        process.Name,
        process.ExecutablePath,
        commandLine = RedactCommandLine(process.CommandLine),
        process.StartedAt
    };

    public static string? RedactCommandLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try
        {
            return ConsoleAuditSecret.Replace(value, match =>
            {
                var text = match.Value;
                var separator = Math.Max(text.LastIndexOf('='), text.LastIndexOf(':'));
                return separator >= 0 ? text[..(separator + 1)] + "[REDACTED]" : "[REDACTED]";
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED: command was too complex to scan safely]";
        }
    }

    private const int MaximumMobileStringCharacters = 64 * 1024;
    private const int MaximumMobileCollectionItems = 256;
    private const int MaximumMobileJsonDepth = 24;
    private static readonly HashSet<string> SuppressedMobileFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "aggregatedOutput", "arguments", "command", "commandLine", "diff",
        "encrypted_content", "encryptedContent", "input", "output", "raw",
        "rawOutput", "result", "stderr", "stdout", "toolResult"
    };

    public static IResult? ExternalActiveConflict(ThreadRuntimeStateStore runtimeStates, string threadId)
    {
        if (!runtimeStates.IsExternallyOwnedActive(threadId)) return null;
        return Results.Conflict(new
        {
            error = "This turn belongs to another Codex app-server connection. Start a mobile-owned task, or continue this task from the phone after the current turn finishes.",
            kind = "externalThreadActive",
            threadId
        });
    }

    public static string? LatestTurnId(JsonElement newestFirstTurnPage)
    {
        if (newestFirstTurnPage.ValueKind != JsonValueKind.Object ||
            !newestFirstTurnPage.TryGetProperty("data", out var turns) ||
            turns.ValueKind != JsonValueKind.Array ||
            turns.GetArrayLength() == 0) return null;
        return Text(turns[0], "id");
    }

    public static object PagedThread(
        JsonElement metadataResult,
        JsonElement turnPage,
        string fallbackId,
        ThreadRuntimeSnapshot? runtimeState,
        LiveThreadSnapshot? liveSnapshot = null,
        JsonElement recentItemsPage = default,
        string? recentItemsTurnId = null,
        BridgeTurnRecoverySnapshot? recoveryState = null,
        IReadOnlyList<ThreadCommandReceipt>? commandReceipts = null)
    {
        var thread = metadataResult.TryGetProperty("thread", out var nested) ? nested : metadataResult;
        var turns = turnPage.ValueKind == JsonValueKind.Object &&
                    turnPage.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Take(20).Select(BoundForMobile).ToArray()
            : Array.Empty<JsonElement>();
        // The app-server page is newest-first; the conversation view renders oldest-first.
        Array.Reverse(turns);
        var mergedTurns = MergeRecentItems(turns, recentItemsPage, recentItemsTurnId);
        mergedTurns = MergeLiveTurns(mergedTurns, liveSnapshot?.Turns ?? Array.Empty<JsonElement>());
        var nextCursor = Text(turnPage, "nextCursor");
        var hasEarlier = !string.IsNullOrEmpty(nextCursor);
        return new
        {
            thread = new
            {
                id = Text(thread, "id") ?? fallbackId,
                name = Text(thread, "name"),
                preview = Text(thread, "preview"),
                cwd = Text(thread, "cwd"),
                status = Element(thread, "status"),
                turns = mergedTurns
            },
            // Keep the legacy fields in the response. Cursor-aware clients use nextCursor;
            // totalTurnsExact explains why the page-local count is not a history-wide count.
            start = 0,
            totalTurns = mergedTurns.Length,
            totalTurnsExact = !hasEarlier,
            hasEarlier,
            nextCursor,
            backwardsCursor = Text(turnPage, "backwardsCursor"),
            runtimeState,
            turnRecovery = recoveryState,
            commandOutbox = commandReceipts ?? Array.Empty<ThreadCommandReceipt>(),
            recentItemsTruncated = !string.IsNullOrWhiteSpace(Text(recentItemsPage, "nextCursor")),
            liveRevision = liveSnapshot?.Revision ?? 0
        };
    }

    private static JsonElement[] MergeRecentItems(
        IReadOnlyList<JsonElement> persistedTurns,
        JsonElement recentItemsPage,
        string? expectedTurnId)
    {
        var result = persistedTurns.Select(turn => turn.Clone()).ToArray();
        if (string.IsNullOrWhiteSpace(expectedTurnId) ||
            recentItemsPage.ValueKind != JsonValueKind.Object ||
            !recentItemsPage.TryGetProperty("data", out var entries) ||
            entries.ValueKind != JsonValueKind.Array) return result;

        var recent = entries.EnumerateArray()
            .Take(64)
            .Reverse()
            .Where(entry => string.IsNullOrWhiteSpace(Text(entry, "turnId")) ||
                            Text(entry, "turnId")!.Equals(expectedTurnId, StringComparison.Ordinal))
            .Select(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("item", out var item)
                ? ProjectRecentItem(item)
                : ProjectRecentItem(entry))
            .ToArray();
        if (recent.Length == 0) return result;

        for (var index = 0; index < result.Length; index++)
        {
            if (!string.Equals(Text(result[index], "id"), expectedTurnId, StringComparison.Ordinal)) continue;
            result[index] = ReplaceTurnItemsWithRecentTail(result[index], recent);
            break;
        }
        return result;
    }

    private static JsonElement ReplaceTurnItemsWithRecentTail(
        JsonElement turn,
        IReadOnlyList<JsonElement> recentItems)
    {
        var items = new List<JsonElement>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var recentTurn = JsonSerializer.SerializeToElement(new { items = recentItems });
        var recentKeys = ItemKeys(recentTurn);
        var recentMessages = MessageKeys(recentTurn);
        AddSummaryItems(
            turn, items, positions, recentKeys, recentMessages,
            item => !string.Equals(Text(item, "type"), "agentMessage", StringComparison.Ordinal));
        AddItems(recentTurn, items, positions, replace: true);
        AddSummaryItems(
            turn, items, positions, recentKeys, recentMessages,
            item => string.Equals(Text(item, "type"), "agentMessage", StringComparison.Ordinal));
        return JsonSerializer.SerializeToElement(new
        {
            id = Text(turn, "id"),
            items = items.ToArray(),
            itemsView = "recentFull",
            status = Element(turn, "status"),
            error = Element(turn, "error"),
            startedAt = Element(turn, "startedAt"),
            completedAt = Element(turn, "completedAt"),
            durationMs = Element(turn, "durationMs")
        });
    }

    private static JsonElement ProjectRecentItem(JsonElement item)
    {
        var type = Text(item, "type") ?? "tool";
        if (type is "userMessage" or "agentMessage") return BoundForMobile(item);

        object projected;
        if (type.Equals("reasoning", StringComparison.Ordinal))
        {
            projected = new
            {
                id = Text(item, "id"),
                type,
                status = Element(item, "status"),
                summary = Element(item, "summary") ?? Element(item, "content"),
                createdAt = Element(item, "createdAt"),
                updatedAt = Element(item, "updatedAt")
            };
        }
        else if (type.Equals("plan", StringComparison.Ordinal))
        {
            projected = new
            {
                id = Text(item, "id"),
                type,
                status = Element(item, "status"),
                text = Text(item, "text") ?? Text(item, "message"),
                content = Element(item, "content"),
                createdAt = Element(item, "createdAt"),
                updatedAt = Element(item, "updatedAt")
            };
        }
        else if (type.Contains("fileChange", StringComparison.OrdinalIgnoreCase))
        {
            var changes = item.TryGetProperty("changes", out var source) && source.ValueKind == JsonValueKind.Array
                ? source.EnumerateArray().Take(12).Select(change => new
                {
                    path = Text(change, "path") ?? Text(change, "filePath"),
                    kind = Text(change, "kind") ?? Text(change, "type")
                }).Where(change => !string.IsNullOrWhiteSpace(change.path)).ToArray()
                : Array.Empty<object>();
            projected = new
            {
                id = Text(item, "id"),
                callId = Text(item, "callId") ?? Text(item, "call_id"),
                type,
                status = Element(item, "status"),
                changes,
                createdAt = Element(item, "createdAt"),
                updatedAt = Element(item, "updatedAt")
            };
        }
        else
        {
            projected = new
            {
                id = Text(item, "id"),
                callId = Text(item, "callId") ?? Text(item, "call_id"),
                type,
                status = Element(item, "status"),
                name = Text(item, "name"),
                tool = Text(item, "tool"),
                server = Text(item, "server"),
                method = Text(item, "method"),
                createdAt = Element(item, "createdAt"),
                updatedAt = Element(item, "updatedAt")
            };
        }
        return BoundForMobile(JsonSerializer.SerializeToElement(projected));
    }

    private static JsonElement[] MergeLiveTurns(
        IReadOnlyList<JsonElement> persistedTurns,
        IReadOnlyList<JsonElement> liveTurns)
    {
        var result = persistedTurns.Select(turn => turn.Clone()).ToList();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < result.Count; index++)
        {
            var id = Text(result[index], "id");
            if (!string.IsNullOrWhiteSpace(id)) positions[id] = index;
        }

        var newestLiveId = liveTurns.Count == 0 ? null : Text(liveTurns[^1], "id");
        var newestLiveAlreadyPersisted = !string.IsNullOrWhiteSpace(newestLiveId) &&
                                         positions.ContainsKey(newestLiveId);
        foreach (var live in liveTurns)
        {
            var id = Text(live, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (positions.TryGetValue(id, out var position)) result[position] = MergeTurn(result[position], live);
            else if (!newestLiveAlreadyPersisted && string.Equals(id, newestLiveId, StringComparison.Ordinal))
            {
                positions[id] = result.Count;
                result.Add(live.Clone());
            }
        }
        return result.TakeLast(20).ToArray();
    }

    private static JsonElement MergeTurn(JsonElement persisted, JsonElement live)
    {
        var items = new List<JsonElement>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var persistedView = Text(persisted, "itemsView") ?? "summary";
        if (persistedView is "recentFull" or "full")
        {
            // thread/items/list is the canonical timeline. Live events may update an
            // item or extend the tail, but must never move an existing reply.
            AddItems(persisted, items, positions, replace: false);
            AddItems(live, items, positions, replace: true);
        }
        else
        {
            // A summary normally contains only the first user message and latest
            // assistant reply. Keep summary-only leading items, use the live tail for
            // process order, then append a summary-only final reply.
            var liveKeys = ItemKeys(live);
            var liveMessages = MessageKeys(live);
            AddSummaryItems(
                persisted, items, positions, liveKeys, liveMessages,
                item => !string.Equals(Text(item, "type"), "agentMessage", StringComparison.Ordinal));
            AddItems(live, items, positions, replace: true);
            AddSummaryItems(
                persisted, items, positions, liveKeys, liveMessages,
                item => string.Equals(Text(item, "type"), "agentMessage", StringComparison.Ordinal));
        }
        return JsonSerializer.SerializeToElement(new
        {
            id = Text(live, "id") ?? Text(persisted, "id"),
            items = items.ToArray(),
            itemsView = "full",
            status = Element(live, "status") ?? Element(persisted, "status"),
            error = Element(live, "error") ?? Element(persisted, "error"),
            startedAt = Element(live, "startedAt") ?? Element(persisted, "startedAt"),
            completedAt = Element(live, "completedAt") ?? Element(persisted, "completedAt"),
            durationMs = Element(live, "durationMs") ?? Element(persisted, "durationMs")
        });
    }

    private static void AddItems(
        JsonElement turn,
        List<JsonElement> items,
        Dictionary<string, int> positions,
        bool replace)
    {
        if (!turn.TryGetProperty("items", out var source) || source.ValueKind != JsonValueKind.Array) return;
        foreach (var item in source.EnumerateArray())
        {
            var key = ItemKey(item);
            if (string.IsNullOrWhiteSpace(key) || !positions.TryGetValue(key, out var position))
            {
                if (!string.IsNullOrWhiteSpace(key)) positions[key] = items.Count;
                items.Add(BoundForMobile(item));
            }
            else if (replace)
            {
                items[position] = MergeItem(items[position], item);
            }
        }
    }

    private static void AddSummaryItems(
        JsonElement turn,
        List<JsonElement> items,
        Dictionary<string, int> positions,
        IReadOnlySet<string> liveKeys,
        IReadOnlySet<string> liveMessages,
        Func<JsonElement, bool> predicate)
    {
        if (!turn.TryGetProperty("items", out var source) || source.ValueKind != JsonValueKind.Array) return;
        foreach (var item in source.EnumerateArray())
        {
            if (!predicate(item)) continue;
            var key = ItemKey(item);
            var message = MessageKey(item);
            if (!string.IsNullOrWhiteSpace(key) && liveKeys.Contains(key)) continue;
            if (IsWeakSummaryItem(item) &&
                !string.IsNullOrWhiteSpace(message) &&
                liveMessages.Contains(message)) continue;
            if (!string.IsNullOrWhiteSpace(key) && positions.ContainsKey(key)) continue;
            if (!string.IsNullOrWhiteSpace(key)) positions[key] = items.Count;
            items.Add(BoundForMobile(item));
        }
    }

    private static HashSet<string> ItemKeys(JsonElement turn)
    {
        if (!turn.TryGetProperty("items", out var source) || source.ValueKind != JsonValueKind.Array)
            return new HashSet<string>(StringComparer.Ordinal);
        return source.EnumerateArray()
            .Select(ItemKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> MessageKeys(JsonElement turn)
    {
        if (!turn.TryGetProperty("items", out var source) || source.ValueKind != JsonValueKind.Array)
            return new HashSet<string>(StringComparer.Ordinal);
        return source.EnumerateArray()
            .Select(MessageKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? ItemKey(JsonElement item)
    {
        var callId = Text(item, "callId") ?? Text(item, "call_id");
        if (!string.IsNullOrWhiteSpace(callId)) return $"call:{callId}";
        var id = Text(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return MessageKey(item);
        const string externalPrefix = "external-call-";
        if (id.StartsWith(externalPrefix, StringComparison.Ordinal))
            return $"call:{id[externalPrefix.Length..]}";
        if (id.StartsWith("call_", StringComparison.Ordinal) ||
            id.StartsWith("call-", StringComparison.Ordinal) ||
            id.StartsWith("exec-", StringComparison.Ordinal))
            return $"call:{id}";
        return $"id:{id}";
    }

    private static bool IsWeakSummaryItem(JsonElement item)
    {
        var id = Text(item, "id");
        return string.IsNullOrWhiteSpace(id) ||
               id.StartsWith("item-", StringComparison.Ordinal) &&
               int.TryParse(id.AsSpan("item-".Length), out _);
    }

    private static string? MessageKey(JsonElement item)
    {
        var type = Text(item, "type");
        if (type is not ("userMessage" or "agentMessage")) return null;
        var text = Text(item, "text") ?? Text(item, "message");
        if (string.IsNullOrWhiteSpace(text) &&
            item.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            text = string.Join('\n', content.EnumerateArray()
                .Select(part => part.ValueKind == JsonValueKind.String ? part.GetString() : Text(part, "text"))
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var phase = type == "agentMessage" ? Text(item, "phase") ?? "" : "";
        return $"{type}:{phase}:{normalized}";
    }

    private static JsonElement MergeItem(JsonElement persisted, JsonElement live)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in persisted.EnumerateObject()) properties[property.Name] = property.Value.Clone();
        foreach (var property in live.EnumerateObject()) properties[property.Name] = property.Value.Clone();
        if (Text(persisted, "id") is { } stableId)
            properties["id"] = JsonSerializer.SerializeToElement(stableId);
        if (string.Equals(Text(live, "type") ?? Text(persisted, "type"), "agentMessage", StringComparison.Ordinal))
        {
            var before = Text(persisted, "text") ?? "";
            var after = Text(live, "text") ?? "";
            properties["text"] = JsonSerializer.SerializeToElement(after.Length >= before.Length ? after : before);
        }
        foreach (var name in new[] { "summary", "content" })
        {
            var before = Element(persisted, name);
            var after = Element(live, name);
            if (before is { } left && (after is null || left.GetRawText().Length > after.Value.GetRawText().Length))
                properties[name] = left;
        }
        return BoundForMobile(JsonSerializer.SerializeToElement(properties));
    }

    public static object LegacyThreadPage(
        JsonElement result,
        string fallbackId,
        int? before,
        int pageSize,
        ThreadRuntimeSnapshot? runtimeState)
    {
        var thread = result.TryGetProperty("thread", out var nested) ? nested : result;
        var turnArray = thread.TryGetProperty("turns", out var candidate) && candidate.ValueKind == JsonValueKind.Array
            ? candidate
            : default;
        var totalTurns = turnArray.ValueKind == JsonValueKind.Array ? turnArray.GetArrayLength() : 0;
        var end = Math.Clamp(before ?? totalTurns, 0, totalTurns);
        var start = Math.Max(0, end - pageSize);
        var turns = turnArray.ValueKind == JsonValueKind.Array
            ? turnArray.EnumerateArray().Skip(start).Take(end - start).Select(BoundForMobile).ToArray()
            : Array.Empty<JsonElement>();
        return new
        {
            thread = new
            {
                id = Text(thread, "id") ?? fallbackId,
                name = Text(thread, "name"),
                preview = Text(thread, "preview"),
                cwd = Text(thread, "cwd"),
                status = Element(thread, "status"),
                turns
            },
            start,
            totalTurns,
            totalTurnsExact = true,
            hasEarlier = start > 0,
            nextCursor = (string?)null,
            backwardsCursor = (string?)null,
            runtimeState
        };
    }

    public static bool IsUnmaterializedThread(CodexRpcException exception) =>
        exception.IsUnmaterializedThread;

    private static JsonElement BoundForMobile(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteBounded(writer, element, 0, null);
        }
        buffer.Position = 0;
        using var document = JsonDocument.Parse(buffer);
        return document.RootElement.Clone();
    }

    private static void WriteBounded(Utf8JsonWriter writer, JsonElement element, int depth, string? propertyName)
    {
        if (depth >= MaximumMobileJsonDepth)
        {
            writer.WriteStringValue("[内容层级过深，已省略]");
            return;
        }

        if (IsLargeInlineImage(element))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", "[大体积图片内容未在手机历史中重复加载]");
            writer.WriteEndObject();
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var propertyCount = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (SuppressedMobileFields.Contains(property.Name)) continue;
                    if (propertyCount++ >= MaximumMobileCollectionItems) break;
                    writer.WritePropertyName(property.Name);
                    WriteBounded(writer, property.Value, depth + 1, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var itemCount = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (itemCount++ >= MaximumMobileCollectionItems) break;
                    WriteBounded(writer, item, depth + 1, propertyName);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? "";
                if (LooksLikeLargeInlineBinary(value))
                {
                    writer.WriteStringValue("[大体积二进制内容已省略]");
                    break;
                }
                writer.WriteStringValue(value.Length > MaximumMobileStringCharacters
                    ? value[..MaximumMobileStringCharacters] + "\n[内容过长，手机端仅显示前段]"
                    : value);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                element.WriteTo(writer);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static bool IsLargeInlineImage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("url", out var url) ||
            url.ValueKind != JsonValueKind.String) return false;
        var typeName = type.GetString();
        return typeName is not null && typeName.Contains("image", StringComparison.OrdinalIgnoreCase) &&
               LooksLikeLargeInlineBinary(url.GetString() ?? "");
    }

    private static bool LooksLikeLargeInlineBinary(string value)
    {
        if (value.Length <= 4096) return false;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
            value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        // Rollout payloads sometimes contain bare Base64 without a data: prefix.
        // Sample a bounded prefix so detection itself remains cheap.
        var sampleLength = Math.Min(value.Length, 8192);
        var allowed = 0;
        for (var index = 0; index < sampleLength; index++)
        {
            var character = value[index];
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')
                allowed++;
        }
        return allowed >= sampleLength * 0.995;
    }

    private static JsonElement? Element(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.Clone()
            : null;

    public static async Task<string> GetThreadCwdAsync(CodexAppServer codex, string threadId, CancellationToken cancellationToken)
    {
        if (codex.TryGetKnownThreadCwd(threadId, out var knownCwd)) return knownCwd;
        JsonElement result = default;
        var retryDelays = new[] { 100, 300, 800, 1600 };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                result = await codex.CallAsync("thread/read", new { threadId, includeTurns = false }, cancellationToken);
                break;
            }
            catch (CodexRpcException ex) when (
                attempt < retryDelays.Length &&
                ex.Code == -32603 &&
                ex.Message.Contains("rollout", StringComparison.OrdinalIgnoreCase) &&
                ex.Message.Contains("empty", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(retryDelays[attempt], cancellationToken);
            }
        }
        var thread = result.TryGetProperty("thread", out var nested) ? nested : result;
        if (!thread.TryGetProperty("cwd", out var cwd) || cwd.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(cwd.GetString()))
            throw new InvalidOperationException("The task workspace could not be identified.");
        return cwd.GetString()!;
    }

    public static async Task<IReadOnlyList<object>> BuildSkillInputsAsync(
        CodexAppServer codex,
        string threadId,
        IReadOnlyCollection<SkillReference>? requested,
        CancellationToken cancellationToken)
    {
        if (requested is null || requested.Count == 0) return Array.Empty<object>();
        if (requested.Count > 10) throw new ArgumentException("Too many skills were selected.");
        var cwd = await GetThreadCwdAsync(codex, threadId, cancellationToken);
        var response = await codex.CallAsync("skills/list", new { cwds = new[] { cwd }, forceReload = false }, cancellationToken);
        var available = new List<(string Name, string Path, bool Enabled)>();
        if (response.TryGetProperty("data", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array) continue;
                foreach (var skill in skills.EnumerateArray())
                {
                    var name = Text(skill, "name");
                    var path = Text(skill, "path");
                    var enabled = skill.TryGetProperty("enabled", out var enabledValue) && enabledValue.ValueKind == JsonValueKind.True;
                    if (name is not null && path is not null) available.Add((name, path, enabled));
                }
            }
        }

        var output = new List<object>(requested.Count);
        foreach (var item in requested)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) throw new ArgumentException("A selected skill has no name.");
            var match = available.FirstOrDefault(candidate =>
                candidate.Name.Equals(item.Name, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(item.Path) || candidate.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)));
            if (match.Name is null) throw new ArgumentException($"Skill '{item.Name}' is not available in this task.");
            if (!match.Enabled) throw new ArgumentException($"Skill '{item.Name}' is disabled.");
            output.Add(new { type = "skill", name = match.Name, path = match.Path });
        }
        return output;
    }

    public static async Task<object> GetToolsAsync(
        CodexAppServer codex,
        string? threadId,
        ThreadRuntimeStateStore runtimeStates,
        CancellationToken cancellationToken)
    {
        var effectiveThreadId = threadId;
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            if (runtimeStates.IsExternallyOwnedActive(threadId)) effectiveThreadId = null;
            else if (!codex.HasThreadAccess(threadId)) effectiveThreadId = null;
        }
        var response = await codex.CallAsync("mcpServerStatus/list", new
        {
            detail = "toolsAndAuthOnly",
            threadId = string.IsNullOrWhiteSpace(effectiveThreadId) ? null : effectiveThreadId,
            limit = 100
        }, cancellationToken);
        var servers = new List<object>();
        if (response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in data.EnumerateArray())
            {
                var tools = new List<object>();
                if (server.TryGetProperty("tools", out var toolMap) && toolMap.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in toolMap.EnumerateObject())
                    {
                        var tool = property.Value;
                        tools.Add(new
                        {
                            name = Text(tool, "name") ?? property.Name,
                            title = Text(tool, "title"),
                            description = Text(tool, "description")
                        });
                    }
                }
                servers.Add(new { name = Text(server, "name") ?? "MCP", tools });
            }
        }

        var apps = new List<object>();
        try
        {
            var appResponse = await codex.CallAsync("app/list", new
            {
                threadId = string.IsNullOrWhiteSpace(effectiveThreadId) ? null : effectiveThreadId,
                limit = 100,
                forceRefetch = false
            }, cancellationToken);
            if (appResponse.TryGetProperty("data", out var appData) && appData.ValueKind == JsonValueKind.Array)
            {
                foreach (var app in appData.EnumerateArray())
                {
                    var accessible = app.TryGetProperty("isAccessible", out var accessibleValue) && accessibleValue.ValueKind == JsonValueKind.True;
                    var enabled = !app.TryGetProperty("isEnabled", out var enabledValue) || enabledValue.ValueKind == JsonValueKind.True;
                    apps.Add(new
                    {
                        id = Text(app, "id"),
                        name = Text(app, "name"),
                        description = Text(app, "description"),
                        accessible,
                        enabled
                    });
                }
            }
        }
        catch (CodexRpcException ex) when (ex.Code == -32601) { }
        return new { servers, apps };
    }

    public static void ValidateGoal(GoalUpdate request)
    {
        if (string.IsNullOrWhiteSpace(request.Objective) && string.IsNullOrWhiteSpace(request.Status) && request.TokenBudget is null)
            throw new ArgumentException("Provide a goal objective, status, or token budget.");
        if (!string.IsNullOrWhiteSpace(request.Objective) && request.Objective.Length > 20_000)
            throw new ArgumentException("The goal objective is too long.");
        if (!string.IsNullOrWhiteSpace(request.Status) && !GoalStatuses.Contains(request.Status))
            throw new ArgumentException("The goal status is invalid.");
        if (request.TokenBudget is <= 0) throw new ArgumentException("The token budget must be positive.");
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

static class SessionCookie
{
    public static CookieOptions Options(HttpContext context) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        IsEssential = true,
        Path = "/",
        MaxAge = TimeSpan.FromDays(30)
    };
}

static class LocalAddress
{
    public static string Get()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    public static string GetConsoleUrl(string? configuredUrls)
    {
        var configured = (configuredUrls ?? "http://0.0.0.0:8787")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "http://0.0.0.0:8787";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)) return $"http://{Get()}:8787";
        var host = uri.Host is "0.0.0.0" or "::" or "[::]" or "*" or "+" ? Get() : uri.Host;
        var formattedHost = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return $"{uri.Scheme}://{formattedHost}:{uri.Port}";
    }
}
