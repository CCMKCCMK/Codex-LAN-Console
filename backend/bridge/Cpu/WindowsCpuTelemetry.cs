using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexLanBridge;

public sealed class WindowsCpuTelemetry : IDisposable
{
    private const uint PdhFormatDouble = 0x00000200;
    private const uint ProcessorInformation = 11;
    private const uint ErrorSuccess = 0;
    private readonly IntPtr _query;
    private readonly Counter? _totalLoad;
    private readonly Counter? _totalPerformance;
    private readonly Counter? _totalFrequency;
    private readonly Counter? _totalLimitFlags;
    private readonly IReadOnlyList<LogicalProcessor> _processors = [];
    private readonly IReadOnlyList<CoreCounters> _coreCounters = [];
    private bool _disposed;

    public WindowsCpuTelemetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        _processors = ReadLogicalProcessors();
        if (PdhOpenQuery(null, UIntPtr.Zero, out _query) != ErrorSuccess) return;
        _totalLoad = AddCounter(@"\Processor Information(_Total)\% Processor Time");
        _totalPerformance = AddCounter(@"\Processor Information(_Total)\% Processor Performance");
        _totalFrequency = AddCounter(@"\Processor Information(_Total)\Actual Frequency");
        _totalLimitFlags = AddCounter(@"\Processor Information(_Total)\Performance Limit Flags");

        var highestEfficiencyClass = _processors.Count == 0 ? (byte)0 : _processors.Max(item => item.EfficiencyClass);
        var selected = _processors.Where(item => item.EfficiencyClass == highestEfficiencyClass).ToArray();
        if (selected.Length == 0) selected = _processors.ToArray();
        _coreCounters = selected.Select(processor => new CoreCounters(
            processor,
            AddCounter($@"\Processor Information({processor.Group},{processor.LogicalProcessorIndex})\% Processor Time"),
            AddCounter($@"\Processor Information({processor.Group},{processor.LogicalProcessorIndex})\% Processor Performance"),
            AddCounter($@"\Processor Information({processor.Group},{processor.LogicalProcessorIndex})\Actual Frequency"),
            AddCounter($@"\Processor Information({processor.Group},{processor.LogicalProcessorIndex})\Performance Limit Flags")))
            .ToArray();
        PdhCollectQueryData(_query);
    }

    public CpuTelemetrySnapshot Read()
    {
        if (_disposed || !OperatingSystem.IsWindows() || _query == IntPtr.Zero)
            return Unavailable("Windows processor telemetry is unavailable.");

        try
        {
            var status = PdhCollectQueryData(_query);
            if (status != ErrorSuccess) return Unavailable($"PDH collection failed with 0x{status:X8}.");

            var power = ReadProcessorPowerInformation();
            var cores = new List<CpuCoreTelemetry>(_coreCounters.Count);
            foreach (var item in _coreCounters)
            {
                ProcessorPowerInfo? powerInfo = null;
                if (power is not null && item.Processor.Group == 0 &&
                    power.TryGetValue(item.Processor.LogicalProcessorIndex, out var mappedPowerInfo))
                    powerInfo = mappedPowerInfo;
                cores.Add(new CpuCoreTelemetry(
                    item.Processor.Group,
                    item.Processor.LogicalProcessorIndex,
                    item.Processor.EfficiencyClass,
                    true,
                    ClampPercent(ReadValue(item.Load)),
                    ReadValue(item.Performance),
                    ReadValue(item.Frequency),
                    ReadFlags(item.LimitFlags),
                    powerInfo?.MaxMhz,
                    powerInfo?.MhzLimit));
            }

            // Averages can combine one loaded thread with several parked siblings and create a
            // false low-frequency diagnosis. Keep every health input bound to the same busiest
            // P-core thread instead.
            var busiestCore = cores
                .Where(item => item.LoadPercent.HasValue)
                .OrderByDescending(item => item.LoadPercent)
                .FirstOrDefault();
            var busiestLimitRatio = busiestCore is { MaximumMhz: > 0, FirmwareLimitMhz: not null }
                ? (double)busiestCore.FirmwareLimitMhz.Value / busiestCore.MaximumMhz.Value
                : (double?)null;

            return new CpuTelemetrySnapshot(
                DateTimeOffset.UtcNow,
                true,
                IsOnAcPower(),
                ClampPercent(ReadValue(_totalLoad)),
                busiestCore?.LoadPercent,
                ReadValue(_totalPerformance),
                ReadFlags(_totalLimitFlags),
                ReadValue(_totalFrequency),
                busiestCore?.FrequencyMhz,
                busiestCore?.PerformancePercent,
                busiestCore?.PerformanceLimitFlags,
                busiestLimitRatio,
                cores,
                null);
        }
        catch (Exception ex)
        {
            return Unavailable(ex.Message);
        }
    }

    private Counter? AddCounter(string path)
    {
        if (_query == IntPtr.Zero) return null;
        var status = PdhAddEnglishCounter(_query, path, UIntPtr.Zero, out var handle);
        return status == ErrorSuccess ? new Counter(handle) : null;
    }

    private static double? ReadValue(Counter? counter)
    {
        if (counter is null || counter.Handle == IntPtr.Zero) return null;
        var status = PdhGetFormattedCounterValue(counter.Handle, PdhFormatDouble, out _, out var value);
        if (status != ErrorSuccess || value.Status != ErrorSuccess || !double.IsFinite(value.DoubleValue)) return null;
        return value.DoubleValue;
    }

    private static uint? ReadFlags(Counter? counter)
    {
        var value = ReadValue(counter);
        return value is >= 0d and <= uint.MaxValue
            ? (uint)Math.Round(value.Value)
            : null;
    }

    private static double? ClampPercent(double? value) =>
        value.HasValue ? Math.Clamp(value.Value, 0d, 100d) : null;

    private IReadOnlyDictionary<byte, ProcessorPowerInfo>? ReadProcessorPowerInformation()
    {
        // PROCESSOR_POWER_INFORMATION.Number is a group-local processor number. The legacy
        // API does not identify the group, so its output is ambiguous on multi-group systems.
        if (_processors.Count == 0 || _processors.Any(item => item.Group != 0)) return null;
        var count = _processors.Count;
        var size = Marshal.SizeOf<ProcessorPowerInfo>();
        var pointer = Marshal.AllocHGlobal(size * count);
        try
        {
            var status = CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, pointer, (uint)(size * count));
            if (status != 0) return null;
            var result = new Dictionary<byte, ProcessorPowerInfo>();
            for (var index = 0; index < count; index++)
            {
                var value = Marshal.PtrToStructure<ProcessorPowerInfo>(pointer + index * size);
                if (value.Number <= byte.MaxValue) result[(byte)value.Number] = value;
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static IReadOnlyList<LogicalProcessor> ReadLogicalProcessors()
    {
        if (!GetSystemCpuSetInformation(IntPtr.Zero, 0, out var required, IntPtr.Zero, 0) &&
            Marshal.GetLastWin32Error() != 122) return FallbackProcessors();
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, required, out required, IntPtr.Zero, 0))
                return FallbackProcessors();
            var result = new List<LogicalProcessor>();
            var offset = 0;
            while (offset + 20 <= (int)required)
            {
                var entry = buffer + offset;
                var size = Marshal.ReadInt32(entry, 0);
                if (size < 20 || offset + size > (int)required) break;
                var type = Marshal.ReadInt32(entry, 4);
                if (type == 0)
                {
                    var group = (ushort)Marshal.ReadInt16(entry, 12);
                    var logical = Marshal.ReadByte(entry, 14);
                    var efficiency = Marshal.ReadByte(entry, 18);
                    result.Add(new LogicalProcessor(group, logical, efficiency));
                }
                offset += size;
            }
            return result.Count > 0 ? result : FallbackProcessors();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IReadOnlyList<LogicalProcessor> FallbackProcessors() =>
        Enumerable.Range(0, Environment.ProcessorCount)
            .Select(index => new LogicalProcessor(0, (byte)index, 0)).ToArray();

    private static bool IsOnAcPower()
    {
        return GetSystemPowerStatus(out var status) && status.AcLineStatus == 1;
    }

    private CpuTelemetrySnapshot Unavailable(string error) => new(
        DateTimeOffset.UtcNow, false, IsOnAcPower(), null, null, null, null, null, null, null, null, null, [], error);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        GC.SuppressFinalize(this);
    }

    private sealed record Counter(IntPtr Handle);
    private sealed record LogicalProcessor(ushort Group, byte LogicalProcessorIndex, byte EfficiencyClass);
    private sealed record CoreCounters(
        LogicalProcessor Processor,
        Counter? Load,
        Counter? Performance,
        Counter? Frequency,
        Counter? LimitFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInfo
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFormattedCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint type,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        uint informationLevel,
        IntPtr inputBuffer,
        uint inputBufferLength,
        IntPtr outputBuffer,
        uint outputBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
