using System.Security.AccessControl;
using System.Security.Principal;

namespace CodexLanBridge;

internal sealed record PairingStoragePaths(
    string Directory,
    string DevicesFile,
    string PairingFile,
    string OpenPairingRequestFile,
    bool AdministratorMode)
{
    internal string AdministratorCodeDirectory => AdministratorMode ? Path.Combine(Directory, "Secrets") : Directory;
    internal string AdministratorCodeFile => Path.Combine(AdministratorCodeDirectory, "administrator-code.json");
}

/// <summary>
/// Keeps the administrator Bridge's credentials out of the ordinary per-user
/// data directory.  The administrator directory deliberately has no inherited
/// access rules: SYSTEM and Administrators may modify it, while the interactive
/// user may only read it (so pairing.txt can be opened during first activation).
/// </summary>
internal static class PairingStoragePolicy
{
    private const string ProductDirectoryName = "CodexLanConsole";
    private const string AdministratorCredentialDirectoryName = "AdminCredentials";

    internal static PairingStoragePaths ResolveCurrent()
    {
        var elevation = WindowsProcessElevation.Current;
        if (OperatingSystem.IsWindows() && !elevation.Detected)
            throw new InvalidOperationException(
                "The Bridge could not verify its Windows process elevation. Startup was stopped to avoid using the wrong credential boundary.");
        var elevated = elevation.Active;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var currentSid = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value
            : null;
        return Resolve(elevated, localAppData, commonAppData, currentSid);
    }

    internal static PairingStoragePaths Resolve(
        bool administratorMode,
        string localAppData,
        string commonAppData,
        string? currentUserSid)
    {
        if (!administratorMode)
        {
            var standardDirectory = Path.Combine(localAppData, ProductDirectoryName);
            return CreatePaths(standardDirectory, false);
        }

        if (string.IsNullOrWhiteSpace(currentUserSid))
            throw new InvalidOperationException("The current Windows account SID is required for administrator credentials.");

        // A SID contains only a stable, filesystem-safe ASCII form.  Parsing it
        // rejects path separators and any caller-provided alias.
        var sid = new SecurityIdentifier(currentUserSid).Value;
        var administratorDirectory = Path.Combine(
            commonAppData,
            ProductDirectoryName,
            AdministratorCredentialDirectoryName,
            sid);
        return CreatePaths(administratorDirectory, true);
    }

    internal static void Prepare(PairingStoragePaths paths)
    {
        if (!paths.AdministratorMode)
        {
            Directory.CreateDirectory(paths.Directory);
            return;
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Administrator credential protection requires Windows ACLs.");

        var userSid = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows account SID could not be read.");
        var administratorRoot = Directory.GetParent(paths.Directory)?.FullName ??
            throw new InvalidOperationException("The administrator credential root is invalid.");
        var productRoot = Directory.GetParent(administratorRoot)?.FullName ??
            throw new InvalidOperationException("The administrator product data root is invalid.");

        Directory.CreateDirectory(productRoot);
        RejectReparsePoint(productRoot);
        ApplyDirectorySecurity(productRoot, userSid);
        Directory.CreateDirectory(administratorRoot);
        RejectReparsePoint(administratorRoot);
        ApplyDirectorySecurity(administratorRoot, userSid);

        Directory.CreateDirectory(paths.Directory);
        RejectReparsePoint(paths.Directory);
        ApplyDirectorySecurity(paths.Directory, userSid);
        Directory.CreateDirectory(paths.AdministratorCodeDirectory);
        RejectReparsePoint(paths.AdministratorCodeDirectory);
        new DirectoryInfo(paths.AdministratorCodeDirectory).SetAccessControl(CreateSecretDirectorySecurity());
        RejectFileReparsePoint(paths.DevicesFile);
        RejectFileReparsePoint(paths.PairingFile);
        RejectFileReparsePoint(paths.OpenPairingRequestFile);
        RejectFileReparsePoint(paths.AdministratorCodeFile);
    }

    internal static void ProtectFile(PairingStoragePaths paths, string file)
    {
        if (!paths.AdministratorMode || !OperatingSystem.IsWindows() || !File.Exists(file)) return;
        var userSid = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows account SID could not be read.");
        var info = new FileInfo(file);
        info.SetAccessControl(CreateFileSecurity(userSid));
    }

    internal static void ProtectSecretFile(PairingStoragePaths paths, string file)
    {
        if (!paths.AdministratorMode || !OperatingSystem.IsWindows() || !File.Exists(file)) return;
        var fullPath = Path.GetFullPath(file);
        var secretPrefix = Path.GetFullPath(paths.AdministratorCodeDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(secretPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Administrator secrets must stay inside the protected secret directory.");
        new FileInfo(fullPath).SetAccessControl(CreateSecretFileSecurity());
    }

    internal static DirectorySecurity CreateDirectorySecurity(SecurityIdentifier userSid)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            userSid, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static FileSecurity CreateFileSecurity(SecurityIdentifier userSid)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            userSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return security;
    }

    internal static DirectorySecurity CreateSecretDirectorySecurity()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static FileSecurity CreateSecretFileSecurity()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static PairingStoragePaths CreatePaths(string directory, bool administratorMode) => new(
        directory,
        Path.Combine(directory, "devices.json"),
        Path.Combine(directory, "pairing.txt"),
        Path.Combine(directory, "open-pairing.request"),
        administratorMode);

    private static void ApplyDirectorySecurity(string directory, SecurityIdentifier userSid)
    {
        var info = new DirectoryInfo(directory);
        info.SetAccessControl(CreateDirectorySecurity(userSid));
    }

    private static void RejectReparsePoint(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException(
                $"Administrator credential storage cannot use a reparse point: {directory}");
    }

    private static void RejectFileReparsePoint(string file)
    {
        if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException(
                $"Administrator credential files cannot use a reparse point: {file}");
    }
}
