namespace CodexLanBridge.Commute;

public sealed class CommuteReminderService(CommuteStore store, CommutePlanner planner,
    NotificationStore notifications, ILogger<CommuteReminderService> logger) : BackgroundService
{
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public string? LastError { get; private set; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Check(stoppingToken); LastError = null; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LastError = "通勤提醒检查暂时失败，正在自动重试。"; logger.LogWarning(ex, "Commute reminder check failed"); }
            LastCheckedAt = DateTimeOffset.UtcNow;
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
    private async Task Check(CancellationToken ct)
    {
        var state = store.Get(); var s = state.Settings;
        if (!s.RemindersEnabled || state.ActiveTrip is not null) return;
        var now = DateTimeOffset.UtcNow;
        var local = CommutePlanner.LocalTime(now);
        if (!s.Days.Contains((int)local.DayOfWeek)) return;
        foreach (var direction in new[] { "toCampus", "toHome" })
        {
            var time = direction == "toCampus" ? s.MorningArrival : s.EveningArrival;
            var localText = $"{local:yyyy-MM-dd}T{time}";
            var target = CommutePlanner.ParseLocal(localText);
            if (target <= now || target > now.AddMinutes(100)) continue;
            if (state.History.Any(t => t.Direction == direction && t.FinishedAt is not null &&
                CommutePlanner.LocalTime(t.FinishedAt.Value).Date == local.Date)) continue;
            var plan = await planner.Plan(new(direction, localText, true), ct);
            var best = plan.Options.FirstOrDefault(x => x.Id == plan.RecommendedId);
            var key = $"commute:{local:yyyy-MM-dd}:{direction}:{time}";
            if (best is not null && best.LeaveAt <= now.AddMinutes(s.RemindMinutes))
            {
                notifications.Publish(key, "commute_departure", "commute", null, null,
                    direction == "toCampus" ? "准备出发去 HDSI" : "准备从 HDSI 回家",
                    $"建议{best.Title}，{CommutePlanner.LocalTime(best.LeaveAt):HH:mm} 出发，约 {best.Minutes:0} 分钟，{best.Basis}。打开通勤助手查看站点和路线。", false);
            }
            else if (best is null && target <= now.AddMinutes(s.RemindMinutes + s.BufferMinutes + 20))
            {
                notifications.Publish(key, "commute_departure", "commute", null, null,
                    "请检查这次通勤",
                    $"距离 {time} 的到达目标已很近，暂未找到可确认的准时方案。请打开通勤助手选择现在出发并核对官方班次。", false);
            }
        }
    }
}

public static class CommuteEndpoints
{
    public static void MapCommute(this WebApplication app)
    {
        // Directory URLs can otherwise hit the Console SPA fallback before DefaultFiles.
        app.MapGet("/commute", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(Path.Combine(app.Environment.WebRootPath, "commute", "index.html"), "text/html; charset=utf-8");
        });
        app.MapGet("/api/commute/state", (CommuteStore store, CommuteReminderService reminders, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { state = store.Get(), loadWarning = store.LoadWarning, reminders = new { reminders.LastCheckedAt, reminders.LastError,
                delivery = "Codex Android 后台通知；普通网页关闭后不能保证提醒", timezone = "America/Los_Angeles" } });
        });
        app.MapPut("/api/commute/settings", (SaveCommuteRequest request, CommuteStore store) =>
        {
            try { return Results.Ok(store.Update(request)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/commute/plan", (PlanRequest request, CommutePlanner planner, CancellationToken ct) => planner.Plan(request, ct));
        app.MapGet("/api/commute/location", (string query, CommutePlanner planner, CancellationToken ct) => planner.Geocode(query, ct));
        app.MapGet("/api/commute/live", (string? direction, string? stopId, string? routeId, CommutePlanner planner, CancellationToken ct) =>
            planner.Live(direction ?? "toCampus", stopId, routeId, ct));
        app.MapPost("/api/commute/trips/start", (StartJourneyRequest request, CommuteStore store) => store.Start(request));
        app.MapPost("/api/commute/trips/finish", (FinishJourneyRequest request, CommuteStore store) => store.Finish(request));
        app.MapPost("/api/commute/notifications/test", (NotificationStore notifications) =>
        {
            var result = notifications.Publish("commute-test:" + Guid.NewGuid(), "commute_departure", "commute", null, null,
                "通勤助手 · 测试提醒", "收到这条声音通知，说明 Android 后台接收通勤提醒的通道已经连通。", false);
            return Results.Ok(new { queued = result is not null });
        });
    }
}
