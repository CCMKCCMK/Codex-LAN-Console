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

Console.WriteLine($"Bridge protocol tests passed: {assertions} assertions.");
