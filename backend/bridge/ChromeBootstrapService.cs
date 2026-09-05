using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexLanBridge;

public sealed record ChromeBootstrapSnapshot(
    bool Supported,
    bool Installed,
    bool Running,
    bool Starting,
    bool AutoStartWithBridge,
    string State,
    string BrowserName,
    string Message,
    DateTimeOffset? LastAttemptAt);

public sealed class ChromeBootstrapService : BackgroundService
{
    public const string LauncherTaskName = "Codex LAN Console Chrome Bootstrap";

    private readonly string _settingsFile;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly object _stateGate = new();
    private ChromeBootstrapSettings _settings;
    private bool _starting;
    private string _state = "checking";
    private string _message = "正在检查 Chrome。";
    private DateTimeOffset? _lastAttemptAt;

    public ChromeBootstrapService()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLanConsole");
        Directory.CreateDirectory(dataDirectory);
        _settingsFile = Path.Combine(dataDirectory, "chrome-bootstrap.json");
        _settings = LoadSettings();
        CaptureRunningExecutable();
    }

    public ChromeBootstrapSnapshot GetSnapshot()
    {
        var runningPath = FindRunningExecutable();
        var running = runningPath is not null || HasRunningChromeProcess();
        if (runningPath is not null) RememberExecutable(runningPath);
        var executable = ResolveExecutable();
        lock (_stateGate)
        {
            var state = running
                ? "ready"
                : executable is null
                    ? "missing"
                    : _starting
                        ? "starting"
                        : _state is "ready" or "checking" ? "idle" : _state;
            var message = running
                ? "Chrome 已在电脑上运行；浏览器任务可以直接使用现有登录状态。"
                : executable is null
                    ? "没有找到可启动的 Chrome，请先在电脑端安装或配置 Chrome。"
                    : _starting
                        ? "正在电脑上启动 Chrome。"
                        : state == "idle"
                            ? "Chrome 当前未运行；开始浏览器任务时会自动启动。"
                            : _message;
            return new ChromeBootstrapSnapshot(
                OperatingSystem.IsWindows(),
                executable is not null,
                running,
                _starting,
                _settings.AutoStartWithBridge,
                state,
                BrowserDisplayName(executable ?? runningPath),
                message,
                _lastAttemptAt);
        }
    }

    public ChromeBootstrapSnapshot SetAutoStart(bool enabled)
    {
        lock (_stateGate)
        {
            _settings.AutoStartWithBridge = enabled;
            SaveSettingsLocked();
            _message = enabled
                ? "Chrome 会在 Bridge 启动时静默恢复，也会在浏览器任务开始前按需启动。"
                : "已关闭随 Bridge 启动；浏览器任务开始前仍会按需启动 Chrome。";
        }
        return GetSnapshot();
    }

    public async Task<ChromeBootstrapSnapshot> EnsureStartedAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        await _launchGate.WaitAsync(cancellationToken);
        try
        {
            var current = GetSnapshot();
            if (current.Running || !OperatingSystem.IsWindows()) return current;
            var executable = ResolveExecutable();
            if (executable is null)
            {
                UpdateState("missing", "没有找到 Chrome 可执行文件，浏览器任务仍会交给 Codex 尝试处理。", false);
                return GetSnapshot();
            }

            RememberExecutable(executable);
            UpdateState("starting", $"正在启动 {BrowserDisplayName(executable)}（{reason}）。", true);
            var invoked = await LaunchThroughLimitedTaskAsync(cancellationToken);
            if (!invoked && !WindowsProcessElevation.Current.Active)
                invoked = LaunchDirect(executable);
            if (!invoked && WindowsProcessElevation.Current.Active)
                invoked = LaunchThroughExplorer(executable);

            if (!invoked)
            {
                UpdateState("error", "Chrome 启动器没有成功运行；请在电脑端重新安装常驻任务。", false);
                return GetSnapshot();
            }

            for (var attempt = 0; attempt < 24; attempt++)
            {
                await Task.Delay(250, cancellationToken);
                if (!HasRunningChromeProcess()) continue;
                CaptureRunningExecutable();
                UpdateState("ready", "Chrome 已启动；浏览器任务可以继续。", false);
                return GetSnapshot();
            }

            UpdateState("error", "已调用 Chrome 启动器，但没有在限定时间内检测到浏览器进程。", false);
            return GetSnapshot();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateState("idle", "Chrome 启动检查已取消。", false);
            throw;
        }
        catch (Exception error)
        {
            UpdateState("error", $"Chrome 启动失败：{error.Message}", false);
            return GetSnapshot();
        }
        finally
        {
            _launchGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
            if (_settings.AutoStartWithBridge)
                await EnsureStartedAsync("Bridge 启动", stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void UpdateState(string state, string message, bool starting)
    {
        lock (_stateGate)
        {
            _state = state;
            _message = message;
            _starting = starting;
            _lastAttemptAt = DateTimeOffset.Now;
        }
    }

    private ChromeBootstrapSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFile)) return new ChromeBootstrapSettings();
            return JsonSerializer.Deserialize<ChromeBootstrapSettings>(File.ReadAllText(_settingsFile))
                   ?? new ChromeBootstrapSettings();
        }
        catch
        {
            return new ChromeBootstrapSettings();
        }
    }

    private void CaptureRunningExecutable()
    {
        var executable = FindRunningExecutable();
        if (executable is not null) RememberExecutable(executable);
    }

    private void RememberExecutable(string executable)
    {
        if (!File.Exists(executable)) return;
        lock (_stateGate)
        {
            if (string.Equals(_settings.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase)) return;
            _settings.ExecutablePath = executable;
            SaveSettingsLocked();
        }
    }

    private void SaveSettingsLocked()
    {
        var temporary = _settingsFile + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _settingsFile, true);
    }

    private string? ResolveExecutable()
    {
        var running = FindRunningExecutable();
        if (running is not null) return running;

        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("CODEX_LAN_CHROME_PATH"),
            _settings.ExecutablePath,
            ReadAppPath(Registry.CurrentUser),
            ReadAppPath(Registry.LocalMachine),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Qoom Chrome", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome SxS", "Application", "chrome.exe")
        };
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim().Trim('"'))
            .FirstOrDefault(File.Exists);
    }

    private static string? ReadAppPath(RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            return key?.GetValue(null) as string;
        }
        catch { return null; }
    }

    private static bool HasRunningChromeProcess()
    {
        try
        {
            var processes = Process.GetProcessesByName("chrome");
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { return false; }
    }

    private static string? FindRunningExecutable()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("chrome"))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return null;
    }

    private static string BrowserDisplayName(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return "Chrome";
        var parent = Directory.GetParent(executable)?.Name ?? "";
        if (parent.Equals("Application", StringComparison.OrdinalIgnoreCase))
            parent = Directory.GetParent(Directory.GetParent(executable)?.FullName ?? "")?.Name ?? parent;
        return parent.Contains("Qoom", StringComparison.OrdinalIgnoreCase) ? "Qoom Chrome" : "Chrome";
    }

    private static bool LaunchDirect(string executable)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--no-startup-window",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
                UseShellExecute = true
            });
            return true;
        }
        catch { return false; }
    }

    private static bool LaunchThroughExplorer(string executable)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{executable}\" --no-startup-window",
                UseShellExecute = true
            });
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> LaunchThroughLimitedTaskAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{LauncherTaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return false;
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private sealed class ChromeBootstrapSettings
    {
        public bool AutoStartWithBridge { get; set; } = true;
        public string? ExecutablePath { get; set; }
    }
}
