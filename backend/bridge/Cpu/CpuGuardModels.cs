namespace CodexLanBridge;

public enum CpuGuardMode
{
    Off,
    Monitor,
    AutoGuard
}

public enum CpuHealthState
{
    Starting,
    Off,
    Idle,
    Healthy,
    Evaluating,
    PolicyLimited,
    Repairing,
    Recovered,
    NeedsAttention,
    OnBattery,
    TelemetryUnavailable
}

public sealed record CpuGuardSettingsSnapshot(
    CpuGuardMode Mode,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastRepairAt,
    IReadOnlyList<DateTimeOffset> RecentRepairs);

public sealed record CpuCoreTelemetry(
    ushort Group,
    byte LogicalProcessor,
    byte EfficiencyClass,
    bool PerformanceCore,
    double? LoadPercent,
    double? PerformancePercent,
    double? FrequencyMhz,
    uint? PerformanceLimitFlags,
    uint? MaximumMhz,
    uint? FirmwareLimitMhz);

public sealed record CpuTelemetrySnapshot(
    DateTimeOffset CapturedAt,
    bool Available,
    bool OnAcPower,
    double? UtilityPercent,
    double? PerformanceCoreLoadPercent,
    double? ProcessorPerformancePercent,
    uint? ProcessorPerformanceLimitFlags,
    double? ActualFrequencyMhz,
    double? PerformanceCoreFrequencyMhz,
    double? PerformanceCorePerformancePercent,
    uint? PerformanceCoreLimitFlags,
    double? PerformanceCoreLimitRatio,
    IReadOnlyList<CpuCoreTelemetry> Cores,
    string? Error);

public sealed record CpuPolicyRepairResult(
    bool Applied,
    DateTimeOffset AttemptedAt,
    string Message,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Errors);

public sealed record CpuHealthSnapshot(
    CpuGuardMode Mode,
    CpuHealthState State,
    DateTimeOffset UpdatedAt,
    CpuTelemetrySnapshot? Telemetry,
    int ConsecutiveSuspectSamples,
    DateTimeOffset? LastRepairAt,
    DateTimeOffset? NextRepairAllowedAt,
    string Summary,
    CpuPolicyRepairResult? LastRepair);
