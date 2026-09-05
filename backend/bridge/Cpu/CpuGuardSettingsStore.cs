using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexLanBridge;

public sealed class CpuGuardSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly string _path;
    private PersistedSettings _settings;

    public CpuGuardSettingsStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexLanConsole"))
    {
    }

    public CpuGuardSettingsStore(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        Directory.CreateDirectory(storageDirectory);
        _path = Path.Combine(storageDirectory, "cpu-guard-settings.json");
        _settings = Load(_path);
    }

    public CpuGuardSettingsSnapshot Get()
    {
        lock (_gate) return ToSnapshot(_settings);
    }

    public CpuGuardSettingsSnapshot SetMode(CpuGuardMode mode)
    {
        lock (_gate)
        {
            _settings = _settings with { Mode = mode, UpdatedAt = DateTimeOffset.UtcNow };
            Save(_settings);
            return ToSnapshot(_settings);
        }
    }

    public CpuGuardSettingsSnapshot RecordRepair(DateTimeOffset repairedAt)
    {
        lock (_gate)
        {
            var cutoff = repairedAt - TimeSpan.FromHours(24);
            var repairs = _settings.RecentRepairs
                .Where(value => value >= cutoff)
                .Append(repairedAt)
                .TakeLast(16)
                .ToArray();
            _settings = _settings with
            {
                UpdatedAt = repairedAt,
                LastRepairAt = repairedAt,
                RecentRepairs = repairs
            };
            Save(_settings);
            return ToSnapshot(_settings);
        }
    }

    private static CpuGuardSettingsSnapshot ToSnapshot(PersistedSettings settings) =>
        new(settings.Mode, settings.UpdatedAt, settings.LastRepairAt, settings.RecentRepairs.ToArray());

    private static PersistedSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return PersistedSettings.Default;
            var value = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(path), JsonOptions);
            if (value is null || !Enum.IsDefined(value.Mode)) return PersistedSettings.Default;
            return value with { RecentRepairs = value.RecentRepairs?.TakeLast(16).ToArray() ?? [] };
        }
        catch
        {
            return PersistedSettings.Default;
        }
    }

    private void Save(PersistedSettings settings)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, _path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private sealed record PersistedSettings(
        CpuGuardMode Mode,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? LastRepairAt,
        DateTimeOffset[] RecentRepairs)
    {
        public static PersistedSettings Default { get; } =
            new(CpuGuardMode.Monitor, DateTimeOffset.UtcNow, null, []);
    }
}

