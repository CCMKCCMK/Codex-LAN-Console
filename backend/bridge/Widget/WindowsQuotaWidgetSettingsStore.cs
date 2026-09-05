using System.Text.Json;

namespace CodexLanBridge;

public sealed record WindowsQuotaWidgetSettings(
    bool Enabled,
    double? Left,
    double? Top)
{
    public static WindowsQuotaWidgetSettings Default { get; } = new(true, null, null);
}

public sealed class WindowsQuotaWidgetSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private WindowsQuotaWidgetSettings _current;

    public WindowsQuotaWidgetSettingsStore()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLanConsole");
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "quota-widget-settings.json");
        _current = Load(_path);
    }

    public WindowsQuotaWidgetSettings Get()
    {
        lock (_gate) return _current;
    }

    public void SetEnabled(bool enabled) => Update(settings => settings with { Enabled = enabled });

    public void SavePosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)) return;
        Update(settings => settings with
        {
            Left = Math.Round(left, 1),
            Top = Math.Round(top, 1)
        });
    }

    public void ResetPosition() => Update(settings => settings with { Left = null, Top = null });

    private void Update(Func<WindowsQuotaWidgetSettings, WindowsQuotaWidgetSettings> transform)
    {
        lock (_gate)
        {
            _current = transform(_current);
            Save(_path, _current);
        }
    }

    private static WindowsQuotaWidgetSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return WindowsQuotaWidgetSettings.Default;
            return JsonSerializer.Deserialize<WindowsQuotaWidgetSettings>(File.ReadAllText(path), JsonOptions)
                   ?? WindowsQuotaWidgetSettings.Default;
        }
        catch
        {
            return WindowsQuotaWidgetSettings.Default;
        }
    }

    private static void Save(string path, WindowsQuotaWidgetSettings settings)
    {
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, path, true);
        }
        catch
        {
            // A widget preference must never stop the bridge.
        }
    }
}
