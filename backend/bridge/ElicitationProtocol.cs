using System.Text.Json;

namespace CodexLanBridge;

public static class ElicitationProtocol
{
    public const string Method = "mcpServer/elicitation/request";

    public static bool IsElicitationRequest(PendingRequest request) =>
        request.Method.Equals(Method, StringComparison.Ordinal);

    public static bool IsToolApproval(PendingRequest request)
    {
        if (!IsElicitationRequest(request) || request.Params.ValueKind != JsonValueKind.Object)
            return false;
        return TryMeta(request.Params, out var meta) &&
               Text(meta, "codex_approval_kind")?.Equals("mcp_tool_call", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string[] AdvertisedPersistence(PendingRequest request)
    {
        if (!TryMeta(request.Params, out var meta) || !meta.TryGetProperty("persist", out var persist))
            return Array.Empty<string>();
        if (persist.ValueKind == JsonValueKind.String)
            return NormalizePersistence([persist.GetString()]);
        if (persist.ValueKind == JsonValueKind.Array)
            return NormalizePersistence(persist.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()));
        return Array.Empty<string>();
    }

    public static string? PreferredPersistence(PendingRequest request)
    {
        var advertised = AdvertisedPersistence(request);
        if (advertised.Contains("always", StringComparer.Ordinal)) return "always";
        if (advertised.Contains("session", StringComparer.Ordinal)) return "session";
        return null;
    }

    public static JsonElement BuildResult(
        PendingRequest request,
        string action,
        JsonElement? content,
        string? persistence)
    {
        if (!IsElicitationRequest(request))
            throw new ArgumentException("The pending request is not an MCP elicitation.");
        if (action is not ("accept" or "decline" or "cancel"))
            throw new ArgumentException("Invalid elicitation action.");

        if (action != "accept")
        {
            content = null;
            persistence = null;
        }
        else
        {
            var mode = Text(request.Params, "mode") ?? "form";
            if (mode is "form" or "openai/form" && content is not { ValueKind: JsonValueKind.Object })
                throw new ArgumentException("Accepted form elicitations require an object response.");
            if (mode is "form" or "openai/form" &&
                content is { } formContent &&
                request.Params.TryGetProperty("requestedSchema", out var requestedSchema) &&
                requestedSchema.ValueKind == JsonValueKind.Object &&
                !TryValidateSchemaValue(formContent, requestedSchema, "$", out var validationError))
            {
                throw new ArgumentException(validationError);
            }
            if (mode is "form" or "openai/form" &&
                content is { } approvalContent &&
                IsToolApproval(request) &&
                !TryValidateApprovalPersistence(approvalContent, persistence, "$", out var persistenceError))
            {
                throw new ArgumentException(persistenceError);
            }
            if (mode == "url" && content is null)
                content = JsonSerializer.SerializeToElement<object?>(null);
        }

        if (persistence is not null)
        {
            if (!IsToolApproval(request))
                throw new ArgumentException("Persistence is only valid for MCP tool approvals.");
            if (!AdvertisedPersistence(request).Contains(persistence, StringComparer.Ordinal))
                throw new ArgumentException("The requested persistence option was not advertised by the tool.");
        }

        var result = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["content"] = content?.Clone()
        };
        if (persistence is not null)
            result["_meta"] = new Dictionary<string, object?> { ["persist"] = persistence };
        else
            result["_meta"] = null;
        return JsonSerializer.SerializeToElement(result);
    }

    public static JsonElement BuildAutomaticApproval(PendingRequest request) =>
        BuildToolApproval(request, PreferredPersistence(request));

    public static JsonElement BuildToolApproval(PendingRequest request, string? persistence)
    {
        if (!IsToolApproval(request))
            throw new ArgumentException("The elicitation is not an MCP tool approval.");
        if (!TryBuildAutomaticApprovalContent(request, persistence, out var content))
            throw new InvalidDataException("The MCP tool approval form requires information that cannot be inferred safely.");
        return BuildResult(request, "accept", content, persistence);
    }

    private static bool TryBuildAutomaticApprovalContent(
        PendingRequest request,
        string? persistence,
        out JsonElement content)
    {
        content = default;
        if (!request.Params.TryGetProperty("requestedSchema", out var schema) ||
            schema.ValueKind != JsonValueKind.Object)
            return false;
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            content = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
            return true;
        }

        var required = schema.TryGetProperty("required", out var requiredElement) &&
                       requiredElement.ValueKind == JsonValueKind.Array
            ? requiredElement.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            if (!required.Contains(property.Name)) continue;
            if (!TryAutomaticValue(property.Name, property.Value, persistence, out var value)) return false;
            values[property.Name] = value;
        }
        content = JsonSerializer.SerializeToElement(values);
        return true;
    }

    private static bool TryAutomaticValue(
        string name,
        JsonElement schema,
        string? persistence,
        out JsonElement value)
    {
        value = default;
        if (schema.ValueKind != JsonValueKind.Object) return false;
        if (schema.TryGetProperty("const", out var constant))
        {
            if (!IsCompatibleApprovalValue(constant, persistence)) return false;
            value = constant.Clone();
            return true;
        }
        if (schema.TryGetProperty("default", out var defaultValue))
        {
            if (!IsCompatibleApprovalValue(defaultValue, persistence)) return false;
            value = defaultValue.Clone();
            return true;
        }
        if (schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            var ranked = choices.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Where(item => IsCompatibleApprovalValue(item, persistence))
                .Select(item => (Element: item.Clone(), Score: ApprovalChoiceScore(item.GetString(), persistence)))
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            if (ranked.Score > 0)
            {
                value = ranked.Element;
                return true;
            }
            return false;
        }
        var type = Text(schema, "type");
        if (type == "boolean" &&
            (name.Contains("approve", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("allow", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("confirm", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("consent", StringComparison.OrdinalIgnoreCase)))
        {
            value = JsonSerializer.SerializeToElement(true);
            return true;
        }
        return false;
    }

    private static int ApprovalChoiceScore(string? value, string? persistence)
    {
        var normalized = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (normalized.Contains("deny") || normalized.Contains("decline") || normalized.Contains("reject") ||
            normalized.Contains("cancel") || normalized == "no") return -1000;
        var target = persistence is "always" or "session" ? persistence : "once";
        var score = normalized.Contains(target, StringComparison.Ordinal) ? 100 : 0;
        if (target == "always" && normalized.Contains("session", StringComparison.Ordinal)) score = Math.Max(score, 80);
        if (normalized.Contains("once", StringComparison.Ordinal)) score = Math.Max(score, 60);
        if (normalized is "approve" or "approved" or "allow" or "allowed" or "accept" or "accepted" or "yes")
            score = Math.Max(score, 75);
        if (normalized.Contains("allow", StringComparison.Ordinal) || normalized.Contains("approve", StringComparison.Ordinal) ||
            normalized.Contains("accept", StringComparison.Ordinal)) score += 30;
        if (normalized is "continue" or "proceed") score = Math.Max(score, 50);
        return score;
    }

    private static bool IsCompatibleApprovalValue(JsonElement value, string? persistence)
    {
        if (value.ValueKind != JsonValueKind.String) return true;
        var scope = ApprovalValuePersistence(value.GetString());
        return persistence switch
        {
            null => scope is null or "once",
            "session" => scope is null or "once" or "session",
            "always" => true,
            _ => false
        };
    }

    private static bool TryValidateApprovalPersistence(
        JsonElement value,
        string? persistence,
        string path,
        out string error)
    {
        error = "";
        if (value.ValueKind == JsonValueKind.String && !IsCompatibleApprovalValue(value, persistence))
        {
            error = $"{path} exceeds the selected approval duration.";
            return false;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (!TryValidateApprovalPersistence(property.Value, persistence, $"{path}.{property.Name}", out error))
                    return false;
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (!TryValidateApprovalPersistence(item, persistence, $"{path}[{index}]", out error)) return false;
                index++;
            }
        }
        return true;
    }

    private static string? ApprovalValuePersistence(string? value)
    {
        var normalized = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (normalized.Contains("always") || normalized.Contains("permanent") || normalized.Contains("forever") ||
            normalized.Contains("persist") || normalized.Contains("remember") || normalized.Contains("device")) return "always";
        if (normalized.Contains("session") || normalized.Contains("task")) return "session";
        if (normalized.Contains("once") || normalized.Contains("turn") || normalized.Contains("onetime")) return "once";
        return null;
    }

    private static bool TryValidateSchemaValue(
        JsonElement value,
        JsonElement schema,
        string path,
        out string error)
    {
        error = "";
        if (schema.ValueKind != JsonValueKind.Object) return true;

        if (schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array &&
            !choices.EnumerateArray().Any(choice => JsonValuesEqual(choice, value)))
        {
            error = $"{path} must be one of the allowed values.";
            return false;
        }
        if (!MatchesConstChoices(value, schema, "oneOf") || !MatchesConstChoices(value, schema, "anyOf"))
        {
            error = $"{path} must be one of the allowed values.";
            return false;
        }
        if (schema.TryGetProperty("const", out var constant) && !JsonValuesEqual(constant, value))
        {
            error = $"{path} must equal the required constant value.";
            return false;
        }

        if (!MatchesSchemaType(value, schema))
        {
            error = $"{path} has the wrong JSON type.";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in required.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var name = item.GetString();
                    if (!string.IsNullOrEmpty(name) && !value.TryGetProperty(name, out _))
                    {
                        error = $"{path}.{name} is required.";
                        return false;
                    }
                }
            }
            if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (!value.TryGetProperty(property.Name, out var propertyValue)) continue;
                    if (!TryValidateSchemaValue(propertyValue, property.Value, $"{path}.{property.Name}", out error))
                        return false;
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            if (Integer(schema, "minItems") is { } minItems && count < minItems)
            {
                error = $"{path} requires at least {minItems} items.";
                return false;
            }
            if (Integer(schema, "maxItems") is { } maxItems && count > maxItems)
            {
                error = $"{path} allows at most {maxItems} items.";
                return false;
            }
            if (schema.TryGetProperty("items", out var itemSchema))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (!TryValidateSchemaValue(item, itemSchema, $"{path}[{index}]", out error)) return false;
                    index++;
                }
            }
        }
        return true;
    }

    private static bool MatchesConstChoices(JsonElement value, JsonElement schema, string propertyName)
    {
        if (!schema.TryGetProperty(propertyName, out var variants) || variants.ValueKind != JsonValueKind.Array)
            return true;
        var options = variants.EnumerateArray().ToArray();
        if (options.Length == 0 || options.Any(option =>
                option.ValueKind != JsonValueKind.Object || !option.TryGetProperty("const", out _))) return true;
        return options.Any(option => JsonValuesEqual(option.GetProperty("const"), value));
    }

    private static bool MatchesSchemaType(JsonElement value, JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type)) return true;
        if (type.ValueKind == JsonValueKind.String) return MatchesType(value, type.GetString());
        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && MatchesType(value, item.GetString()));
        return true;
    }

    private static bool JsonValuesEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number &&
                   left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber) &&
                   leftNumber == rightNumber;
        return left.ValueKind switch
        {
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count() &&
                                    left.EnumerateObject().All(property =>
                                        right.TryGetProperty(property.Name, out var other) &&
                                        JsonValuesEqual(property.Value, other)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() &&
                                   left.EnumerateArray().Zip(right.EnumerateArray())
                                       .All(pair => JsonValuesEqual(pair.First, pair.Second)),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.TryGetDecimal(out var leftNumber) &&
                                    right.TryGetDecimal(out var rightNumber) && leftNumber == rightNumber,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => left.GetRawText().Equals(right.GetRawText(), StringComparison.Ordinal)
        };
    }

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" or "secret" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static int? Integer(JsonElement schema, string propertyName) =>
        schema.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number) && number >= 0
            ? number
            : null;

    private static bool TryMeta(JsonElement parameters, out JsonElement meta)
    {
        meta = default;
        if (parameters.ValueKind != JsonValueKind.Object) return false;
        return parameters.TryGetProperty("_meta", out meta) && meta.ValueKind == JsonValueKind.Object;
    }

    private static string? Text(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] NormalizePersistence(IEnumerable<string?> values) => values
        .Where(value => value is "session" or "always")
        .Cast<string>()
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
