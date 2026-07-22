using System.Text.Json;

namespace CodexLanBridge;

public enum AutoApprovalDisposition
{
    NotAttempted,
    Approved,
    NoLongerPending,
    Failed
}

public static class ApprovalProtocol
{
    public static bool IsApprovalRequest(PendingRequest request) => request.Method is
        "item/commandExecution/requestApproval" or
        "item/fileChange/requestApproval" or
        "item/permissions/requestApproval" or
        "applyPatchApproval" or
        "execCommandApproval";

    public static JsonElement BuildResult(PendingRequest request, string decision)
    {
        if (!IsApprovalRequest(request))
            throw new ArgumentException("The pending request is not a supported approval request.");
        if (decision is not ("accept" or "acceptForSession" or "decline" or "cancel"))
            throw new ArgumentException("Invalid approval decision.");

        if (request.Method.Equals("item/permissions/requestApproval", StringComparison.Ordinal))
        {
            if (decision == "cancel")
                throw new ArgumentException("Permission approval requests do not support cancel; use decline instead.");
            var granted = decision is "accept" or "acceptForSession"
                ? RequiredObject(request.Params, "permissions").Clone()
                : JsonSerializer.SerializeToElement(new { });
            return JsonSerializer.SerializeToElement(new
            {
                permissions = granted,
                scope = decision == "acceptForSession" ? "session" : "turn"
            });
        }

        var protocolDecision = request.Method is "applyPatchApproval" or "execCommandApproval"
            ? decision switch
            {
                "accept" => "approved",
                "acceptForSession" => "approved_for_session",
                "decline" => "denied",
                "cancel" => "abort",
                _ => throw new ArgumentException("Invalid approval decision.")
            }
            : decision;
        return JsonSerializer.SerializeToElement(new { decision = protocolDecision });
    }

    public static bool ShouldPublishPendingNotification(
        AutoApprovalDisposition disposition,
        bool isStillPending) =>
        isStillPending && disposition is AutoApprovalDisposition.NotAttempted or AutoApprovalDisposition.Failed;

    private static JsonElement RequiredObject(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Object) return property;
        throw new InvalidDataException($"The approval request is missing {propertyName}.");
    }
}
