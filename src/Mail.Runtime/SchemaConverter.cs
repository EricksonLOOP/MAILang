using Mail.Contracts;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mail.Runtime;

/// <summary>
/// Converts between MailValue and JSON for provider boundaries.
/// </summary>
public static class SchemaConverter
{
    public static string ToJson(MailValue value)
    {
        var node = ToJsonNode(value);
        return node.ToJsonString();
    }

    public static MailSchema FromJson(string json, string typeName, FieldContract[] contract)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false });
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Expected JSON object for schema '{typeName}', got {root.ValueKind}.");

        // Check for duplicate properties — JsonDocument does not deduplicate
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
            if (!seen.Add(prop.Name))
                throw new InvalidProviderResponseException($"Duplicate property '{prop.Name}' in response JSON.");

        var fields = ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
        var validator = new ArgumentValidator();

        var rawArgs = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
            rawArgs[prop.Name] = prop.Value.Clone(); // Clone to survive document disposal

        // Reuse ArgumentValidator logic
        var validated = validator.Validate(rawArgs.ToImmutable(), contract, typeName);
        return new MailSchema(typeName, validated.Fields);
    }

    private static JsonNode ToJsonNode(MailValue value) => value switch
    {
        MailString s  => JsonValue.Create(s.Value)!,
        MailBool b    => JsonValue.Create(b.Value)!,
        MailInt i     => JsonValue.Create(i.Value)!,
        MailSchema sc => SchemaToObject(sc),
        _ => throw new InvalidOperationException($"Cannot convert {value.GetType().Name} to JSON."),
    };

    private static JsonObject SchemaToObject(MailSchema schema)
    {
        var obj = new JsonObject();
        foreach (var (key, val) in schema.Fields)
            obj[key] = ToJsonNode(val);
        return obj;
    }
}
