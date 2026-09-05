using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;

namespace CodexLanBridge;

public sealed record FileLeaseDescriptor(
    string Id,
    string Name,
    string ContentType,
    long Size,
    string? ThreadId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string DownloadUrl,
    string ViewUrl);

public sealed record ResolvedFileLease(FileLeaseDescriptor Descriptor, string Path, string ContentType);

public sealed class FileTransferService : BackgroundService
{
    public const long MaximumFileBytes = 128L * 1024 * 1024;
    public const long MaximumRequestBytes = 256L * 1024 * 1024;
    public const int MaximumFilesPerRequest = 10;
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan UploadRetention = TimeSpan.FromDays(30);
    private readonly string _uploadRoot;
    private readonly string _registryPath;
    private readonly string _codexHome;
    private readonly ConcurrentDictionary<string, StoredFileLease> _leases = new(StringComparer.Ordinal);
    private readonly object _registryGate = new();
    private readonly FileExtensionContentTypeProvider _contentTypes = new();

    public FileTransferService()
        : this(DefaultUploadRoot(), DefaultCodexHome())
    {
    }

    public FileTransferService(string uploadRoot, string codexHome)
    {
        if (string.IsNullOrWhiteSpace(uploadRoot)) throw new ArgumentException("An upload path is required.");
        if (string.IsNullOrWhiteSpace(codexHome)) throw new ArgumentException("A Codex home path is required.");
        _uploadRoot = Path.GetFullPath(uploadRoot);
        _codexHome = Path.GetFullPath(codexHome);
        _registryPath = Path.Combine(_uploadRoot, "leases.json");
        Directory.CreateDirectory(_uploadRoot);
        LoadRegistry();
        CleanupExpired();
    }

    public async Task<IReadOnlyList<FileLeaseDescriptor>> StoreUploadsAsync(
        IFormFileCollection files,
        string? threadId,
        CancellationToken cancellationToken)
    {
        if (files.Count is < 1 or > MaximumFilesPerRequest)
            throw new ArgumentException($"Upload between 1 and {MaximumFilesPerRequest} files at a time.");
        long total = 0;
        foreach (var file in files)
        {
            if (file.Length < 0 || file.Length > MaximumFileBytes)
                throw new ArgumentException($"Each file must be no larger than {MaximumFileBytes / 1024 / 1024} MiB.");
            total = checked(total + file.Length);
            if (total > MaximumRequestBytes)
                throw new ArgumentException($"One upload request must be no larger than {MaximumRequestBytes / 1024 / 1024} MiB.");
        }

        var created = new List<StoredFileLease>(files.Count);
        var createdDirectories = new List<string>(files.Count);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var directory = Path.Combine(_uploadRoot, id);
                Directory.CreateDirectory(directory);
                createdDirectories.Add(directory);
                var originalName = SafeDisplayName(file.FileName);
                var extension = SafeExtension(originalName);
                var physicalName = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() + extension;
                var destination = Path.Combine(directory, physicalName);
                var partial = destination + ".part";
                await using (var input = file.OpenReadStream())
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[64 * 1024];
                    long copied = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        copied += read;
                        if (copied > MaximumFileBytes) throw new ArgumentException("The uploaded file exceeded the size limit.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                File.Move(partial, destination);
                var now = DateTimeOffset.UtcNow;
                var stored = new StoredFileLease(
                    id,
                    originalName,
                    destination,
                    directory,
                    physicalName,
                    ContentType(originalName),
                    new FileInfo(destination).Length,
                    string.IsNullOrWhiteSpace(threadId) ? null : threadId,
                    true,
                    now,
                    now.Add(UploadRetention));
                _leases[id] = stored;
                created.Add(stored);
            }
            SaveRegistry();
            return created.Select(ToDescriptor).ToArray();
        }
        catch
        {
            foreach (var item in created)
            {
                _leases.TryRemove(item.Id, out _);
                TryDeleteManagedDirectory(item);
            }
            foreach (var directory in createdDirectories)
            {
                try
                {
                    if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        Directory.Delete(directory, true);
                }
                catch { }
            }
            throw;
        }
    }

    public FileLeaseDescriptor RegisterExisting(string path, string allowedWorkspaceRoot, string threadId)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.");
        if (Path.IsPathRooted(path))
        {
            var requestedPath = Path.GetFullPath(path);
            var existing = _leases.Values.FirstOrDefault(item =>
                item.ExpiresAt > DateTimeOffset.UtcNow &&
                string.Equals(item.ThreadId, threadId, StringComparison.Ordinal) &&
                string.Equals(Path.GetFullPath(item.AbsolutePath), requestedPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(item.AbsolutePath));
            if (existing is not null) return ToDescriptor(existing);
        }
        var rooted = Path.IsPathRooted(path);
        string? workspaceRoot = null;
        if (!rooted || Directory.Exists(allowedWorkspaceRoot))
            workspaceRoot = CanonicalDirectory(allowedWorkspaceRoot);

        var fullPath = CanonicalFile(rooted ? path : Path.Combine(workspaceRoot!, path));
        string allowedRoot;
        if (workspaceRoot is not null && IsWithinRoot(fullPath, workspaceRoot))
        {
            allowedRoot = workspaceRoot;
        }
        else if (!TryGetTrustedArtifactRoot(fullPath, threadId, out allowedRoot))
        {
            if (workspaceRoot is null)
                throw new DirectoryNotFoundException("The task workspace no longer exists, and the file is not in its Codex delivery folder.");
            throw new UnauthorizedAccessException("The file is outside the task workspace and its Codex delivery folder.");
        }
        EnsureNoReparsePoints(allowedRoot, fullPath);
        var leaseRoot = Path.GetDirectoryName(fullPath) ?? allowedRoot;
        var entry = Path.GetFileName(fullPath);
        var now = DateTimeOffset.UtcNow;
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var file = new FileInfo(fullPath);
        var stored = new StoredFileLease(
            id,
            file.Name,
            fullPath,
            leaseRoot,
            entry,
            ContentType(file.Name),
            file.Length,
            threadId,
            false,
            now,
            now.Add(LeaseLifetime));
        _leases[id] = stored;
        SaveRegistry();
        return ToDescriptor(stored);
    }

    public async Task<FileLeaseDescriptor> StoreThreadDeliveryAsync(
        string sourcePath,
        string threadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("A task id is required.");
        var source = CanonicalFile(sourcePath);
        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length > MaximumFileBytes)
            throw new ArgumentException($"The delivery file must be no larger than {MaximumFileBytes / 1024 / 1024} MiB.");
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Linked files cannot be shared.");

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var directory = Path.Combine(_uploadRoot, id);
        Directory.CreateDirectory(directory);
        var originalName = SafeDisplayName(sourceInfo.Name);
        var physicalName = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() +
                           SafeExtension(originalName);
        var destination = Path.Combine(directory, physicalName);
        var partial = destination + ".part";
        try
        {
            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read | FileShare.Delete,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             partial,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, 64 * 1024, cancellationToken);
            }
            File.Move(partial, destination);

            var now = DateTimeOffset.UtcNow;
            var stored = new StoredFileLease(
                id,
                originalName,
                destination,
                directory,
                physicalName,
                ContentType(originalName),
                new FileInfo(destination).Length,
                threadId,
                true,
                now,
                now.Add(UploadRetention));
            _leases[id] = stored;
            SaveRegistry();
            return ToDescriptor(stored);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            try
            {
                if (Directory.Exists(directory) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(directory, true);
            }
            catch { }
            throw;
        }
    }

    public IReadOnlyList<FileLeaseDescriptor> List(string? threadId)
    {
        CleanupExpired();
        return _leases.Values
            .Where(item => string.IsNullOrWhiteSpace(threadId) || item.ThreadId == threadId)
            .OrderByDescending(item => item.CreatedAt)
            .Select(ToDescriptor)
            .ToArray();
    }

    public ResolvedFileLease ResolveDownload(string id, string? expectedThreadId = null)
    {
        var stored = RequireLease(id, expectedThreadId);
        var path = CanonicalFile(stored.AbsolutePath);
        EnsureWithinRoot(path, CanonicalDirectory(stored.RootPath));
        EnsureNoReparsePoints(stored.RootPath, path);
        return new ResolvedFileLease(ToDescriptor(stored), path, stored.ContentType);
    }

    public ResolvedFileLease ResolveView(string id, string? subpath)
    {
        var stored = RequireLease(id, null);
        var root = CanonicalDirectory(stored.RootPath);
        var requested = string.IsNullOrWhiteSpace(subpath)
            ? stored.EntryPath
            : Uri.UnescapeDataString(subpath).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(requested)) throw new ArgumentException("The file path is outside this lease.");
        var path = Path.GetFullPath(Path.Combine(root, requested));
        EnsureWithinRoot(path, root);
        if (!string.Equals(
                path,
                Path.GetFullPath(stored.AbsolutePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Only the leased file can be viewed.");
        if (!File.Exists(path)) throw new FileNotFoundException("The requested file does not exist.");
        EnsureNoReparsePoints(root, path);
        return new ResolvedFileLease(ToDescriptor(stored), path, ContentType(path));
    }

    public IReadOnlyList<object> BuildCodexInputs(string threadId, IReadOnlyCollection<string>? leaseIds)
    {
        if (leaseIds is null || leaseIds.Count == 0) return Array.Empty<object>();
        if (leaseIds.Count > MaximumFilesPerRequest) throw new ArgumentException("Too many attachments.");
        var inputs = new List<object>(leaseIds.Count);
        foreach (var id in leaseIds.Distinct(StringComparer.Ordinal))
        {
            var file = ResolveDownload(id, threadId);
            inputs.Add(file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? new { type = "localImage", path = file.Path, detail = "auto" }
                : new { type = "mention", name = file.Descriptor.Name, path = file.Path });
        }
        return inputs;
    }

    public bool Delete(string id)
    {
        if (!_leases.TryRemove(id, out var stored)) return false;
        if (stored.ManagedUpload) TryDeleteManagedDirectory(stored);
        SaveRegistry();
        return true;
    }

    private StoredFileLease RequireLease(string id, string? expectedThreadId)
    {
        if (string.IsNullOrWhiteSpace(id) || !_leases.TryGetValue(id, out var stored))
            throw new FileNotFoundException("The file lease was not found.");
        if (stored.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Delete(id);
            throw new FileNotFoundException("The file lease has expired.");
        }
        if (!string.IsNullOrWhiteSpace(expectedThreadId) &&
            !string.Equals(stored.ThreadId, expectedThreadId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("This attachment belongs to another task.");
        if (!File.Exists(stored.AbsolutePath))
        {
            Delete(id);
            throw new FileNotFoundException("The leased file no longer exists.");
        }
        return stored;
    }

    private FileLeaseDescriptor ToDescriptor(StoredFileLease stored)
    {
        var encodedName = string.Join("/", stored.EntryPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(part => part.Length > 0).Select(Uri.EscapeDataString));
        return new FileLeaseDescriptor(
            stored.Id,
            stored.OriginalName,
            stored.ContentType,
            stored.Size,
            stored.ThreadId,
            stored.CreatedAt,
            stored.ExpiresAt,
            $"/api/files/{stored.Id}/download",
            $"/api/files/{stored.Id}/view/{encodedName}");
    }

    private string ContentType(string path) =>
        _contentTypes.TryGetContentType(path, out var value) ? value : "application/octet-stream";

    private static string SafeDisplayName(string value)
    {
        var name = Path.GetFileName(value ?? "").Trim();
        if (name.Length == 0) name = "upload";
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name.Length > 180 ? name[..180] : name;
    }

    private static string SafeExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length is >= 2 and <= 11 && extension[1..].All(char.IsAsciiLetterOrDigit)
            ? extension.ToLowerInvariant()
            : "";
    }

    private static string DefaultUploadRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "CodexLanConsole", "Uploads");
    }

    private static string DefaultCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
    }

    private static string CanonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A workspace path is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The workspace directory does not exist.");
        return full;
    }

    private static string CanonicalFile(string path)
    {
        var full = Path.GetFullPath(path);
        RejectAlternateDataStream(full);
        if (!File.Exists(full)) throw new FileNotFoundException("The requested file does not exist.");
        return full;
    }

    private static void RejectAlternateDataStream(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.GetPathRoot(path) ?? "";
        if (path.AsSpan(root.Length).Contains(':'))
            throw new ArgumentException("Windows alternate data streams cannot be shared.");
    }

    private static void EnsureWithinRoot(string path, string root)
    {
        if (!IsWithinRoot(path, root))
            throw new UnauthorizedAccessException("The file is outside the task workspace.");
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private bool TryGetTrustedArtifactRoot(string path, string threadId, out string root)
    {
        root = "";
        if (string.IsNullOrWhiteSpace(threadId) ||
            threadId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return false;

        var visualizations = Path.Combine(_codexHome, "visualizations");
        if (!Directory.Exists(visualizations)) return false;
        visualizations = CanonicalDirectory(visualizations);
        if (!IsWithinRoot(path, visualizations)) return false;

        var relative = Path.GetRelativePath(visualizations, path);
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 ||
            parts[0].Length != 4 || !parts[0].All(char.IsAsciiDigit) ||
            parts[1].Length != 2 || !parts[1].All(char.IsAsciiDigit) ||
            parts[2].Length != 2 || !parts[2].All(char.IsAsciiDigit) ||
            !string.Equals(parts[3], threadId, StringComparison.Ordinal))
            return false;

        if (!DateOnly.TryParseExact(
                $"{parts[0]}-{parts[1]}-{parts[2]}",
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _))
            return false;

        var candidate = Path.Combine(visualizations, parts[0], parts[1], parts[2], parts[3]);
        if (!Directory.Exists(candidate)) return false;
        candidate = CanonicalDirectory(candidate);
        if (!IsWithinRoot(path, candidate)) return false;
        EnsureNoReparsePoints(visualizations, path);
        root = candidate;
        return true;
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = canonicalRoot;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Linked directories cannot be shared.");
        var relative = Path.GetRelativePath(canonicalRoot, path);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0 || part == ".") continue;
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Linked files or directories cannot be shared.");
        }
    }

    private void LoadRegistry()
    {
        if (!File.Exists(_registryPath)) return;
        try
        {
            var entries = JsonSerializer.Deserialize<StoredFileLease[]>(File.ReadAllText(_registryPath)) ?? Array.Empty<StoredFileLease>();
            foreach (var item in entries)
                if (item.ExpiresAt > DateTimeOffset.UtcNow && File.Exists(item.AbsolutePath)) _leases[item.Id] = item;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Could not load file leases: {ex.Message}"); }
    }

    private void SaveRegistry()
    {
        lock (_registryGate)
        {
            try
            {
                Directory.CreateDirectory(_uploadRoot);
                var temporary = _registryPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_leases.Values.ToArray()));
                File.Move(temporary, _registryPath, true);
            }
            catch (Exception ex) { Console.Error.WriteLine($"Could not save file leases: {ex.Message}"); }
        }
    }

    private void CleanupExpired()
    {
        var changed = false;
        foreach (var pair in _leases)
        {
            if (pair.Value.ExpiresAt > DateTimeOffset.UtcNow && File.Exists(pair.Value.AbsolutePath)) continue;
            if (!_leases.TryRemove(pair.Key, out var removed)) continue;
            if (removed.ManagedUpload) TryDeleteManagedDirectory(removed);
            changed = true;
        }
        try
        {
            var cutoff = DateTime.UtcNow - UploadRetention;
            foreach (var directory in Directory.EnumerateDirectories(_uploadRoot))
            {
                var info = new DirectoryInfo(directory);
                if (info.LastWriteTimeUtc >= cutoff || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                info.Delete(true);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"Could not clean old uploads: {ex.Message}"); }
        if (changed) SaveRegistry();
    }

    private void TryDeleteManagedDirectory(StoredFileLease stored)
    {
        try
        {
            var directory = CanonicalDirectory(stored.RootPath);
            EnsureWithinRoot(directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(_uploadRoot)));
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) Directory.Delete(directory, true);
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) { Console.Error.WriteLine($"Could not remove upload {stored.Id}: {ex.Message}"); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) CleanupExpired();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private sealed record StoredFileLease(
        string Id,
        string OriginalName,
        string AbsolutePath,
        string RootPath,
        string EntryPath,
        string ContentType,
        long Size,
        string? ThreadId,
        bool ManagedUpload,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);
}
