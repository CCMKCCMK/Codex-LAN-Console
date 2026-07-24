namespace CodexLanBridge;

public sealed record ExecutionPermissions(
    string Permissions,
    string ApprovalPolicy,
    string ApprovalsReviewer)
{
    public static ExecutionPermissions Default { get; } = new(":workspace", "on-request", "auto_review");

    public bool IsUnrestrictedAutonomy =>
        Permissions == ":danger-full-access" && ApprovalPolicy == "never";

    public static ExecutionPermissions Parse(
        string? permissions,
        string? approvalPolicy,
        string? approvalsReviewer)
    {
        var profile = permissions?.Trim() switch
        {
            null or "" or ":workspace" or "workspace-write" => ":workspace",
            ":read-only" or "read-only" => ":read-only",
            ":danger-full-access" or "danger-full-access" => ":danger-full-access",
            _ => throw new ArgumentException("The selected runtime permission is not supported.")
        };
        var approval = approvalPolicy?.Trim() switch
        {
            null or "" or "on-request" => "on-request",
            "never" => "never",
            "untrusted" => "untrusted",
            _ => throw new ArgumentException("The selected approval policy is not supported.")
        };
        var reviewer = approvalsReviewer?.Trim() switch
        {
            null or "" or "auto_review" => "auto_review",
            "user" => "user",
            _ => throw new ArgumentException("The selected approval reviewer is not supported.")
        };
        if (approval != "on-request") reviewer = "user";
        return new ExecutionPermissions(profile, approval, reviewer);
    }

    public string LegacySandbox => Permissions switch
    {
        ":read-only" => "read-only",
        ":danger-full-access" => "danger-full-access",
        _ => "workspace-write"
    };

    public ExecutionPermissions RouteApprovalsToBridge(bool autoApproveAll)
    {
        if (!autoApproveAll || ApprovalPolicy != "on-request" || ApprovalsReviewer == "user") return this;

        // Auto-approval can only answer requests routed back to this app-server
        // client. Do not let the independent auto-reviewer consume them first.
        return this with { ApprovalsReviewer = "user" };
    }

    public object LegacyTurnSandboxPolicy(string cwd) => Permissions switch
    {
        ":read-only" => new { type = "readOnly", networkAccess = false },
        ":danger-full-access" => new { type = "dangerFullAccess" },
        _ => new
        {
            type = "workspaceWrite",
            writableRoots = new[] { cwd },
            networkAccess = false,
            excludeSlashTmp = false,
            excludeTmpdirEnvVar = false
        }
    };
}
