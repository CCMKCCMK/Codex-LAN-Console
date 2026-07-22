using System.Text;
using System.Text.Json;

namespace CodexLanBridge;

public sealed record ApprovalSettingsSnapshot(
    bool AutoApproveAll,
    string Scope,
    DateTimeOffset? UpdatedAt,
    long AutoApprovedCount,
    DateTimeOffset? LastAutoApprovedAt);

public sealed class ApprovalSettingsStore
{
    public const string EnableConfirmation = "AUTO APPROVE ALL";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private PersistedApprovalSettings _settings = new(false, null, 0, null);

    public ApprovalSettingsStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexLanConsole"))
    {
    }

    public ApprovalSettingsStore(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        Directory.CreateDirectory(storageDirectory);
        _path = Path.Combine(storageDirectory, "approval-settings.json");
        Load();
    }

    public ApprovalSettingsSnapshot Get()
    {
        lock (_gate) return Snapshot();
    }

    public ApprovalSettingsSnapshot SetAutoApproveAll(bool enabled)
    {
        lock (_gate)
        {
            var updated = _settings with
            {
                AutoApproveAll = enabled,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            Save(updated);
            _settings = updated;
            return Snapshot();
        }
    }

    public void RecordAutoApprovals(int count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            var updated = _settings with
            {
                AutoApprovedCount = checked(_settings.AutoApprovedCount + count),
                LastAutoApprovedAt = DateTimeOffset.UtcNow
            };
            Save(updated);
            _settings = updated;
        }
    }

    private ApprovalSettingsSnapshot Snapshot() => new(
        _settings.AutoApproveAll,
        "bridge",
        _settings.UpdatedAt,
        _settings.AutoApprovedCount,
        _settings.LastAutoApprovedAt);

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<PersistedApprovalSettings>(File.ReadAllText(_path), _json);
            if (loaded is not null && loaded.AutoApprovedCount >= 0) _settings = loaded;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not load approval settings: {ex.Message}");
        }
    }

    private void Save(PersistedApprovalSettings settings)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, _json), new UTF8Encoding(false));
            File.Move(temporary, _path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private sealed record PersistedApprovalSettings(
        bool AutoApproveAll,
        DateTimeOffset? UpdatedAt,
        long AutoApprovedCount,
        DateTimeOffset? LastAutoApprovedAt);
}
