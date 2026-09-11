using System.Text.Json;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Mcp;

/// <summary>Validates a call's arguments against a tool's declared JSON Schema document — a minimal,
/// structural check (required properties present; a present property's JSON kind matches its declared
/// <c>type</c>) rather than a full JSON Schema implementation, which the schemas D5 tools declare
/// (built by <c>McpStartupValidation</c>'s own walk of the same document shape) do not need.</summary>
internal static class ToolArgumentSchema
{
    /// <summary>Whether <paramref name="arguments"/> satisfies <paramref name="schema"/>.</summary>
    /// <param name="schema">The tool's declared parameter schema.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns><see langword="true"/> when every declared <c>required</c> property is present and
    /// every present property whose schema names a <c>type</c> matches it.</returns>
    internal static bool IsValid(JsonElement schema, IDictionary<string, JsonElement> arguments)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String
                    && name.GetString() is { } requiredName
                    && !arguments.ContainsKey(requiredName))
                {
                    return false;
                }
            }
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (!arguments.TryGetValue(property.Name, out var value))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("type", out var declaredType)
                    && declaredType.ValueKind == JsonValueKind.String
                    && !MatchesType(value, declaredType.GetString()))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" or "integer" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };
}

/// <summary>Resolves the resource a call is authorized against (S15.5). A tool's schema names a
/// resource parameter by declaring a <c>resourceId</c> property; a tool that names none is authorized
/// at the tool level. This is the module's own convention — the contract states only that a schema
/// "names a resource parameter" without fixing its name, and <c>resourceId</c> is the name this slice
/// picked.</summary>
internal static class ToolResourceScope
{
    /// <summary>The declared name of the resource-id parameter, by this module's own convention.</summary>
    internal const string ResourceIdParameter = "resourceId";

    /// <summary>Resolves the resource a call names, when its schema declares one.</summary>
    /// <param name="schema">The tool's declared parameter schema.</param>
    /// <param name="tool">The tool being called — becomes the resource's <see cref="ResourceRef.Type"/>,
    /// since a tool's arguments carry no separate resource-type parameter of their own.</param>
    /// <param name="arguments">The call's arguments, already validated against <paramref name="schema"/>.</param>
    /// <returns>The resource the call names, or <see langword="null"/> when the schema names no
    /// resource parameter.</returns>
    internal static ResourceRef? TryResolve(
        JsonElement schema, ToolName tool, IDictionary<string, JsonElement> arguments)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty(ResourceIdParameter, out _))
        {
            return null;
        }

        if (arguments.TryGetValue(ResourceIdParameter, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return new ResourceRef(tool.Value, value.GetString()!);
        }

        return null;
    }
}
