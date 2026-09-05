using System.Runtime.InteropServices;

namespace CodexLanBridge;

public sealed class WindowsCpuPowerPolicy
{
    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MinimumProcessorState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid MaximumProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid ProcessorPerformanceBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid MaximumProcessorFrequency = new("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
    private static readonly Guid SystemCoolingPolicy = new("94d3a615-a899-4ac5-ae2b-e4d8f634367f");
    private const uint ActiveCoolingPolicy = 1;

    private readonly object _sync = new();
    private readonly CpuPowerBaselineStore _baselineStore = new();

    public WindowsCpuPowerPolicy()
    {
        // Capture the machine-specific known-good policy while the service is
        // starting normally. If the service starts on battery, automatic repair
        // remains fail-closed until a deliberate manual baseline capture.
        if (OperatingSystem.IsWindows() && IsOnAcPower())
        {
            lock (_sync)
                EnsureBaseline(allowCapture: true, out _, out _);
        }
    }

    // Compatibility entry point. A parameterless call is always treated as an
    // automatic repair and therefore never re-applies an already-matching scheme.
    public CpuPolicyRepairResult Repair() => Repair(manual: false);

    public CpuPolicyRepairResult CaptureCurrentBaseline()
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var changes = new List<string>();
        var errors = new List<string>();
        if (!OperatingSystem.IsWindows())
            return new CpuPolicyRepairResult(false, attemptedAt, "CPU baseline capture is available only on Windows.", changes, errors);
        if (!IsOnAcPower())
            return new CpuPolicyRepairResult(false, attemptedAt, "The CPU baseline was not captured while running on battery.", changes, errors);

        lock (_sync)
        {
            IntPtr schemePointer = IntPtr.Zero;
            try
            {
                var result = PowerGetActiveScheme(IntPtr.Zero, out schemePointer);
                if (result != 0 || schemePointer == IntPtr.Zero)
                {
                    errors.Add($"Could not read the active power scheme (0x{result:X8}).");
                    return new CpuPolicyRepairResult(false, attemptedAt,
                        "The current CPU power policy could not be saved as a baseline.", changes, errors);
                }

                var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
                var values = ReadValues(scheme, out var readErrors);
                if (values is null)
                {
                    errors.AddRange(readErrors);
                    return new CpuPolicyRepairResult(false, attemptedAt,
                        "The current CPU power policy could not be saved as a baseline.", changes, errors);
                }

                var safeValues = values with { CoolingPolicy = ActiveCoolingPolicy };
                if (!_baselineStore.TrySave(scheme, safeValues, out _, out var saveError))
                {
                    errors.Add(saveError ?? "The CPU power baseline could not be saved.");
                    return new CpuPolicyRepairResult(false, attemptedAt,
                        "The current CPU power policy could not be saved as a baseline.", changes, errors);
                }

                changes.Add($"Saved this machine's current AC processor settings to {_baselineStore.Path}.");
                if (values.CoolingPolicy != ActiveCoolingPolicy)
                {
                    changes.Add(
                        $"Stored active cooling (index {ActiveCoolingPolicy}) as the safety baseline; " +
                        $"the observed cooling index was {values.CoolingPolicy}.");
                }

                return new CpuPolicyRepairResult(true, attemptedAt,
                    "The current AC CPU power policy is now this machine's known-good baseline.", changes, errors);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                return new CpuPolicyRepairResult(false, attemptedAt,
                    "The current CPU power policy could not be saved as a baseline.", changes, errors);
            }
            finally
            {
                if (schemePointer != IntPtr.Zero) LocalFree(schemePointer);
            }
        }
    }

    public CpuPolicyRepairResult Repair(bool manual)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var changes = new List<string>();
        var errors = new List<string>();
        if (!OperatingSystem.IsWindows())
            return new CpuPolicyRepairResult(false, attemptedAt, "CPU policy repair is available only on Windows.", changes, errors);
        if (!IsOnAcPower())
            return new CpuPolicyRepairResult(false, attemptedAt, "CPU policy was not changed while running on battery.", changes, errors);

        lock (_sync)
        {
            IntPtr schemePointer = IntPtr.Zero;
            try
            {
                if (!EnsureBaseline(allowCapture: manual, out var baseline, out var baselineError) || baseline is null)
                {
                    errors.Add(baselineError ??
                        "No machine-specific CPU power baseline exists. Run one manual repair while the system is healthy and connected to AC power.");
                    return Result(false, attemptedAt, changes, errors, noDrift: false, manual);
                }

                var result = PowerGetActiveScheme(IntPtr.Zero, out schemePointer);
                if (result != 0 || schemePointer == IntPtr.Zero)
                {
                    errors.Add($"Could not read the active power scheme (0x{result:X8}).");
                    return Result(false, attemptedAt, changes, errors, noDrift: false, manual);
                }

                var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
                var current = ReadValues(scheme, out var readErrors);
                if (current is null)
                {
                    errors.AddRange(readErrors);
                    return Result(false, attemptedAt, changes, errors, noDrift: false, manual);
                }

                RestoreIfDrifted(
                    scheme, MinimumProcessorState,
                    current.MinimumProcessorState, baseline.AcValues.MinimumProcessorState,
                    value => $"AC minimum processor state: {value}%",
                    changes, errors);
                RestoreIfDrifted(
                    scheme, MaximumProcessorState,
                    current.MaximumProcessorState, baseline.AcValues.MaximumProcessorState,
                    value => $"AC maximum processor state: {value}%",
                    changes, errors);
                RestoreIfDrifted(
                    scheme, ProcessorPerformanceBoostMode,
                    current.BoostMode, baseline.AcValues.BoostMode,
                    value => $"AC processor boost mode index: {value}",
                    changes, errors);
                RestoreIfDrifted(
                    scheme, MaximumProcessorFrequency,
                    current.MaximumProcessorFrequencyMhz, baseline.AcValues.MaximumProcessorFrequencyMhz,
                    value => value == 0
                        ? "AC maximum processor frequency: unlimited"
                        : $"AC maximum processor frequency: {value} MHz",
                    changes, errors);
                RestoreIfDrifted(
                    scheme, SystemCoolingPolicy,
                    current.CoolingPolicy, baseline.AcValues.CoolingPolicy,
                    value => value == ActiveCoolingPolicy
                        ? "AC cooling policy: active"
                        : $"AC cooling policy index: {value}",
                    changes, errors);

                var driftWasWritten = changes.Count > 0;
                if (!driftWasWritten && !manual)
                    return Result(false, attemptedAt, changes, errors, noDrift: true, manual);

                // Re-selecting the existing scheme commits changed values. For a
                // manual repair it is also a permitted one-shot refresh when no
                // values drifted. Automatic repair never does this without drift.
                result = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
                if (result == 0)
                {
                    changes.Add(driftWasWritten
                        ? "Re-applied the current Windows power scheme after restoring drifted values."
                        : "Manually re-applied the current Windows power scheme; baseline values already matched.");
                }
                else
                {
                    errors.Add($"Could not re-apply the active power scheme (0x{result:X8}).");
                }

                return Result(result == 0, attemptedAt, changes, errors, noDrift: !driftWasWritten, manual);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                return Result(false, attemptedAt, changes, errors, noDrift: false, manual);
            }
            finally
            {
                if (schemePointer != IntPtr.Zero) LocalFree(schemePointer);
            }
        }
    }

    private bool EnsureBaseline(
        bool allowCapture,
        out CpuPowerPolicyBaseline? baseline,
        out string? error)
    {
        if (_baselineStore.TryLoad(out baseline, out error))
            return true;

        if (error is not null)
            return false;

        if (!allowCapture)
        {
            error = $"No machine-specific CPU power baseline exists at {_baselineStore.Path}. " +
                    "Automatic repair is disabled until a healthy AC baseline has been captured.";
            return false;
        }

        if (!IsOnAcPower())
        {
            error = "The CPU power baseline can only be captured while connected to AC power.";
            return false;
        }

        IntPtr schemePointer = IntPtr.Zero;
        try
        {
            var result = PowerGetActiveScheme(IntPtr.Zero, out schemePointer);
            if (result != 0 || schemePointer == IntPtr.Zero)
            {
                error = $"Could not read the active power scheme while capturing the baseline (0x{result:X8}).";
                return false;
            }

            var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            var values = ReadValues(scheme, out var readErrors);
            if (values is null)
            {
                error = string.Join(" ", readErrors);
                return false;
            }

            // Active cooling is the only fixed safety invariant. Windows uses
            // index 1 for active cooling; all performance values are captured
            // directly from this machine instead of being guessed.
            var safeValues = values with { CoolingPolicy = ActiveCoolingPolicy };
            return _baselineStore.TrySave(scheme, safeValues, out baseline, out error);
        }
        finally
        {
            if (schemePointer != IntPtr.Zero) LocalFree(schemePointer);
        }
    }

    private static CpuPowerPolicyValues? ReadValues(Guid scheme, out IReadOnlyList<string> errors)
    {
        var readErrors = new List<string>();
        var minimum = ReadAcValue(scheme, MinimumProcessorState, "AC minimum processor state", readErrors);
        var maximum = ReadAcValue(scheme, MaximumProcessorState, "AC maximum processor state", readErrors);
        var boost = ReadAcValue(scheme, ProcessorPerformanceBoostMode, "AC processor boost mode", readErrors);
        var frequency = ReadAcValue(scheme, MaximumProcessorFrequency, "AC maximum processor frequency", readErrors);
        var cooling = ReadAcValue(scheme, SystemCoolingPolicy, "AC cooling policy", readErrors);
        errors = readErrors;

        if (readErrors.Count > 0 || minimum is null || maximum is null ||
            boost is null || frequency is null || cooling is null)
            return null;

        return new CpuPowerPolicyValues(
            minimum.Value,
            maximum.Value,
            boost.Value,
            frequency.Value,
            cooling.Value);
    }

    private static uint? ReadAcValue(
        Guid scheme,
        Guid setting,
        string description,
        ICollection<string> errors)
    {
        var subgroup = ProcessorSubgroup;
        var result = PowerReadACValueIndex(
            IntPtr.Zero,
            ref scheme,
            ref subgroup,
            ref setting,
            out var value);
        if (result == 0)
            return value;

        errors.Add($"{description} could not be read (0x{result:X8}).");
        return null;
    }

    private static void RestoreIfDrifted(
        Guid scheme,
        Guid setting,
        uint currentValue,
        uint baselineValue,
        Func<uint, string> describe,
        ICollection<string> changes,
        ICollection<string> errors)
    {
        if (currentValue == baselineValue)
            return;

        var subgroup = ProcessorSubgroup;
        var result = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, baselineValue);
        if (result == 0)
            changes.Add($"Restored {describe(baselineValue)} (was {describe(currentValue)}).");
        else
            errors.Add($"Could not restore {describe(baselineValue)} from {describe(currentValue)} (0x{result:X8}).");
    }

    private static CpuPolicyRepairResult Result(
        bool applied,
        DateTimeOffset at,
        IReadOnlyList<string> changes,
        IReadOnlyList<string> errors,
        bool noDrift,
        bool manual) =>
        new(applied, at,
            errors.Count > 0
                ? changes.Count > 0
                    ? "CPU policy was only partially restored; Windows rejected one or more operations."
                    : "Windows CPU performance policy could not be restored."
                : noDrift
                    ? manual
                        ? "The saved CPU baseline already matched; the current power scheme was manually refreshed."
                        : "The saved CPU baseline already matches. Automatic repair made no changes."
                    : "Drifted CPU power settings were restored from this machine's saved healthy baseline.",
            changes, errors);

    private static bool IsOnAcPower() => GetSystemPowerStatus(out var status) && status.AcLineStatus == 1;

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

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint acValueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
