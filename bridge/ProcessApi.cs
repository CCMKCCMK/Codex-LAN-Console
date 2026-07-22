using System.Diagnostics;

namespace CodexLanBridge;

public sealed record ProcessInfo(int Pid, string Name, double MemoryMb, DateTime? StartedAt, string? WindowTitle);

public static class ProcessApi
{
    private static readonly string[] Allowed = ["chatgpt", "codex", "dotnet", "java", "gradle", "adb", "node", "python", "git"];
    public static bool IsAllowed(string name) => Allowed.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));
    public static IReadOnlyList<ProcessInfo> List()
    {
        var output = new List<ProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (!IsAllowed(name)) continue;
                    output.Add(new ProcessInfo(
                        process.Id,
                        name,
                        Math.Round(process.WorkingSet64 / 1048576d, 1),
                        process.StartTime,
                        process.MainWindowTitle));
                }
                catch
                {
                    // A process can exit or become inaccessible between enumeration and inspection.
                }
            }
        }
        return output.OrderByDescending(x => x.MemoryMb).ToArray();
    }
}
