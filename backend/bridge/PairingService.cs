using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexLanBridge;

public enum PairingAttemptResult
{
    Success,
    InvalidCode,
    RateLimited
}

public sealed class PairingService
{
    public const string SessionCookieName = "CodexLanSession";
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(1);
    private const int MaximumClientFailures = 5;
    private const int MaximumGlobalFailures = 20;
    private readonly object _gate = new();
    private readonly string _configFile;
    private readonly Queue<DateTimeOffset> _globalFailures = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _clientFailures = new(StringComparer.Ordinal);
    private HashSet<string> _hashes = new(StringComparer.Ordinal);
    private string _code = NewCode();
    public string Code { get { lock (_gate) return _code; } }
    public string PairingFile { get; }
    public bool HasDevices { get { lock (_gate) return _hashes.Count > 0; } }

    public PairingService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexLanConsole");
        Directory.CreateDirectory(dir);
        _configFile = Path.Combine(dir, "devices.json");
        PairingFile = Path.Combine(dir, "pairing.txt");
        if (File.Exists(_configFile))
        {
            try { _hashes = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_configFile)) ?? _hashes; } catch { }
        }
        WritePairingFile();
    }

    public PairingAttemptResult TryPair(
        string code,
        string deviceName,
        string clientKey,
        out string token,
        out int retryAfterSeconds)
    {
        token = "";
        retryAfterSeconds = 0;
        clientKey = string.IsNullOrWhiteSpace(clientKey) ? "unknown" : clientKey;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            PurgeFailures(now);
            var clientFailures = GetClientFailures(clientKey);
            if (clientFailures.Count >= MaximumClientFailures || _globalFailures.Count >= MaximumGlobalFailures)
            {
                var clientWait = clientFailures.Count >= MaximumClientFailures
                    ? clientFailures.Peek() + AttemptWindow - now
                    : TimeSpan.Zero;
                var globalWait = _globalFailures.Count >= MaximumGlobalFailures
                    ? _globalFailures.Peek() + AttemptWindow - now
                    : TimeSpan.Zero;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(Math.Max(clientWait.TotalSeconds, globalWait.TotalSeconds)));
                return PairingAttemptResult.RateLimited;
            }

            var valid = code.Length == 6 && code.All(char.IsAsciiDigit) &&
                        CryptographicOperations.FixedTimeEquals(
                            Encoding.ASCII.GetBytes(code),
                            Encoding.ASCII.GetBytes(_code));
            if (!valid)
            {
                clientFailures.Enqueue(now);
                _globalFailures.Enqueue(now);
                return PairingAttemptResult.InvalidCode;
            }

            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _hashes.Add(Hash(token));
            WriteTextAtomically(_configFile, JsonSerializer.Serialize(_hashes));
            _code = NewCode();
            _globalFailures.Clear();
            _clientFailures.Clear();
            WritePairingFile();
            return PairingAttemptResult.Success;
        }
    }

    public bool Validate(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64 || token.Any(character => !char.IsAsciiHexDigit(character))) return false;
        lock (_gate) return _hashes.Contains(Hash(token));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string NewCode() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

    private Queue<DateTimeOffset> GetClientFailures(string clientKey)
    {
        if (_clientFailures.TryGetValue(clientKey, out var failures)) return failures;
        failures = new Queue<DateTimeOffset>();
        _clientFailures[clientKey] = failures;
        return failures;
    }

    private void PurgeFailures(DateTimeOffset now)
    {
        var cutoff = now - AttemptWindow;
        DateTimeOffset failure;
        while (_globalFailures.TryPeek(out failure) && failure <= cutoff) _globalFailures.Dequeue();
        foreach (var pair in _clientFailures.ToArray())
        {
            while (pair.Value.TryPeek(out failure) && failure <= cutoff) pair.Value.Dequeue();
            if (pair.Value.Count == 0) _clientFailures.Remove(pair.Key);
        }
    }

    private void WritePairingFile() => WriteTextAtomically(
        PairingFile,
        $"Pairing code: {_code}{Environment.NewLine}Created: {DateTimeOffset.Now:O}{Environment.NewLine}");

    private static void WriteTextAtomically(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
