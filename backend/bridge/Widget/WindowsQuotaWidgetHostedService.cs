namespace CodexLanBridge;

public sealed class WindowsQuotaWidgetHostedService : IHostedService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

    private readonly QuotaMonitorService _quota;
    private readonly WindowsQuotaWidgetSettingsStore _settings;
    private readonly ILogger<WindowsQuotaWidgetHostedService> _logger;
    private readonly object _gate = new();

    private QuotaWidgetSnapshot _latest;
    private CancellationTokenSource? _lifetime;
    private Thread? _supervisorThread;
    private Thread? _widgetThread;
    private NativeQuotaWidgetWindow? _window;

    public WindowsQuotaWidgetHostedService(
        QuotaMonitorService quota,
        WindowsQuotaWidgetSettingsStore settings,
        ILogger<WindowsQuotaWidgetHostedService> logger)
    {
        _quota = quota;
        _settings = settings;
        _logger = logger;
        _latest = quota.GetSnapshot();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        _settings.SetEnabled(true);
        _quota.SnapshotChanged += OnSnapshotChanged;

        lock (_gate)
        {
            if (_supervisorThread is { IsAlive: true }) return Task.CompletedTask;

            _lifetime = new CancellationTokenSource();
            var lifetime = _lifetime;
            _supervisorThread = new Thread(() => RunSupervisor(lifetime.Token))
            {
                IsBackground = true,
                Name = "Codex quota widget supervisor"
            };
            _supervisorThread.Start();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _quota.SnapshotChanged -= OnSnapshotChanged;

        CancellationTokenSource? lifetime;
        Thread? supervisor;
        lock (_gate)
        {
            lifetime = _lifetime;
            supervisor = _supervisorThread;
        }

        lifetime?.Cancel();
        RequestWidgetShutdown();
        if (supervisor is { IsAlive: true }) supervisor.Join(TimeSpan.FromSeconds(4));

        lock (_gate)
        {
            if (ReferenceEquals(_lifetime, lifetime))
            {
                _lifetime = null;
                _supervisorThread = null;
            }
        }
        lifetime?.Dispose();
        return Task.CompletedTask;
    }

    private void RunSupervisor(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var widgetThread = new Thread(() => RunWidgetSession(cancellationToken))
                {
                    IsBackground = true,
                    Name = "Codex quota desktop widget"
                };
                widgetThread.SetApartmentState(ApartmentState.STA);
                lock (_gate) _widgetThread = widgetThread;
                widgetThread.Start();

                while (widgetThread.IsAlive && !cancellationToken.IsCancellationRequested)
                    widgetThread.Join(TimeSpan.FromMilliseconds(250));

                if (cancellationToken.IsCancellationRequested && widgetThread.IsAlive)
                {
                    RequestWidgetShutdown();
                    widgetThread.Join(TimeSpan.FromSeconds(2));
                }

                lock (_gate)
                {
                    if (ReferenceEquals(_widgetThread, widgetThread)) _widgetThread = null;
                }

                if (cancellationToken.IsCancellationRequested) break;

                _logger.LogWarning("The Windows quota widget exited; it will be restored automatically.");
                if (cancellationToken.WaitHandle.WaitOne(RestartDelay)) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The Windows quota widget supervisor stopped unexpectedly.");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_supervisorThread, Thread.CurrentThread)) _supervisorThread = null;
            }
        }
    }

    private void RunWidgetSession(CancellationToken cancellationToken)
    {
        NativeQuotaWidgetWindow? window = null;

        try
        {
            window = new NativeQuotaWidgetWindow(Volatile.Read(ref _latest), _settings, _logger);
            lock (_gate) _window = window;

            _quota.RequestRefresh();
            if (cancellationToken.IsCancellationRequested) window.RequestShutdown();
            window.Run(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The Windows quota widget stopped unexpectedly.");
        }
        finally
        {
            window?.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_window, window)) _window = null;
            }
        }
    }

    private void RequestWidgetShutdown()
    {
        NativeQuotaWidgetWindow? window;
        lock (_gate) window = _window;
        window?.RequestShutdown();
    }

    private void OnSnapshotChanged(QuotaWidgetSnapshot snapshot)
    {
        Volatile.Write(ref _latest, snapshot);
        NativeQuotaWidgetWindow? window;
        lock (_gate) window = _window;
        window?.ApplySnapshot(snapshot);
    }
}
