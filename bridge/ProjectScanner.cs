namespace CodexLanBridge;

public sealed record ProjectInfo(string Name, string Path, DateTime UpdatedAt, string Kind);

public sealed class ProjectScanner
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", "build", ".gradle", ".idea", ".vs" };
    private readonly string _root = Environment.GetEnvironmentVariable("CODEX_PROJECT_ROOT") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex");

    public IReadOnlyList<ProjectInfo> Scan()
    {
        if (!Directory.Exists(_root)) return [];
        var result = new List<ProjectInfo>();
        var queue = new Queue<(string path, int depth)>();
        queue.Enqueue((_root, 0));
        while (queue.Count > 0 && result.Count < 250)
        {
            var (path, depth) = queue.Dequeue();
            try
            {
                var files = Directory.EnumerateFiles(path).Take(100).ToArray();
                var names = files.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var kind = names.Contains("package.json") ? "Node" : names.Any(n => n?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true) ? ".NET" :
                    names.Contains("settings.gradle") || names.Contains("settings.gradle.kts") ? "Android/Gradle" :
                    names.Any(n => n?.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) == true) ? "LaTeX" :
                    Directory.Exists(Path.Combine(path, ".git")) ? "Git" : "";
                if (!string.IsNullOrEmpty(kind))
                {
                    var updated = files.Select(f => File.GetLastWriteTime(f)).DefaultIfEmpty(Directory.GetLastWriteTime(path)).Max();
                    result.Add(new(Path.GetFileName(path), path, updated, string.IsNullOrEmpty(kind) ? "Folder" : kind));
                }
                if (depth < 5)
                {
                    foreach (var dir in Directory.EnumerateDirectories(path).Take(100))
                        if (!Excluded.Contains(Path.GetFileName(dir))) queue.Enqueue((dir, depth + 1));
                }
            }
            catch { }
        }
        return result.OrderByDescending(x => x.UpdatedAt).ToArray();
    }
}
