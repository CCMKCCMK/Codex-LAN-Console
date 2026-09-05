namespace CodexLanBridge;

/// <summary>
/// Low-overhead Windows CPU health monitor. It samples slowly while healthy and switches to
/// five-second samples only while sustained load is being evaluated. Policy repair changes
/// Windows preferences only; firmware, thermal and electrical limits remain authoritative.
/// </summary>
public sealed class CpuGuardService : BackgroundService, IDisposable
{
    private static readonly TimeSpan SteadyInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OffInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumRepairInterval = TimeSpan.FromMinutes(10);
    private const int MaximumRepairsPerHour = 2;
    private const int RequiredSuspectSamples = 6;
    private const double EvaluationPerformanceCoreLoadPercent = 60d;
    private const double SuspectPerformanceCoreLoadPercent = 75d;
    private const double SuspectPerformancePercent = 105d;
    private const double SuspectFrequencyMhz = 1600d;
    private const double RecoveredPerformancePercent = 120d;
    private const double RecoveredFrequencyMhz = 1800d;

    private readonly ILogger<CpuGuardService> _logger;
    private readonly NotificationStore _notifications;
    private readonly CpuGuardSettingsStore _settings;
    private readonly WindowsCpuTelemetry _telemetry;
    private readonly WindowsCpuPowerPolicy _policy;
    private readonly BoundedCpuTransitionLog _transitions;
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private CpuHealthSnapshot _snapshot;
    private int _suspectSamples;
    private int _verificationSamplesRemaining;
    private int _resetEvaluationRequested;
    private DateTimeOffset? _verificationDeadline;
    private bool _disposed;

    public CpuGuardService(ILogger<CpuGuardService> logger, NotificationStore notifications)
    {
        _logger = logger;
        _notifications = notifications;
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLanConsole");
        _settings = new CpuGuardSettingsStore(dataDirectory);
        _telemetry = new WindowsCpuTelemetry();
        _policy = new WindowsCpuPowerPolicy();
        _transitions = new BoundedCpuTransitionLog(dataDirectory);
        var settings = _settings.Get();
        _snapshot = new CpuHealthSnapshot(
            settings.Mode,
            CpuHealthState.Starting,
            DateTimeOffset.UtcNow,
            null,
            0,
            settings.LastRepairAt,
            NextRepairAllowed(settings),
            "CPU monitoring is starting.",
            null);
    }

    public CpuHealthSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    public CpuGuardSettingsSnapshot GetSettings() => _settings.Get();

    public CpuGuardSettingsSnapshot SetMode(CpuGuardMode mode)
    {
        var settings = _settings.SetMode(mode);
        Interlocked.Exchange(ref _resetEvaluationRequested, 1);
        RequestRefresh();
        return settings;
    }

    public void RequestRefresh()
    {
        try { _refreshSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task<CpuPolicyRepairResult> RepairNowAsync(CancellationToken cancellationToken = default)
    {
        await _sampleGate.WaitAsync(cancellationToken);
        try
        {
            var telemetry = _telemetry.Read();
            return ApplyRepair(telemetry, manual: true);
        }
        finally { _sampleGate.Release(); }
    }

    public async Task<CpuPolicyRepairResult> CaptureCurrentBaselineAsync(
        CancellationToken cancellationToken = default)
    {
        await _sampleGate.WaitAsync(cancellationToken);
        try { return _policy.CaptureCurrentBaseline(); }
        finally { _sampleGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PDH rate counters require two observations. The short initial delay also avoids
        // competing with the bridge and desktop initialization path.
        try { await Task.Delay(EvaluationInterval, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await SampleAsync(stoppingToken);
            var current = GetSnapshot();
            var evaluating = current.State is CpuHealthState.Evaluating or
                CpuHealthState.PolicyLimited or CpuHealthState.Repairing;
            var interval = current.State == CpuHealthState.Off
                ? OffInterval
                : evaluating ? EvaluationInterval : SteadyInterval;
            try
            {
                await _refreshSignal.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        await _sampleGate.WaitAsync(cancellationToken);
        try
        {
            var settings = _settings.Get();
            var telemetry = _telemetry.Read();
            if (Interlocked.Exchange(ref _resetEvaluationRequested, 0) != 0) ResetEvaluation();
            if (settings.Mode == CpuGuardMode.Off)
            {
                ResetEvaluation();
                Publish(CpuHealthState.Off, telemetry, "CPU guard and monitoring are off.", null, settings);
                return;
            }
            if (!telemetry.Available)
            {
                ResetEvaluation();
                Publish(CpuHealthState.TelemetryUnavailable, telemetry,
                    telemetry.Error ?? "CPU telemetry is unavailable.", null, settings);
                return;
            }
            if (!telemetry.OnAcPower)
            {
                ResetEvaluation();
                Publish(CpuHealthState.OnBattery, telemetry,
                    "On battery: automatic CPU policy repair is suspended.", null, settings);
                return;
            }

            var load = telemetry.PerformanceCoreLoadPercent;
            var performance = telemetry.PerformanceCorePerformancePercent ?? telemetry.ProcessorPerformancePercent;
            var frequency = telemetry.PerformanceCoreFrequencyMhz;
            if (!load.HasValue || !performance.HasValue || !frequency.HasValue)
            {
                ResetEvaluation();
                Publish(CpuHealthState.TelemetryUnavailable, telemetry,
                    "P-core load, performance and actual-frequency counters are warming up.", null, settings);
                return;
            }
            if (load.Value < EvaluationPerformanceCoreLoadPercent)
            {
                ResetEvaluation();
                Publish(CpuHealthState.Idle, telemetry,
                    "No P-core is under sustained pressure; idle frequency changes are normal.", null, settings);
                return;
            }
            if (performance.Value >= RecoveredPerformancePercent && frequency.Value >= RecoveredFrequencyMhz)
            {
                _suspectSamples = 0;
                var recovered = _verificationSamplesRemaining > 0;
                _verificationSamplesRemaining = 0;
                _verificationDeadline = null;
                Publish(recovered ? CpuHealthState.Recovered : CpuHealthState.Healthy, telemetry,
                    recovered ? "Processor performance recovered after policy repair." :
                    "Processor frequency is responding normally to CPU load.", null, settings);
                return;
            }

            // Requiring all three independent observations prevents false alarms from parked
            // cores, E-core-only work, or a stale/reference-frequency counter.
            var suspect = load.Value >= SuspectPerformanceCoreLoadPercent &&
                          performance.Value < SuspectPerformancePercent &&
                          frequency.Value < SuspectFrequencyMhz;
            if (!suspect)
            {
                ResetEvaluation();
                Publish(CpuHealthState.Healthy, telemetry,
                    "Processor performance is within the expected transition range.", null, settings);
                return;
            }

            _suspectSamples++;
            if (_verificationSamplesRemaining > 0)
            {
                if (_verificationDeadline is { } deadline && DateTimeOffset.UtcNow > deadline)
                {
                    ResetEvaluation();
                    Publish(CpuHealthState.NeedsAttention, telemetry,
                        "CPU recovery verification expired before normal P-core response returned.",
                        GetSnapshot().LastRepair, settings);
                    return;
                }
                _verificationSamplesRemaining--;
                if (_verificationSamplesRemaining == 0)
                {
                    _verificationDeadline = null;
                    Publish(CpuHealthState.NeedsAttention, telemetry,
                        "Frequency stayed low after Windows policy repair; thermal or firmware limits may be active.",
                        GetSnapshot().LastRepair, settings);
                }
                else
                {
                    Publish(CpuHealthState.Repairing, telemetry,
                        "Windows policy was repaired; waiting for the processor to respond.",
                        GetSnapshot().LastRepair, settings);
                }
                return;
            }

            var limitFlags = telemetry.PerformanceCoreLimitFlags.GetValueOrDefault();
            var state = limitFlags != 0 ? CpuHealthState.PolicyLimited : CpuHealthState.Evaluating;
            var summary = $"Busy P-core stayed slow: {load.Value:F0}% load, {frequency.Value:F0} MHz, " +
                          $"{performance.Value:F0}% performance ({_suspectSamples}/{RequiredSuspectSamples}).";
            Publish(state, telemetry, summary, null, settings);
            if (_suspectSamples < RequiredSuspectSamples) return;
            if (limitFlags != 0)
            {
                Publish(CpuHealthState.NeedsAttention, telemetry,
                    $"P-core limit flags 0x{limitFlags:X} are active; automatic policy repair was withheld to protect the hardware.",
                    null, settings);
                return;
            }
            if (settings.Mode == CpuGuardMode.AutoGuard)
            {
                ApplyRepair(telemetry, manual: false);
                return;
            }
            Publish(CpuHealthState.NeedsAttention, telemetry,
                "Sustained abnormal P-core frequency was confirmed in monitor-only mode.", null, settings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CPU guard sampling failed.");
        }
        finally { _sampleGate.Release(); }
    }

    private CpuPolicyRepairResult ApplyRepair(CpuTelemetrySnapshot telemetry, bool manual)
    {
        var settings = _settings.Get();
        var now = DateTimeOffset.UtcNow;
        if (!telemetry.OnAcPower)
            return PublishRepairResult(new CpuPolicyRepairResult(false, now,
                "CPU policy repair is suspended on battery power.", [], []), telemetry, settings);
        if (!CanRepair(settings, now, out var nextAllowed))
            return PublishRepairResult(new CpuPolicyRepairResult(false, now,
                $"CPU policy repair is rate-limited until {nextAllowed:O}.", [], []), telemetry, settings);

        Publish(CpuHealthState.Repairing, telemetry,
            manual ? "Applying an explicitly requested Windows CPU policy repair." :
            "Sustained abnormal performance detected; refreshing Windows CPU policy.", null, settings);
        var result = _policy.Repair(manual);
        // Rate-limit attempts as well as successes. A rejected overlay or firmware lock must
        // never cause the service to hammer the power APIs every few seconds.
        settings = _settings.RecordRepair(result.AttemptedAt);
        if (result.Applied)
        {
            _suspectSamples = 0;
            _verificationSamplesRemaining = RequiredSuspectSamples;
            _verificationDeadline = DateTimeOffset.UtcNow.AddSeconds(45);
            RequestRefresh();
        }
        else
        {
            _verificationSamplesRemaining = 0;
            _verificationDeadline = null;
        }
        return PublishRepairResult(result, telemetry, settings);
    }

    private CpuPolicyRepairResult PublishRepairResult(
        CpuPolicyRepairResult result,
        CpuTelemetrySnapshot telemetry,
        CpuGuardSettingsSnapshot settings)
    {
        Publish(result.Applied ? CpuHealthState.Repairing : CpuHealthState.NeedsAttention,
            telemetry, result.Message, result, settings);
        return result;
    }

    private void Publish(
        CpuHealthState state,
        CpuTelemetrySnapshot telemetry,
        string summary,
        CpuPolicyRepairResult? repair,
        CpuGuardSettingsSnapshot settings)
    {
        var previous = Volatile.Read(ref _snapshot);
        var snapshot = new CpuHealthSnapshot(
            settings.Mode,
            state,
            DateTimeOffset.UtcNow,
            telemetry,
            _suspectSamples,
            settings.LastRepairAt,
            NextRepairAllowed(settings),
            summary,
            repair ?? previous.LastRepair);
        Volatile.Write(ref _snapshot, snapshot);
        _transitions.Write(previous.State, state, summary, telemetry);
        if (previous.State == state) return;
        if (state == CpuHealthState.NeedsAttention)
        {
            _notifications.Publish(
                $"cpu:attention:{DateTimeOffset.UtcNow:yyyyMMddHH}",
                "system_attention", null, null, null,
                "CPU performance needs attention",
                "Windows policy recovery did not restore the busy performance cores. Check cooling, power, or firmware limits.",
                false);
        }
        else if (state == CpuHealthState.Recovered)
        {
            _notifications.Publish(
                $"cpu:recovered:{DateTimeOffset.UtcNow:yyyyMMddHH}",
                "system_recovered", null, null, null,
                "CPU performance recovered",
                "The performance cores returned to their normal response range.",
                false);
        }
    }

    private static bool CanRepair(
        CpuGuardSettingsSnapshot settings,
        DateTimeOffset now,
        out DateTimeOffset nextAllowed)
    {
        var recentHour = settings.RecentRepairs.Where(value => now - value < TimeSpan.FromHours(1)).ToArray();
        var intervalLimit = settings.LastRepairAt?.Add(MinimumRepairInterval) ?? DateTimeOffset.MinValue;
        var hourlyLimit = recentHour.Length >= MaximumRepairsPerHour
            ? recentHour.Min().AddHours(1)
            : DateTimeOffset.MinValue;
        nextAllowed = intervalLimit > hourlyLimit ? intervalLimit : hourlyLimit;
        return now >= nextAllowed;
    }

    private static DateTimeOffset? NextRepairAllowed(CpuGuardSettingsSnapshot settings)
    {
        CanRepair(settings, DateTimeOffset.UtcNow, out var next);
        return next == DateTimeOffset.MinValue ? null : next;
    }

    private void ResetEvaluation()
    {
        _suspectSamples = 0;
        _verificationSamplesRemaining = 0;
        _verificationDeadline = null;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _telemetry.Dispose();
        _sampleGate.Dispose();
        _refreshSignal.Dispose();
        base.Dispose();
    }
}
