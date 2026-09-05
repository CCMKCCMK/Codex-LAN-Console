using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexLanBridge;

internal sealed record CpuPowerPolicyValues(
    uint MinimumProcessorState,
    uint MaximumProcessorState,
    uint BoostMode,
    uint MaximumProcessorFrequencyMhz,
    uint CoolingPolicy);

internal sealed record CpuPowerPolicyBaseline(
    int SchemaVersion,
    string MachineFingerprint,
    string MachineName,
    DateTimeOffset CapturedAt,
    Guid SourceScheme,
    CpuPowerPolicyValues AcValues);

internal sealed class CpuPowerBaselineStore
{
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly string _machineFingerprint;

    public CpuPowerBaselineStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _path = System.IO.Path.Combine(localAppData, "CodexLanConsole", "cpu-power-baseline.json");
        _machineFingerprint = GetMachineFingerprint();
    }

    public string Path => _path;

    public bool TryLoad(out CpuPowerPolicyBaseline? baseline, out string? error)
    {
        baseline = null;
        error = null;

        if (!File.Exists(_path))
            return false;

        try
        {
            var json = File.ReadAllText(_path);
            baseline = JsonSerializer.Deserialize<CpuPowerPolicyBaseline>(json, JsonOptions);
            if (baseline is null)
            {
                error = $"The CPU power baseline is empty or invalid: {_path}";
                return false;
            }

            if (baseline.SchemaVersion != CurrentSchemaVersion)
            {
                error = $"The CPU power baseline schema is unsupported ({baseline.SchemaVersion}): {_path}";
                baseline = null;
                return false;
            }

            if (!string.Equals(baseline.MachineFingerprint, _machineFingerprint, StringComparison.Ordinal))
            {
                error = $"The saved CPU power baseline belongs to a different machine: {_path}";
                baseline = null;
                return false;
            }

            if (!Validate(baseline.AcValues, out error))
            {
                baseline = null;
                error = $"The saved CPU power baseline is unsafe: {error} ({_path})";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not read the CPU power baseline at {_path}: {ex.Message}";
            baseline = null;
            return false;
        }
    }

    public bool TrySave(Guid sourceScheme, CpuPowerPolicyValues values, out CpuPowerPolicyBaseline? baseline, out string? error)
    {
        baseline = null;
        error = null;

        if (!Validate(values, out error))
            return false;

        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The CPU power baseline directory is unavailable.");
            Directory.CreateDirectory(directory);

            baseline = new CpuPowerPolicyBaseline(
                CurrentSchemaVersion,
                _machineFingerprint,
                Environment.MachineName,
                DateTimeOffset.UtcNow,
                sourceScheme,
                values);

            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(baseline, JsonOptions), new UTF8Encoding(false));
                File.Move(temporaryPath, _path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not save the CPU power baseline at {_path}: {ex.Message}";
            baseline = null;
            return false;
        }
    }

    private static bool Validate(CpuPowerPolicyValues values, out string? error)
    {
        if (values.MinimumProcessorState > 100)
        {
            error = "minimum processor state is outside 0-100 percent";
            return false;
        }

        if (values.MaximumProcessorState is 0 or > 100 ||
            values.MinimumProcessorState > values.MaximumProcessorState)
        {
            error = "maximum processor state is invalid";
            return false;
        }

        if (values.BoostMode > 7)
        {
            error = "processor boost mode is outside the Windows-defined range";
            return false;
        }

        if (values.MaximumProcessorFrequencyMhz > 100_000)
        {
            error = "maximum processor frequency is implausible";
            return false;
        }

        // Windows defines 0 as passive and 1 as active cooling. This guard only
        // accepts an active-cooling baseline so it can never preserve passive
        // cooling as a supposedly healthy performance configuration.
        if (values.CoolingPolicy != 1)
        {
            error = "cooling policy is not active (Windows active index is 1)";
            return false;
        }

        error = null;
        return true;
    }

    private static string GetMachineFingerprint()
    {
        string source;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            source = key?.GetValue("MachineGuid") as string
                ?? $"{Environment.MachineName}|{Environment.OSVersion.VersionString}|{Environment.ProcessorCount}";
        }
        catch
        {
            source = $"{Environment.MachineName}|{Environment.OSVersion.VersionString}|{Environment.ProcessorCount}";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}
