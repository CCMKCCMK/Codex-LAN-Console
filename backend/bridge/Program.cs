using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using CodexLanBridge;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("CODEX_LAN_URLS") ?? "http://0.0.0.0:8787");
builder.Services.AddSingleton<PairingService>();
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddSingleton<ThreadRuntimeStateStore>();
builder.Services.AddSingleton<ApprovalSettingsStore>();
builder.Services.AddSingleton<CodexAppServer>();
builder.Services.AddSingleton<ProjectScanner>();
builder.Services.AddSingleton<LocalPortRelayService>();
builder.Services.AddSingleton<FileTransferService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CodexAppServer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalPortRelayService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<FileTransferService>());
builder.Services.AddHostedService<NotificationMonitor>();
builder.Services.AddHostedService<ExternalRolloutMonitor>();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = FileTransferService.MaximumRequestBytes;
    options.ValueLengthLimit = 64 * 1024;
});

var app = builder.Build();
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
    if (!context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/api/health") ||
        context.Request.Path.StartsWithSegments("/api/pair"))
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

app.MapGet("/api/health", (PairingService pairing, CodexAppServer codex) => new
{
    ok = true,
    name = "Codex LAN Console",
    paired = pairing.HasDevices,
    codex = codex.IsReady,
    machine = Environment.MachineName,
    time = DateTimeOffset.Now
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
        threads,
        projects = projects.Scan().Take(12).ToArray(),
        processes = ProcessApi.List().Take(20).ToArray(),
        pending = codex.Pending.Values.OrderByDescending(x => x.CreatedAt).ToArray(),
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

    var metadata = await codex.CallAsync(
        "thread/read",
        new { threadId = id, includeTurns = false },
        cancellationToken);
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
        return Results.Ok(ApiHelpers.PagedThread(metadata, turnPage, id, runtimeStates.Get(id)));
    }
    catch (CodexRpcException ex) when (ApiHelpers.IsUnmaterializedThread(ex))
    {
        return Results.Ok(ApiHelpers.PagedThread(metadata, default, id, runtimeStates.Get(id)));
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

app.MapPost("/api/threads", async (
    ThreadCreate request,
    CodexAppServer codex,
    CancellationToken cancellationToken) =>
{
    var workspace = string.IsNullOrWhiteSpace(request.Cwd) ? Environment.CurrentDirectory : request.Cwd;
    var permissions = ExecutionPermissions.Parse(request.Permissions, request.ApprovalPolicy, request.ApprovalsReviewer);
    return Results.Ok(await codex.StartThreadAsync(workspace, permissions, cancellationToken));
});

app.MapPost("/api/threads/{id}/messages", async (
    string id,
    MessageRequest request,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    FileTransferService files,
    CancellationToken cancellationToken) =>
{
    if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } conflict) return conflict;
    var input = new List<object>();
    if (!string.IsNullOrWhiteSpace(request.Text))
        input.Add(new { type = "text", text = request.Text, text_elements = Array.Empty<object>() });
    input.AddRange(files.BuildCodexInputs(id, request.AttachmentIds));
    input.AddRange(await ApiHelpers.BuildSkillInputsAsync(codex, id, request.Skills, cancellationToken));
    if (input.Count == 0) return Results.BadRequest(new { error = "Message and attachments are empty." });
    var permissions = ExecutionPermissions.Parse(request.Permissions, request.ApprovalPolicy, request.ApprovalsReviewer);
    var result = await codex.SendUserInputAsync(
        id, input, request.ClientUserMessageId, null, permissions, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/threads/{id}/steer", async (
    string id,
    SteerRequest request,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    FileTransferService files,
    CancellationToken cancellationToken) =>
{
    if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } conflict) return conflict;
    var input = new List<object>();
    if (!string.IsNullOrWhiteSpace(request.Text))
        input.Add(new { type = "text", text = request.Text, text_elements = Array.Empty<object>() });
    input.AddRange(files.BuildCodexInputs(id, request.AttachmentIds));
    input.AddRange(await ApiHelpers.BuildSkillInputsAsync(codex, id, request.Skills, cancellationToken));
    if (input.Count == 0) return Results.BadRequest(new { error = "Message and attachments are empty." });
    var permissions = ExecutionPermissions.Parse(request.Permissions, request.ApprovalPolicy, request.ApprovalsReviewer);
    var result = await codex.SendUserInputAsync(
        id, input, request.ClientUserMessageId, request.TurnId, permissions, cancellationToken);
    return Results.Ok(result);
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
    codex.Pending.Values.Where(CodexAppServer.IsApprovalRequest).OrderByDescending(x => x.CreatedAt));
app.MapPost("/api/approvals/{key}", async (string key, ApprovalDecision request, CodexAppServer codex) =>
{
    if (request.Decision is not ("accept" or "acceptForSession" or "decline" or "cancel"))
        return Results.BadRequest(new { error = "Invalid decision." });
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
    return Results.Ok(files.RegisterExisting(request.Path, cwd, request.ThreadId));
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
    if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } conflict) return conflict;
    await codex.EnsureThreadLoadedAsync(id, cancellationToken);
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
    return Results.Ok(await codex.CallAsync("thread/goal/set", new
    {
        threadId = id,
        objective = request.Objective,
        status = request.Status,
        tokenBudget = request.TokenBudget
    }, cancellationToken));
});

app.MapDelete("/api/threads/{id}/goal", async (
    string id,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    CancellationToken cancellationToken) =>
{
    if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } conflict) return conflict;
    await codex.EnsureThreadLoadedAsync(id, cancellationToken);
    return Results.Ok(await codex.CallAsync("thread/goal/clear", new { threadId = id }, cancellationToken));
});

app.MapPost("/api/threads/{id}/compact", async (
    string id,
    CodexAppServer codex,
    ThreadRuntimeStateStore runtimeStates,
    CancellationToken cancellationToken) =>
{
    if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } conflict) return conflict;
    await codex.EnsureThreadLoadedAsync(id, cancellationToken);
    return Results.Ok(await codex.CallAsync("thread/compact/start", new { threadId = id }, cancellationToken));
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
            return Results.Ok(await codex.CallAsync("thread/compact/start", new { threadId = id }, cancellationToken));
        case "goal":
        case "go":
        {
            if (ApiHelpers.ExternalActiveConflict(runtimeStates, id) is { } goalConflict) return goalConflict;
            await codex.EnsureThreadLoadedAsync(id, cancellationToken);
            var arguments = request.Arguments?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(request.Objective) && string.IsNullOrWhiteSpace(request.Status) && request.TokenBudget is null && arguments.Length == 0)
                return Results.Ok(await codex.CallAsync("thread/goal/get", new { threadId = id }, cancellationToken));
            if (arguments.Equals("clear", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(await codex.CallAsync("thread/goal/clear", new { threadId = id }, cancellationToken));
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
            return Results.Ok(await codex.CallAsync("thread/goal/set", new
            {
                threadId = id,
                objective = update.Objective,
                status = update.Status,
                tokenBudget = update.TokenBudget
            }, cancellationToken));
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
app.MapPost("/api/processes/{pid:int}/stop", (int pid, StopProcessRequest request) =>
{
    if (request.Confirmation != $"STOP {pid}") return Results.BadRequest(new { error = $"Type STOP {pid} to confirm." });
    if (pid == Environment.ProcessId) return Results.BadRequest(new { error = "The bridge cannot stop itself." });
    try
    {
        using var process = Process.GetProcessById(pid);
        if (!ProcessApi.IsAllowed(process.ProcessName)) return Results.BadRequest(new { error = "This process is not managed by Codex LAN Console." });
        process.Kill(entireProcessTree: true);
        return Results.Ok();
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapFallbackToFile("index.html");

var pairing = app.Services.GetRequiredService<PairingService>();
Console.WriteLine($"Codex LAN Console: {LocalAddress.GetConsoleUrl(Environment.GetEnvironmentVariable("CODEX_LAN_URLS"))}");
Console.WriteLine($"Pairing code: {pairing.Code} (valid until restart or successful pairing)");
Console.WriteLine($"Pairing details: {pairing.PairingFile}");
app.Run();

record PairRequest(string? Code, string? DeviceName);
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
    string? ApprovalsReviewer);
record SteerRequest(
    string? TurnId,
    string? Text,
    string? ClientUserMessageId,
    string[]? AttachmentIds,
    SkillReference[]? Skills,
    string? Permissions,
    string? ApprovalPolicy,
    string? ApprovalsReviewer);
record SkillReference(string Name, string? Path);
record ExistingFileRequest(string ThreadId, string Path);
record GoalUpdate(string? Objective, string? Status, long? TokenBudget);
record CommandRequest(string? Command, string? Arguments, string? Objective, string? Status, long? TokenBudget);
record ApprovalDecision(string Decision);
record ApprovalBatchDecision(string Decision);
record ApprovalSettingsUpdate(bool AutoApproveAll, string? Confirmation);
record UserInputResponse(Dictionary<string, UserInputAnswer> Answers);
record LocalLinkRequest(string Url);
record StopProcessRequest(string Confirmation);

static class ApiHelpers
{
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

    private const int MaximumMobileStringCharacters = 64 * 1024;
    private const int MaximumMobileVerboseStringCharacters = 32 * 1024;
    private const int MaximumMobileCollectionItems = 256;
    private const int MaximumMobileJsonDepth = 24;
    private static readonly HashSet<string> VerboseFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "aggregatedOutput", "diff", "output", "result", "toolResult"
    };

    public static IResult? ExternalActiveConflict(ThreadRuntimeStateStore runtimeStates, string threadId)
    {
        if (!runtimeStates.IsExternallyOwnedActive(threadId)) return null;
        return Results.Conflict(new
        {
            error = "This task is currently running in Codex Desktop. Wait for that turn to finish or control it from the computer.",
            kind = "externalThreadActive",
            threadId
        });
    }

    public static object PagedThread(
        JsonElement metadataResult,
        JsonElement turnPage,
        string fallbackId,
        ThreadRuntimeSnapshot? runtimeState)
    {
        var thread = metadataResult.TryGetProperty("thread", out var nested) ? nested : metadataResult;
        var turns = turnPage.ValueKind == JsonValueKind.Object &&
                    turnPage.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Take(20).Select(BoundForMobile).ToArray()
            : Array.Empty<JsonElement>();
        // The app-server page is newest-first; the conversation view renders oldest-first.
        Array.Reverse(turns);
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
                turns
            },
            // Keep the legacy fields in the response. Cursor-aware clients use nextCursor;
            // totalTurnsExact explains why the page-local count is not a history-wide count.
            start = 0,
            totalTurns = turns.Length,
            totalTurnsExact = !hasEarlier,
            hasEarlier,
            nextCursor,
            backwardsCursor = Text(turnPage, "backwardsCursor"),
            runtimeState
        };
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
        exception.Message.Contains("not materialized", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("before first user message", StringComparison.OrdinalIgnoreCase);

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
                var maximum = propertyName is not null && VerboseFields.Contains(propertyName)
                    ? MaximumMobileVerboseStringCharacters
                    : MaximumMobileStringCharacters;
                writer.WriteStringValue(value.Length > maximum
                    ? value[..maximum] + "\n[内容过长，手机端仅显示前段]"
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

    private static bool LooksLikeLargeInlineBinary(string value) =>
        value.Length > 4096 &&
        value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
        value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) >= 0;

    private static JsonElement? Element(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.Clone()
            : null;

    public static async Task<string> GetThreadCwdAsync(CodexAppServer codex, string threadId, CancellationToken cancellationToken)
    {
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
            else await codex.EnsureThreadLoadedAsync(threadId, cancellationToken);
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
