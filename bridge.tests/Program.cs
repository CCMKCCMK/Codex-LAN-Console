using System.Text.Json;
using CodexLanBridge;

var assertions = 0;

void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

PendingRequest Pending(string method, string parameters = "{}") => new(
    "test-key",
    method,
    JsonDocument.Parse(parameters).RootElement.Clone(),
    DateTimeOffset.UtcNow);

var unrestricted = ExecutionPermissions.Parse(":danger-full-access", "never", "auto_review");
Assert(unrestricted.Permissions == ":danger-full-access", "Full-access profile was not preserved.");
Assert(unrestricted.ApprovalPolicy == "never", "Never-approve policy was not preserved.");
Assert(unrestricted.ApprovalsReviewer == "user", "Reviewer must be disabled when approval policy is never.");
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

Console.WriteLine($"Bridge protocol tests passed: {assertions} assertions.");
