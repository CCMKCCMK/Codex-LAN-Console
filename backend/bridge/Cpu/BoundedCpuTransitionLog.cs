using System.Text;
using System.Text.Json;

namespace CodexLanBridge;

internal sealed class BoundedCpuTransitionLog
{
    private const long MaximumBytes = 128 * 1024;
    private const int RetainedBytes = 64 * 1024;
    private readonly object _gate = new();
    private readonly string _path;

    public BoundedCpuTransitionLog(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        _path = Path.Combine(storageDirectory, "cpu-guard-transitions.jsonl");
    }

    public void Write(CpuHealthState from, CpuHealthState to, string summary, CpuTelemetrySnapshot? telemetry)
    {
        if (from == to) return;
        var line = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.UtcNow,
            from,
            to,
            summary,
            utilityPercent = telemetry?.UtilityPercent,
            pCoreLoadPercent = telemetry?.PerformanceCoreLoadPercent,
            pCorePerformancePercent = telemetry?.PerformanceCorePerformancePercent,
            pCoreFrequencyMhz = telemetry?.PerformanceCoreFrequencyMhz,
            onAcPower = telemetry?.OnAcPower
        }) + Environment.NewLine;

        lock (_gate)
        {
            try
            {
                File.AppendAllText(_path, line, new UTF8Encoding(false));
                var info = new FileInfo(_path);
                if (info.Length <= MaximumBytes) return;
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                stream.Seek(Math.Max(0, stream.Length - RetainedBytes), SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true);
                if (stream.Position > 0) reader.ReadLine();
                var retained = reader.ReadToEnd();
                stream.SetLength(0);
                stream.Position = 0;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true);
                writer.Write(retained);
            }
            catch
            {
                // Diagnostics must never interfere with the bridge.
            }
        }
    }
}
