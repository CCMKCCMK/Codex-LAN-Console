using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexLanBridge;

public sealed record AppServerDiagnosticEntry(
    string Reason,
    long Generation,
    int ProcessId,
    bool ProcessExited,
    int? ExitCode,
    string StderrTail,
    long? OversizeBytes,
    string? Method,
    long? RequestId,
    string? ThreadId,
    string? TurnId,
    bool? EndedWithNewline = null,
    string? CommandId = null,
    int? RpcCode = null,
    string? ErrorMessage = null);

/// <summary>
/// Stores bounded, redacted transport diagnostics. It never writes protocol
/// payloads, prompts, request parameters, cookies, or authorization headers.
/// </summary>
public sealed partial class AppServerDiagnosticLog
{
    private const long MaximumBytes = 512 * 1024;
    private const int RetainedBytes = 256 * 1024;
    private readonly object _gate = new();
    private readonly string _path;

    public AppServerDiagnosticLog() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexLanConsole",
        "app-server-diagnostics.jsonl"))
    {
    }

    public AppServerDiagnosticLog(string path)
    {
        _path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

    public void Write(AppServerDiagnosticEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.UtcNow,
                reason = LimitIdentifier(entry.Reason, 128),
                generation = entry.Generation,
                processId = entry.ProcessId,
                processExited = entry.ProcessExited,
                exitCode = entry.ExitCode,
                stderrTail = SanitizeText(entry.StderrTail),
                oversizeBytes = entry.OversizeBytes,
                method = LimitIdentifier(entry.Method, 256),
                requestId = entry.RequestId,
                threadId = LimitIdentifier(entry.ThreadId, 256),
                turnId = LimitIdentifier(entry.TurnId, 256),
                endedWithNewline = entry.EndedWithNewline,
                commandId = LimitIdentifier(entry.CommandId, 128),
                rpcCode = entry.RpcCode,
                errorMessage = entry.ErrorMessage is null ? null : SanitizeText(entry.ErrorMessage)
            }) + Environment.NewLine;

            lock (_gate)
            {
                File.AppendAllText(_path, line, new UTF8Encoding(false));
                var info = new FileInfo(_path);
                if (info.Length <= MaximumBytes) return;
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
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
            // Diagnostics must never become a transport failure.
        }
    }

    public static string SanitizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            text = text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = CredentialAssignmentRegex().Replace(text, "$1=[redacted]");
        text = BearerRegex().Replace(text, "$1 [redacted]");
        text = KnownTokenRegex().Replace(text, "[redacted-token]");
        text = JwtRegex().Replace(text, "[redacted-jwt]");
        text = QueryValueRegex().Replace(text, "$1[redacted]");
        return text.Length <= 4096 ? text : text[^4096..];
    }

    private static string? LimitIdentifier(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var redacted = SanitizeText(value);
        var safe = new string(redacted.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '/' or ':' or '.' or '_' or '-').ToArray());
        if (safe.Length == 0) return null;
        return safe.Length <= maximumCharacters ? safe : safe[..maximumCharacters];
    }

    [GeneratedRegex("(?i)\\b(token|password|secret|api[_-]?key|authorization)\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex("(?i)\\b(bearer)\\s+[^\\s,;]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)\\b(?:gh[pousr]_[A-Za-z0-9_]{16,}|sk-[A-Za-z0-9_-]{16,})\\b")]
    private static partial Regex KnownTokenRegex();

    [GeneratedRegex("\\b[A-Za-z0-9_-]{16,}\\.[A-Za-z0-9_-]{16,}\\.[A-Za-z0-9_-]{16,}\\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex("([?&][A-Za-z0-9_.-]{1,64}=)[^&\\s]+")]
    private static partial Regex QueryValueRegex();
}

internal sealed class AppServerStderrTail
{
    private const int MaximumLines = 12;
    private const int MaximumCharacters = 4096;
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();

    public void Observe(string line)
    {
        var safe = AppServerDiagnosticLog.SanitizeText(line);
        if (safe.Length == 0) return;
        lock (_gate)
        {
            _lines.Enqueue(safe);
            while (_lines.Count > MaximumLines) _lines.Dequeue();
            while (_lines.Sum(item => item.Length + 1) > MaximumCharacters && _lines.Count > 1)
                _lines.Dequeue();
        }
    }

    public string Snapshot()
    {
        lock (_gate) return string.Join('\n', _lines);
    }
}
