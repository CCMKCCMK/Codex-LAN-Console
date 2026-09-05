using System.Text;
using System.Text.Json;

namespace CodexLanBridge;

/// <summary>
/// Keeps a small local-only record of unhandled API failures. The log deliberately
/// excludes request headers, cookies, authorization tokens, query values, and bodies.
/// </summary>
public sealed class ApiErrorLog
{
    private const long MaximumBytes = 512 * 1024;
    private const int RetainedBytes = 256 * 1024;
    private const int MaximumMessageCharacters = 8 * 1024;
    private const int MaximumStackCharacters = 32 * 1024;
    private readonly object _gate = new();
    private readonly string _path;

    public ApiErrorLog()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLanConsole");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "api-errors.jsonl");
    }

    public void Write(
        string requestId,
        string method,
        string path,
        Exception exception)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.UtcNow,
                requestId = Limit(requestId, 128),
                method = Limit(method, 16),
                path = Limit(path, 2048),
                exceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                message = Limit(exception.Message, MaximumMessageCharacters),
                stack = Limit(exception.StackTrace ?? "", MaximumStackCharacters)
            }) + Environment.NewLine;

            lock (_gate)
            {
                File.AppendAllText(_path, line, new UTF8Encoding(false));
                var info = new FileInfo(_path);
                if (info.Length <= MaximumBytes) return;

                using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read);
                stream.Seek(Math.Max(0, stream.Length - RetainedBytes), SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true);
                if (stream.Position > 0) reader.ReadLine();
                var retained = reader.ReadToEnd();
                stream.SetLength(0);
                stream.Position = 0;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true);
                writer.Write(retained);
            }
        }
        catch
        {
            // Diagnostics must never turn one API failure into another.
        }
    }

    private static string Limit(string? value, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters] + "\n[truncated]";
    }
}
