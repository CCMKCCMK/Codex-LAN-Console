using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace CodexLanBridge;

public sealed record ConsoleLaunchAuditProcess(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string? ExecutablePath,
    string? CommandLine,
    DateTimeOffset? StartedAt);

public sealed record ConsoleLaunchAuditEvent(
    long Id,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset ObservedAt,
    long WindowHandle,
    ConsoleLaunchAuditProcess WindowProcess,
    string? WindowTitle,
    string? WindowClass,
    ConsoleLaunchAuditProcess CommandProcess,
    ConsoleLaunchAuditProcess? CandidateLaunchingProcess,
    IReadOnlyList<ConsoleLaunchAuditProcess> ParentChain,
    bool ParentChainComplete,
    string? CommandLine,
    string? ExecutablePath,
    int RepeatCount,
    double? IntervalMilliseconds,
    double? AverageIntervalMilliseconds,
    string Classification,
    string Explanation);

public sealed record ConsoleLaunchAuditSnapshot(
    bool IsSupported,
    bool IsRunning,
    string Status,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ConsoleLaunchAuditEvent> Events);

/// <summary>
/// Observes foreground console windows and correlates them with recent process
/// snapshots. The service is diagnostic only: it never hides, terminates, or
/// changes a process. Data is kept in bounded in-memory collections.
/// </summary>
public sealed class ConsoleLaunchAuditService : IHostedService, IDisposable
{
    private const int MaximumEvents = 256;
    private const int MaximumSnapshots = 12;
    private const int MaximumRepeatKeys = 512;
    private const int MaximumParentDepth = 32;
    private const int MaximumCommandLineLength = 16_384;
    private static readonly TimeSpan SamplingInterval = TimeSpan.FromSeconds(1);

    private readonly object _dataGate = new();
    private readonly object _lifecycleGate = new();
    private readonly List<ConsoleLaunchAuditEvent> _events = new(MaximumEvents);
    private readonly Queue<NativeProcessSnapshot> _snapshots = new(MaximumSnapshots);
    private readonly Dictionary<string, RepeatState> _repeatStates = new(StringComparer.Ordinal);
    private readonly WinEventDelegate _winEventCallback;

    private CancellationTokenSource? _stopSource;
    private Channel<RawForegroundEvent>? _foregroundEvents;
    private Task? _workers;
    private Thread? _hookThread;
    private IntPtr _hookHandle;
    private uint _hookThreadId;
    private long _nextId;
    private volatile bool _isRunning;
    private string _status;

    public ConsoleLaunchAuditService()
    {
        _winEventCallback = OnWinEvent;
        _status = OperatingSystem.IsWindows() ? "stopped" : "not-supported";
    }

    public bool IsSupported => OperatingSystem.IsWindows();
    public bool IsRunning => _isRunning;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported || cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;

        lock (_lifecycleGate)
        {
            if (_stopSource is not null) return Task.CompletedTask;

            _status = "starting";
            _stopSource = new CancellationTokenSource();
            _foregroundEvents = Channel.CreateBounded<RawForegroundEvent>(
                new BoundedChannelOptions(64)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                    AllowSynchronousContinuations = false
                });

            var token = _stopSource.Token;
            _workers = Task.WhenAll(
                Task.Run(() => SampleProcessesAsync(token), CancellationToken.None),
                Task.Run(() => ProcessForegroundEventsAsync(_foregroundEvents.Reader, token), CancellationToken.None));

            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "Console launch audit WinEvent hook"
            };
            _hookThread.Start();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? stopSource;
        Channel<RawForegroundEvent>? channel;
        Task? workers;
        Thread? hookThread;
        uint hookThreadId;

        lock (_lifecycleGate)
        {
            stopSource = _stopSource;
            channel = _foregroundEvents;
            workers = _workers;
            hookThread = _hookThread;
            hookThreadId = _hookThreadId;
        }

        if (stopSource is null) return;

        stopSource.Cancel();
        channel?.Writer.TryComplete();
        if (hookThreadId != 0) PostThreadMessage(hookThreadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);

        if (workers is not null)
        {
            try { await workers.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (OperationCanceledException) { }
        }

        if (hookThread is { IsAlive: true })
        {
            try { hookThread.Join(TimeSpan.FromSeconds(2)); }
            catch (ThreadStateException) { }
        }

        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_stopSource, stopSource))
            {
                _stopSource.Dispose();
                _stopSource = null;
                _foregroundEvents = null;
                _workers = null;
                _hookThread = null;
                _hookThreadId = 0;
                _isRunning = false;
                _status = "stopped";
            }
        }
    }

    public ConsoleLaunchAuditSnapshot Snapshot(int limit = 100) =>
        new(IsSupported, IsRunning, _status, DateTimeOffset.UtcNow, List(limit));

    public IReadOnlyList<ConsoleLaunchAuditEvent> List(int limit = 100)
    {
        limit = Math.Clamp(limit, 0, MaximumEvents);
        lock (_dataGate)
        {
            if (limit == 0 || _events.Count == 0) return Array.Empty<ConsoleLaunchAuditEvent>();
            var count = Math.Min(limit, _events.Count);
            var output = new ConsoleLaunchAuditEvent[count];
            for (var index = 0; index < count; index++)
                output[index] = _events[_events.Count - 1 - index];
            return output;
        }
    }

    public void Clear()
    {
        lock (_dataGate)
        {
            _events.Clear();
            _repeatStates.Clear();
        }
    }

    public bool MatchesObservedSource(
        int processId,
        string processName,
        string? executablePath,
        DateTimeOffset? startedAt)
    {
        lock (_dataGate)
        {
            foreach (var item in _events)
            {
                var source = item.CandidateLaunchingProcess;
                if (source is null || source.ProcessId != processId) continue;
                if (!string.Equals(
                        NormalizeProcessName(source.Name),
                        NormalizeProcessName(processName),
                        StringComparison.OrdinalIgnoreCase)) continue;

                if (!string.IsNullOrWhiteSpace(source.ExecutablePath) &&
                    !string.IsNullOrWhiteSpace(executablePath) &&
                    !string.Equals(
                        Path.GetFullPath(source.ExecutablePath),
                        Path.GetFullPath(executablePath),
                        StringComparison.OrdinalIgnoreCase)) continue;

                if (source.StartedAt is { } observedStart && startedAt is { } currentStart &&
                    Math.Abs((observedStart - currentStart).TotalSeconds) > 2) continue;

                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch { }
        GC.SuppressFinalize(this);
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _hookHandle = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _winEventCallback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);

        if (_hookHandle == IntPtr.Zero)
        {
            _status = $"hook-error:{Marshal.GetLastWin32Error()}";
            _hookThreadId = 0;
            return;
        }

        _isRunning = true;
        _status = "running";
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _hookThreadId = 0;
            _isRunning = false;
            if (_stopSource is { IsCancellationRequested: false }) _status = "hook-stopped";
        }
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        try
        {
            if (eventType != EventSystemForeground || window == IntPtr.Zero) return;
            var channel = _foregroundEvents;
            if (channel is null) return;

            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return;

            channel.Writer.TryWrite(new RawForegroundEvent(
                DateTimeOffset.UtcNow,
                window.ToInt64(),
                unchecked((int)processId),
                ReadWindowText(window),
                ReadWindowClass(window)));
        }
        catch
        {
            // Native callbacks must never leak an exception across the ABI boundary.
        }
    }

    private async Task SampleProcessesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { AddSnapshot(CaptureProcessSnapshot()); }
            catch { }

            try { await Task.Delay(SamplingInterval, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessForegroundEventsAsync(
        ChannelReader<RawForegroundEvent> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var raw in reader.ReadAllAsync(cancellationToken))
            {
                try { ProcessForegroundEvent(raw); }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ProcessForegroundEvent(RawForegroundEvent raw)
    {
        var identity = ReadProcess(raw.WindowProcessId, 0, null);
        if (!IsRelevantWindow(identity.Name, raw.WindowClass, raw.WindowTitle)) return;

        var snapshot = CaptureProcessSnapshot();
        AddSnapshot(snapshot);
        if (!snapshot.Processes.TryGetValue(raw.WindowProcessId, out var windowProcess))
            windowProcess = identity;

        var commandProcess = SelectCommandProcess(windowProcess, snapshot, raw.ObservedAt);
        var (parentChain, complete) = BuildParentChain(commandProcess, snapshot);
        var candidateLauncher = parentChain.FirstOrDefault(process => !IsConsoleProcess(process.Name));
        var signature = BuildRepeatSignature(
            windowProcess,
            commandProcess,
            candidateLauncher,
            raw.WindowClass);

        long eventId;
        DateTimeOffset firstObservedAt;
        int repeatCount;
        double? intervalMilliseconds;
        double? averageIntervalMilliseconds;
        lock (_dataGate)
        {
            if (_repeatStates.TryGetValue(signature, out var previous))
            {
                eventId = Interlocked.Increment(ref _nextId);
                firstObservedAt = previous.FirstSeen;
                repeatCount = previous.Count + 1;
                intervalMilliseconds = Math.Round((raw.ObservedAt - previous.LastSeen).TotalMilliseconds, 1);
                var totalIntervalMilliseconds = previous.TotalIntervalMilliseconds + intervalMilliseconds.Value;
                averageIntervalMilliseconds = Math.Round(totalIntervalMilliseconds / (repeatCount - 1), 1);
                _repeatStates[signature] = new RepeatState(
                    repeatCount,
                    firstObservedAt,
                    raw.ObservedAt,
                    totalIntervalMilliseconds,
                    eventId);
                _events.RemoveAll(item => item.Id == previous.EventId);
            }
            else
            {
                eventId = Interlocked.Increment(ref _nextId);
                firstObservedAt = raw.ObservedAt;
                repeatCount = 1;
                intervalMilliseconds = null;
                averageIntervalMilliseconds = null;
                if (_repeatStates.Count >= MaximumRepeatKeys)
                {
                    var oldest = _repeatStates.MinBy(pair => pair.Value.LastSeen).Key;
                    if (oldest is not null) _repeatStates.Remove(oldest);
                }
                _repeatStates[signature] = new RepeatState(1, raw.ObservedAt, raw.ObservedAt, 0, eventId);
            }

            var (classification, explanation) = Classify(windowProcess, commandProcess, candidateLauncher);

            var item = new ConsoleLaunchAuditEvent(
                eventId,
                firstObservedAt,
                raw.ObservedAt,
                raw.WindowHandle,
                windowProcess,
                Limit(raw.WindowTitle, 512),
                Limit(raw.WindowClass, 128),
                commandProcess,
                candidateLauncher,
                parentChain,
                complete,
                commandProcess.CommandLine,
                commandProcess.ExecutablePath,
                repeatCount,
                intervalMilliseconds,
                averageIntervalMilliseconds,
                classification,
                explanation);

            _events.Add(item);
            if (_events.Count > MaximumEvents) _events.RemoveRange(0, _events.Count - MaximumEvents);
        }
    }

    private NativeProcessSnapshot CaptureProcessSnapshot()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var seeds = EnumerateProcesses();
        var processes = seeds.Values.ToDictionary(
            seed => seed.ProcessId,
            seed => new ConsoleLaunchAuditProcess(
                seed.ProcessId,
                seed.ParentProcessId,
                NormalizeProcessName(seed.Name),
                null,
                null,
                null));

        var enrich = new HashSet<int>(processes.Values
            .Where(process => IsConsoleProcess(process.Name))
            .Select(process => process.ProcessId));

        foreach (var processId in enrich.ToArray())
        {
            var current = processId;
            for (var depth = 0; depth < MaximumParentDepth && current > 0; depth++)
            {
                if (!seeds.TryGetValue(current, out var seed)) break;
                enrich.Add(current);
                if (seed.ParentProcessId <= 0 || seed.ParentProcessId == current) break;
                current = seed.ParentProcessId;
            }
        }

        foreach (var processId in enrich)
        {
            if (!seeds.TryGetValue(processId, out var seed)) continue;
            processes[processId] = ReadProcess(seed.ProcessId, seed.ParentProcessId, seed.Name);
        }

        return new NativeProcessSnapshot(capturedAt, processes);
    }

    private static Dictionary<int, ProcessSeed> EnumerateProcesses()
    {
        var output = new Dictionary<int, ProcessSeed>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return output;

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return output;
            do
            {
                var processId = unchecked((int)entry.ProcessId);
                if (processId <= 0) continue;
                output[processId] = new ProcessSeed(
                    processId,
                    unchecked((int)entry.ParentProcessId),
                    entry.ExecutableFile ?? "unknown");
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return output;
    }

    private static ConsoleLaunchAuditProcess ReadProcess(int processId, int parentProcessId, string? nameHint)
    {
        string? executablePath = null;
        string? commandLine = null;
        DateTimeOffset? startedAt = null;

        var process = OpenProcess(ProcessQueryLimitedInformation, false, unchecked((uint)processId));
        if (process != IntPtr.Zero)
        {
            try
            {
                executablePath = QueryExecutablePath(process);
                commandLine = QueryCommandLine(process);
                startedAt = QueryStartedAt(process);
            }
            finally
            {
                CloseHandle(process);
            }
        }

        var name = NormalizeProcessName(
            !string.IsNullOrWhiteSpace(executablePath)
                ? Path.GetFileName(executablePath)
                : nameHint);
        return new ConsoleLaunchAuditProcess(
            processId,
            parentProcessId,
            name,
            Limit(executablePath, 2_048),
            Limit(commandLine, MaximumCommandLineLength),
            startedAt);
    }

    private ConsoleLaunchAuditProcess SelectCommandProcess(
        ConsoleLaunchAuditProcess windowProcess,
        NativeProcessSnapshot snapshot,
        DateTimeOffset observedAt)
    {
        if (IsCommandShell(windowProcess.Name)) return windowProcess;

        ConsoleLaunchAuditProcess? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var process in snapshot.Processes.Values)
        {
            if (!IsCommandShell(process.Name)) continue;
            var ageSeconds = process.StartedAt.HasValue
                ? (observedAt - process.StartedAt.Value).TotalSeconds
                : double.PositiveInfinity;
            if (ageSeconds < -2 || ageSeconds > 12) continue;

            var score = 120 - Math.Max(0, ageSeconds) * 10;
            if (process.ParentProcessId == windowProcess.ParentProcessId && process.ParentProcessId > 0)
                score += 250;
            if (IsAncestor(process.ProcessId, windowProcess.ProcessId, snapshot)) score += 500;
            if (IsAncestor(windowProcess.ProcessId, process.ProcessId, snapshot)) score += 500;
            if (!WasPresentBefore(process.ProcessId, snapshot.CapturedAt)) score += 100;

            if (score > bestScore)
            {
                best = process;
                bestScore = score;
            }
        }

        return best ?? windowProcess;
    }

    private static bool IsAncestor(int possibleAncestor, int processId, NativeProcessSnapshot snapshot)
    {
        var visited = new HashSet<int>();
        var current = processId;
        for (var depth = 0; depth < MaximumParentDepth; depth++)
        {
            if (!visited.Add(current) || !snapshot.Processes.TryGetValue(current, out var process)) return false;
            if (process.ParentProcessId == possibleAncestor) return true;
            if (process.ParentProcessId <= 0 || process.ParentProcessId == current) return false;
            current = process.ParentProcessId;
        }
        return false;
    }

    private bool WasPresentBefore(int processId, DateTimeOffset currentSnapshotAt)
    {
        lock (_dataGate)
        {
            return _snapshots.Any(snapshot =>
                snapshot.CapturedAt < currentSnapshotAt &&
                currentSnapshotAt - snapshot.CapturedAt <= TimeSpan.FromSeconds(5) &&
                snapshot.Processes.ContainsKey(processId));
        }
    }

    private static (IReadOnlyList<ConsoleLaunchAuditProcess> Chain, bool Complete) BuildParentChain(
        ConsoleLaunchAuditProcess process,
        NativeProcessSnapshot snapshot)
    {
        var output = new List<ConsoleLaunchAuditProcess>();
        var visited = new HashSet<int> { process.ProcessId };
        var parentId = process.ParentProcessId;

        for (var depth = 0; depth < MaximumParentDepth; depth++)
        {
            if (parentId <= 0) return (output, true);
            if (!visited.Add(parentId)) return (output, false);
            if (!snapshot.Processes.TryGetValue(parentId, out var parent)) return (output, false);
            output.Add(parent);
            if (parent.ParentProcessId <= 0 || parent.ParentProcessId == parent.ProcessId)
                return (output, true);
            parentId = parent.ParentProcessId;
        }

        return (output, false);
    }

    private void AddSnapshot(NativeProcessSnapshot snapshot)
    {
        lock (_dataGate)
        {
            _snapshots.Enqueue(snapshot);
            while (_snapshots.Count > MaximumSnapshots) _snapshots.Dequeue();
        }
    }

    private static string BuildRepeatSignature(
        ConsoleLaunchAuditProcess windowProcess,
        ConsoleLaunchAuditProcess commandProcess,
        ConsoleLaunchAuditProcess? launcher,
        string? windowClass) =>
        string.Join('\u001f',
            windowProcess.Name,
            commandProcess.ExecutablePath ?? commandProcess.Name,
            commandProcess.CommandLine ?? "",
            launcher?.ExecutablePath ?? launcher?.Name ?? "",
            launcher?.CommandLine ?? "",
            windowClass ?? "");

    private static (string Classification, string Explanation) Classify(
        ConsoleLaunchAuditProcess windowProcess,
        ConsoleLaunchAuditProcess commandProcess,
        ConsoleLaunchAuditProcess? launcher)
    {
        var command = commandProcess.CommandLine ?? "";
        if (string.Equals(launcher?.Name, "devspace", StringComparison.OrdinalIgnoreCase) &&
            launcher?.ExecutablePath?.Contains("devspace-sentinel", StringComparison.OrdinalIgnoreCase) == true &&
            command.Contains("PerfDisk_PhysicalDisk", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "DevSpace Sentinel 磁盘负载探针",
                "VS Code 启动了 DevSpace Sentinel；它的 5 秒调度器通过 PowerShell 查询磁盘负载，查询耗时叠加后形成约 6 至 8 秒一次的终端弹窗。");
        }

        if (string.Equals(launcher?.Name, "chatgpt", StringComparison.OrdinalIgnoreCase) &&
            (command.Contains("Win32_PerfFormattedData_PerfProc_Process", StringComparison.OrdinalIgnoreCase) ||
             command.Contains("Select-Object ProcessId,ParentProcessId", StringComparison.OrdinalIgnoreCase) ||
             command.Contains("PerfDisk_PhysicalDisk", StringComparison.OrdinalIgnoreCase)))
        {
            return (
                "ChatGPT 内部进程资源监控",
                "ChatGPT 为显示会话关联进程及资源占用而启动 PowerShell；终端程序只是窗口宿主，不是启动源。");
        }

        if (launcher is not null)
        {
            return (
                $"由 {launcher.Name}.exe 直接启动",
                $"父进程链表明 {launcher.Name}.exe 启动了 {commandProcess.Name}.exe；{windowProcess.Name}.exe 负责显示窗口。");
        }

        return (
            "来源尚未完全匹配",
            "已记录窗口进程和命令进程，但父进程可能在采样前退出。展开详情可查看现有证据。");
    }

    private static bool IsRelevantWindow(string name, string? windowClass, string? title)
    {
        if (IsConsoleProcess(name)) return true;
        if (!string.IsNullOrWhiteSpace(windowClass) &&
            (windowClass.Contains("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase) ||
             windowClass.Contains("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase) ||
             windowClass.Contains("PseudoConsole", StringComparison.OrdinalIgnoreCase))) return true;

        if (string.IsNullOrWhiteSpace(title)) return false;
        return title.Contains("Command Prompt", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Windows Terminal", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("命令提示符", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("终端", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConsoleProcess(string? name)
    {
        var normalized = NormalizeProcessName(name);
        return IsCommandShell(normalized) ||
               normalized is "conhost" or "openconsole" or "windowsterminal" or "wt" or "mintty" or "wezterm" or "alacritty" ||
               normalized.Contains("terminal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandShell(string? name)
    {
        var normalized = NormalizeProcessName(name);
        return normalized is "cmd" or "powershell" or "pwsh";
    }

    private static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        var fileName = Path.GetFileName(name.Trim());
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4].ToLowerInvariant()
            : fileName.ToLowerInvariant();
    }

    private static string? QueryExecutablePath(IntPtr process)
    {
        var capacity = 32_768;
        var builder = new StringBuilder(capacity);
        return QueryFullProcessImageName(process, 0, builder, ref capacity)
            ? builder.ToString()
            : null;
    }

    private static string? QueryCommandLine(IntPtr process)
    {
        var status = NtQueryInformationProcess(
            process,
            ProcessCommandLineInformation,
            IntPtr.Zero,
            0,
            out var required);
        if (required == 0 || required > 1_048_576 || (status >= 0 && required < 2)) return null;

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            status = NtQueryInformationProcess(
                process,
                ProcessCommandLineInformation,
                buffer,
                required,
                out _);
            if (status < 0) return null;
            var value = Marshal.PtrToStructure<NativeUnicodeString>(buffer);
            if (value.Length == 0 || value.Buffer == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char));
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static DateTimeOffset? QueryStartedAt(IntPtr process)
    {
        if (!GetProcessTimes(process, out var created, out _, out _, out _)) return null;
        var value = ((long)created.High << 32) | created.Low;
        try { return DateTimeOffset.FromFileTime(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? ReadWindowText(IntPtr window)
    {
        var length = Math.Min(GetWindowTextLength(window), 512);
        if (length <= 0) return null;
        var builder = new StringBuilder(length + 1);
        return GetWindowText(window, builder, builder.Capacity) > 0 ? builder.ToString() : null;
    }

    private static string? ReadWindowClass(IntPtr window)
    {
        var builder = new StringBuilder(256);
        return GetClassName(window, builder, builder.Capacity) > 0 ? builder.ToString() : null;
    }

    private static string? Limit(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maximumLength
            ? value
            : value[..maximumLength];

    private sealed record RawForegroundEvent(
        DateTimeOffset ObservedAt,
        long WindowHandle,
        int WindowProcessId,
        string? WindowTitle,
        string? WindowClass);

    private sealed record RepeatState(
        int Count,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen,
        double TotalIntervalMilliseconds,
        long EventId);
    private sealed record ProcessSeed(int ProcessId, int ParentProcessId, string Name);
    private sealed record NativeProcessSnapshot(
        DateTimeOffset CapturedAt,
        IReadOnlyDictionary<int, ConsoleLaunchAuditProcess> Processes);

    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint WmQuit = 0x0012;
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);
}
