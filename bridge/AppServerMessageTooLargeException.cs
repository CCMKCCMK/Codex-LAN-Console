namespace CodexLanBridge;

public sealed class AppServerMessageTooLargeException : IOException
{
    public long ActualBytes { get; }
    public long MaximumBytes { get; }

    public AppServerMessageTooLargeException(long actualBytes, long maximumBytes)
        : base($"Codex app-server message exceeded the {maximumBytes / (1024 * 1024)} MiB safety limit ({actualBytes} bytes received).")
    {
        ActualBytes = actualBytes;
        MaximumBytes = maximumBytes;
    }
}
