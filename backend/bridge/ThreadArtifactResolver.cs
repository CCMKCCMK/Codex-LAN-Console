using System.Text.Json;

namespace CodexLanBridge;

public static class ThreadArtifactResolver
{
    public static async Task<string?> ResolveAsync(
        CodexAppServer codex,
        string threadId,
        string requestedPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(requestedPath)) return null;
        JsonElement page;
        try
        {
            page = await codex.CallAsync(
                "thread/turns/list",
                new
                {
                    threadId,
                    limit = 20,
                    sortDirection = "desc",
                    itemsView = "summary"
                },
                cancellationToken);
        }
        catch
        {
            return null;
        }

        return ResolveFromThreadPage(page, requestedPath);
    }

    public static string? ResolveFromThreadPage(JsonElement page, string requestedPath)
    {
        var normalizedRequest = NormalizeReference(requestedPath);
        if (string.IsNullOrWhiteSpace(normalizedRequest)) return null;
        var requestedName = Path.GetFileName(normalizedRequest);
        if (string.IsNullOrWhiteSpace(requestedName)) return null;

        var matches = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var message in AgentMessageStrings(page))
        {
            var normalizedMessage = message.Replace('\\', '/');
            if (Path.IsPathRooted(normalizedRequest))
            {
                var fullRequest = Path.GetFullPath(normalizedRequest);
                if (normalizedMessage.Contains(fullRequest.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(fullRequest))
                    matches.Add(fullRequest);
            }

            foreach (var candidate in AbsoluteCandidates(normalizedMessage, requestedName))
            {
                try
                {
                    var full = Path.GetFullPath(candidate);
                    if (File.Exists(full)) matches.Add(full);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Ignore malformed text that merely resembles a local path.
                }
            }
        }

        return matches.Count == 1 ? matches.Single() : null;
    }

    private static IEnumerable<string> AgentMessageStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "agentMessage", StringComparison.Ordinal))
            {
                foreach (var value in StringValues(element)) yield return value;
                yield break;
            }

            foreach (var property in element.EnumerateObject())
                foreach (var value in AgentMessageStrings(property.Value))
                    yield return value;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var value in AgentMessageStrings(item))
                    yield return value;
        }
    }

    private static IEnumerable<string> StringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString() ?? "";
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    foreach (var value in StringValues(property.Value))
                        yield return value;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var value in StringValues(item))
                        yield return value;
                break;
        }
    }

    private static IEnumerable<string> AbsoluteCandidates(string text, string fileName)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var searchFrom = 0;
        while (searchFrom < text.Length)
        {
            var nameIndex = text.IndexOf(fileName, searchFrom, comparison);
            if (nameIndex < 0) yield break;
            var start = LastWindowsRoot(text, nameIndex);
            if (start >= 0)
            {
                var candidate = text[start..(nameIndex + fileName.Length)]
                    .Trim(' ', '\t', '\r', '\n', '`', '"', '\'', '<', '>', '(', ')', '[', ']');
                if (candidate.Length > 3) yield return NormalizeReference(candidate);
            }
            searchFrom = nameIndex + fileName.Length;
        }
    }

    private static int LastWindowsRoot(string text, int before)
    {
        for (var index = before - 2; index >= 0; index--)
        {
            if (index + 2 < text.Length &&
                char.IsAsciiLetter(text[index]) &&
                text[index + 1] == ':' &&
                text[index + 2] == '/')
                return index;
        }
        return -1;
    }

    private static string NormalizeReference(string path)
    {
        var value = Uri.UnescapeDataString(path.Trim()).Trim('<', '>');
        if (value.Length >= 4 &&
            value[0] == '/' &&
            char.IsAsciiLetter(value[1]) &&
            value[2] == ':' &&
            (value[3] == '/' || value[3] == '\\'))
            value = value[1..];
        return value.Replace('/', Path.DirectorySeparatorChar);
    }
}
