using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexLanBridge;

public enum PairingAttemptResult
{
    Success,
    InvalidCode,
    RateLimited,
    PairingClosed
}

public sealed class PairingService
{
    public const string SessionCookieName = "CodexLanSession";
    internal static readonly TimeSpan DefaultCodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(1);
    private const int MaximumClientFailures = 5;
    private const int MaximumGlobalFailures = 20;
    private readonly object _gate = new();
    private readonly PairingStoragePaths _storage;
    private readonly string _configFile;
    private readonly bool _administratorMode;
    private readonly bool _protectStorage;
    private readonly Func<string> _newCode;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _codeLifetime;
    private readonly PersistentAdministratorCode _administratorCode;
    private readonly Queue<DateTimeOffset> _globalFailures = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _clientFailures = new(StringComparer.Ordinal);
    private HashSet<string> _hashes = new(StringComparer.Ordinal);
    private string _code = "";
    private bool _pairingOpen;
    private DateTimeOffset? _codeCreatedAt;
    private DateTimeOffset? _codeExpiresAt;
    public string Code
    {
        get
        {
            lock (_gate)
            {
                RefreshCodeStateLocked();
                return _code;
            }
        }
    }
    public string PairingFile { get; }
    public bool HasDevices { get { lock (_gate) return _hashes.Count > 0; } }
    public bool IsPairingOpen
    {
        get
        {
            lock (_gate)
            {
                RefreshCodeStateLocked();
                return _pairingOpen || _administratorCode.IsConfigured;
            }
        }
    }
    public bool IsTemporaryPairingOpen
    {
        get
        {
            lock (_gate)
            {
                RefreshCodeStateLocked();
                return _pairingOpen;
            }
        }
    }
    public bool HasPersistentAdministratorCode => _administratorCode.IsConfigured;
    public DateTimeOffset? CodeExpiresAt
    {
        get
        {
            lock (_gate)
            {
                RefreshCodeStateLocked();
                return _codeExpiresAt;
            }
        }
    }
    public bool AdministratorMode => _administratorMode;

    public PairingService() : this(PairingStoragePolicy.ResolveCurrent(), NewCode, prepareStorage: true)
    {
    }

    internal PairingService(
        PairingStoragePaths storage,
        Func<string>? newCode = null,
        bool prepareStorage = false,
        Func<DateTimeOffset>? now = null,
        TimeSpan? codeLifetime = null,
        PersistentAdministratorCode? administratorCode = null)
    {
        _storage = storage;
        _administratorMode = storage.AdministratorMode;
        _protectStorage = prepareStorage && storage.AdministratorMode;
        _newCode = newCode ?? NewCode;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _codeLifetime = codeLifetime ?? DefaultCodeLifetime;
        if (_codeLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(codeLifetime), "The pairing-code lifetime must be positive.");
        if (prepareStorage) PairingStoragePolicy.Prepare(storage);
        else Directory.CreateDirectory(storage.Directory);
        _administratorCode = administratorCode ?? new PersistentAdministratorCode(storage.AdministratorCodeFile);
        if (_protectStorage && _administratorCode.IsConfigured)
            PairingStoragePolicy.ProtectSecretFile(storage, _administratorCode.Path);
        _configFile = storage.DevicesFile;
        PairingFile = storage.PairingFile;
        if (File.Exists(_configFile))
        {
            try
            {
                _hashes = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_configFile)) ??
                    throw new InvalidDataException("The paired-device store is empty or invalid.");
            }
            catch when (!_administratorMode)
            {
                // Preserve the standard Bridge's historical self-recovery behavior.
            }
        }
        var locallyOpened = _administratorMode && ConsumeAdministratorOpenRequest();
        _pairingOpen = !_administratorMode || _hashes.Count == 0 || locallyOpened;
        if (_pairingOpen) IssueCodeLocked();
        WritePairingFileLocked();
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
            RefreshCodeStateLocked();
            var persistentCodeAvailable = _administratorCode.IsConfigured;
            if (!_pairingOpen && !persistentCodeAvailable)
                return PairingAttemptResult.PairingClosed;

            var now = _now();
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

            var persistentValid = persistentCodeAvailable && _administratorCode.Validate(code);
            var temporaryValid = _pairingOpen && code.Length == 6 && code.All(char.IsAsciiDigit) &&
                                 CryptographicOperations.FixedTimeEquals(
                                     Encoding.ASCII.GetBytes(code),
                                     Encoding.ASCII.GetBytes(_code));
            var valid = persistentValid || temporaryValid;
            if (!valid)
            {
                clientFailures.Enqueue(now);
                _globalFailures.Enqueue(now);
                return PairingAttemptResult.InvalidCode;
            }

            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _hashes.Add(Hash(token));
            WriteTextAtomically(_configFile, JsonSerializer.Serialize(_hashes));
            if (_administratorMode) ClosePairingLocked();
            else IssueCodeLocked();
            _globalFailures.Clear();
            _clientFailures.Clear();
            WritePairingFileLocked();
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

    private bool ConsumeAdministratorOpenRequest()
    {
        if (!File.Exists(_storage.OpenPairingRequestFile)) return false;
        if ((File.GetAttributes(_storage.OpenPairingRequestFile) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException(
                $"Administrator pairing requests cannot use a reparse point: {_storage.OpenPairingRequestFile}");
        File.Delete(_storage.OpenPairingRequestFile);
        return true;
    }

    private void IssueCodeLocked()
    {
        _pairingOpen = true;
        _code = _newCode();
        _codeCreatedAt = _now();
        _codeExpiresAt = _codeCreatedAt.Value + _codeLifetime;
    }

    private void ClosePairingLocked()
    {
        _pairingOpen = false;
        _code = "";
        _codeCreatedAt = null;
        _codeExpiresAt = null;
    }

    private void RefreshCodeStateLocked()
    {
        if (!_pairingOpen || !_codeExpiresAt.HasValue || _now() < _codeExpiresAt.Value) return;
        if (_administratorMode) ClosePairingLocked();
        else IssueCodeLocked();
        WritePairingFileLocked();
    }

    private void WritePairingFileLocked() => WriteTextAtomically(
        PairingFile,
        _pairingOpen
            ? $"Pairing code: {_code}{Environment.NewLine}" +
              $"Created: {_codeCreatedAt:O}{Environment.NewLine}" +
              $"Expires: {_codeExpiresAt:O}{Environment.NewLine}"
            : (_administratorCode.IsConfigured
                ? $"Persistent administrator sign-in is enabled.{Environment.NewLine}" +
                  $"The administrator code is stored only as a slow salted verifier.{Environment.NewLine}"
                : $"Pairing is closed for Administrator Mode.{Environment.NewLine}") +
              $"Existing protected devices remain authorized.{Environment.NewLine}" +
              $"Use the local Windows manager to open a time-limited window for another device.{Environment.NewLine}");

    private void WriteTextAtomically(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        if (_protectStorage) PairingStoragePolicy.ProtectFile(_storage, temporary);
        File.Move(temporary, path, true);
        if (_protectStorage) PairingStoragePolicy.ProtectFile(_storage, path);
    }
}
