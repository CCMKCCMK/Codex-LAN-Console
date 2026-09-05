using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexLanBridge;

/// <summary>
/// A user-chosen administrator pairing code stored as a slow salted verifier.
/// The plaintext code is never written to disk. The caller selects the storage
/// boundary so an elevated Bridge never trusts a verifier writable by a
/// standard process.
/// </summary>
public sealed class PersistentAdministratorCode
{
    private const int CurrentVersion = 1;
    private const int DefaultIterations = 600_000;
    private const int SaltBytes = 24;
    private const int HashBytes = 32;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _iterations;
    private AdministratorCodeVerifier? _verifier;

    public PersistentAdministratorCode(string path, int iterations = DefaultIterations)
    {
        if (iterations < 1_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        _iterations = iterations;
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("An explicit administrator-code verifier path is required.", nameof(path))
            : System.IO.Path.GetFullPath(path);
        _verifier = Load(_path);
    }

    public string Path => _path;
    public bool IsConfigured { get { lock (_gate) return _verifier is not null; } }

    public bool Validate(string? code)
    {
        if (!ValidFormat(code)) return false;
        AdministratorCodeVerifier? verifier;
        lock (_gate) verifier = _verifier;
        if (verifier is null) return false;
        try
        {
            var salt = Convert.FromBase64String(verifier.Salt);
            var expected = Convert.FromBase64String(verifier.Hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.ASCII.GetBytes(code!),
                salt,
                verifier.Iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            try { return CryptographicOperations.FixedTimeEquals(actual, expected); }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
                CryptographicOperations.ZeroMemory(expected);
            }
        }
        catch (FormatException) { return false; }
    }

    public void Configure(string code, Action<string>? protectFile = null)
    {
        if (!ValidFormat(code))
            throw new ArgumentException("The administrator code must contain exactly six ASCII digits.", nameof(code));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.ASCII.GetBytes(code),
            salt,
            _iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        try
        {
            var verifier = new AdministratorCodeVerifier(
                CurrentVersion,
                _iterations,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(hash));
            var directory = System.IO.Path.GetDirectoryName(_path) ??
                throw new InvalidOperationException("The administrator-code directory is invalid.");
            Directory.CreateDirectory(directory);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                protectFile?.Invoke(temporary);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
                writer.Write(JsonSerializer.Serialize(verifier));
            }
            File.Move(temporary, _path, true);
            protectFile?.Invoke(_path);
            lock (_gate) _verifier = verifier;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static bool ValidFormat(string? code) =>
        code is { Length: 6 } && code.All(char.IsAsciiDigit);

    private static AdministratorCodeVerifier? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<AdministratorCodeVerifier>(File.ReadAllText(path));
            if (value is null || value.Version != CurrentVersion || value.Iterations < 1_000 ||
                Convert.FromBase64String(value.Salt).Length < 16 ||
                Convert.FromBase64String(value.Hash).Length != HashBytes)
                throw new InvalidDataException("The administrator-code verifier is invalid.");
            return value;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
        {
            throw new InvalidDataException("The administrator-code verifier is corrupt.", ex);
        }
    }

    private sealed record AdministratorCodeVerifier(int Version, int Iterations, string Salt, string Hash);
}
