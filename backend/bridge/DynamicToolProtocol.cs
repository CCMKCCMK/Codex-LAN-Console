using System.Text.Json;

namespace CodexLanBridge;

internal static class DynamicToolProtocol
{
    public static object Unavailable(JsonElement parameters)
    {
        return new
        {
            success = false,
            contentItems = new[]
            {
                new
                {
                    type = "inputText",
                    text = "This client-owned dynamic tool is unavailable in Codex LAN Console. " +
                        "No action was performed. Do not retry the same unavailable tool. " +
                        "Use the tools and MCP servers available on this connection when they can perform " +
                        "the requested task; otherwise explain which capability is missing to the user."
                }
            }
        };
    }
}
