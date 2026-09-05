using System.Security.Cryptography;
using System.Text;

namespace CodexLanBridge;

/// <summary>
/// Per-install credential for loopback-only management calls. Requiring a non-simple custom
/// header prevents an arbitrary webpage from changing local machine settings through a form.
/// </summary>
public sealed class LocalControlTokenStore
{
    public const string HeaderName = "X-Codex-Local-Control";
    private readonly string _token;

    public LocalControlTokenStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLanConsole");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "local-control-token.txt");
        _token = LoadOrCreate(path);
    }

    public bool Validate(string? supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(_token));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied.Trim()));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private static string LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 32) return existing;
            }
        }
        catch { }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, token, new UTF8Encoding(false));
        File.Move(temporary, path, true);
        return token;
    }
}
