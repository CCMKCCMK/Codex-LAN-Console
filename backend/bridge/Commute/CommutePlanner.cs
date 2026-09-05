using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexLanBridge.Commute;

public sealed record PlanRequest(string Direction = "toCampus", string? When = null, bool ArriveBy = false);
public sealed record RouteLeg(string Mode, string From, string To, double Minutes, double Meters,
    long StartTime, long EndTime, string Route, string? StopId, string? RouteId, string? Geometry,
    bool RealTime, string[] Streets);
public sealed record CommuteOption(string Id, string Mode, string Title, bool Available, string? UnavailableReason,
    double Minutes, double MovingMinutes, double WalkMinutes, double WaitMinutes, double DistanceMeters,
    DateTimeOffset LeaveAt, DateTimeOffset ArriveAt, bool RealTime, bool OnTime, string Basis,
    int Samples, double Score, RouteLeg[] Legs, string[] Notes);
public sealed record PlanResult(DateTimeOffset UpdatedAt, string Direction, Place From, Place To,
    bool ArriveBy, DateTimeOffset RequestedTime, CommuteOption[] Options, string? RecommendedId, string[] Warnings);

public sealed class CommutePlanner
{
    public const string Source = "https://wayfinder.ucsd.onebusawaycloud.com";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(18), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private readonly ConcurrentDictionary<string, (DateTimeOffset Time, JsonElement Data)> cache = new();
    private readonly SemaphoreSlim gate = new(4);
    private readonly CommuteStore store;
    public static TimeZoneInfo Pacific { get; } = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    public CommutePlanner(CommuteStore store) { this.store = store; http.DefaultRequestHeaders.UserAgent.ParseAdd("TritonCommute/1.0 (personal commute planner)"); }
    public static DateTimeOffset LocalTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Pacific);
    public static DateTimeOffset ParseLocal(string text)
    {
        if (!DateTime.TryParseExact(text, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new ArgumentException("请提供圣地亚哥当地日期和时间。");
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Pacific.IsInvalidTime(local) || Pacific.IsAmbiguousTime(local)) throw new ArgumentException("此时间位于夏令时切换区间，请换一个时间。");
        return new DateTimeOffset(local, Pacific.GetUtcOffset(local));
    }
    private async Task<JsonElement> Get(string endpoint, int cacheSeconds, CancellationToken ct)
    {
        if (cache.TryGetValue(endpoint, out var old) && DateTimeOffset.UtcNow - old.Time < TimeSpan.FromSeconds(cacheSeconds)) return old.Data;
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(endpoint, out old) && DateTimeOffset.UtcNow - old.Time < TimeSpan.FromSeconds(cacheSeconds)) return old.Data;
            using var response = await http.GetAsync(Source + "/api/" + endpoint, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement.Clone();
            if (N(root, "code") is > 0 and not 200 || P(root, "error").ValueKind is JsonValueKind.Object or JsonValueKind.String)
                throw new InvalidOperationException("交通数据服务暂时无法提供这条路线。");
            if (cache.Count > 180) foreach (var key in cache.OrderBy(x => x.Value.Time).Take(80).Select(x => x.Key)) cache.TryRemove(key, out _);
            cache[endpoint] = (DateTimeOffset.UtcNow, root);
            return root;
        }
        finally { gate.Release(); }
    }
    public async Task<Place> Geocode(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 180) throw new ArgumentException("请输入地点名称。");
        var result = await Get("oba/geocode-location?query=" + Uri.EscapeDataString(query), 86400, ct);
        var loc = P(result, "location"); var geo = P(P(loc, "geometry"), "location");
        var place = new Place(S(loc, "formatted_address"), N(geo, "lat"), N(geo, "lng"));
        CommuteStore.ValidatePlace(place); return place;
    }
    public async Task<PlanResult> Plan(PlanRequest request, CancellationToken ct)
    {
        CommuteStore.ValidateDirection(request.Direction);
        var now = DateTimeOffset.UtcNow;
        var when = request.When is null ? now : ParseLocal(request.When);
        if (when < now.AddMinutes(-1) || when > now.AddDays(14)) throw new ArgumentException("请选择现在至未来 14 天内的时间。");
        var state = store.Get(); var s = state.Settings;
        var from = request.Direction == "toCampus" ? s.Home : s.Campus;
        var to = request.Direction == "toCampus" ? s.Campus : s.Home;
        var warnings = new ConcurrentBag<string>();
        var tasks = new[] { "walk", "bus", "bike", "car" }.Select(async mode =>
        {
            try
            {
                var prep = mode is "bike" or "car" ? 3 : 1;
                var park = mode == "car" ? s.ParkingMinutes : 0;
                var routeTime = request.ArriveBy ? when.AddMinutes(-s.BufferMinutes - park) : when.AddMinutes(prep);
                var local = LocalTime(routeTime);
                string Coord(Place p) => FormattableString.Invariant($"{p.Lat:F7},{p.Lon:F7}");
                var endpoint = $"otp/plan?fromPlace={Coord(from)}&toPlace={Coord(to)}&mode={(mode == "bus" ? "TRANSIT,WALK" : mode == "bike" ? "BICYCLE" : mode == "car" ? "CAR" : "WALK")}" +
                    $"&arriveBy={request.ArriveBy.ToString().ToLowerInvariant()}&date={local:yyyy-MM-dd}&time={local:HH:mm}";
                var result = await Get(endpoint, 30, ct);
                return A(P(result, "plan"), "itineraries").Take(6).SelectMany(itinerary =>
                {
                    var variants = mode == "bike" ? new[] { "bike", "scooter" } : new[] { mode };
                    return variants.Select(m => Normalize(itinerary, m, request, state, when, now));
                }).Where(x => x is not null).Cast<CommuteOption>().ToArray();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            { warnings.Add($"{Name(mode)}路线暂不可用，请稍后刷新或打开官方地图。"); return []; }
        });
        var options = (await Task.WhenAll(tasks)).SelectMany(x => x).OrderBy(x => x.Score).ToArray();
        var recommended = options.Where(x => x.Available && x.OnTime).FirstOrDefault() ?? options.FirstOrDefault(x => x.Available);
        if (options.Any(x => x.Mode == "bus") && !options.Any(x => x.Mode == "bus" && x.RealTime))
            warnings.Add("公交方案目前按时刻表计算；临出发时再核对实时到站信息。");
        if (!options.Any(x => x.Mode == "bus")) warnings.Add("本次查询没有可行的公交方案；可能未运营、赶不上班次或数据暂缺，不代表所有线路停运。");
        return new(now, request.Direction, from, to, request.ArriveBy, when, options, recommended?.Id, warnings.ToArray());
    }
    internal static CommuteOption? Normalize(JsonElement itinerary, string mode, PlanRequest request,
        CommuteState state, DateTimeOffset when, DateTimeOffset now)
    {
        var s = state.Settings; var raw = A(itinerary, "legs");
        var transit = raw.Where(l => B(l, "transitLeg")).ToArray();
        if (mode == "bus" && transit.Length == 0 || raw.Length == 0) return null;
        var samples = state.History.Where(t => t.Mode == mode && t.Direction == request.Direction && t.FinishedAt is not null &&
            t.DistanceMeters > 200 && (t.FinishedAt.Value - t.StartedAt).TotalMinutes is > 1 and < 120).Take(15).ToArray();
        var baseSpeed = mode switch { "bike" => s.BikeKph, "scooter" => s.ScooterKph, _ => s.WalkKph };
        // Completed door-to-door trips provide a conservative personal speed; never infer ownership from preferences.
        var speed = samples.Length >= 3 && mode is "walk" or "bike" or "scooter"
            ? Math.Clamp(samples.Select(t => t.DistanceMeters / 1000 / ((t.FinishedAt!.Value - t.StartedAt).TotalHours))
                .Order().ElementAt(samples.Length / 2), baseSpeed * .5, baseSpeed * 1.3) : baseSpeed;
        var prep = mode is "bike" or "scooter" or "car" ? 3d : 1d;
        var park = mode == "car" ? s.ParkingMinutes : 0;
        var distance = raw.Sum(l => N(l, "distance"));
        double Duration(JsonElement leg) => mode switch
        {
            "walk" or "bike" or "scooter" => N(leg, "distance") / (speed * 1000 / 60),
            "bus" when S(leg, "mode") == "WALK" => N(leg, "distance") / (s.WalkKph * 1000 / 60),
            _ => N(leg, "duration") / 60
        };
        var legs = new List<RouteLeg>();
        var leave = when; var cursor = when.AddMinutes(prep); double wait = 0, walk = 0;
        if (request.ArriveBy)
        {
            if (transit.Length > 0)
            {
                var approach = raw.TakeWhile(l => !B(l, "transitLeg")).Sum(Duration);
                cursor = Epoch(N(transit[0], "startTime")).AddMinutes(-approach - 2);
                leave = cursor.AddMinutes(-prep);
            }
            else
            {
                cursor = when.AddMinutes(-s.BufferMinutes - park - raw.Sum(Duration));
                leave = cursor.AddMinutes(-prep);
            }
        }
        foreach (var leg in raw)
        {
            var duration = Duration(leg); var start = cursor;
            if (B(leg, "transitLeg"))
            {
                start = Epoch(N(leg, "startTime"));
                // Never recommend a bus the user's slower approach or transfer can no longer catch.
                if (cursor.AddMinutes(1) > start) return null;
                wait += (start - cursor).TotalMinutes;
                cursor = Epoch(N(leg, "endTime"));
            }
            else { cursor = cursor.AddMinutes(duration); if (S(leg, "mode") == "WALK") walk += duration; }
            var a = P(leg, "from"); var b = P(leg, "to"); var agency = S(leg, "agencyId");
            string? ObaId(string id) => string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(agency) ? null : agency + "_" + id[(id.IndexOf(':') + 1)..];
            legs.Add(new(S(leg, "mode"), S(a, "name"), S(b, "name"), duration, N(leg, "distance"),
                start.ToUnixTimeMilliseconds(), cursor.ToUnixTimeMilliseconds(), S(leg, "route"),
                ObaId(S(a, "stopId")), ObaId(S(leg, "routeId")), S(P(leg, "legGeometry"), "points"),
                B(leg, "realTime"), A(leg, "steps").Select(x => S(x, "streetName")).Where(x => x.Length > 0).Distinct().Take(12).ToArray()));
        }
        cursor = cursor.AddMinutes(park);
        if (request.ArriveBy && cursor > when.AddMinutes(-s.BufferMinutes + 1)) return null;
        if (leave < now.AddMinutes(-1)) return null;
        var minutes = Math.Ceiling((cursor - leave).TotalMinutes);
        var available = CommuteStore.Available(s, mode, request.Direction);
        var pref = Array.IndexOf(s.Preferred, mode);
        var learned = s.Preferred.Length == 0 && state.History.Take(20).Count(x => x.Mode == mode && x.Rating != "bad") >= 3;
        // An early-arriving bus can be short on board yet require leaving home much earlier.
        // Count that extra time in arrive-by ranking instead of optimizing ride duration alone.
        var earlyArrival = request.ArriveBy ? Math.Max(0, (when.AddMinutes(-s.BufferMinutes) - cursor).TotalMinutes) : 0;
        var score = minutes + earlyArrival + (mode == "bus" ? walk * .15 + N(itinerary, "transfers") * 3 : 0) - (pref >= 0 ? 3 - pref : learned ? 2 : 0);
        var title = mode == "bus" ? string.Join(" → ", transit.Select(x => S(x, "route"))) + " 公交" : Name(mode);
        var notes = new List<string> { $"包含 {prep:0} 分钟出门准备" };
        if (mode == "car") notes.Add($"另外预留 {park} 分钟停车及步行；无实时路况或车位保证，请按实际停车点调节");
        if (mode == "scooter") notes.Add("按自行车路网估算；不代表全程允许骑行，遇人行道、禁行区请下车推行");
        if (mode is "bike" or "scooter") notes.Add("上下坡、等红灯会影响速度；骑行时间为估计值");
        if (pref >= 0) notes.Add("已考虑你选择的偏好（最多优先 3 分钟）");
        if (learned) notes.Add("偏好根据最近已完成行程推测，可随时修改");
        var realtime = transit.Length > 0 && transit.All(l => B(l, "realTime")) && (when - now).Duration() < TimeSpan.FromHours(2);
        return new($"{mode}-{string.Join('-', transit.Select(x => S(x, "tripId")))}-{legs[0].StartTime}", mode, title,
            available, available ? null : "不在出发地点或尚未拥有", minutes, minutes - wait, walk, wait, distance,
            leave, cursor, realtime, !request.ArriveBy || cursor <= when, mode == "bus" ? realtime ? "实时预测" : "时刻表" :
                samples.Length >= 3 ? "历史行程校准" : "道路距离估算", samples.Length, score, legs.ToArray(), notes.ToArray());
    }
    public async Task<object> Live(string direction, string? stopId, string? routeId, CancellationToken ct)
    {
        CommuteStore.ValidateDirection(direction);
        stopId ??= direction == "toCampus" ? "UCSD_9912" : "UCSD_10772";
        routeId ??= "UCSD_1050";
        if (!Regex.IsMatch(stopId, @"^(UCSD|MTS|NCTD)_[A-Za-z0-9_-]{1,80}$") ||
            !Regex.IsMatch(routeId, @"^(UCSD|MTS|NCTD)_[A-Za-z0-9_-]{1,80}$")) throw new ArgumentException("站点或线路编号无效。");
        var errors = new ConcurrentBag<string>();
        async Task<JsonElement> Safe(string endpoint, int seconds)
        {
            try { return await Get(endpoint, seconds, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            { errors.Add("部分公交数据暂不可用；不会用旧位置冒充实时位置。"); return default; }
        }
        var arrivalsTask = Safe($"oba/arrivals-and-departures-for-stop/{stopId}?minutesAfter=90", 30);
        var vehiclesTask = Safe($"oba/trips-for-route/{routeId}", 30);
        var stopsTask = Safe($"oba/stops-for-route/{routeId}", 3600);
        await Task.WhenAll(arrivalsTask, vehiclesTask, stopsTask);
        var arrivals = await arrivalsTask; var vehicles = await vehiclesTask; var stops = await stopsTask;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool Fresh(JsonElement x, string field) => N(x, field) > 0 && now - N(x, field) is >= -60000 and < 180000;
        var stopList = A(P(P(stops, "data"), "references"), "stops");
        var stop = stopList.FirstOrDefault(x => S(x, "id") == stopId);
        var departures = A(P(P(arrivals, "data"), "entry"), "arrivalsAndDepartures")
            .Where(x => P(x, "departureEnabled").ValueKind != JsonValueKind.False)
            .Select(x =>
            {
                var live = B(x, "predicted") && Fresh(x, "lastUpdateTime") && N(x, "predictedDepartureTime") > 0;
                var time = live ? N(x, "predictedDepartureTime") : N(x, "scheduledDepartureTime");
                return new { route = S(x, "routeShortName"), routeId = S(x, "routeId"), destination = S(x, "tripHeadsign"),
                    departureTime = time, scheduledTime = N(x, "scheduledDepartureTime"), realtime = live,
                    stopsAway = N(x, "numberOfStopsAway"), updatedAt = N(x, "lastUpdateTime"), tripId = S(x, "tripId") };
            }).Where(x => x.departureTime > now - 30000).OrderBy(x => x.departureTime).Take(10).ToArray();
        var positions = A(P(vehicles, "data"), "list").Select(x => P(x, "status"))
            .Where(x => Fresh(x, "lastLocationUpdateTime") && N(P(x, "lastKnownLocation"), "lat") != 0)
            .Select(x => new { id = S(x, "vehicleId"), lat = N(P(x, "lastKnownLocation"), "lat"),
                lon = N(P(x, "lastKnownLocation"), "lon"), updatedAt = N(x, "lastLocationUpdateTime"), nextStop = S(x, "nextStop") })
            .DistinctBy(x => x.id).ToArray();
        return new { updatedAt = DateTimeOffset.UtcNow, sourceTime = N(arrivals, "currentTime"), stopId, routeId,
            stopName = S(stop, "name"), departures, vehicles = positions,
            stops = stopList.Select(x => new { id = S(x, "id"), name = S(x, "name"), lat = N(x, "lat"), lon = N(x, "lon") }),
            geometry = A(P(P(stops, "data"), "entry"), "polylines").Select(x => S(x, "points")),
            errors = errors.Distinct().ToArray(), source = Source,
            serviceNote = routeId == "UCSD_1050" ? "Mesa Loop：官网列示工作日 07:30–18:00，节假日除外；高峰约每 30 分钟。以当天班次及公告为准。" : "运行时间以该线路当天的官方班次为准。" };
    }
    public static string Name(string mode) => mode switch { "walk" => "步行", "bus" => "公交", "bike" => "自行车", "scooter" => "Scooter", "car" => "开车", _ => mode };
    private static DateTimeOffset Epoch(double millis) => DateTimeOffset.FromUnixTimeMilliseconds((long)millis);
    internal static JsonElement P(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
    internal static string S(JsonElement e, string key) => P(e, key).ValueKind == JsonValueKind.String ? P(e, key).GetString()! : "";
    internal static double N(JsonElement e, string key) => P(e, key).ValueKind == JsonValueKind.Number && P(e, key).TryGetDouble(out var d) ? d : 0;
    internal static bool B(JsonElement e, string key) => P(e, key).ValueKind == JsonValueKind.True;
    internal static JsonElement[] A(JsonElement e, string key) => P(e, key).ValueKind == JsonValueKind.Array ? P(e, key).EnumerateArray().ToArray() : [];
}
