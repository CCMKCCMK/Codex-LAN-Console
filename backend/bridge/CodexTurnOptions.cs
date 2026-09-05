using System.Text.Json;

namespace CodexLanBridge;

/// <summary>
/// Optional app-server settings for one queued command. Null means that Codex
/// should keep the task's current settings. Values are canonicalized against
/// model/list before they are persisted.
/// </summary>
public sealed record CodexCommandOptions(string? Model, string? ReasoningEffort)
{
    public bool HasOverrides => !string.IsNullOrWhiteSpace(Model) ||
                                !string.IsNullOrWhiteSpace(ReasoningEffort);
}

internal sealed class CodexTurnBusyException : Exception
{
    public CodexTurnBusyException(string message) : base(message) { }
}

public sealed record CodexReasoningEffortOption(string Effort, string Description);

public sealed record CodexModelOption(
    string Id,
    string Model,
    string DisplayName,
    string Description,
    string DefaultReasoningEffort,
    IReadOnlyList<CodexReasoningEffortOption> SupportedReasoningEfforts,
    bool IsDefault);

/// <summary>
/// Reads the authoritative model catalog from the same app-server that will
/// execute the command. This prevents a mobile client from sending arbitrary
/// model or effort strings while still supporting future catalog values.
/// </summary>
public sealed class CodexModelCatalog
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private readonly CodexAppServer _codex;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private IReadOnlyList<CodexModelOption>? _cached;
    private DateTimeOffset _cachedAt;

    public CodexModelCatalog(CodexAppServer codex) => _codex = codex;

    public async Task<IReadOnlyList<CodexModelOption>> ListAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!forceRefresh && _cached is { } cached &&
            DateTimeOffset.UtcNow - _cachedAt < CacheLifetime) return cached;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _cached is { } fresh &&
                DateTimeOffset.UtcNow - _cachedAt < CacheLifetime) return fresh;
            var response = await _codex.CallAsync("model/list", new
            {
                limit = 100,
                includeHidden = false
            }, cancellationToken);
            var parsed = Parse(response);
            if (parsed.Count == 0)
                throw new InvalidOperationException("Codex did not advertise any selectable models.");
            _cached = parsed;
            _cachedAt = DateTimeOffset.UtcNow;
            return parsed;
        }
        finally { _refreshGate.Release(); }
    }

    public async Task<CodexCommandOptions?> NormalizeAsync(
        string? requestedModel,
        string? requestedReasoningEffort,
        CancellationToken cancellationToken)
    {
        var model = Clean(requestedModel, 200, "The model identifier is too long.");
        var effort = Clean(requestedReasoningEffort, 100, "The reasoning effort is too long.");
        if (model is null && effort is null) return null;
        if (model is null)
            throw new ArgumentException("Select a model when selecting a reasoning effort.");

        var catalog = await ListAsync(forceRefresh: false, cancellationToken);
        try { return ValidateSelection(catalog, model, effort); }
        catch (ArgumentException)
        {
            // Entitlements and staged model catalogs can change while the Bridge
            // is running. Refresh once before declaring a selection invalid.
            catalog = await ListAsync(forceRefresh: true, cancellationToken);
            return ValidateSelection(catalog, model, effort);
        }
    }

    internal static IReadOnlyList<CodexModelOption> Parse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array) return Array.Empty<CodexModelOption>();

        var models = new List<CodexModelOption>();
        foreach (var item in data.EnumerateArray())
        {
            var id = Text(item, "id");
            var model = Text(item, "model");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(model)) continue;
            var efforts = new List<CodexReasoningEffortOption>();
            if (item.TryGetProperty("supportedReasoningEfforts", out var advertised) &&
                advertised.ValueKind == JsonValueKind.Array)
            {
                foreach (var preset in advertised.EnumerateArray())
                {
                    // Current Codex app-server model/list uses `reasoningEffort`.
                    // Keep `effort` as a compatibility fallback for older builds.
                    var value = Text(preset, "reasoningEffort") ?? Text(preset, "effort");
                    if (string.IsNullOrWhiteSpace(value) ||
                        efforts.Any(existing => existing.Effort.Equals(value, StringComparison.Ordinal))) continue;
                    efforts.Add(new CodexReasoningEffortOption(value, Text(preset, "description") ?? ""));
                }
            }
            var defaultEffort = Text(item, "defaultReasoningEffort") ??
                                efforts.FirstOrDefault()?.Effort ?? "";
            models.Add(new CodexModelOption(
                id,
                model,
                Text(item, "displayName") ?? model,
                Text(item, "description") ?? "",
                defaultEffort,
                efforts,
                item.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True));
        }
        return models;
    }

    internal static CodexCommandOptions ValidateSelection(
        IReadOnlyList<CodexModelOption> catalog,
        string requestedModel,
        string? requestedReasoningEffort)
    {
        var match = catalog.FirstOrDefault(candidate =>
            candidate.Model.Equals(requestedModel, StringComparison.Ordinal) ||
            candidate.Id.Equals(requestedModel, StringComparison.Ordinal));
        if (match is null) throw new ArgumentException($"Model '{requestedModel}' is not available in Codex.");
        if (requestedReasoningEffort is not null &&
            !match.SupportedReasoningEfforts.Any(candidate =>
                candidate.Effort.Equals(requestedReasoningEffort, StringComparison.Ordinal)))
        {
            var supported = string.Join(", ", match.SupportedReasoningEfforts.Select(candidate => candidate.Effort));
            throw new ArgumentException(
                supported.Length == 0
                    ? $"Model '{match.Model}' does not advertise selectable reasoning efforts."
                    : $"Reasoning effort '{requestedReasoningEffort}' is not available for model '{match.Model}'. Supported values: {supported}.");
        }
        return new CodexCommandOptions(match.Model, requestedReasoningEffort);
    }

    private static string? Clean(string? value, int maximumLength, string lengthError)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > maximumLength) throw new ArgumentException(lengthError);
        if (value.Any(char.IsControl)) throw new ArgumentException("Model options cannot contain control characters.");
        return value;
    }

    private static string? Text(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
