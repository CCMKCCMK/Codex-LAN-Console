using System.Text.Json;

namespace CodexLanBridge.Commute;

public sealed record ScooterSettings
{
    public Place? Charger { get; init; }
    public double ReferenceRangeKm { get; init; } = 15;
    public double TotalMassKg { get; init; } = 95;
    public double ReservePercent { get; init; } = 25;
    public int AlertSeconds { get; init; } = 60;
    public bool AlertsEnabled { get; init; } = true;
    public bool TerrainEnabled { get; init; } = true;
}
public sealed record ScooterPoint(long Seq, DateTimeOffset At, double Lat, double Lon,
    double Accuracy, double? Elevation = null);
public sealed record ScooterRide
{
    public string Id { get; init; } = "";
    public string CycleId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; set; }
    public double Meters { get; set; }
    public double Ascent { get; set; }
    public double Descent { get; set; }
    public double TerrainMeters { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public int Gaps { get; set; }
    public long LastSeq { get; set; } = -1;
    public ScooterPoint? LastPoint { get; set; }
    public double? ElevationAnchor { get; set; }
    public bool ManualDistance { get; set; }
    public double Minutes => Math.Max(0, ((StoppedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalMinutes);
}
public sealed record ScooterCycle(string Id, DateTimeOffset FullAt,
    DateTimeOffset? EndedAt = null, string? EndReason = null);
public sealed record ScooterData
{
    public int Revision { get; set; }
    public ScooterSettings Settings { get; set; } = new();
    public List<ScooterCycle> Cycles { get; set; } = [];
    public List<ScooterRide> Rides { get; set; } = [];
    public Dictionary<string, string> Requests { get; set; } = [];
}
public sealed record ScooterAction(string Action, string RequestId, string? RideId = null, double? DistanceKm = null, DateTimeOffset? At = null);
public sealed record ScooterBatch(string RideId, ScooterPoint[] Points);
public sealed record ScooterSettingsRequest(int Revision, ScooterSettings Settings);
public sealed record ScooterModel(double CapacityMeters, double ConservativeMeters, int Cycles,
    double? BacktestErrorPercent, string Confidence);

public sealed class ScooterStore
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private ScooterData data = new();
    public string? LoadWarning { get; private set; }
    public ScooterStore(CommuteStore commute) : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexLanConsole", "Scooter"), commute.Get().Settings.Home) { }
    internal ScooterStore(string dir, Place home)
    {
        directory = dir;
        data.Settings = new() { Charger = home };
        var path = Path.Combine(dir, "state.json");
        if (!File.Exists(path)) return;
        try
        {
            data = JsonSerializer.Deserialize<ScooterData>(File.ReadAllText(path), json) ?? throw new JsonException();
            Validate(data.Settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        { LoadWarning = "Scooter 数据无法读取，已保护原文件；请先恢复备份，不要继续写入。"; }
    }
    public ScooterData Get() { lock (gate) return JsonSerializer.Deserialize<ScooterData>(JsonSerializer.Serialize(data, json), json)!; }
    private void Save()
    {
        if (LoadWarning is not null) throw new InvalidOperationException(LoadWarning);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, json));
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(path + ".tmp", path, true);
    }
    public static ScooterRide? Active(ScooterData d) => d.Rides.LastOrDefault(r => r.StoppedAt is null);
    public static void Validate(ScooterSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Charger is null) throw new ArgumentException("请设置充电地点。");
        CommuteStore.ValidatePlace(s.Charger);
        if (!double.IsFinite(s.ReferenceRangeKm) || s.ReferenceRangeKm is < 1 or > 150 ||
            !double.IsFinite(s.TotalMassKg) || s.TotalMassKg is < 20 or > 250 ||
            !double.IsFinite(s.ReservePercent) || s.ReservePercent is < 10 or > 60 || s.AlertSeconds is < 15 or > 3600)
            throw new ArgumentException("请检查参考续航、总重量、余量和提醒间隔（15–3600 秒）。");
    }
    public ScooterData Update(ScooterSettingsRequest request)
    {
        Validate(request.Settings);
        lock (gate)
        {
            if (request.Revision != data.Revision) throw new InvalidOperationException("记录已更新，请刷新设置后重试。");
            data.Settings = request.Settings; data.Revision++; Save(); return Get();
        }
    }
    public ScooterData Apply(ScooterAction a)
    {
        if (!Guid.TryParse(a.RequestId, out _) || a.Action is not ("full" or "start" or "stop" or "empty"))
            throw new ArgumentException("无效的记录操作。");
        if (a.DistanceKm is not null && (!double.IsFinite(a.DistanceKm.Value) || a.DistanceKm is < 0 or > 300))
            throw new ArgumentException("手动里程必须在 0–300 km 之间。");
        lock (gate)
        {
            if (LoadWarning is not null) throw new InvalidOperationException(LoadWarning);
            if (data.Requests.TryGetValue(a.RequestId, out var previous))
            { if (previous != a.Action) throw new ArgumentException("请勿复用请求编号。"); return Get(); }
            var now = DateTimeOffset.UtcNow; var ride = Active(data); var cycle = data.Cycles.LastOrDefault(c => c.EndedAt is null);
            if (a.Action == "stop" && data.Rides.Any(r => r.Id == a.RideId && r.StoppedAt is not null)) return Get();
            var stoppedAt = a.At ?? now;
            if (stoppedAt > now.AddSeconds(5) || (ride is not null && stoppedAt < ride.StartedAt)) throw new ArgumentException("停止时间不正确。");
            if (a.Action == "full")
            {
                if (ride is not null) throw new ArgumentException("请先停止骑行，再记录已充满。");
                if (cycle is not null) data.Cycles[data.Cycles.IndexOf(cycle)] = cycle with { EndedAt = now, EndReason = "recharged" };
                data.Cycles.Add(new(Guid.NewGuid().ToString("N"), now));
            }
            else if (a.Action == "start")
            {
                if (cycle is null) throw new ArgumentException("请充满电后先点击“已充满”。");
                if (ride is null) data.Rides.Add(new() { Id = Guid.NewGuid().ToString("N"), CycleId = cycle.Id, StartedAt = now });
            }
            else
            {
                if (a.Action == "stop" && (ride is null || ride.Id != a.RideId)) throw new ArgumentException("骑行已结束或编号不匹配。");
                if (ride is not null)
                {
                    if (a.RideId != ride.Id) throw new ArgumentException("请刷新当前骑行后再操作。");
                    if (a.DistanceKm is not null) { ride.Meters = a.DistanceKm.Value * 1000; ride.ManualDistance = true; }
                    ride.StoppedAt = stoppedAt;
                }
                if (a.Action == "empty")
                {
                    if (cycle is null) throw new ArgumentException("没有进行中的充电周期。");
                    data.Cycles[data.Cycles.IndexOf(cycle)] = cycle with { EndedAt = now, EndReason = "depleted" };
                }
            }
            data.Requests[a.RequestId] = a.Action;
            while (data.Requests.Count > 500) data.Requests.Remove(data.Requests.Keys.First());
            data.Revision++; Save(); return Get();
        }
    }
    public static bool ValidPoint(ScooterPoint p) => p.Seq >= 0 && double.IsFinite(p.Lat) && double.IsFinite(p.Lon)
        && p.Lat is >= -90 and <= 90 && p.Lon is >= -180 and <= 180 && double.IsFinite(p.Accuracy)
        && p.Accuracy is > 0 and <= 35 && p.At <= DateTimeOffset.UtcNow.AddMinutes(2);
    public ScooterData Add(ScooterBatch batch)
    {
        lock (gate)
        {
            if (LoadWarning is not null) throw new InvalidOperationException(LoadWarning);
            var ride = data.Rides.FirstOrDefault(r => r.Id == batch.RideId) ?? throw new ArgumentException("骑行不存在。");
            if (batch.Points is null || batch.Points.Length is < 1 or > 100) throw new ArgumentException("每批最多 100 个定位点。");
            var accepted = new List<ScooterPoint>();
            foreach (var p in batch.Points.OrderBy(p => p.Seq))
            {
                if (p.Seq <= ride.LastSeq) continue; // offline retries are idempotent
                ride.LastSeq = p.Seq;
                if (!ValidPoint(p) || p.At < ride.StartedAt.AddSeconds(-5) || p.At > (ride.StoppedAt ?? DateTimeOffset.MaxValue)) { ride.Rejected++; continue; }
                var last = ride.LastPoint;
                if (last is null && (p.At - ride.StartedAt).TotalSeconds > 120) ride.Gaps++;
                if (last is not null)
                {
                    var seconds = (p.At - last.At).TotalSeconds;
                    if (seconds <= 0) { ride.Rejected++; continue; }
                    var meters = Distance(last.Lat, last.Lon, p.Lat, p.Lon);
                    if (seconds > 120) { ride.Gaps++; ride.ElevationAnchor = null; }
                    else if (meters / seconds > 16.7) { ride.Rejected++; continue; }
                    else if (meters < Math.Max(3, (last.Accuracy + p.Accuracy) * .3)) continue;
                    else
                    {
                        if (!ride.ManualDistance) ride.Meters += meters;
                        if (p.Elevation.HasValue && last.Elevation.HasValue)
                        {
                            ride.TerrainMeters += meters;
                            var delta = p.Elevation.Value - (ride.ElevationAnchor ?? last.Elevation.Value);
                            if (Math.Abs(delta) >= 3)
                            { if (delta > 0) ride.Ascent += delta; else ride.Descent -= delta; ride.ElevationAnchor = p.Elevation; }
                        }
                    }
                }
                ride.ElevationAnchor ??= p.Elevation;
                ride.LastPoint = p; ride.Accepted++; accepted.Add(p);
            }
            Directory.CreateDirectory(directory);
            // Private per-ride trace, not the source checkout. Retained for terrain/model audits.
            if (accepted.Count > 0) File.AppendAllLines(Path.Combine(directory, ride.Id + ".jsonl"),
                accepted.Select(p => JsonSerializer.Serialize(p, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            data.Revision++; Save(); return Get();
        }
    }
    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        static double R(double x) => x * Math.PI / 180;
        var a = Math.Pow(Math.Sin(R(lat2 - lat1) / 2), 2) + Math.Cos(R(lat1)) * Math.Cos(R(lat2)) * Math.Pow(Math.Sin(R(lon2 - lon1) / 2), 2);
        return 6371000 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
    public static double Effort(ScooterSettings s, double meters, double ascent, double descent)
    {
        // Mechanical climbing work / estimated efficiency / flat Wh per km.
        // Unknown regeneration is NOT treated as free battery charge.
        var climb = s.TotalMassKg * 9.81 / (3600 * .72 * 12) * 1000;
        return Math.Max(meters * .8, meters + ascent * climb - Math.Min(meters * .2, descent * climb * .15));
    }
    public static ScooterModel Model(ScooterData d)
    {
        var values = d.Cycles.Where(c => c.EndReason == "depleted").Select(c =>
        {
            var rides = d.Rides.Where(r => r.CycleId == c.Id).ToArray();
            var meters = rides.Sum(r => r.Meters);
            return rides.Length > 0 && meters >= 1000 && rides.All(r => r.Gaps == 0 && !r.ManualDistance && r.LastPoint is not null && r.StoppedAt is not null &&
                    (r.StoppedAt.Value - r.LastPoint.At).TotalSeconds <= 120 && r.TerrainMeters >= r.Meters * .8)
                ? Effort(d.Settings, meters, rides.Sum(r => r.Ascent), rides.Sum(r => r.Descent)) : 0;
        }).Where(x => x > 0).TakeLast(20).ToArray();
        var sorted = values.Order().ToArray();
        var capacity = sorted.Length == 0 ? d.Settings.ReferenceRangeKm * 1000 : sorted[sorted.Length / 2];
        var lower = sorted.Length < 3 ? capacity * .65 : sorted[(int)Math.Floor((sorted.Length - 1) * .1)] * .9;
        double? error = values.Length < 4 ? null : values.Skip(3).Select((v, i) =>
            Math.Abs(values.Take(i + 3).Order().ElementAt((i + 3) / 2) - v) / v * 100).Average();
        return new(capacity, lower, values.Length, error, values.Length < 3 ? "未充分标定" : values.Length < 8 ? "初步标定" : "持续校准中");
    }
}
