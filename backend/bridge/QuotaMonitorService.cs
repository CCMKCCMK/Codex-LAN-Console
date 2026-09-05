using System.Text.Json;

namespace CodexLanBridge;

public sealed record QuotaEstimate(
    string Method,
    double? RatePercentPerHour,
    long? EtaSeconds,
    bool ReachesReset,
    string Confidence);

public sealed record QuotaEstimateSet(
    QuotaEstimate Recent,
    QuotaEstimate Trend,
    QuotaEstimate WindowAverage);

public sealed record QuotaWindowView(
    string Key,
    string Label,
    int UsedPercent,
    int RemainingPercent,
    long? ResetsAt,
    long? WindowDurationMins);

public sealed record QuotaWidgetSnapshot(
    bool Available,
    DateTimeOffset? UpdatedAt,
    bool Stale,
    string? PlanType,
    string? ActiveLimitId,
    QuotaWindowView? Window,
    QuotaEstimateSet? Estimators,
    QuotaEstimate? PrimaryEstimate,
    int HistorySamples,
    string? Error)
{
    public static QuotaWidgetSnapshot Empty(string? error = null) =>
        new(false, null, true, null, null, null, null, null, 0, error);
}

internal sealed record QuotaSample(string Key, int UsedPercent, long? ResetsAt, DateTimeOffset CapturedAt);

public sealed class QuotaMonitorService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly CodexAppServer _codex;
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly object _gate = new();
    private readonly string _historyPath;
    private List<QuotaSample> _history;
    private QuotaWidgetSnapshot _snapshot = QuotaWidgetSnapshot.Empty();

    public event Action<QuotaWidgetSnapshot>? SnapshotChanged;

    public QuotaMonitorService(CodexAppServer codex)
    {
        _codex = codex;
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLanConsole");
        Directory.CreateDirectory(dataDir);
        _historyPath = Path.Combine(dataDir, "quota-history.json");
        _history = LoadHistory(_historyPath);
        _codex.AppServerNotification += OnAppServerNotification;
    }

    public QuotaWidgetSnapshot GetSnapshot()
    {
        var current = Volatile.Read(ref _snapshot);
        var stale = current.UpdatedAt is null ||
                    DateTimeOffset.UtcNow - current.UpdatedAt.Value > TimeSpan.FromMinutes(3) ||
                    !_codex.IsReady;
        return current with { Stale = stale };
    }

    public void RequestRefresh()
    {
        try { _refreshSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var ready = _codex.IsReady;
            if (ready) await RefreshAsync(stoppingToken);
            try
            {
                await _refreshSignal.WaitAsync(
                    ready ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(2),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private void OnAppServerNotification(string method, JsonElement _)
    {
        if (method.Equals("account/rateLimits/updated", StringComparison.Ordinal) ||
            method.Equals("turn/completed", StringComparison.Ordinal)) RequestRefresh();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var payload = await _codex.CallAsync("account/rateLimits/read", new { }, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var parsed = ParseWindows(payload);
            if (parsed.Count == 0)
            {
                Publish(QuotaWidgetSnapshot.Empty("Codex did not return a rate-limit window."));
                return;
            }

            var active = parsed
                .OrderByDescending(item => item.Window.UsedPercent)
                .ThenBy(item => item.Window.ResetsAt ?? long.MaxValue)
                .First();
            var sample = new QuotaSample(active.Window.Key, active.Window.UsedPercent, active.Window.ResetsAt, now);
            List<QuotaSample> matching;
            lock (_gate)
            {
                var last = _history.LastOrDefault(item => item.Key == sample.Key && item.ResetsAt == sample.ResetsAt);
                var changed = false;
                if (last is null || last.UsedPercent != sample.UsedPercent || now - last.CapturedAt >= TimeSpan.FromMinutes(10))
                {
                    _history.Add(sample);
                    changed = true;
                }
                var cutoff = now - TimeSpan.FromDays(8);
                var trimmed = _history.Where(item => item.CapturedAt >= cutoff).TakeLast(2500).ToList();
                changed |= trimmed.Count != _history.Count;
                _history = trimmed;
                matching = _history.Where(item => item.Key == sample.Key && item.ResetsAt == sample.ResetsAt)
                    .OrderBy(item => item.CapturedAt).ToList();
                if (changed) SaveHistory(_historyPath, _history);
            }

            var estimates = BuildEstimates(active.Window, matching, now);
            var primary = PickPrimary(estimates);
            Publish(new QuotaWidgetSnapshot(
                true,
                now,
                false,
                active.PlanType,
                active.LimitId,
                active.Window,
                estimates,
                primary,
                matching.Count,
                null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Quota refresh failed: {ex.Message}");
            var current = Volatile.Read(ref _snapshot);
            Publish(current.Available
                ? current with { Stale = true, Error = "Codex quota data is temporarily unavailable." }
                : QuotaWidgetSnapshot.Empty("Codex quota data is temporarily unavailable."));
        }
    }

    private void Publish(QuotaWidgetSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        SnapshotChanged?.Invoke(snapshot);
    }

    private static List<ParsedWindow> ParseWindows(JsonElement payload)
    {
        var snapshots = new List<(string Id, JsonElement Value)>();
        if (payload.ValueKind != JsonValueKind.Object) return new();
        if (payload.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byId.EnumerateObject()) snapshots.Add((property.Name, property.Value));
        }
        if (snapshots.Count == 0 && payload.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            snapshots.Add((ReadString(legacy, "limitId") ?? "codex", legacy));

        var result = new List<ParsedWindow>();
        foreach (var (fallbackId, snapshot) in snapshots)
        {
            var id = ReadString(snapshot, "limitId") ?? fallbackId;
            var plan = ReadString(snapshot, "planType");
            AddWindow(result, id, plan, "primary", snapshot);
            AddWindow(result, id, plan, "secondary", snapshot);
        }
        return result;
    }

    private static void AddWindow(List<ParsedWindow> result, string id, string? plan, string name, JsonElement snapshot)
    {
        if (!snapshot.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("usedPercent", out var usedElement) || !usedElement.TryGetInt32(out var used)) return;
        var duration = ReadInt64(window, "windowDurationMins");
        var reset = ReadInt64(window, "resetsAt");
        var label = FormatWindowLabel(duration, name);
        result.Add(new ParsedWindow(
            id,
            plan,
            new QuotaWindowView(
                $"{id}:{name}",
                label,
                Math.Clamp(used, 0, 100),
                Math.Clamp(100 - used, 0, 100),
                reset,
                duration)));
    }

    private static QuotaEstimateSet BuildEstimates(QuotaWindowView window, List<QuotaSample> samples, DateTimeOffset now)
    {
        var recentRate = Slope(samples.Where(item => now - item.CapturedAt <= TimeSpan.FromHours(1)).ToList(), 5);
        var trendRate = MedianPairSlope(samples.Where(item => now - item.CapturedAt <= TimeSpan.FromHours(12)).ToList());
        double? averageRate = null;
        if (window.ResetsAt is long reset && window.WindowDurationMins is long duration && duration > 0)
        {
            var startedAt = DateTimeOffset.FromUnixTimeSeconds(reset).AddMinutes(-duration);
            var elapsedHours = (now - startedAt).TotalHours;
            if (elapsedHours > 0.05) averageRate = window.UsedPercent / elapsedHours;
        }

        return new QuotaEstimateSet(
            MakeEstimate("recent", recentRate, window, now, recentRate.HasValue ? "medium" : "warmingUp"),
            MakeEstimate("trend", trendRate, window, now, trendRate.HasValue ? "high" : "warmingUp"),
            MakeEstimate("windowAverage", averageRate, window, now, averageRate.HasValue ? "baseline" : "unavailable"));
    }

    private static double? Slope(List<QuotaSample> samples, double minimumMinutes)
    {
        if (samples.Count < 2) return null;
        var first = samples[0];
        var last = samples[^1];
        var hours = (last.CapturedAt - first.CapturedAt).TotalHours;
        if (hours * 60 < minimumMinutes) return null;
        var delta = last.UsedPercent - first.UsedPercent;
        return delta <= 0 ? 0 : delta / hours;
    }

    private static double? MedianPairSlope(List<QuotaSample> samples)
    {
        if (samples.Count < 3) return null;
        var slopes = new List<double>();
        for (var i = 0; i < samples.Count - 1; i++)
        for (var j = i + 1; j < samples.Count; j++)
        {
            var hours = (samples[j].CapturedAt - samples[i].CapturedAt).TotalHours;
            var delta = samples[j].UsedPercent - samples[i].UsedPercent;
            if (hours >= 0.25 && delta >= 0) slopes.Add(delta / hours);
        }
        if (slopes.Count == 0) return null;
        slopes.Sort();
        return slopes[slopes.Count / 2];
    }

    private static QuotaEstimate MakeEstimate(
        string method,
        double? rate,
        QuotaWindowView window,
        DateTimeOffset now,
        string confidence)
    {
        long? eta = null;
        if (rate is > 0.0001) eta = (long)Math.Round(window.RemainingPercent / rate.Value * 3600);
        var secondsToReset = window.ResetsAt is long reset
            ? Math.Max(0, reset - now.ToUnixTimeSeconds())
            : (long?)null;
        var reachesReset = !eta.HasValue || (secondsToReset.HasValue && eta.Value >= secondsToReset.Value);
        return new QuotaEstimate(method, rate.HasValue ? Math.Round(rate.Value, 3) : null, eta, reachesReset, confidence);
    }

    private static QuotaEstimate PickPrimary(QuotaEstimateSet set)
    {
        var available = new[] { set.Recent, set.Trend, set.WindowAverage }
            .Where(item => item.RatePercentPerHour.HasValue)
            .OrderBy(item => item.RatePercentPerHour)
            .ToArray();
        return available.Length == 0 ? set.WindowAverage : available[available.Length / 2];
    }

    private static string FormatWindowLabel(long? minutes, string fallback)
    {
        if (minutes is null or <= 0) return fallback;
        if (minutes.Value % 10080 == 0) return $"{minutes.Value / 10080}w";
        if (minutes.Value % 1440 == 0) return $"{minutes.Value / 1440}d";
        if (minutes.Value % 60 == 0) return $"{minutes.Value / 60}h";
        return $"{minutes.Value}m";
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? ReadInt64(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result) ? result : null;

    private static List<QuotaSample> LoadHistory(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<QuotaSample>>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch { return new(); }
    }

    private static void SaveHistory(string path, List<QuotaSample> samples)
    {
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(samples, JsonOptions));
            File.Move(temporary, path, true);
        }
        catch { }
    }

    private sealed record ParsedWindow(string LimitId, string? PlanType, QuotaWindowView Window);
}
