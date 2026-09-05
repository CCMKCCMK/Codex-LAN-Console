using System.Globalization;
using System.Text.Json;

namespace CodexLanBridge.Commute;

public sealed record Place(string Name, double Lat, double Lon);
public sealed record CommuteSettings
{
    // Public campus example only. Existing private settings are loaded from AppData.
    public Place Home { get; init; } = new("设置出发地（示例：Geisel Library）", 32.88114, -117.23757);
    public Place Campus { get; init; } = new("HDSI · 3234 Matthews Lane", 32.8805631, -117.2338242);
    public string MorningArrival { get; init; } = "09:00";
    public string EveningArrival { get; init; } = "18:00";
    public int[] Days { get; init; } = [1, 2, 3, 4, 5];
    public bool RemindersEnabled { get; init; }
    public int RemindMinutes { get; init; } = 10;
    public int BufferMinutes { get; init; } = 5;
    public int ParkingMinutes { get; init; } = 12;
    public double WalkKph { get; init; } = 4.5;
    public double BikeKph { get; init; } = 15;
    public double ScooterKph { get; init; } = 12;
    public string[] Preferred { get; init; } = [];
    public Dictionary<string, string> Vehicles { get; init; } = new()
        { ["bike"] = "unavailable", ["scooter"] = "unavailable", ["car"] = "unavailable" };
}
public sealed record Journey(string Id, string Direction, string Mode, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, double DistanceMeters, double ExpectedMinutes, string? Rating = null);
public sealed record CommuteState(int Revision, CommuteSettings Settings, Journey? ActiveTrip,
    List<Journey> History, string Location = "home");
public sealed record SaveCommuteRequest(int Revision, CommuteSettings Settings);
public sealed record StartJourneyRequest(string Direction, string Mode, double DistanceMeters, double ExpectedMinutes);
public sealed record FinishJourneyRequest(string Id, bool Cancel = false, string? Rating = null);

public sealed class CommuteStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private CommuteState state = new(0, new(), null, []);
    public string? LoadWarning { get; private set; }
    public CommuteStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexLanConsole", "commute.json")) { }
    internal CommuteStore(string file)
    {
        path = file;
        if (!File.Exists(path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<CommuteState>(File.ReadAllText(path), json)
                ?? throw new InvalidDataException("Empty commute state.");
            Validate(loaded.Settings);
            if (loaded.History is null) throw new InvalidDataException("Missing trip history.");
            state = loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A damaged optional feature must never prevent the main Bridge from starting.
            // The original remains untouched; a backup is mandatory before a later explicit save.
            LoadWarning = "通勤设置文件未能读取，原文件已保留。当前显示默认设置，请检查后重新保存。";
            Console.Error.WriteLine("Commute state could not be loaded: " + ex.GetType().Name);
        }
    }
    public CommuteState Get() { lock (gate) return Clone(); }
    private CommuteState Clone() => JsonSerializer.Deserialize<CommuteState>(JsonSerializer.Serialize(state, json), json)!;
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (LoadWarning is not null && File.Exists(path))
            File.Copy(path, path + ".recovery-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, json));
        File.Move(temp, path, true);
        LoadWarning = null;
    }
    public CommuteState Update(SaveCommuteRequest request)
    {
        Validate(request.Settings);
        lock (gate)
        {
            if (request.Revision != state.Revision) throw new InvalidOperationException("通勤设置已在另一台设备修改，请刷新后重试。");
            state = state with { Revision = state.Revision + 1, Settings = request.Settings };
            Save(); return Clone();
        }
    }
    public CommuteState Start(StartJourneyRequest request)
    {
        ValidateDirection(request.Direction);
        if (!Modes.Contains(request.Mode) || !double.IsFinite(request.DistanceMeters) || request.DistanceMeters is < 0 or > 200000 ||
            !double.IsFinite(request.ExpectedMinutes) || request.ExpectedMinutes is <= 0 or > 600)
            throw new ArgumentException("行程参数不正确。");
        lock (gate)
        {
            if (state.ActiveTrip is not null) throw new ArgumentException("请先结束或取消当前行程。");
            if (!Available(state.Settings, request.Mode, request.Direction)) throw new ArgumentException("这辆车不在出发地点，请先更新它的位置。");
            state = state with { Revision = state.Revision + 1, Location = "travelling",
                ActiveTrip = new(Guid.NewGuid().ToString("N"), request.Direction, request.Mode, DateTimeOffset.UtcNow, null,
                    request.DistanceMeters, request.ExpectedMinutes) };
            Save(); return Clone();
        }
    }
    public CommuteState Finish(FinishJourneyRequest request)
    {
        lock (gate)
        {
            var trip = state.ActiveTrip;
            if (trip is null || trip.Id != request.Id) throw new ArgumentException("当前行程已发生变化，请刷新后重试。");
            var origin = trip.Direction == "toCampus" ? "home" : "campus";
            var destination = trip.Direction == "toCampus" ? "campus" : "home";
            if (!request.Cancel)
            {
                state.History.Insert(0, trip with { FinishedAt = DateTimeOffset.UtcNow,
                    Rating = request.Rating is "good" or "bad" ? request.Rating : null });
                if (state.History.Count > 300) state.History.RemoveRange(300, state.History.Count - 300);
                if (state.Settings.Vehicles.ContainsKey(trip.Mode)) state.Settings.Vehicles[trip.Mode] = destination;
            }
            state = state with { Revision = state.Revision + 1, ActiveTrip = null, Location = request.Cancel ? origin : destination };
            Save(); return Clone();
        }
    }
    public static readonly string[] Modes = ["walk", "bus", "bike", "scooter", "car"];
    public static bool Available(CommuteSettings settings, string mode, string direction) =>
        mode is "walk" or "bus" || settings.Vehicles.GetValueOrDefault(mode) == (direction == "toCampus" ? "home" : "campus");
    public static void ValidateDirection(string direction)
    {
        if (direction is not ("toCampus" or "toHome")) throw new ArgumentException("请选择去 HDSI 或回家。");
    }
    public static void ValidatePlace(Place place)
    {
        if (place is null || string.IsNullOrWhiteSpace(place.Name) || place.Name.Length > 160 || !double.IsFinite(place.Lat) ||
            !double.IsFinite(place.Lon) || place.Lat is < 32.5 or > 33.5 || place.Lon is < -117.6 or > -116.5)
            throw new ArgumentException("目前支持圣地亚哥地区，请检查地点和坐标。");
    }
    internal static void Validate(CommuteSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        ValidatePlace(s.Home); ValidatePlace(s.Campus);
        if (!TimeOnly.TryParseExact(s.MorningArrival, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !TimeOnly.TryParseExact(s.EveningArrival, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            s.Days is null || s.Days.Any(d => d is < 0 or > 6) || s.Days.Distinct().Count() != s.Days.Length ||
            s.RemindMinutes is < 1 or > 60 || s.BufferMinutes is < 0 or > 60 || s.ParkingMinutes is < 0 or > 60 ||
            !double.IsFinite(s.WalkKph) || s.WalkKph is < 2 or > 7 || !double.IsFinite(s.BikeKph) || s.BikeKph is < 5 or > 30 ||
            !double.IsFinite(s.ScooterKph) || s.ScooterKph is < 5 or > 24 || s.Preferred is null ||
            s.Preferred.Length > 2 || s.Preferred.Distinct().Count() != s.Preferred.Length || s.Preferred.Any(p => !Modes.Contains(p)) ||
            s.Vehicles is null || s.Vehicles.Count != 3 || new[] { "bike", "scooter", "car" }.Any(k => !s.Vehicles.ContainsKey(k)) ||
            s.Vehicles.Values.Any(v => v is not ("home" or "campus" or "unavailable")))
            throw new ArgumentException("请检查时间、提醒间隔、速度和交通工具设置。");
    }
}
