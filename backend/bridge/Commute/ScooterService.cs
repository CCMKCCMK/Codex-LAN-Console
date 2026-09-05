using System.Collections.Concurrent;
using System.Text.Json;

namespace CodexLanBridge.Commute;

public sealed record ScooterReturn(double Meters, double Ascent, double Descent, double EffortMeters,
    double[][] Line, bool TerrainAvailable, DateTimeOffset At);
public sealed record ScooterEstimate(double Percent, double RemainingKm, double? RemainingMinutes,
    double UsedKm, double UsedMinutes, double Ascent, double Descent, bool PositionFresh,
    bool? ReturnAtRisk, string Message, ScooterReturn? ReturnRoute);
public sealed record ScooterSnapshot(ScooterData Data, ScooterModel Model, ScooterEstimate Estimate, string? LoadWarning);

public sealed class ScooterService(ScooterStore store, NotificationStore notifications,
    ILogger<ScooterService> logger) : BackgroundService
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    private readonly ConcurrentDictionary<string, double> elevations = new();
    private readonly SemaphoreSlim terrainGate = new(1);
    private readonly SemaphoreSlim returnGate = new(1);
    private ScooterReturn? returnRoute;
    private string routeKey = "";
    private DateTimeOffset routeAttempt;
    private static string Cell(double lat, double lon) => FormattableString.Invariant($"{Math.Round(lat * 2000) / 2000:F4},{Math.Round(lon * 2000) / 2000:F4}");
    private async Task<double?[]> Elevations((double Lat, double Lon)[] points, CancellationToken ct)
    {
        await terrainGate.WaitAsync(ct);
        try
        {
            var missing = points.Where(p => !elevations.ContainsKey(Cell(p.Lat, p.Lon))).GroupBy(p => Cell(p.Lat, p.Lon)).Select(g => g.First()).Take(100).ToArray();
            if (missing.Length > 0)
            {
                var lat = string.Join(",", missing.Select(p => p.Lat.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)));
                var lon = string.Join(",", missing.Select(p => p.Lon.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)));
                using var response = await http.GetAsync($"https://api.open-meteo.com/v1/elevation?latitude={lat}&longitude={lon}", ct);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var values = doc.RootElement.GetProperty("elevation").EnumerateArray().ToArray();
                if (values.Length != missing.Length) throw new InvalidDataException("Elevation count mismatch");
                if (elevations.Count > 20000) elevations.Clear();
                for (var i = 0; i < values.Length; i++)
                    if (values[i].TryGetDouble(out var h) && double.IsFinite(h) && h is >= -500 and <= 9000) elevations[Cell(missing[i].Lat, missing[i].Lon)] = h;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        { /* Missing terrain stays unknown; GPS altitude is never silently substituted. */ }
        finally { terrainGate.Release(); }
        return points.Select(p => elevations.TryGetValue(Cell(p.Lat, p.Lon), out var v) ? (double?)v : null).ToArray();
    }
    public async Task<ScooterSnapshot> Add(ScooterBatch batch, CancellationToken ct)
    {
        if (batch.Points is null || batch.Points.Length is < 1 or > 100) throw new ArgumentException("每批最多 100 个点。");
        var d = store.Get();
        if (!d.Rides.Any(r => r.Id == batch.RideId)) throw new ArgumentException("骑行不存在。");
        // Reject malformed coordinates before sending anything to a terrain provider.
        var clean = batch.Points.Select(p => p with { Elevation = null }).ToArray();
        var valid = clean.Select((p, i) => (p, i)).Where(x => ScooterStore.ValidPoint(x.p)).ToArray();
        if (d.Settings.TerrainEnabled && valid.Length > 0)
        {
            var heights = await Elevations(valid.Select(x => (x.p.Lat, x.p.Lon)).ToArray(), ct);
            for (var i = 0; i < valid.Length; i++) clean[valid[i].i] = valid[i].p with { Elevation = heights[i] };
        }
        store.Add(batch with { Points = clean });
        return Snapshot();
    }
    public ScooterSnapshot Snapshot()
    {
        var d = store.Get(); var model = ScooterStore.Model(d); var cycle = d.Cycles.LastOrDefault();
        var rides = d.Rides.Where(r => r.CycleId == cycle?.Id).ToArray();
        var meters = rides.Sum(r => r.Meters); var up = rides.Sum(r => r.Ascent); var down = rides.Sum(r => r.Descent);
        var used = ScooterStore.Effort(d.Settings, meters, up, down);
        var remaining = cycle is null || cycle.EndReason == "depleted" ? 0 : Math.Max(0, model.CapacityMeters - used);
        var active = ScooterStore.Active(d); var point = active?.LastPoint;
        var fresh = point is not null && DateTimeOffset.UtcNow - point.At < TimeSpan.FromSeconds(90);
        var route = returnRoute is not null && DateTimeOffset.UtcNow - returnRoute.At < TimeSpan.FromSeconds(90) ? returnRoute : null;
        var incomplete = rides.Any(r => r.Gaps > 0 || r.ManualDistance) || rides.Sum(r => r.TerrainMeters) < meters * .8;
        bool? risk = fresh && route is not null ?
            route.EffortMeters * (route.TerrainAvailable ? 1 : 1.3) + model.CapacityMeters * d.Settings.ReservePercent / 100 > Math.Max(0, model.ConservativeMeters - used) : null;
        if (incomplete && risk == false) risk = null;
        var minutes = rides.Sum(r => r.Minutes);
        var message = cycle is null ? "先充满电，再开始一个续航测试周期。" : cycle.EndReason == "depleted" ? "已记录没电；请充电，勿继续骑行测试。" :
            !fresh && active is not null ? "定位不新鲜，无法判断能否返回充电点。" : risk == true ? "预计电量不足以留出返程余量，请尽快返回充电点。" :
            incomplete ? "本周期有缺失路段或地形，估算可能偏乐观，请额外预留电量。" : risk == false ? "按当前估算有返程余量；不是保证，请同时观察车上电量。" : "尚无可用的返回路线，不能确认返程电量是否足够。";
        return new(d, model, new(model.CapacityMeters > 0 ? remaining / model.CapacityMeters * 100 : 0,
            remaining / 1000, meters > 100 && minutes > 0 ? remaining / meters * minutes : null,
            meters / 1000, minutes, up, down, fresh, risk, message, route), store.LoadWarning);
    }
    private async Task UpdateReturn(CancellationToken ct)
    {
        var d = store.Get(); var point = ScooterStore.Active(d)?.LastPoint; var charger = d.Settings.Charger;
        if (point is null || charger is null || DateTimeOffset.UtcNow - point.At > TimeSpan.FromSeconds(90)) return;
        var key = Cell(point.Lat, point.Lon) + ":" + Cell(charger.Lat, charger.Lon);
        if (key == routeKey && DateTimeOffset.UtcNow - routeAttempt < TimeSpan.FromSeconds(30)) return;
        if (!await returnGate.WaitAsync(0, ct)) return;
        try
        {
            routeAttempt = DateTimeOffset.UtcNow; routeKey = key;
            returnRoute = null;
            var distance = ScooterStore.Distance(point.Lat, point.Lon, charger.Lat, charger.Lon);
            if (distance < 40) { returnRoute = new(0, 0, 0, 0, [[point.Lat, point.Lon], [charger.Lat, charger.Lon]], true, DateTimeOffset.UtcNow); return; }
            var url = FormattableString.Invariant($"{CommutePlanner.Source}/api/otp/plan?fromPlace={point.Lat:F6},{point.Lon:F6}&toPlace={charger.Lat:F6},{charger.Lon:F6}&mode=BICYCLE&arriveBy=false");
            using var response = await http.GetAsync(url, ct); response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var itinerary = doc.RootElement.GetProperty("plan").GetProperty("itineraries")[0];
            var line = new List<double[]>(); double meters = 0;
            foreach (var leg in itinerary.GetProperty("legs").EnumerateArray())
            {
                meters += leg.GetProperty("distance").GetDouble();
                if (leg.TryGetProperty("legGeometry", out var geometry) && geometry.TryGetProperty("points", out var encoded)) line.AddRange(Decode(encoded.GetString() ?? ""));
            }
            if (line.Count < 2 || !double.IsFinite(meters) || meters <= 0) throw new InvalidDataException("No route geometry");
            var sampled = Enumerable.Range(0, Math.Min(60, line.Count)).Select(i => line[(int)Math.Round(i * (line.Count - 1.0) / (Math.Min(60, line.Count) - 1))]).ToArray();
            var heights = d.Settings.TerrainEnabled ? await Elevations(sampled.Select(p => (p[0], p[1])).ToArray(), ct) : new double?[sampled.Length];
            double up = 0, down = 0; double? anchor = heights.FirstOrDefault();
            foreach (var h in heights.Skip(1))
            {
                if (h is null) { anchor = null; continue; }
                if (anchor is not null && Math.Abs(h.Value - anchor.Value) >= 3)
                { if (h > anchor) up += h.Value - anchor.Value; else down += anchor.Value - h.Value; anchor = h; }
                anchor ??= h;
            }
            returnRoute = new(meters, up, down, ScooterStore.Effort(d.Settings, meters, up, down), line.ToArray(), heights.All(h => h.HasValue), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException or InvalidDataException or IndexOutOfRangeException)
        { returnRoute = null; }
        finally { returnGate.Release(); }
    }
    internal static IEnumerable<double[]> Decode(string text)
    {
        int index = 0, lat = 0, lon = 0, count = 0;
        int Read()
        {
            int result = 0, shift = 0, b;
            do { if (index >= text.Length || shift > 30) throw new InvalidDataException("Invalid geometry"); b = text[index++] - 63; result |= (b & 31) << shift; shift += 5; } while (b >= 32);
            return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
        }
        while (index < text.Length && count++ < 20000) { lat += Read(); lon += Read(); yield return [lat / 1e5, lon / 1e5]; }
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await UpdateReturn(ct);
                var view = Snapshot(); var ride = ScooterStore.Active(view.Data);
                if (ride is not null && view.Data.Settings.AlertsEnabled && view.Estimate.ReturnAtRisk == true)
                {
                    var bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / view.Data.Settings.AlertSeconds;
                    notifications.Publish($"scooter:{ride.Id}:{bucket}", "commute_departure", "commute", null, null,
                        "Scooter · 请预留返程电量", view.Estimate.Message, false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Scooter check failed: {Kind}", ex.GetType().Name); }
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

public static class ScooterEndpoints
{
    public static void MapScooter(this WebApplication app)
    {
        app.MapGet("/api/commute/scooter", (ScooterService service) => service.Snapshot());
        app.MapPost("/api/commute/scooter/action", (ScooterAction action, ScooterStore store, ScooterService service) => { store.Apply(action); return service.Snapshot(); });
        app.MapPost("/api/commute/scooter/points", (ScooterBatch batch, ScooterService service, CancellationToken ct) => service.Add(batch, ct));
        app.MapPut("/api/commute/scooter/settings", (ScooterSettingsRequest request, ScooterStore store, ScooterService service) =>
        { store.Update(request); return service.Snapshot(); });
        app.MapGet("/api/commute/scooter/export", (ScooterStore store) => Results.Json(store.Get()));
    }
}
