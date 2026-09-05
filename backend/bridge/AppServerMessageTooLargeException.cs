namespace CodexLanBridge;

public sealed class AppServerMessageTooLargeException : IOException
{
    public long ActualBytes { get; }
    public long MaximumBytes { get; }
    public long? RequestId { get; }
    public string? Method { get; }
    public string? ThreadId { get; }
    public string? TurnId { get; }
    public long? Generation { get; }
    public int? ProcessId { get; }

    public AppServerMessageTooLargeException(long actualBytes, long maximumBytes)
        : this(actualBytes, maximumBytes, null, null, null, null, null, null)
    {
    }

    public AppServerMessageTooLargeException(
        long actualBytes,
        long maximumBytes,
        long? requestId,
        string? method,
        string? threadId,
        string? turnId,
        long? generation,
        int? processId)
        : base($"Codex app-server message exceeded the {maximumBytes / (1024 * 1024)} MiB safety limit ({actualBytes} bytes received).")
    {
        ActualBytes = actualBytes;
        MaximumBytes = maximumBytes;
        RequestId = requestId;
        Method = method;
        ThreadId = threadId;
        TurnId = turnId;
        Generation = generation;
        ProcessId = processId;
    }
}

public sealed class AppServerDisconnectedException : IOException
{
    public long RequestId { get; }
    public string Method { get; }
    public string? ThreadId { get; }
    public string? TurnId { get; }
    public long Generation { get; }
    public int ProcessId { get; }
    public bool ProcessExited { get; }
    public int? ExitCode { get; }

    public AppServerDisconnectedException(
        long requestId,
        string method,
        string? threadId,
        string? turnId,
        long generation,
        int processId,
        bool processExited,
        int? exitCode)
        : base("Codex app-server disconnected before acknowledging the request.")
    {
        RequestId = requestId;
        Method = method;
        ThreadId = threadId;
        TurnId = turnId;
        Generation = generation;
        ProcessId = processId;
        ProcessExited = processExited;
        ExitCode = exitCode;
    }
}
