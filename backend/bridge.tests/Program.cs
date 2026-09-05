using System.Text.Json;
using System.Text;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using CodexLanBridge;

var assertions = 0;

void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

CommuteTests.Run(Assert);
ScooterTests.Run(Assert);

var leaseNow = new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);
var accessLeases = new ThreadAccessLeaseTracker(() => leaseNow);
var initialLeaseRevision = accessLeases.MarkLoaded("lease-thread");
Assert(
    accessLeases.IsLoaded("lease-thread") &&
    !accessLeases.TryBeginRelease(
        "lease-thread",
        initialLeaseRevision,
        TimeSpan.FromMinutes(2),
        out _),
    "A newly acquired task-access lease must not be released before its idle timeout.");
leaseNow += TimeSpan.FromMinutes(2);
Assert(
    accessLeases.TryBeginRelease(
        "lease-thread",
        initialLeaseRevision,
        TimeSpan.FromMinutes(2),
        out var idleReleaseRevision),
    "An idle task-access lease must become eligible for unsubscribe.");
accessLeases.FinishRelease("lease-thread", idleReleaseRevision, released: true);
Assert(
    !accessLeases.IsLoaded("lease-thread"),
    "A successful unsubscribe must clear Bridge ownership of the task.");

accessLeases.MarkLoaded("lease-thread");
accessLeases.MarkTurnStarted("lease-thread");
leaseNow += TimeSpan.FromHours(1);
Assert(
    !accessLeases.TryBeginRelease(
        "lease-thread",
        expectedRevision: null,
        TimeSpan.Zero,
        out _),
    "A running turn must never be released by the idle sweep.");
var completedLeaseRevision = accessLeases.MarkTurnCompleted("lease-thread");
leaseNow += TimeSpan.FromSeconds(5);
Assert(
    accessLeases.TryBeginRelease(
        "lease-thread",
        completedLeaseRevision,
        TimeSpan.FromSeconds(5),
        out var completedReleaseRevision),
    "A completed turn must release its task access after the short completion grace period.");
accessLeases.FinishRelease("lease-thread", completedReleaseRevision, released: false);
var supersededRevision = accessLeases.Touch("lease-thread");
Assert(
    supersededRevision != completedLeaseRevision &&
    !accessLeases.TryBeginRelease(
        "lease-thread",
        completedLeaseRevision,
        TimeSpan.Zero,
        out _),
    "A newer interaction must cancel a previously scheduled access release.");

var administratorMode = WindowsProcessElevation.Current;
Assert(
    administratorMode.Scope == WindowsProcessElevation.BridgeOwnedTasksOnlyScope,
    "Administrator mode must state that elevation applies only to Bridge-owned work.");
Assert(
    !OperatingSystem.IsWindows() || administratorMode.Detected,
    "The Windows Bridge must read elevation from its current process token.");
var administratorModeJson = JsonSerializer.SerializeToElement(administratorMode);
Assert(
    administratorModeJson.TryGetProperty("detected", out var detectedProperty) &&
    detectedProperty.ValueKind is JsonValueKind.True or JsonValueKind.False,
    "Administrator mode must serialize an explicit detection state.");
Assert(
    administratorModeJson.TryGetProperty("active", out var activeProperty) &&
    activeProperty.ValueKind is JsonValueKind.True or JsonValueKind.False,
    "Administrator mode must serialize the exact current-process elevation state.");

var standardStorage = PairingStoragePolicy.Resolve(
    administratorMode: false,
    @"C:\Users\Example\AppData\Local",
    @"C:\ProgramData",
    "S-1-5-21-100-200-300-400");
var administratorStorage = PairingStoragePolicy.Resolve(
    administratorMode: true,
    @"C:\Users\Example\AppData\Local",
    @"C:\ProgramData",
    "S-1-5-21-100-200-300-400");
Assert(
    standardStorage.DevicesFile == @"C:\Users\Example\AppData\Local\CodexLanConsole\devices.json",
    "Standard mode must preserve its existing LocalAppData device store.");
Assert(
    standardStorage.AdministratorCodeFile ==
    @"C:\Users\Example\AppData\Local\CodexLanConsole\administrator-code.json",
    "A Standard Bridge may use a per-user fixed pairing code without crossing into the protected Administrator store.");
Assert(
    administratorStorage.DevicesFile ==
    @"C:\ProgramData\CodexLanConsole\AdminCredentials\S-1-5-21-100-200-300-400\devices.json" &&
    administratorStorage.AdministratorCodeFile ==
    @"C:\ProgramData\CodexLanConsole\AdminCredentials\S-1-5-21-100-200-300-400\Secrets\administrator-code.json" &&
    !administratorStorage.DevicesFile.Equals(standardStorage.DevicesFile, StringComparison.OrdinalIgnoreCase),
    "Administrator Mode must keep device tokens and the fixed-code verifier inside its protected per-user boundary.");

if (OperatingSystem.IsWindows())
{
    var fixtureUser = new SecurityIdentifier("S-1-5-21-100-200-300-400");
    var directorySecurity = PairingStoragePolicy.CreateDirectorySecurity(fixtureUser);
    Assert(directorySecurity.AreAccessRulesProtected,
        "Administrator credential directories must disable inherited ACL entries.");
    var directoryRules = directorySecurity.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .ToArray();
    Assert(
        directoryRules.All(rule => !rule.IsInherited) &&
        directoryRules.Any(rule => Equals(rule.IdentityReference, fixtureUser) &&
                                   rule.AccessControlType == AccessControlType.Allow &&
                                   rule.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute) &&
                                   !rule.FileSystemRights.HasFlag(FileSystemRights.WriteData)) &&
        directoryRules.Any(rule =>
            Equals(rule.IdentityReference, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) &&
            rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)) &&
        directoryRules.Any(rule =>
            Equals(rule.IdentityReference, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) &&
            rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)),
        "Administrator credentials must grant only read/execute to the interactive user and full control to Administrators/SYSTEM.");

    var secretDirectorySecurity = PairingStoragePolicy.CreateSecretDirectorySecurity();
    var secretDirectoryRules = secretDirectorySecurity.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>()
        .ToArray();
    Assert(
        secretDirectorySecurity.AreAccessRulesProtected &&
        secretDirectoryRules.All(rule => !rule.IsInherited) &&
        secretDirectoryRules.All(rule =>
            Equals(rule.IdentityReference, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) ||
            Equals(rule.IdentityReference, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))) &&
        secretDirectoryRules.All(rule => rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)),
        "The fixed-code secret directory must be unreadable and unwritable by the interactive standard token.");
}

var standardBinding = BridgeBindingPolicy.Resolve(
    administratorMode: false,
    "http://0.0.0.0:9911;http://192.168.50.2:9911",
    Array.Empty<BridgeNetworkAddress>());
Assert(
    standardBinding.Urls.SequenceEqual(new[] { "http://0.0.0.0:9911", "http://192.168.50.2:9911" }),
    "Standard mode must preserve explicitly configured bindings.");
var administratorBinding = BridgeBindingPolicy.Resolve(
    administratorMode: true,
    "http://0.0.0.0:9911;http://192.168.50.2:9911",
    new[]
    {
        new BridgeNetworkAddress("Wi-Fi", "Intel Wi-Fi", true, IPAddress.Parse("192.168.50.2")),
        new BridgeNetworkAddress("Tailscale", "Tailscale Tunnel", true, IPAddress.Parse("100.64.0.10")),
        new BridgeNetworkAddress("Tailscale spoof", "Tailscale Tunnel", true, IPAddress.Parse("10.20.30.40")),
        new BridgeNetworkAddress("Tailscale old", "Tailscale Tunnel", false, IPAddress.Parse("100.111.183.45")),
        new BridgeNetworkAddress("Loopback", "Tailscale test", true, IPAddress.Loopback)
    });
Assert(
    administratorBinding.Urls.SequenceEqual(new[]
    {
        "http://127.0.0.1:8787",
        "http://100.64.0.10:8787"
    }) &&
    !administratorBinding.UrlSetting.Contains("192.168.50.2", StringComparison.Ordinal) &&
    !administratorBinding.UrlSetting.Contains("0.0.0.0", StringComparison.Ordinal),
    "Administrator Mode must ignore URL overrides and bind only loopback plus an active Tailscale IPv4 address.");
var missingAdministratorNetworkWasRejected = false;
try
{
    _ = BridgeBindingPolicy.Resolve(
        administratorMode: true,
        configuredUrls: "http://0.0.0.0:8787",
        new[] { new BridgeNetworkAddress("Wi-Fi", "Intel Wi-Fi", true, IPAddress.Parse("192.168.50.2")) });
}
catch (InvalidOperationException error) when (error.Message.Contains("Tailscale", StringComparison.Ordinal))
{
    missingAdministratorNetworkWasRejected = true;
}
Assert(
    missingAdministratorNetworkWasRejected,
    "Administrator Mode must fail fast without Tailscale so the always-on task can retry after the adapter starts.");

var pairingPolicyDirectory = Path.Combine(
    Path.GetTempPath(),
    "codex-lan-admin-pairing-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var adminPaths = new PairingStoragePaths(
        Path.Combine(pairingPolicyDirectory, "admin"),
        Path.Combine(pairingPolicyDirectory, "admin", "devices.json"),
        Path.Combine(pairingPolicyDirectory, "admin", "pairing.txt"),
        Path.Combine(pairingPolicyDirectory, "admin", "open-pairing.request"),
        AdministratorMode: true);
    var emptyPersistentCode = new PersistentAdministratorCode(
        Path.Combine(pairingPolicyDirectory, "unconfigured-administrator-code.json"),
        iterations: 1_000);
    var pairingNow = new DateTimeOffset(2026, 7, 27, 3, 0, 0, TimeSpan.Zero);
    var adminPairing = new PairingService(
        adminPaths,
        () => "123456",
        now: () => pairingNow,
        codeLifetime: TimeSpan.FromMinutes(10),
        administratorCode: emptyPersistentCode);
    Assert(
        adminPairing.IsPairingOpen && adminPairing.Code == "123456" &&
        adminPairing.CodeExpiresAt == pairingNow.AddMinutes(10) && !adminPairing.HasDevices,
        "A fresh Administrator Mode store must open one time-limited pairing window.");
    var firstAdminResult = adminPairing.TryPair(
        "123456", "phone", "100.64.0.2", out var adminToken, out _);
    Assert(
        firstAdminResult == PairingAttemptResult.Success && adminPairing.Validate(adminToken) &&
        adminPairing.HasDevices && !adminPairing.IsPairingOpen && adminPairing.Code.Length == 0,
        "A successful Administrator Mode pairing must close its window without publishing a replacement code.");
    var secondAdminResult = adminPairing.TryPair(
        "123456", "second phone", "100.64.0.3", out var rejectedToken, out _);
    Assert(
        secondAdminResult == PairingAttemptResult.PairingClosed && rejectedToken.Length == 0,
        "Administrator Mode must reject pairing while its local window is closed.");
    var closedPairingText = File.ReadAllText(adminPaths.PairingFile);
    Assert(
        closedPairingText.Contains("closed", StringComparison.OrdinalIgnoreCase) &&
        !closedPairingText.Contains("123456", StringComparison.Ordinal),
        "A closed Administrator Mode pairing file must not retain a usable code.");

    File.WriteAllText(adminPaths.OpenPairingRequestFile, "Requested locally");
    var reopenedAdminPairing = new PairingService(
        adminPaths,
        () => "234567",
        now: () => pairingNow,
        codeLifetime: TimeSpan.FromMinutes(10),
        administratorCode: emptyPersistentCode);
    Assert(
        reopenedAdminPairing.HasDevices && reopenedAdminPairing.IsPairingOpen &&
        reopenedAdminPairing.Code == "234567" && reopenedAdminPairing.Validate(adminToken) &&
        !File.Exists(adminPaths.OpenPairingRequestFile),
        "A local one-shot request must reopen pairing without revoking an existing device.");
    Assert(
        reopenedAdminPairing.TryPair(
            "234567", "second phone", "100.64.0.3", out var secondAdminToken, out _) == PairingAttemptResult.Success &&
        reopenedAdminPairing.Validate(adminToken) && reopenedAdminPairing.Validate(secondAdminToken) &&
        !reopenedAdminPairing.IsPairingOpen,
        "A second Administrator device must be added while every existing token remains valid.");
    var persistedAdminHashes = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(adminPaths.DevicesFile));
    Assert(
        persistedAdminHashes?.Count == 2 &&
        !File.ReadAllText(adminPaths.DevicesFile).Contains(adminToken, StringComparison.OrdinalIgnoreCase) &&
        !File.ReadAllText(adminPaths.DevicesFile).Contains(secondAdminToken, StringComparison.OrdinalIgnoreCase),
        "Administrator storage must preserve multiple hashes without persisting raw device tokens.");

    File.WriteAllText(adminPaths.OpenPairingRequestFile, "Requested locally");
    var expiringAdminPairing = new PairingService(
        adminPaths,
        () => "345678",
        now: () => pairingNow,
        codeLifetime: TimeSpan.FromMinutes(10),
        administratorCode: emptyPersistentCode);
    pairingNow = pairingNow.AddMinutes(11);
    Assert(
        !expiringAdminPairing.IsPairingOpen && expiringAdminPairing.Code.Length == 0 &&
        expiringAdminPairing.TryPair(
            "345678", "late phone", "100.64.0.4", out var expiredToken, out _) == PairingAttemptResult.PairingClosed &&
        expiredToken.Length == 0 && expiringAdminPairing.Validate(adminToken) &&
        expiringAdminPairing.Validate(secondAdminToken),
        "An expired local window must close without revoking already paired Administrator devices.");

    var restartedAdminPairing = new PairingService(
        adminPaths,
        () => throw new InvalidOperationException("A closed store must not generate a code."),
        now: () => pairingNow,
        administratorCode: emptyPersistentCode);
    Assert(
        restartedAdminPairing.HasDevices && !restartedAdminPairing.IsPairingOpen &&
        restartedAdminPairing.Validate(adminToken) && restartedAdminPairing.Validate(secondAdminToken),
        "Administrator pairing must remain closed and preserve every protected device across restarts.");

    var persistentCodePath = Path.Combine(pairingPolicyDirectory, "persistent-administrator-code.json");
    var persistentCode = new PersistentAdministratorCode(persistentCodePath, iterations: 1_000);
    persistentCode.Configure("456789");
    Assert(
        persistentCode.IsConfigured && persistentCode.Validate("456789") &&
        !persistentCode.Validate("456788") &&
        !File.ReadAllText(persistentCodePath).Contains("456789", StringComparison.Ordinal),
        "The persistent administrator code must validate without ever being stored in plaintext.");
    var persistentAdminPairing = new PairingService(
        adminPaths,
        () => throw new InvalidOperationException("A persistent code must not open a temporary window."),
        now: () => pairingNow,
        administratorCode: persistentCode);
    Assert(
        persistentAdminPairing.HasPersistentAdministratorCode && persistentAdminPairing.IsPairingOpen &&
        !persistentAdminPairing.IsTemporaryPairingOpen && persistentAdminPairing.Code.Length == 0 &&
        persistentAdminPairing.TryPair(
            "456789", "third phone", "100.64.0.5", out var persistentAdminToken, out _) == PairingAttemptResult.Success &&
        persistentAdminPairing.Validate(persistentAdminToken) && persistentAdminPairing.IsPairingOpen,
        "The fixed administrator code must admit another device at any time without revoking existing administrators.");
    var reloadedPersistentCode = new PersistentAdministratorCode(persistentCodePath, iterations: 1_000);
    Assert(reloadedPersistentCode.Validate("456789"),
        "The persistent administrator verifier must survive Bridge restarts.");

    var corruptAdminPaths = new PairingStoragePaths(
        Path.Combine(pairingPolicyDirectory, "corrupt-admin"),
        Path.Combine(pairingPolicyDirectory, "corrupt-admin", "devices.json"),
        Path.Combine(pairingPolicyDirectory, "corrupt-admin", "pairing.txt"),
        Path.Combine(pairingPolicyDirectory, "corrupt-admin", "open-pairing.request"),
        AdministratorMode: true);
    Directory.CreateDirectory(corruptAdminPaths.Directory);
    File.WriteAllText(corruptAdminPaths.DevicesFile, "not-json");
    var corruptAdminWasRejected = false;
    try { _ = new PairingService(corruptAdminPaths, () => "567890", administratorCode: emptyPersistentCode); }
    catch (JsonException) { corruptAdminWasRejected = true; }
    Assert(corruptAdminWasRejected,
        "A corrupt Administrator Mode device store must fail closed instead of reopening pairing.");

    var standardCodes = new Queue<string>(new[] { "234567", "345678", "456789" });
    var standardPaths = new PairingStoragePaths(
        Path.Combine(pairingPolicyDirectory, "standard"),
        Path.Combine(pairingPolicyDirectory, "standard", "devices.json"),
        Path.Combine(pairingPolicyDirectory, "standard", "pairing.txt"),
        Path.Combine(pairingPolicyDirectory, "standard", "open-pairing.request"),
        AdministratorMode: false);
    var standardPairing = new PairingService(
        standardPaths,
        () => standardCodes.Dequeue(),
        administratorCode: emptyPersistentCode);
    Assert(
        standardPairing.TryPair("234567", "phone one", "192.168.50.20", out _, out _) == PairingAttemptResult.Success &&
        standardPairing.IsPairingOpen && standardPairing.Code == "345678" &&
        standardPairing.TryPair("345678", "phone two", "192.168.50.21", out _, out _) == PairingAttemptResult.Success,
        "Standard mode must retain its existing multi-device pairing behavior.");

    var ignoredStandardCodePath = Path.Combine(pairingPolicyDirectory, "ignored-standard-code.json");
    var ignoredStandardCode = new PersistentAdministratorCode(ignoredStandardCodePath, iterations: 1_000);
    ignoredStandardCode.Configure("654321");
    var isolatedStandardPaths = new PairingStoragePaths(
        Path.Combine(pairingPolicyDirectory, "standard-isolated"),
        Path.Combine(pairingPolicyDirectory, "standard-isolated", "devices.json"),
        Path.Combine(pairingPolicyDirectory, "standard-isolated", "pairing.txt"),
        Path.Combine(pairingPolicyDirectory, "standard-isolated", "open-pairing.request"),
        AdministratorMode: false);
    var isolatedStandardPairing = new PairingService(
        isolatedStandardPaths,
        () => "123456",
        administratorCode: ignoredStandardCode);
    Assert(
        isolatedStandardPairing.HasPersistentAdministratorCode &&
        isolatedStandardPairing.TryPair(
            "654321", "fixed-code phone", "192.168.50.22", out var fixedStandardToken, out _) == PairingAttemptResult.Success &&
        isolatedStandardPairing.Validate(fixedStandardToken),
        "A Standard Bridge may use its LocalAppData fixed code, while the elevated Bridge remains isolated in ProgramData.");
}
finally
{
    try { if (Directory.Exists(pairingPolicyDirectory)) Directory.Delete(pairingPolicyDirectory, true); } catch { }
}

PendingRequest Pending(string method, string parameters = "{}") => new(
    "test-key",
    method,
    JsonDocument.Parse(parameters).RootElement.Clone(),
    DateTimeOffset.UtcNow);

var exactLimitWasAccepted = true;
try { CodexAppServer.ThrowIfMessageTooLarge(CodexAppServer.MaximumAppServerMessageBytes); }
catch { exactLimitWasAccepted = false; }
Assert(exactLimitWasAccepted, "A response exactly at the safety limit must be accepted.");
var oversizedWasRejected = false;
try { CodexAppServer.ThrowIfMessageTooLarge(CodexAppServer.MaximumAppServerMessageBytes + 1); }
catch (AppServerMessageTooLargeException ex)
{
    oversizedWasRejected = ex.ActualBytes == CodexAppServer.MaximumAppServerMessageBytes + 1 &&
                           ex.MaximumBytes == CodexAppServer.MaximumAppServerMessageBytes;
}
Assert(oversizedWasRejected, "An oversized app-server response must be rejected before parsing.");

var oversizedHistory =
    "{\"id\":7101,\"result\":{\"history\":\"" + new string('x', 12 * 1024) + "\"}}\n";
var followingCommandResponse = "{\"id\":7102,\"result\":{\"accepted\":true}}\n";
await using (var appServerOutput = new MemoryStream(Encoding.UTF8.GetBytes(
                 oversizedHistory + followingCommandResponse)))
{
    var oversizedMessages = new List<AppServerOversizedMessage>();
    var acknowledgedCommand = false;
    await AppServerNdjsonReader.ReadAsync(
        appServerOutput,
        maximumMessageBytes: 1024,
        line =>
        {
            using var message = JsonDocument.Parse(line);
            if (message.RootElement.GetProperty("id").GetInt64() == 7102 &&
                message.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean())
                acknowledgedCommand = true;
            return Task.CompletedTask;
        },
        oversized =>
        {
            oversizedMessages.Add(oversized);
            return Task.CompletedTask;
        });
    Assert(
        oversizedMessages is [{ NumericId: 7101, ServerMethod: null, EndedWithNewline: true }] &&
        oversizedMessages[0].ActualBytes == Encoding.UTF8.GetByteCount(oversizedHistory) - 1,
        "The bounded reader must identify and fully drain one oversized history response.");
    Assert(
        acknowledgedCommand,
        "An oversized history response must not corrupt or disconnect the following command response lane.");
}

var diagnosticDirectory = Path.Combine(
    Path.GetTempPath(),
    "codex-lan-app-server-diagnostic-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var diagnosticPath = Path.Combine(diagnosticDirectory, "app-server.jsonl");
    var diagnostics = new AppServerDiagnosticLog(diagnosticPath);
    diagnostics.Write(new AppServerDiagnosticEntry(
        "oversizedMessageDiscarded",
        17,
        4321,
        false,
        null,
        "Authorization=top-secret ghp_1234567890abcdefghijkl https://host/path?token=private-value",
        33 * 1024 * 1024,
        "thread/read",
        7101,
        "thread-safe-id",
        "turn-safe-id",
        true));
    var diagnosticText = File.ReadAllText(diagnosticPath);
    using var diagnosticJson = JsonDocument.Parse(diagnosticText);
    var diagnosticRoot = diagnosticJson.RootElement;
    Assert(
        diagnosticRoot.GetProperty("reason").GetString() == "oversizedMessageDiscarded" &&
        diagnosticRoot.GetProperty("generation").GetInt64() == 17 &&
        diagnosticRoot.GetProperty("processId").GetInt32() == 4321 &&
        !diagnosticRoot.GetProperty("processExited").GetBoolean() &&
        diagnosticRoot.GetProperty("exitCode").ValueKind == JsonValueKind.Null &&
        diagnosticRoot.GetProperty("oversizeBytes").GetInt64() == 33 * 1024 * 1024 &&
        diagnosticRoot.GetProperty("method").GetString() == "thread/read" &&
        diagnosticRoot.GetProperty("requestId").GetInt64() == 7101 &&
        diagnosticRoot.GetProperty("threadId").GetString() == "thread-safe-id" &&
        diagnosticRoot.GetProperty("turnId").GetString() == "turn-safe-id",
        "Oversize diagnostics must persist transport generation, process, size, method, request, thread, and turn context.");
    Assert(
        !diagnosticText.Contains("top-secret", StringComparison.Ordinal) &&
        !diagnosticText.Contains("ghp_1234567890abcdefghijkl", StringComparison.Ordinal) &&
        !diagnosticText.Contains("private-value", StringComparison.Ordinal),
        "Persisted app-server diagnostics must redact credentials and URL query values from stderr tails.");
}
finally
{
    try { if (Directory.Exists(diagnosticDirectory)) Directory.Delete(diagnosticDirectory, true); } catch { }
}

var unrestricted = ExecutionPermissions.Parse(":danger-full-access", "never", "auto_review");
Assert(unrestricted.Permissions == ":danger-full-access", "Full-access profile was not preserved.");
Assert(unrestricted.ApprovalPolicy == "never", "Never-approve policy was not preserved.");
Assert(unrestricted.ApprovalsReviewer == "user", "Reviewer must be disabled when approval policy is never.");
Assert(unrestricted.IsUnrestrictedAutonomy, "Full access plus never approval must be recognized as unrestricted autonomy.");
Assert(unrestricted.LegacySandbox == "danger-full-access", "Legacy full-access sandbox is incorrect.");
Assert(
    JsonSerializer.Serialize(unrestricted.LegacyTurnSandboxPolicy("C:\\workspace")).Contains("dangerFullAccess"),
    "Legacy full-access turn sandbox is incorrect.");

var defaults = ExecutionPermissions.Parse(null, null, null);
Assert(defaults.Permissions == ":workspace", "Default permission profile changed unexpectedly.");
Assert(defaults.ApprovalPolicy == "on-request", "Default approval policy changed unexpectedly.");
Assert(defaults.ApprovalsReviewer == "auto_review", "Default reviewer changed unexpectedly.");
Assert(
    defaults.RouteApprovalsToBridge(true).ApprovalsReviewer == "user",
    "Bridge automatic approval must receive on-request approvals before the independent reviewer.");
Assert(
    defaults.RouteApprovalsToBridge(false).ApprovalsReviewer == "auto_review",
    "Disabled bridge automatic approval must not change the selected reviewer.");

var command = ApprovalProtocol.BuildResult(Pending("item/commandExecution/requestApproval"), "accept");
Assert(command.GetProperty("decision").GetString() == "accept", "Command approval protocol is incorrect.");

var legacy = ApprovalProtocol.BuildResult(Pending("execCommandApproval"), "acceptForSession");
Assert(legacy.GetProperty("decision").GetString() == "approved_for_session", "Legacy session approval is incorrect.");

const string permissionParams = """
{
  "permissions": {
    "network": { "enabled": true },
    "fileSystem": { "write": ["C:\\outside"] }
  }
}
""";
var permissionOnce = ApprovalProtocol.BuildResult(
    Pending("item/permissions/requestApproval", permissionParams),
    "accept");
Assert(permissionOnce.GetProperty("scope").GetString() == "turn", "One-time permission scope is incorrect.");
Assert(
    permissionOnce.GetProperty("permissions").GetProperty("network").GetProperty("enabled").GetBoolean(),
    "Requested network permission was not granted.");

var permissionSession = ApprovalProtocol.BuildResult(
    Pending("item/permissions/requestApproval", permissionParams),
    "acceptForSession");
Assert(permissionSession.GetProperty("scope").GetString() == "session", "Session permission scope is incorrect.");

var permissionDenied = ApprovalProtocol.BuildResult(
    Pending("item/permissions/requestApproval", permissionParams),
    "decline");
Assert(
    !permissionDenied.GetProperty("permissions").EnumerateObject().Any(),
    "Declined permission request must return an empty grant.");

const string computerUseElicitation = """
{
  "threadId": "thread-mobile",
  "turnId": "turn-mobile",
  "mode": "form",
  "message": "Allow Codex to control this application?",
  "_meta": {
    "codex_approval_kind": "mcp_tool_call",
    "connector_id": "computer-use",
    "connector_name": "Computer Use",
    "persist": ["session", "always"]
  },
  "requestedSchema": {
    "type": "object",
    "properties": {}
  }
}
""";
var computerUse = Pending("mcpServer/elicitation/request", computerUseElicitation);
Assert(ElicitationProtocol.IsElicitationRequest(computerUse), "MCP elicitation was not recognized.");
Assert(ElicitationProtocol.IsToolApproval(computerUse), "Computer Use tool approval was not recognized.");
Assert(ElicitationProtocol.PreferredPersistence(computerUse) == "always", "Autonomous tool approval should prefer advertised permanent permission.");
Assert(CodexAppServer.IsUserApprovalRequest(computerUse), "MCP tool approval must be included in mobile approval handling.");
Assert(CodexAppServer.IsSupportedServerRequest(computerUse), "MCP elicitation must be a supported server request.");
var automaticComputerUse = ElicitationProtocol.BuildAutomaticApproval(computerUse);
Assert(automaticComputerUse.GetProperty("action").GetString() == "accept", "Computer Use automatic approval action is incorrect.");
Assert(automaticComputerUse.GetProperty("content").ValueKind == JsonValueKind.Object, "Computer Use acceptance must include form content.");
Assert(automaticComputerUse.GetProperty("_meta").GetProperty("persist").GetString() == "always", "Computer Use persistent approval is incorrect.");

const string typedToolApproval = """
{
  "mode": "form",
  "_meta": { "codex_approval_kind": "mcp_tool_call", "persist": "session" },
  "requestedSchema": {
    "type": "object",
    "properties": {
      "approved": { "type": "boolean" },
      "scope": { "type": "string", "enum": ["deny", "allow_once", "allow_session"] }
    },
    "required": ["approved", "scope"]
  }
}
""";
var typedAutomatic = ElicitationProtocol.BuildAutomaticApproval(Pending("mcpServer/elicitation/request", typedToolApproval));
Assert(typedAutomatic.GetProperty("content").GetProperty("approved").GetBoolean(), "Typed approval boolean was not accepted.");
Assert(
    typedAutomatic.GetProperty("content").GetProperty("scope").GetString() == "allow_session",
    "Typed approval enum did not choose the strongest advertised safe value.");
var typedOnce = ElicitationProtocol.BuildToolApproval(Pending("mcpServer/elicitation/request", typedToolApproval), null);
Assert(
    typedOnce.GetProperty("content").GetProperty("scope").GetString() == "allow_once",
    "A one-time mobile approval must not silently choose persistent scope.");

const string alwaysOnlyToolApproval = """
{
  "mode": "form",
  "_meta": { "codex_approval_kind": "mcp_tool_call", "persist": "session" },
  "requestedSchema": {
    "type": "object",
    "properties": {
      "scope": { "type": "string", "enum": ["allow_always"] }
    },
    "required": ["scope"]
  }
}
""";
var onceScopeEscalationRejected = false;
try { ElicitationProtocol.BuildToolApproval(Pending("mcpServer/elicitation/request", alwaysOnlyToolApproval), null); }
catch (InvalidDataException) { onceScopeEscalationRejected = true; }
Assert(onceScopeEscalationRejected, "A one-time approval must reject an always-only scope enum.");
var sessionScopeEscalationRejected = false;
try { ElicitationProtocol.BuildToolApproval(Pending("mcpServer/elicitation/request", alwaysOnlyToolApproval), "session"); }
catch (InvalidDataException) { sessionScopeEscalationRejected = true; }
Assert(sessionScopeEscalationRejected, "A session approval must reject an always-only scope enum.");
var submittedOnceEscalationRejected = false;
try
{
    ElicitationProtocol.BuildResult(
        Pending("mcpServer/elicitation/request", alwaysOnlyToolApproval),
        "accept",
        JsonSerializer.SerializeToElement(new { scope = "allow_always" }),
        null);
}
catch (ArgumentException) { submittedOnceEscalationRejected = true; }
Assert(submittedOnceEscalationRejected, "A submitted one-time approval must not carry an always scope in its content.");
var submittedSessionEscalationRejected = false;
try
{
    ElicitationProtocol.BuildResult(
        Pending("mcpServer/elicitation/request", alwaysOnlyToolApproval),
        "accept",
        JsonSerializer.SerializeToElement(new { scope = "allow_always" }),
        "session");
}
catch (ArgumentException) { submittedSessionEscalationRejected = true; }
Assert(submittedSessionEscalationRejected, "A submitted session approval must not carry an always scope in its content.");

const string legacyMetaLookalike = """
{
  "mode": "form",
  "meta": { "codex_approval_kind": "mcp_tool_call" },
  "requestedSchema": { "type": "object", "properties": {} }
}
""";
Assert(
    !ElicitationProtocol.IsToolApproval(Pending("mcpServer/elicitation/request", legacyMetaLookalike)),
    "Only the protocol-defined _meta field may classify an MCP tool approval.");

const string unsafeDefaultToolApproval = """
{
  "mode": "form",
  "_meta": { "codex_approval_kind": "mcp_tool_call" },
  "requestedSchema": {
    "type": "object",
    "properties": {
      "scope": { "type": "string", "default": "allow_always" }
    },
    "required": ["scope"]
  }
}
""";
var unsafeDefaultRejected = false;
try { ElicitationProtocol.BuildAutomaticApproval(Pending("mcpServer/elicitation/request", unsafeDefaultToolApproval)); }
catch (InvalidDataException) { unsafeDefaultRejected = true; }
Assert(unsafeDefaultRejected, "An unsafe default must not silently widen a one-time approval.");

const string ordinaryFormElicitation = """
{
  "threadId": "thread-mobile",
  "turnId": "turn-mobile",
  "mode": "form",
  "message": "Choose a value",
  "requestedSchema": {
    "type": "object",
    "properties": { "count": { "type": "integer" } },
    "required": ["count"]
  }
}
""";
var ordinaryForm = Pending("mcpServer/elicitation/request", ordinaryFormElicitation);
Assert(!ElicitationProtocol.IsToolApproval(ordinaryForm), "An ordinary MCP form must not be auto-approved.");
var ordinaryContent = JsonSerializer.SerializeToElement(new { count = 3 });
var ordinaryAccepted = ElicitationProtocol.BuildResult(ordinaryForm, "accept", ordinaryContent, null);
Assert(ordinaryAccepted.GetProperty("content").GetProperty("count").GetInt32() == 3, "Typed MCP form content was not preserved.");
var ordinaryDeclined = ElicitationProtocol.BuildResult(ordinaryForm, "decline", ordinaryContent, null);
Assert(ordinaryDeclined.GetProperty("content").ValueKind == JsonValueKind.Null, "Declined MCP forms must return null content.");

const string constrainedFormElicitation = """
{
  "mode": "form",
  "message": "Choose values",
  "requestedSchema": {
    "type": "object",
    "properties": {
      "profile": {
        "type": "object",
        "properties": {
          "name": { "type": "string" },
          "roles": {
            "type": "array",
            "items": { "type": "string", "enum": ["reader", "writer", "owner"] },
            "minItems": 2,
            "maxItems": 2
          }
        },
        "required": ["name", "roles"]
      }
    },
    "required": ["profile"]
  }
}
""";
var constrainedForm = Pending("mcpServer/elicitation/request", constrainedFormElicitation);
var validConstrained = ElicitationProtocol.BuildResult(
    constrainedForm,
    "accept",
    JsonSerializer.SerializeToElement(new { profile = new { name = "Mobile", roles = new[] { "reader", "writer" } } }),
    null);
Assert(validConstrained.GetProperty("content").GetProperty("profile").GetProperty("roles").GetArrayLength() == 2,
    "A valid constrained MCP form should be accepted.");
var missingRequiredRejected = false;
try
{
    ElicitationProtocol.BuildResult(
        constrainedForm,
        "accept",
        JsonSerializer.SerializeToElement(new { profile = new { roles = new[] { "reader", "writer" } } }),
        null);
}
catch (ArgumentException) { missingRequiredRejected = true; }
Assert(missingRequiredRejected, "Nested required MCP form fields must be validated.");
var tooFewItemsRejected = false;
try
{
    ElicitationProtocol.BuildResult(
        constrainedForm,
        "accept",
        JsonSerializer.SerializeToElement(new { profile = new { name = "Mobile", roles = new[] { "reader" } } }),
        null);
}
catch (ArgumentException) { tooFewItemsRejected = true; }
Assert(tooFewItemsRejected, "MCP form minItems must be validated.");
var tooManyItemsRejected = false;
try
{
    ElicitationProtocol.BuildResult(
        constrainedForm,
        "accept",
        JsonSerializer.SerializeToElement(new { profile = new { name = "Mobile", roles = new[] { "reader", "writer", "owner" } } }),
        null);
}
catch (ArgumentException) { tooManyItemsRejected = true; }
Assert(tooManyItemsRejected, "MCP form maxItems must be validated.");
var invalidEnumRejected = false;
try
{
    ElicitationProtocol.BuildResult(
        constrainedForm,
        "accept",
        JsonSerializer.SerializeToElement(new { profile = new { name = "Mobile", roles = new[] { "reader", "admin" } } }),
        null);
}
catch (ArgumentException) { invalidEnumRejected = true; }
Assert(invalidEnumRejected, "Nested MCP enum values must be validated.");

var unadvertisedPersistenceWasRejected = false;
try { ElicitationProtocol.BuildResult(computerUse, "accept", JsonSerializer.SerializeToElement(new { }), "device"); }
catch (ArgumentException) { unadvertisedPersistenceWasRejected = true; }
Assert(unadvertisedPersistenceWasRejected, "Unadvertised MCP persistence must be rejected.");

Assert(
    CodexAppServer.IsSupportedServerRequest(Pending("currentTime/read", "{\"threadId\":\"thread-mobile\"}")),
    "Current-time host requests must be handled automatically.");
Assert(
    !CodexAppServer.IsSupportedServerRequest(Pending("future/unknownRequest")),
    "Unknown server requests must fail immediately instead of remaining pending forever.");

var permissionCancelWasRejected = false;
try
{
    ApprovalProtocol.BuildResult(Pending("item/permissions/requestApproval", permissionParams), "cancel");
}
catch (ArgumentException)
{
    permissionCancelWasRejected = true;
}
Assert(permissionCancelWasRejected, "Permission approval must reject the unsupported cancel decision.");

Assert(
    ApprovalProtocol.ShouldPublishPendingNotification(AutoApprovalDisposition.NotAttempted, true),
    "A pending request must be shown when automatic approval was not attempted.");
Assert(
    ApprovalProtocol.ShouldPublishPendingNotification(AutoApprovalDisposition.Failed, true),
    "A pending request must be shown when automatic approval failed.");
Assert(
    !ApprovalProtocol.ShouldPublishPendingNotification(AutoApprovalDisposition.Approved, false),
    "An automatically approved request must not produce a pending notification.");
Assert(
    !ApprovalProtocol.ShouldPublishPendingNotification(AutoApprovalDisposition.NoLongerPending, false),
    "A request resolved by a competing operation must not produce a pending notification.");
Assert(
    !ApprovalProtocol.ShouldPublishPendingNotification(AutoApprovalDisposition.NotAttempted, false),
    "A request that disappeared before publication must not produce a stale notification.");

var invalidWasRejected = false;
try { ExecutionPermissions.Parse(":unknown", "never", "user"); }
catch (ArgumentException) { invalidWasRejected = true; }
Assert(invalidWasRejected, "Unknown permission profiles must be rejected.");

var settingsDirectory = Path.Combine(Path.GetTempPath(), "codex-lan-approval-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var settings = new ApprovalSettingsStore(settingsDirectory);
    Assert(!settings.Get().AutoApproveAll, "Automatic approval must default to off.");
    settings.SetAutoApproveAll(true);
    settings.RecordAutoApprovals(2);
    var reloaded = new ApprovalSettingsStore(settingsDirectory).Get();
    Assert(reloaded.AutoApproveAll, "Automatic approval setting did not persist.");
    Assert(reloaded.AutoApprovedCount == 2, "Automatic approval count did not persist.");
    Assert(reloaded.Scope == "bridge", "Automatic approval scope is incorrect.");
}
finally
{
    try { if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, true); } catch { }
}

var now = DateTimeOffset.UtcNow;

Assert(
    BridgeTurnRecoveryStore.IsRetryableDisconnect("responseStreamDisconnected", "any message"),
    "A structured response-stream disconnect must be eligible for safe continuation.");
Assert(
    BridgeTurnRecoveryStore.IsRetryableDisconnect(
        "other",
        "stream disconnected before completion: error sending request for url (https://chatgpt.com/backend-api/codex/responses)"),
    "The legacy stream-disconnected error emitted by current Codex builds must be recognized.");
Assert(
    !BridgeTurnRecoveryStore.IsRetryableDisconnect("usageLimitExceeded", "stream disconnected before completion"),
    "A structured non-network failure must never be retried based on message text alone.");
Assert(
    !BridgeTurnRecoveryStore.IsRetryableDisconnect("unauthorized", "request failed"),
    "Authentication failures must never trigger automatic continuation.");

var recoveryDirectory = Path.Combine(Path.GetTempPath(), "codex-lan-recovery-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var recovery = new BridgeTurnRecoveryStore(recoveryDirectory, _ => TimeSpan.Zero);
    var permissions = new ExecutionPermissions(":danger-full-access", "never", "user");
    recovery.TrackStarted("recover-thread", "turn-original", permissions, now);
    var firstFailure = JsonSerializer.SerializeToElement(new
    {
        id = "turn-original",
        status = "failed",
        error = new
        {
            message = "response stream disconnected",
            codexErrorInfo = new { responseStreamDisconnected = new { httpStatusCode = (int?)502 } },
            additionalDetails = (string?)null
        }
    });
    var firstRetry = recovery.ObserveCompleted("recover-thread", firstFailure, now);
    Assert(
        firstRetry is { Attempt: 1, FailedTurnId: "turn-original" } &&
        firstRetry.Permissions == permissions,
        "The first transient disconnect must schedule one continuation with the original execution permissions.");
    Assert(
        recovery.Snapshot(now).Single() is { Status: "waitingToContinue", Attempt: 0, MaximumAttempts: 2 },
        "Waiting recovery state must be visible without consuming an attempt before dispatch.");

    using (var disconnectedRequest = new CancellationTokenSource())
    {
        disconnectedRequest.Cancel();
        var dispatchWasCancelled = false;
        try
        {
            await recovery.ReplacePendingRetryAfterAcknowledgedDispatchAsync(
                "recover-thread",
                () => Task.FromCanceled<string>(disconnectedRequest.Token));
        }
        catch (OperationCanceledException) { dispatchWasCancelled = true; }
        Assert(dispatchWasCancelled, "The simulated mobile request must cancel before dispatch acknowledgement.");
        Assert(
            recovery.Snapshot(now).Single() is { ThreadId: "recover-thread", Status: "waitingToContinue" },
            "A mobile disconnect before dispatch acknowledgement must preserve the scheduled continuation.");
    }

    var acknowledgedDispatch = await recovery.ReplacePendingRetryAfterAcknowledgedDispatchAsync(
        "recover-thread",
        () => Task.FromResult("acknowledged"));
    Assert(acknowledgedDispatch == "acknowledged", "The acknowledged user dispatch result must be preserved.");
    Assert(
        recovery.Snapshot(now).All(item => item.ThreadId != "recover-thread"),
        "A successfully acknowledged manual dispatch must clear the superseded scheduled continuation.");

    recovery.TrackStarted("recover-thread", "turn-original", permissions, now);
    firstRetry = recovery.ObserveCompleted("recover-thread", firstFailure, now);

    var reloadedRecovery = new BridgeTurnRecoveryStore(recoveryDirectory, _ => TimeSpan.Zero);
    Assert(
        reloadedRecovery.Snapshot(now).Single() is
        { ThreadId: "recover-thread", CurrentTurnId: "turn-original", Status: "waitingToContinue" },
        "A safely acknowledged bridge-owned turn must survive a bridge restart in the recovery ledger.");

    Assert(firstRetry is not null && recovery.TryBeginAttempt(firstRetry, now),
        "A due recovery attempt must transition atomically to starting.");
    Assert(firstRetry is not null && recovery.MarkAttemptStarted(firstRetry, "turn-continuation-1", now),
        "An acknowledged continuation turn must replace the failed turn in the recovery ledger.");
    var secondFailure = JsonSerializer.SerializeToElement(new
    {
        id = "turn-continuation-1",
        status = "failed",
        error = new
        {
            message = "connection closed while reading the response stream",
            codexErrorInfo = new { responseStreamConnectionFailed = new { httpStatusCode = (int?)null } },
            additionalDetails = (string?)null
        }
    });
    var secondRetry = recovery.ObserveCompleted("recover-thread", secondFailure, now);
    Assert(secondRetry is { Attempt: 2, FailedTurnId: "turn-continuation-1" },
        "A second transient disconnect must schedule the final bounded continuation.");
    Assert(secondRetry is not null && recovery.TryBeginAttempt(secondRetry, now) &&
           recovery.MarkAttemptStarted(secondRetry, "turn-continuation-2", now),
        "The second bounded continuation must be tracked only after acknowledgement.");
    var thirdFailure = JsonSerializer.SerializeToElement(new
    {
        id = "turn-continuation-2",
        status = "failed",
        error = new
        {
            message = "response stream disconnected",
            codexErrorInfo = "responseStreamDisconnected",
            additionalDetails = (string?)null
        }
    });
    Assert(recovery.ObserveCompleted("recover-thread", thirdFailure, now) is null,
        "A third failed continuation must not schedule an unbounded retry.");
    Assert(recovery.Snapshot(now).Single() is { Status: "retryExhausted", Attempt: 2 },
        "The exhausted two-attempt cap must be visible to the mobile client.");

    recovery.TrackStarted("normal-complete", "turn-complete", permissions, now);
    var completed = JsonSerializer.SerializeToElement(new
    {
        id = "turn-complete",
        status = "completed",
        error = (object?)null
    });
    Assert(recovery.ObserveCompleted("normal-complete", completed, now) is null &&
           recovery.Snapshot(now).All(item => item.ThreadId != "normal-complete"),
        "Normal completion must clear recovery state and never retry.");

    recovery.TrackStarted("user-stop", "turn-stopped", permissions, now);
    var interrupted = JsonSerializer.SerializeToElement(new
    {
        id = "turn-stopped",
        status = "interrupted",
        error = (object?)null
    });
    Assert(recovery.ObserveCompleted("user-stop", interrupted, now) is null &&
           recovery.Snapshot(now).All(item => item.ThreadId != "user-stop"),
        "An interrupted turn must clear recovery state and never retry.");

    recovery.TrackStarted("explicit-cancel", "turn-cancel", permissions, now);
    recovery.CancelByUser("explicit-cancel");
    Assert(recovery.ObserveCompleted("explicit-cancel", firstFailure, now) is null,
        "An explicit user stop must suppress a later failure notification from scheduling recovery.");

    recovery.TrackStarted("manual-wins", "turn-old", permissions, now);
    recovery.TrackStarted("manual-wins", "turn-new-manual", permissions, now);
    recovery.MarkOwnershipUncertain("manual-wins", "turn-old", "stale scheduled recovery");
    Assert(recovery.Snapshot(now).Single(item => item.ThreadId == "manual-wins") is
        { CurrentTurnId: "turn-new-manual", Status: "running" },
        "A stale recovery callback must not overwrite a newer manual turn's ownership state.");

    recovery.TrackStarted("not-network", "turn-auth", permissions, now);
    var unauthorizedFailure = JsonSerializer.SerializeToElement(new
    {
        id = "turn-auth",
        status = "failed",
        error = new { message = "Unauthorized", codexErrorInfo = "unauthorized", additionalDetails = (string?)null }
    });
    Assert(recovery.ObserveCompleted("not-network", unauthorizedFailure, now) is null &&
           recovery.Snapshot(now).Single(item => item.ThreadId == "not-network").Status == "notRetryable",
        "A non-network failure must be reported without automatic continuation.");

    recovery.TrackStarted("superseded-terminal", "turn-stale", permissions, now);
    recovery.MarkOwnershipUncertain("superseded-terminal", "turn-stale", "acknowledgement was not observed", now);
    Assert(
        !recovery.DiscardIfSupersededByTurn("superseded-terminal", "turn-stale") &&
        recovery.SnapshotFor("superseded-terminal", now) is { CurrentTurnId: "turn-stale" },
        "Seeing the same persisted turn must not discard a recovery record.");
    Assert(
        recovery.DiscardIfSupersededByTurn("superseded-terminal", "turn-new") &&
        recovery.SnapshotFor("superseded-terminal", now) is null,
        "A newer persisted turn must immediately clear a terminal ownership warning.");

    recovery.TrackStarted("superseded-waiting", "turn-waiting", permissions, now);
    var waitingFailure = JsonSerializer.SerializeToElement(new
    {
        id = "turn-waiting",
        status = "failed",
        error = new { message = "response stream disconnected", codexErrorInfo = "responseStreamDisconnected" }
    });
    recovery.ObserveCompleted("superseded-waiting", waitingFailure, now);
    Assert(
        recovery.DiscardIfSupersededByTurn("superseded-waiting", "turn-manual"),
        "A newer manual turn must supersede a retry that has not been dispatched.");

    recovery.TrackStarted("dispatch-race", "turn-race", permissions, now);
    var raceRetry = recovery.ObserveCompleted("dispatch-race", JsonSerializer.SerializeToElement(new
    {
        id = "turn-race",
        status = "failed",
        error = new { message = "response stream disconnected", codexErrorInfo = "responseStreamDisconnected" }
    }), now);
    Assert(raceRetry is not null && recovery.TryBeginAttempt(raceRetry, now),
        "The dispatch-race fixture must enter startingContinuation.");
    Assert(
        !recovery.DiscardIfSupersededByTurn("dispatch-race", "turn-possibly-new") &&
        recovery.SnapshotFor("dispatch-race", now) is { Status: "startingContinuation" },
        "An ordinary page read must not clear a continuation during its acknowledgement race.");
}
finally
{
    try { if (Directory.Exists(recoveryDirectory)) Directory.Delete(recoveryDirectory, true); } catch { }
}

var runtimeStates = new ThreadRuntimeStateStore();
runtimeStates.BeginGeneration();

var matchingOwnership = new ThreadRuntimeStateStore();
var matchingGeneration = matchingOwnership.BeginGeneration();
matchingOwnership.ObserveTurnStarted("bridge-owned", "turn-bridge", matchingGeneration, now);
matchingOwnership.ObserveRolloutLifecycle(
    "bridge-owned", "task_started", "turn-bridge", now + TimeSpan.FromSeconds(1));
Assert(
    matchingOwnership.Get("bridge-owned") is
    {
        Source: "appServer", IsRunning: true, CanControl: true, ActiveTurnId: "turn-bridge"
    } &&
    matchingOwnership.IsCurrentBridgeOwnedTurn("bridge-owned", "turn-bridge") &&
    !matchingOwnership.IsExternallyOwnedActive("bridge-owned"),
    "A matching rollout originator must not hide current-generation app-server ownership of the same turn.");

var differentOwnership = new ThreadRuntimeStateStore();
var differentGeneration = differentOwnership.BeginGeneration();
differentOwnership.ObserveTurnStarted("externally-owned", "turn-bridge", differentGeneration, now);
differentOwnership.ObserveRolloutLifecycle(
    "externally-owned", "task_started", "turn-desktop", now + TimeSpan.FromSeconds(1));
Assert(
    differentOwnership.Get("externally-owned") is
    {
        Source: "rollout", IsRunning: true, CanControl: false, ActiveTurnId: "turn-desktop"
    } && differentOwnership.IsExternallyOwnedActive("externally-owned"),
    "A different fresh rollout turn must remain externally owned and non-controllable.");

matchingOwnership.ObserveTurnCompleted(
    "bridge-owned", "turn-bridge", "completed", matchingGeneration, now + TimeSpan.FromSeconds(2));
Assert(
    matchingOwnership.Get("bridge-owned") is
    {
        Source: "appServer", Phase: "idle", IsRunning: false, LastOutcome: "completed", CanControl: true
    } && !matchingOwnership.IsCurrentBridgeOwnedTurn("bridge-owned", "turn-bridge"),
    "A direct terminal app-server event must end a matching stale task_started rollout immediately.");

matchingOwnership.BeginGeneration();
Assert(
    matchingOwnership.Get("bridge-owned") is
    { Source: "rollout", IsRunning: false, CanControl: true } &&
    !matchingOwnership.IsCurrentBridgeOwnedTurn("bridge-owned", "turn-bridge"),
    "A new app-server generation must invalidate bridge ownership retained from an older connection.");

var delayedTerminalRollout = new ThreadRuntimeStateStore();
var delayedTerminalGeneration = delayedTerminalRollout.BeginGeneration();
delayedTerminalRollout.ObserveTurnStarted(
    "delayed-terminal", "turn-current", delayedTerminalGeneration, now);
delayedTerminalRollout.ObserveRolloutLifecycle(
    "delayed-terminal", "task_complete", "turn-old", now + TimeSpan.FromSeconds(1));
Assert(
    delayedTerminalRollout.Get("delayed-terminal") is
    { Source: "appServer", IsRunning: true, CanControl: true, ActiveTurnId: "turn-current" },
    "A terminal rollout record for an old originator must not hide a known active bridge-owned turn.");

runtimeStates.ObserveHistoricalStatus(
    "indexed-active",
    JsonSerializer.SerializeToElement(new { type = "active" }),
    now);
var indexedActive = runtimeStates.Get("indexed-active");
Assert(
    indexedActive is { Phase: "unknown", IsRunning: null, Source: "history", CanControl: true },
    "A persisted active bit must not be promoted to a live bridge-owned turn.");
Assert(
    !runtimeStates.IsExternallyOwnedActive("indexed-active"),
    "A thread-list entry alone must not block a new instruction as externally owned.");

runtimeStates.ObserveRolloutLifecycle(
    "orphaned-rollout",
    "task_started",
    "turn-orphaned",
    now - TimeSpan.FromHours(1));
var staleRollout = runtimeStates.Get("orphaned-rollout");
Assert(
    staleRollout is { Phase: "unknown", IsRunning: null, Source: "rollout", CanControl: true, Stale: true },
    "An inactive bare-running rollout must expire instead of blocking the thread for 24 hours.");
Assert(
    !runtimeStates.IsExternallyOwnedActive("orphaned-rollout"),
    "An expired rollout must not cause an external active conflict.");

runtimeStates.ObserveRolloutActivity("orphaned-rollout", now - TimeSpan.FromMinutes(1));
var refreshedRollout = runtimeStates.Get("orphaned-rollout");
Assert(
    refreshedRollout is { Phase: "running", IsRunning: true, Source: "rollout", CanControl: false, Stale: false },
    "Recent rollout file activity must keep a genuinely active desktop turn live.");
Assert(
    refreshedRollout is not null && refreshedRollout.ObservedAt >= now - TimeSpan.FromMinutes(1) &&
    refreshedRollout.FreshUntil > now,
    "The mobile snapshot must expose effective rollout freshness instead of the old lifecycle time.");

runtimeStates.ObservePersistedTurn(
    "orphaned-rollout",
    "turn-orphaned",
    "interrupted",
    completedAt: now - TimeSpan.FromMinutes(2),
    observedAt: now);
var interruptedTurn = runtimeStates.Get("orphaned-rollout");
Assert(
    interruptedTurn is
    {
        Phase: "idle", IsRunning: false, ActiveTurnId: null, LastOutcome: "interrupted",
        Source: "history", CanControl: true, Stale: false
    },
    "The latest terminal persisted turn must override a fresh orphaned rollout for the same turn.");
Assert(
    !runtimeStates.IsExternallyOwnedActive("orphaned-rollout"),
    "A persisted interrupted turn must immediately release the external ownership guard.");

var persistedResponse = JsonSerializer.SerializeToElement(new
{
    data = new[]
    {
        new
        {
            id = "turn-failed",
            status = "failed",
            completedAt = now.ToUnixTimeSeconds()
        }
    }
});
runtimeStates.ObserveRolloutLifecycle("persisted-terminal", "task_started", "turn-failed", now);
runtimeStates.ObserveLatestPersistedTurn("persisted-terminal", persistedResponse, now);
var failedTurn = runtimeStates.Get("persisted-terminal");
Assert(
    failedTurn is { Phase: "error", IsRunning: false, LastOutcome: "failed", CanControl: true },
    "Latest-turn reconciliation must recognize a terminal response returned by thread/turns/list.");

var activePersistedResponse = JsonSerializer.SerializeToElement(new
{
    data = new[]
    {
        new
        {
            id = "turn-active-null-completed-at",
            status = "inProgress",
            completedAt = (long?)null
        }
    }
});
runtimeStates.ObserveRolloutLifecycle(
    "active-null-completed-at",
    "task_started",
    "turn-active-null-completed-at",
    now);
runtimeStates.ObserveLatestPersistedTurn("active-null-completed-at", activePersistedResponse, now);
Assert(
    runtimeStates.Get("active-null-completed-at") is
    { Phase: "running", IsRunning: true, ActiveTurnId: "turn-active-null-completed-at" },
    "An active turn with completedAt:null must not fail latest-turn reconciliation or end the live task.");

var inferredExternal = new ThreadRuntimeStateStore();
inferredExternal.ObserveRolloutActivity("large-desktop-rollout", now);
inferredExternal.ObservePersistedTurn(
    "large-desktop-rollout",
    "turn-large-desktop",
    "inProgress",
    observedAt: now);
var inferredExternalSnapshot = inferredExternal.Get("large-desktop-rollout");
Assert(
    inferredExternalSnapshot is
    {
        Phase: "running", IsRunning: true, ActiveTurnId: "turn-large-desktop",
        Source: "rollout", CanControl: false, Stale: false
    } &&
    inferredExternalSnapshot.FreshUntil > now &&
    inferredExternalSnapshot.FreshUntil <= now + TimeSpan.FromMinutes(2),
    "Recent Desktop rollout activity plus a persisted in-progress turn must create only a short external-running lease.");

var oldExternalActivity = new ThreadRuntimeStateStore();
oldExternalActivity.ObserveRolloutActivity("old-desktop-rollout", now - TimeSpan.FromMinutes(3));
oldExternalActivity.ObservePersistedTurn(
    "old-desktop-rollout",
    "turn-old-desktop",
    "inProgress",
    observedAt: now);
Assert(
    oldExternalActivity.Get("old-desktop-rollout") is
    { Phase: "unknown", IsRunning: null, Source: "history", CanControl: true },
    "Old rollout activity must not turn a persisted in-progress record into a live external task.");

var staleActiveIndex = new ThreadRuntimeStateStore();
staleActiveIndex.ObserveRolloutActivity("stale-active-index", now);
staleActiveIndex.ObserveHistoricalStatus(
    "stale-active-index",
    JsonSerializer.SerializeToElement(new { type = "active" }),
    now);
Assert(
    staleActiveIndex.Get("stale-active-index") is
    { Phase: "unknown", IsRunning: null, Source: "history", CanControl: true },
    "A denormalized active thread index must not combine with file activity to infer a live task without a turn id.");

inferredExternal.ObservePersistedTurn(
    "large-desktop-rollout",
    "turn-large-desktop",
    "completed",
    completedAt: now + TimeSpan.FromSeconds(1),
    observedAt: now + TimeSpan.FromSeconds(1));
Assert(
    inferredExternal.Get("large-desktop-rollout") is
    { Phase: "idle", IsRunning: false, LastOutcome: "completed", Source: "history", CanControl: true },
    "A terminal latest-turn result must immediately override an activity-inferred external-running lease.");

runtimeStates.ObserveRolloutLifecycle("missing-completion-time", "task_started", "turn-new", now);
runtimeStates.ObservePersistedTurn(
    "missing-completion-time",
    "turn-old",
    "completed",
    completedAt: null,
    observedAt: now + TimeSpan.FromMinutes(1));
Assert(
    runtimeStates.Get("missing-completion-time") is
    { Phase: "running", IsRunning: true, ActiveTurnId: "turn-new", CanControl: false },
    "A terminal turn without completedAt must not use fetch time to override a different live turn.");

runtimeStates.ObserveRolloutLifecycle("same-turn-missing-time", "task_started", "turn-same", now);
runtimeStates.ObserveRolloutActivity("same-turn-missing-time", now + TimeSpan.FromSeconds(1));
runtimeStates.ObservePersistedTurn(
    "same-turn-missing-time",
    "turn-same",
    "interrupted",
    completedAt: null,
    observedAt: now + TimeSpan.FromMinutes(1));
Assert(
    runtimeStates.Get("same-turn-missing-time") is
    {
        Phase: "running", IsRunning: true, ActiveTurnId: "turn-same",
        Source: "rollout", CanControl: false, Stale: false
    },
    "An interrupted result without completedAt must not end the same turn while its Desktop rollout is fresh and growing.");

var delayedRolloutDiscovery = new ThreadRuntimeStateStore();
delayedRolloutDiscovery.ObservePersistedTurn(
    "delayed-rollout-discovery",
    "turn-delayed",
    "interrupted",
    completedAt: null,
    observedAt: now);
delayedRolloutDiscovery.ObserveRolloutLifecycle(
    "delayed-rollout-discovery",
    "task_started",
    "turn-delayed",
    now + TimeSpan.FromSeconds(1));
delayedRolloutDiscovery.ObserveRolloutActivity(
    "delayed-rollout-discovery",
    now + TimeSpan.FromSeconds(2));
Assert(
    delayedRolloutDiscovery.Get("delayed-rollout-discovery") is
    {
        Phase: "running", IsRunning: true, ActiveTurnId: "turn-delayed",
        Source: "rollout", CanControl: false, Stale: false
    },
    "A fresh rollout discovered after an untimestamped persisted terminal record must restore the live task state.");

var directCompletion = new ThreadRuntimeStateStore();
var directCompletionGeneration = directCompletion.BeginGeneration();
directCompletion.ObserveRolloutLifecycle(
    "direct-completion",
    "task_started",
    "turn-direct-completion",
    now);
directCompletion.ObserveTurnCompleted(
    "direct-completion",
    "turn-direct-completion",
    "completed",
    directCompletionGeneration,
    now + TimeSpan.FromSeconds(1));
Assert(
    directCompletion.Get("direct-completion") is
    { Phase: "idle", IsRunning: false, LastOutcome: "completed", Source: "appServer", CanControl: true },
    "A direct app-server turn/completed event must remain authoritative over a fresh matching Desktop rollout.");

var staleSameTurn = new ThreadRuntimeStateStore();
staleSameTurn.ObserveRolloutLifecycle(
    "stale-same-turn-missing-time",
    "task_started",
    "turn-stale-same",
    now - TimeSpan.FromMinutes(31));
staleSameTurn.ObserveRolloutActivity(
    "stale-same-turn-missing-time",
    now - TimeSpan.FromMinutes(31));
staleSameTurn.ObservePersistedTurn(
    "stale-same-turn-missing-time",
    "turn-stale-same",
    "interrupted",
    completedAt: null,
    observedAt: now);
Assert(
    staleSameTurn.Get("stale-same-turn-missing-time") is
    { Phase: "idle", IsRunning: false, LastOutcome: "interrupted", Source: "history", CanControl: true },
    "An untimestamped terminal result may end a matching rollout once all external activity is stale.");

var noRolloutTerminal = new ThreadRuntimeStateStore();
noRolloutTerminal.ObservePersistedTurn(
    "no-rollout-missing-time",
    "turn-no-rollout",
    "interrupted",
    completedAt: null,
    observedAt: now);
Assert(
    noRolloutTerminal.Get("no-rollout-missing-time") is
    { Phase: "idle", IsRunning: false, LastOutcome: "interrupted", Source: "history", CanControl: true },
    "An untimestamped terminal result remains usable when no fresh external rollout contradicts it.");

runtimeStates.ObserveHistoricalStatus(
    "persisted-terminal",
    JsonSerializer.SerializeToElement(new { type = "active" }),
    now + TimeSpan.FromMinutes(1));
Assert(
    runtimeStates.Get("persisted-terminal") is { Phase: "unknown", IsRunning: null, Source: "history" },
    "A newer persisted active bit must replace an older terminal result with unknown, not completed.");

var fileTransferDirectory = Path.Combine(
    Path.GetTempPath(),
    "codex-lan-file-transfer-tests-" + Guid.NewGuid().ToString("N"));
try
{
    const string artifactThreadId = "019f9baf-8539-75e1-b351-fe6197407058";
    var uploadRoot = Path.Combine(fileTransferDirectory, "uploads");
    var codexHome = Path.Combine(fileTransferDirectory, ".codex");
    var workspace = Path.Combine(fileTransferDirectory, "workspace");
    var artifactDirectory = Path.Combine(
        codexHome, "visualizations", "2026", "07", "25", artifactThreadId);
    var otherArtifactDirectory = Path.Combine(
        codexHome, "visualizations", "2026", "07", "25", "other-thread");
    Directory.CreateDirectory(workspace);
    Directory.CreateDirectory(artifactDirectory);
    Directory.CreateDirectory(otherArtifactDirectory);

    var workspaceFile = Path.Combine(workspace, "workspace-result.pdf");
    var artifactFile = Path.Combine(artifactDirectory, "pdf-worker-legacy-iphone16.png");
    var historicalArtifactFile = Path.Combine(artifactDirectory, "historical-task-result.png");
    var artifactSibling = Path.Combine(artifactDirectory, "unleased-secret.txt");
    var otherArtifact = Path.Combine(otherArtifactDirectory, "other.png");
    var unrelatedFile = Path.Combine(fileTransferDirectory, "unrelated.png");
    File.WriteAllText(workspaceFile, "workspace");
    File.WriteAllText(artifactFile, "artifact");
    File.WriteAllText(historicalArtifactFile, "historical-artifact");
    File.WriteAllText(artifactSibling, "secret");
    File.WriteAllText(otherArtifact, "other");
    File.WriteAllText(unrelatedFile, "outside");

    using var transfers = new FileTransferService(uploadRoot, codexHome);
    var workspaceLease = transfers.RegisterExisting(
        workspaceFile, workspace, artifactThreadId);
    Assert(
        transfers.ResolveDownload(workspaceLease.Id, artifactThreadId).Path == workspaceFile,
        "Files inside the task workspace must remain downloadable.");

    var artifactLease = transfers.RegisterExisting(
        artifactFile, workspace, artifactThreadId);
    Assert(
        transfers.ResolveDownload(artifactLease.Id, artifactThreadId).Path == artifactFile &&
        transfers.ResolveView(artifactLease.Id, null).Path == artifactFile,
        "A file in the matching thread's dated Codex visualization folder must be downloadable and viewable.");

    var missingHistoricalWorkspace = Path.Combine(fileTransferDirectory, "removed-workspace");
    var historicalArtifactLease = transfers.RegisterExisting(
        historicalArtifactFile,
        missingHistoricalWorkspace,
        artifactThreadId);
    Assert(
        transfers.ResolveDownload(historicalArtifactLease.Id, artifactThreadId).Path == historicalArtifactFile,
        "A trusted Codex delivery must remain downloadable after the historical task workspace was removed or unmounted.");

    var missingRelativeWorkspaceWasRejected = false;
    try
    {
        transfers.RegisterExisting(
            "relative-result.pdf",
            missingHistoricalWorkspace,
            artifactThreadId);
    }
    catch (DirectoryNotFoundException) { missingRelativeWorkspaceWasRejected = true; }
    Assert(
        missingRelativeWorkspaceWasRejected,
        "Relative paths must still require an existing task workspace.");

    var siblingWasRejected = false;
    try { transfers.ResolveView(artifactLease.Id, Path.GetFileName(artifactSibling)); }
    catch (UnauthorizedAccessException) { siblingWasRejected = true; }
    Assert(
        siblingWasRejected,
        "Changing a view URL must not expose an unleased sibling file from the artifact folder.");

    var otherThreadWasRejected = false;
    try { transfers.RegisterExisting(otherArtifact, workspace, artifactThreadId); }
    catch (UnauthorizedAccessException) { otherThreadWasRejected = true; }
    Assert(
        otherThreadWasRejected,
        "A Codex artifact belonging to another thread must not be registered.");

    var unrelatedWasRejected = false;
    try { transfers.RegisterExisting(unrelatedFile, workspace, artifactThreadId); }
    catch (UnauthorizedAccessException) { unrelatedWasRejected = true; }
    Assert(
        unrelatedWasRejected,
        "An arbitrary absolute path outside both the workspace and matching delivery folder must not be registered.");

    var referencedPage = JsonSerializer.SerializeToElement(new
    {
        data = new[]
        {
            new
            {
                items = new object[]
                {
                    new
                    {
                        type = "agentMessage",
                        text = $"APK: [download](</{unrelatedFile.Replace('\\', '/')}>)"
                    }
                }
            }
        }
    });
    var referencedPath = ThreadArtifactResolver.ResolveFromThreadPage(
        referencedPage,
        Path.GetFileName(unrelatedFile));
    Assert(
        string.Equals(referencedPath, unrelatedFile, StringComparison.OrdinalIgnoreCase),
        "A bare delivery filename must resolve to the unique existing absolute path in an assistant message.");

    var userOnlyPage = JsonSerializer.SerializeToElement(new
    {
        data = new[]
        {
            new
            {
                items = new object[]
                {
                    new { type = "userMessage", text = unrelatedFile }
                }
            }
        }
    });
    Assert(
        ThreadArtifactResolver.ResolveFromThreadPage(userOnlyPage, unrelatedFile) is null,
        "A path mentioned only by the user must not authorize a managed delivery.");

    var deliveryLease = await transfers.StoreThreadDeliveryAsync(
        referencedPath!,
        artifactThreadId,
        CancellationToken.None);
    File.Delete(unrelatedFile);
    var delivery = transfers.ResolveDownload(deliveryLease.Id, artifactThreadId);
    Assert(
        File.Exists(delivery.Path) &&
        File.ReadAllText(delivery.Path) == "outside" &&
        delivery.Descriptor.Name == "unrelated.png",
        "A verified external delivery must be snapshotted so later source moves do not break the download.");

    var alternateDataStreamWasRejected = false;
    try
    {
        transfers.RegisterExisting(
            workspaceFile + ":hidden",
            workspace,
            artifactThreadId);
    }
    catch (ArgumentException) { alternateDataStreamWasRejected = true; }
    Assert(
        alternateDataStreamWasRejected,
        "A Windows alternate data stream must never be registered as a downloadable file.");

    var linkedTarget = Path.Combine(fileTransferDirectory, "linked-target");
    var linkedArtifactDirectory = Path.Combine(artifactDirectory, "linked");
    Directory.CreateDirectory(linkedTarget);
    File.WriteAllText(Path.Combine(linkedTarget, "linked.png"), "linked");
    try
    {
        Directory.CreateSymbolicLink(linkedArtifactDirectory, linkedTarget);
        var linkedArtifactWasRejected = false;
        try
        {
            transfers.RegisterExisting(
                Path.Combine(linkedArtifactDirectory, "linked.png"),
                workspace,
                artifactThreadId);
        }
        catch (UnauthorizedAccessException) { linkedArtifactWasRejected = true; }
        Assert(
            linkedArtifactWasRejected,
            "A reparse point inside a trusted delivery folder must not bypass the file boundary.");
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
    {
        // Creating symbolic links is optional on Windows. Production validation
        // still checks every existing path component for the reparse attribute.
    }
}
finally
{
    try
    {
        if (Directory.Exists(fileTransferDirectory))
            Directory.Delete(fileTransferDirectory, true);
    }
    catch { }
}

var liveEvents = new ThreadLiveEventStore();
liveEvents.BeginGeneration(7);
liveEvents.Observe(
    "turn/started",
    JsonSerializer.SerializeToElement(new
    {
        threadId = "live-thread",
        turn = new
        {
            id = "live-turn",
            status = "inProgress",
            startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            items = new object[]
            {
                new { type = "userMessage", id = "user-1", clientId = "phone", content = new[] { new { type = "text", text = "continue" } } },
                new { type = "agentMessage", id = "agent-1", text = "", phase = "commentary" }
            }
        }
    }),
    7);
var beforeDelta = liveEvents.Snapshot("live-thread").Revision;
var liveWait = liveEvents.WaitForChangeAsync(
    "live-thread",
    beforeDelta,
    TimeSpan.FromSeconds(2),
    CancellationToken.None);
liveEvents.Observe(
    "item/agentMessage/delta",
    JsonSerializer.SerializeToElement(new
    {
        threadId = "live-thread", turnId = "live-turn", itemId = "agent-1", delta = "first update"
    }),
    7);
var changedSnapshot = await liveWait;
Assert(changedSnapshot.Revision > beforeDelta, "Live long-poll must wake after an item delta.");
var streamedTurn = changedSnapshot.Turns.Single();
var streamedAgent = streamedTurn.GetProperty("items").EnumerateArray()
    .Single(item => item.GetProperty("id").GetString() == "agent-1");
Assert(streamedAgent.GetProperty("text").GetString() == "first update", "Agent deltas must be accumulated in order.");
Assert(streamedAgent.GetProperty("phase").GetString() == "commentary", "Agent message phase must survive the live snapshot.");

for (var index = 0; index < 300; index++)
{
    liveEvents.Observe(
        "item/started",
        JsonSerializer.SerializeToElement(new
        {
            threadId = "live-thread",
            turnId = "live-turn",
            item = new { type = "commandExecution", id = $"tool-{index}", status = "inProgress" }
        }),
        7);
}
liveEvents.Observe(
    "item/completed",
    JsonSerializer.SerializeToElement(new
    {
        threadId = "live-thread",
        turnId = "live-turn",
        item = new { type = "agentMessage", id = "agent-final", text = "final answer", phase = "final_answer" }
    }),
    7);
var longSnapshotItems = liveEvents.Snapshot("live-thread").Turns.Single().GetProperty("items").EnumerateArray().ToArray();
Assert(
    longSnapshotItems.Any(item => item.GetProperty("id").GetString() == "user-1"),
    "A long tool-heavy turn must never evict the user message.");
Assert(
    longSnapshotItems.Any(item => item.GetProperty("id").GetString() == "agent-1") &&
    longSnapshotItems.Any(item => item.GetProperty("id").GetString() == "agent-final"),
    "A long tool-heavy turn must never discard intermediate or final assistant messages.");

var externalEvents = new ThreadLiveEventStore();
externalEvents.ObserveExternalTurn("external-thread", "external-turn", "running");
externalEvents.ObserveExternalItem("external-thread", "external-turn",
    JsonSerializer.SerializeToElement(new { id = "external-reasoning", type = "reasoning", summary = new[] { "Checking the live endpoint" } }));
externalEvents.ObserveExternalItem("external-thread", "external-turn",
    JsonSerializer.SerializeToElement(new { id = "external-command", type = "commandExecution", status = "completed", name = "exec" }));
externalEvents.ObserveExternalItem("external-thread", "external-turn",
    JsonSerializer.SerializeToElement(new { id = "external-file", type = "fileChange", status = "completed", changes = new[] { new { path = "C:\\repo\\app.js", kind = "update" } } }));
externalEvents.ObserveExternalItem("external-thread", "external-turn",
    JsonSerializer.SerializeToElement(new { id = "external-agent", type = "agentMessage", text = "Live check complete", phase = "commentary" }));
var externalTurn = externalEvents.Snapshot("external-thread").Turns.Single();
Assert(externalTurn.GetProperty("status").GetString() == "inProgress",
    "An externally observed running turn must be exposed as in progress.");
Assert(externalTurn.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("type").GetString()).SequenceEqual(
        new[] { "reasoning", "commandExecution", "fileChange", "agentMessage" }),
    "External rollout projections must preserve the native event order.");
externalEvents.ObserveExternalTurn("external-thread", "external-turn", "task_complete");
Assert(externalEvents.Snapshot("external-thread").Turns.Single().GetProperty("status").GetString() == "completed",
    "An external task completion must close the live turn.");
externalEvents.BeginGeneration(8);
Assert(externalEvents.Snapshot("external-thread").Turns.Single().GetProperty("items").GetArrayLength() == 4,
    "An app-server reconnect must not erase independently observed Desktop rollout progress.");
externalEvents.Observe(
    "turn/started",
    JsonSerializer.SerializeToElement(new
    {
        threadId = "app-only-thread",
        turn = new { id = "app-only-turn", status = "inProgress", items = Array.Empty<object>() }
    }),
    8);
externalEvents.BeginGeneration(9);
Assert(externalEvents.Snapshot("app-only-thread").Turns.Count == 0,
    "A disconnected app-server generation must not leave an unverified app-only live turn behind.");
Assert(externalEvents.Snapshot("external-thread").Turns.Count == 1,
    "Discarding an app-only generation must still preserve external rollout turns.");

var metadataTurnId = ExternalRolloutMonitor.ResponseTurnId(JsonSerializer.SerializeToElement(new
{
    type = "reasoning",
    internal_chat_message_metadata_passthrough = new { turn_id = "turn-from-metadata" }
}));
Assert(metadataTurnId == "turn-from-metadata",
    "Every response item must be able to restore its turn from rollout metadata without a history scan.");

var externalSummary = JsonSerializer.SerializeToElement(new
{
    data = new[]
    {
        new
        {
            id = "external-turn",
            status = "inProgress",
            items = new object[]
            {
                new { id = "external-user", type = "userMessage", content = new[] { new { type = "text", text = "show every step" } } },
                new { id = "external-agent", type = "agentMessage", text = "stale summary", phase = "commentary" }
            }
        }
    }
});
var externalPage = JsonSerializer.SerializeToElement(ApiHelpers.PagedThread(
    JsonSerializer.SerializeToElement(new { thread = new { id = "external-thread" } }),
    externalSummary,
    "external-thread",
    null,
    externalEvents.Snapshot("external-thread")));
Assert(externalPage.GetProperty("thread").GetProperty("turns")[0].GetProperty("items")
        .EnumerateArray().Select(item => item.GetProperty("type").GetString()).SequenceEqual(
            new[] { "userMessage", "reasoning", "commandExecution", "fileChange", "agentMessage" }),
    "Live rollout order must replace stale persisted message positions without losing the original user message.");

var summaryPage = JsonSerializer.SerializeToElement(new
{
    data = new[]
    {
        new
        {
            id = "projected-turn",
            status = "completed",
            items = new object[]
            {
                new { id = "projected-user", type = "userMessage", content = new[] { new { type = "text", text = "please fix it" } } },
                new { id = "projected-agent", type = "agentMessage", text = "done", phase = "final_answer" }
            }
        }
    },
    nextCursor = (string?)null
});
var recentItemsPage = JsonSerializer.SerializeToElement(new
{
    data = new object[]
    {
        new { turnId = "projected-turn", item = new { id = "projected-agent", type = "agentMessage", text = "done", phase = "final_answer" } },
        new { turnId = "projected-turn", item = new { id = "projected-file", type = "fileChange", status = "completed", diff = "large private diff", changes = new[] { new { path = "C:\\repo\\sw.js", kind = "update" } } } },
        new { turnId = "projected-turn", item = new { id = "projected-command", type = "commandExecution", status = "completed", aggregatedOutput = "large private command output" } },
        new { turnId = "projected-turn", item = new { id = "projected-user", type = "userMessage", content = new[] { new { type = "text", text = "please fix it" } } } }
    },
    nextCursor = "older-items"
});
var projectedPage = JsonSerializer.SerializeToElement(ApiHelpers.PagedThread(
    JsonSerializer.SerializeToElement(new { thread = new { id = "projected-thread", name = "Projection" } }),
    summaryPage,
    "projected-thread",
    null,
    null,
    recentItemsPage,
    "projected-turn"));
var projectedItems = projectedPage.GetProperty("thread").GetProperty("turns")[0].GetProperty("items")
    .EnumerateArray().ToArray();
Assert(
    projectedItems.Select(item => item.GetProperty("type").GetString()).SequenceEqual(
        new[] { "userMessage", "commandExecution", "fileChange", "agentMessage" }),
    "Recent persisted process items must be restored in chronological order between messages.");
Assert(
    !projectedItems[1].TryGetProperty("aggregatedOutput", out _) &&
    !projectedItems[2].TryGetProperty("diff", out _) &&
    projectedItems[2].GetProperty("changes")[0].GetProperty("path").GetString() == "C:\\repo\\sw.js",
    "Mobile process projection must retain a useful filename without command output or diffs.");
Assert(projectedPage.GetProperty("recentItemsTruncated").GetBoolean(),
    "A bounded recent-item response must disclose that older process steps exist.");

var unsafeFallbackPage = JsonSerializer.SerializeToElement(ApiHelpers.PagedThread(
    JsonSerializer.SerializeToElement(new { thread = new { id = "unsafe-thread" } }),
    JsonSerializer.SerializeToElement(new
    {
        data = new[]
        {
            new
            {
                id = "unsafe-turn",
                status = "completed",
                items = new object[]
                {
                    new { id = "safe-user", type = "userMessage", content = new[] { new { type = "text", text = "test" } } },
                    new
                    {
                        id = "unsafe-tool", type = "commandExecution", status = "completed",
                        output = "private output", diff = "private diff", arguments = new { secret = "private" },
                        blob = new string('A', 6_000)
                    }
                }
            }
        }
    }),
    "unsafe-thread",
    null));
var safeFallbackTool = unsafeFallbackPage.GetProperty("thread").GetProperty("turns")[0].GetProperty("items")[1];
Assert(!safeFallbackTool.TryGetProperty("output", out _) &&
       !safeFallbackTool.TryGetProperty("diff", out _) &&
       !safeFallbackTool.TryGetProperty("arguments", out _),
    "The summary fallback must remove tool output, diffs, and arguments rather than merely truncate them.");
Assert(safeFallbackTool.GetProperty("blob").GetString() == "[大体积二进制内容已省略]",
    "Bare Base64-like binary must be replaced even when it has no data URL prefix.");

var modelCatalogPayload = JsonSerializer.SerializeToElement(new
{
    data = new object[]
    {
        new
        {
            id = "sol",
            model = "gpt-5.6-sol",
            displayName = "GPT-5.6 Sol",
            description = "Frontier coding model",
            defaultReasoningEffort = "low",
            supportedReasoningEfforts = new[]
            {
                new { reasoningEffort = "low", description = "Fast" },
                new { reasoningEffort = "high", description = "Deep" },
                new { reasoningEffort = "ultra", description = "Proactive multi-agent" }
            },
            isDefault = true
        },
        new
        {
            id = "terra",
            model = "gpt-5.6-terra",
            displayName = "GPT-5.6 Terra",
            description = "Balanced coding model",
            defaultReasoningEffort = "medium",
            supportedReasoningEfforts = new[]
            {
                new { reasoningEffort = "medium", description = "Balanced" },
                new { reasoningEffort = "xhigh", description = "Deeper" }
            },
            isDefault = false
        }
    }
});
var parsedModels = CodexModelCatalog.Parse(modelCatalogPayload);
Assert(parsedModels.Count == 2 &&
       parsedModels[0].Model == "gpt-5.6-sol" &&
       parsedModels[0].SupportedReasoningEfforts.Select(item => item.Effort)
           .SequenceEqual(new[] { "low", "high", "ultra" }),
    "Model parsing must preserve the exact catalog model slug and advertised effort order.");
var canonicalSelection = CodexModelCatalog.ValidateSelection(parsedModels, "sol", "ultra");
Assert(canonicalSelection == new CodexCommandOptions("gpt-5.6-sol", "ultra"),
    "A catalog preset id must canonicalize to the app-server model slug before persistence.");
var unsupportedEffortRejected = false;
try { _ = CodexModelCatalog.ValidateSelection(parsedModels, "gpt-5.6-terra", "ultra"); }
catch (ArgumentException ex) when (ex.Message.Contains("medium, xhigh", StringComparison.Ordinal))
{
    unsupportedEffortRejected = true;
}
Assert(unsupportedEffortRejected,
    "A reasoning effort must be rejected unless the selected model advertises it.");
var unavailableModelRejected = false;
try { _ = CodexModelCatalog.ValidateSelection(parsedModels, "invented-model", "low"); }
catch (ArgumentException) { unavailableModelRejected = true; }
Assert(unavailableModelRejected, "An arbitrary model string must never reach turn/start.");

var selectedTurnParameters = CodexAppServer.BuildTurnStartParameters(
    "thread-model",
    new object[] { new { type = "text", text = "test" } },
    "client-model",
    new ExecutionPermissions(":workspace", "on-request", "user"),
    canonicalSelection);
Assert(selectedTurnParameters.GetProperty("model").GetString() == "gpt-5.6-sol" &&
       selectedTurnParameters.GetProperty("effort").GetString() == "ultra",
    "The canonical selection must be emitted as official turn/start model and effort fields.");
var inheritedTurnParameters = CodexAppServer.BuildTurnStartParameters(
    "thread-default",
    new object[] { new { type = "text", text = "test" } },
    "client-default",
    new ExecutionPermissions(":workspace", "on-request", "user"),
    turnOptions: null);
Assert(!inheritedTurnParameters.TryGetProperty("model", out _) &&
       !inheritedTurnParameters.TryGetProperty("effort", out _),
    "Commands without a selector must preserve the task's current Codex settings.");

var outboxFixture = Path.Combine(Path.GetTempPath(), "codex-lan-outbox-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(outboxFixture);
try
{
    var outboxNow = new DateTimeOffset(2026, 7, 27, 2, 0, 0, TimeSpan.Zero);
    var outbox = new ThreadCommandOutboxStore(outboxFixture, () => outboxNow);
    var commandInput = JsonSerializer.SerializeToElement(new object[]
    {
        new { type = "text", text = "return the CA file", text_elements = Array.Empty<object>() }
    });
    var fullComputer = new ExecutionPermissions(":danger-full-access", "never", "user");

    var legacyOutboxDirectory = Path.Combine(outboxFixture, "legacy-without-model-options");
    Directory.CreateDirectory(legacyOutboxDirectory);
    File.WriteAllText(
        Path.Combine(legacyOutboxDirectory, "command-outbox.json"),
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            commands = new[]
            {
                new
                {
                    schemaVersion = 1,
                    id = "legacy-receipt",
                    threadId = "legacy-thread",
                    clientUserMessageId = "legacy-client",
                    expectedTurnId = (string?)null,
                    input = commandInput,
                    permissions = fullComputer,
                    status = ThreadCommandStatus.Queued,
                    attempt = 0,
                    createdAt = outboxNow,
                    updatedAt = outboxNow,
                    nextAttemptAt = outboxNow,
                    dispatchStartedAt = (DateTimeOffset?)null,
                    requestWrittenAt = (DateTimeOffset?)null,
                    deliveredAt = (DateTimeOffset?)null,
                    acceptedTurnId = (string?)null,
                    lastError = (string?)null
                }
            }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    var legacyOutbox = new ThreadCommandOutboxStore(legacyOutboxDirectory, () => outboxNow);
    Assert(legacyOutbox.Find("legacy-thread", "legacy-receipt") is
           { Status: ThreadCommandStatus.Queued, Options: null },
        "Schema-v1 outbox files written before model options existed must still load unchanged.");

    // An external task_started record with no usable turn id is an orphan from
    // this Bridge's perspective. The command must still be accepted durably,
    // but it is not safe to dispatch until the external lease ends or goes stale.
    var orphanExternal = new ThreadRuntimeSnapshot(
        "orphan-thread", "running", true, null, Array.Empty<string>(), null,
        "rollout", false, outboxNow, 0, false);
    Assert(!ThreadCommandOutboxStore.CanDispatch(orphanExternal),
        "An empty external active turn must queue instead of racing Desktop ownership.");
    var orphanReceipt = outbox.Enqueue(
        "orphan-thread", commandInput, "client-orphan", null, fullComputer);
    Assert(orphanReceipt.Status == ThreadCommandStatus.Queued &&
           outbox.DispatchCandidates(outboxNow).Any(candidate => candidate.Id == orphanReceipt.Id),
        "A command for an orphan/external active task must be accepted into the durable queue.");
    Assert(File.ReadAllText(outbox.Path).Contains(":danger-full-access", StringComparison.Ordinal) &&
           File.ReadAllText(outbox.Path).Contains("\"approvalPolicy\": \"never\"", StringComparison.Ordinal),
        "The durable outbox must preserve the exact full-computer permission profile.");

    var modelReceipt = outbox.Enqueue(
        "model-thread",
        commandInput,
        "client-model-selection",
        null,
        fullComputer,
        canonicalSelection);
    Assert(modelReceipt.Options == canonicalSelection &&
           File.ReadAllText(outbox.Path).Contains("\"reasoningEffort\": \"ultra\"", StringComparison.Ordinal),
        "The durable receipt and outbox file must preserve the canonical model selection.");

    // The dispatcher consults runtime ownership before TryBeginDispatch. This
    // leaves the external command queued and gives it a bounded retry timestamp.
    outbox.DeferQueued(orphanReceipt.Id, TimeSpan.FromSeconds(2), "Desktop owns the turn.");
    Assert(outbox.Find("orphan-thread", orphanReceipt.Id)?.Status == ThreadCommandStatus.Queued &&
           outbox.DispatchCandidates(outboxNow).All(candidate => candidate.Id != orphanReceipt.Id),
        "A fresh Desktop-owned active task must remain queued rather than return a 409 or dispatch in parallel.");

    // An active-writer RPC response is a transient ownership race, not a
    // policy rejection. The exact durable message must return to the queue and
    // become eligible only after the bounded delay.
    var busyReceipt = outbox.Enqueue("busy-thread", commandInput, "client-busy", null, fullComputer);
    Assert(outbox.TryBeginDispatch(busyReceipt.Id, out _), "The active-writer fixture must begin dispatch.");
    outbox.ObserveRequestWritten("busy-thread", "client-busy", outboxNow);
    outbox.RequeueBusy(busyReceipt.Id, TimeSpan.FromSeconds(10), "thread already has an active writer", outboxNow);
    Assert(outbox.Find("busy-thread", busyReceipt.Id) is
           { Status: ThreadCommandStatus.Queued, LastError: "thread already has an active writer" } &&
           outbox.DispatchCandidates(outboxNow).All(candidate => candidate.Id != busyReceipt.Id),
        "An active-writer conflict must stay queued without being reported as a computer rejection.");
    outboxNow = outboxNow.AddSeconds(10);
    Assert(outbox.DispatchCandidates(outboxNow).Any(candidate => candidate.Id == busyReceipt.Id),
        "A busy command must become dispatchable after its retry delay.");

    var activeWriterRpc = new CodexRpcException(JsonSerializer.SerializeToElement(new
    {
        code = -32600,
        message = "thread busy-thread already has an active writer"
    }));
    Assert(activeWriterRpc.IsActiveTurnConflict && activeWriterRpc.SuggestedHttpStatus == 409,
        "The current app-server active-writer wording must be classified as a transient conflict.");

    // A normal RPC acknowledgement is final. A subsequent process disconnect
    // must not regress a delivered command into dispatchUncertain.
    var ackReceipt = outbox.Enqueue("ack-thread", commandInput, "client-ack", null, fullComputer);
    Assert(outbox.TryBeginDispatch(ackReceipt.Id, out _), "An idle queued command must enter dispatching.");
    outbox.ObserveRequestWritten("ack-thread", "client-ack", outboxNow);
    outbox.MarkDelivered(ackReceipt.Id, "turn-ack", outboxNow);
    outbox.MarkAllDispatchingUncertain("connection closed", outboxNow);
    Assert(outbox.Find("ack-thread", ackReceipt.Id) is { Status: ThreadCommandStatus.Delivered, AcceptedTurnId: "turn-ack" },
        "An acknowledgement recorded before disconnect must remain delivered.");
    Assert(outbox.WasAcceptedByBridge("ack-thread", "turn-ack") &&
           !outbox.WasAcceptedByBridge("ack-thread", "turn-external") &&
           !outbox.WasAcceptedByBridge("other-thread", "turn-ack"),
        "Only the exact task/turn acknowledged by this Bridge may bypass an orphaned rollout lease.");

    // A bare task_started/turn/started is not proof that the phone's user
    // message was durably appended. Losing the connection at this boundary
    // must preserve uncertainty and must not replay the original input.
    var startedReceipt = outbox.Enqueue("started-thread", commandInput, "client-started", null, fullComputer);
    Assert(outbox.TryBeginDispatch(startedReceipt.Id, out _), "The task_started fixture must begin dispatch.");
    outbox.ObserveRequestWritten("started-thread", "client-started", outboxNow);
    outbox.ObserveTurnStarted("started-thread", "turn-started", outboxNow);
    outbox.MarkAllDispatchingUncertain("connection closed", outboxNow);
    Assert(outbox.Find("started-thread", startedReceipt.Id) is
           { Status: ThreadCommandStatus.DispatchUncertain, AcceptedTurnId: "turn-started" },
        "Bare task_started followed by disconnect must remain dispatchUncertain and never replay.");

    // A disconnect during thread/resume or another preflight request cannot
    // have delivered the user's turn, so it must safely requeue.
    var prewriteReceipt = outbox.Enqueue("prewrite-thread", commandInput, "client-prewrite", null, fullComputer);
    Assert(outbox.TryBeginDispatch(prewriteReceipt.Id, out _), "The prewrite fixture must begin dispatch.");
    outbox.MarkAllDispatchingUncertain("connection closed", outboxNow);
    Assert(outbox.Find("prewrite-thread", prewriteReceipt.Id)?.Status == ThreadCommandStatus.Queued &&
           outbox.DispatchCandidates(outboxNow).Any(candidate => candidate.Id == prewriteReceipt.Id),
        "A disconnect before turn/start reaches the pipe must requeue instead of remaining uncertain forever.");

    // Client ids are supplied by the phone and are only idempotent per task.
    // Correlation must include the task id so identical ids cannot cross-deliver.
    var duplicateA = outbox.Enqueue("duplicate-a", commandInput, "same-client-id", null, fullComputer);
    var duplicateB = outbox.Enqueue("duplicate-b", commandInput, "same-client-id", null, fullComputer);
    Assert(outbox.TryBeginDispatch(duplicateA.Id, out _) && outbox.TryBeginDispatch(duplicateB.Id, out _),
        "Both per-task duplicate-id fixtures must begin dispatch independently.");
    outbox.ObserveRequestWritten("duplicate-b", "same-client-id", outboxNow);
    outbox.ObserveClientMessage(null, "same-client-id", null, outboxNow);
    Assert(outbox.Find("duplicate-a", duplicateA.Id)?.Status == ThreadCommandStatus.Dispatching &&
           outbox.Find("duplicate-b", duplicateB.Id)?.Status == ThreadCommandStatus.Dispatching,
        "An unscoped ambiguous client id must not deliver either task.");
    outbox.ObserveClientMessage("duplicate-b", "same-client-id", "duplicate-turn", outboxNow);
    Assert(outbox.Find("duplicate-a", duplicateA.Id)?.Status == ThreadCommandStatus.Dispatching &&
           outbox.Find("duplicate-b", duplicateB.Id) is
               { Status: ThreadCommandStatus.Delivered, AcceptedTurnId: "duplicate-turn" },
        "A task-scoped client id must deliver only its matching receipt.");
    outbox.MarkAllDispatchingUncertain("connection closed", outboxNow);
    Assert(outbox.Find("duplicate-a", duplicateA.Id)?.Status == ThreadCommandStatus.Queued,
        "The duplicate-id command whose request was never written must remain safely replayable.");

    // Simulate a restart with one request written but not acknowledged, and one
    // command that was only queued. Only the former becomes uncertain.
    var uncertainReceipt = outbox.Enqueue("restart-uncertain", commandInput, "client-uncertain", null, fullComputer);
    Assert(outbox.TryBeginDispatch(uncertainReceipt.Id, out _), "The restart fixture must persist dispatching first.");
    outbox.ObserveRequestWritten("restart-uncertain", "client-uncertain", outboxNow);
    var restartPrewriteReceipt = outbox.Enqueue(
        "restart-prewrite", commandInput, "client-restart-prewrite", null, fullComputer);
    Assert(outbox.TryBeginDispatch(restartPrewriteReceipt.Id, out _),
        "The restart prewrite fixture must persist dispatching without a completed request write.");
    var queuedReceipt = outbox.Enqueue("restart-queued", commandInput, "client-queued", null, fullComputer);
    outboxNow = outboxNow.AddSeconds(1);
    var restartedOutbox = new ThreadCommandOutboxStore(outboxFixture, () => outboxNow);
    Assert(restartedOutbox.Find("restart-uncertain", uncertainReceipt.Id)?.Status == ThreadCommandStatus.DispatchUncertain,
        "Restart persistence must convert an in-flight request to dispatchUncertain instead of replaying it.");
    Assert(restartedOutbox.Find("restart-prewrite", restartPrewriteReceipt.Id)?.Status == ThreadCommandStatus.Queued,
        "Restart persistence must safely requeue preflight work that never wrote turn/start.");
    Assert(restartedOutbox.Find("restart-queued", queuedReceipt.Id)?.Status == ThreadCommandStatus.Queued,
        "Restart persistence must retain never-dispatched commands as queued.");
    Assert(restartedOutbox.Find("model-thread", modelReceipt.Id)?.Options == canonicalSelection,
        "Restart persistence must retain model and effort overrides for delayed dispatch.");

    // Exact clientUserMessageId evidence in persisted history safely resolves
    // an uncertain command without issuing another turn/start.
    restartedOutbox.ReconcileHistory("restart-uncertain", JsonSerializer.SerializeToElement(new
    {
        data = new[]
        {
            new
            {
                id = "reconciled-turn",
                status = "inProgress",
                items = new[] { new { type = "userMessage", clientId = "client-uncertain" } }
            }
        }
    }));
    Assert(restartedOutbox.Find("restart-uncertain", uncertainReceipt.Id) is
           { Status: ThreadCommandStatus.Delivered, AcceptedTurnId: "reconciled-turn" },
        "Persisted client message evidence must reconcile an uncertain dispatch without duplication.");
}
finally
{
    try { Directory.Delete(outboxFixture, recursive: true); } catch { }
}

// New threads are kept only in app-server memory until the first turn starts.
var draftClock = DateTimeOffset.UtcNow;
var draftLeases = new ThreadAccessLeaseTracker(() => draftClock);
draftLeases.MarkLoaded("draft");
draftLeases.MarkAwaitingFirstTurn("draft");
draftClock += TimeSpan.FromHours(1);
Assert(!draftLeases.TryBeginRelease("draft", null, TimeSpan.FromMinutes(2), out _),
    "Idle cleanup must not unsubscribe a new thread before its first message can materialize it.");
Assert(draftLeases.IsAwaitingFirstTurn("draft"), "A new thread must be recognized during history initialization.");
draftLeases.MarkTurnStarted("draft");
Assert(!draftLeases.IsAwaitingFirstTurn("draft"), "The initialization exception must not mask history errors on old threads.");
Assert(!draftLeases.TryBeginRelease("draft", null, TimeSpan.Zero, out _),
    "An executing first turn must retain access.");
draftLeases.MarkTurnCompleted("draft");
draftClock += TimeSpan.FromSeconds(6);
Assert(!draftLeases.IsStartingFirstTurn("draft"), "The first-turn history grace window must be strictly bounded.");
Assert(draftLeases.TryBeginRelease("draft", null, TimeSpan.FromSeconds(5), out _),
    "A materialized completed thread must still be released promptly.");
var emptyHistoryError = new CodexRpcException(JsonSerializer.SerializeToElement(new
{
    code = -32600,
    message = "thread draft is not materialized yet; thread/turns/list is unavailable before first user message"
}), "thread/turns/list", 42);
Assert(emptyHistoryError.IsUnmaterializedThread && !emptyHistoryError.IsThreadNotFound,
    "New-thread history unavailability must be distinct from a missing old thread.");
Assert(emptyHistoryError.Method == "thread/turns/list" && emptyHistoryError.RequestId == 42,
    "RPC exceptions must preserve method and request id for asynchronous outbox diagnostics.");
var missingHistoryError = new CodexRpcException(JsonSerializer.SerializeToElement(new
{
    code = -32600, message = "no rollout found for thread id missing"
}));
Assert(!missingHistoryError.IsUnmaterializedThread && missingHistoryError.IsThreadNotFound,
    "A genuinely missing thread must not be treated as a new empty one.");
var historyRace = new CodexRpcException(JsonSerializer.SerializeToElement(new
{
    code = -32601, message = "paginated_threads is not supported yet"
}));
Assert(historyRace.IsHistoryInitializing && !emptyHistoryError.IsHistoryInitializing,
    "The observed first-turn materialization race must have a narrow, distinct classifier.");
var dynamicResult = JsonSerializer.SerializeToElement(DynamicToolProtocol.Unavailable(default));
Assert(CodexAppServer.IsSupportedServerRequest(Pending("item/tool/call")) &&
       dynamicResult.GetProperty("success").GetBoolean() == false &&
       dynamicResult.GetProperty("contentItems")[0].GetProperty("type").GetString() == "inputText",
    "Inherited dynamic calls must resolve with a protocol-valid tool failure, never fake success or a pending approval.");

Console.WriteLine($"Bridge protocol tests passed: {assertions} assertions.");
