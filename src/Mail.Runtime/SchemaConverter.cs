using Mail.Contracts;
using System.Collections.Immutable;
using System.Globalization;
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
        if (value is MailNull) return "null";
        var node = ToJsonNode(value);
        return node?.ToJsonString() ?? "null";
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

        var rawArgs = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
            rawArgs[prop.Name] = prop.Value.Clone(); // Clone to survive document disposal

        // Reuse ArgumentValidator logic
        var validator = new ArgumentValidator();
        var validated = validator.Validate(rawArgs.ToImmutable(), contract, typeName);
        return new MailSchema(typeName, validated.Fields);
    }

    internal static JsonNode? ToJsonNode(MailValue value) => value switch
    {
        MailNull      => null,
        MailString s  => JsonValue.Create(s.Value)!,
        MailBool b    => JsonValue.Create(b.Value)!,
        MailInt i     => JsonValue.Create(i.Value)!,
        MailDecimal d => JsonValue.Create(FormatDecimal(d.Value))!,
        MailList l    => new JsonArray(l.Elements.Select(e => (JsonNode?)ToJsonNode(e)).ToArray()),
        MailEnum e    => JsonValue.Create(e.Symbol)!,
        MailSchema sc => SchemaToObject(sc),
        _ => throw new InvalidOperationException($"Cannot convert {value.GetType().Name} to JSON."),
    };

    // Fixed-point notation, no exponent. ToString(InvariantCulture) preserves the decimal's internal scale
    // (e.g. 125.50m → "125.50", not "125.5"), which is required for round-trip fidelity.
    private static string FormatDecimal(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static JsonObject SchemaToObject(MailSchema schema)
    {
        var obj = new JsonObject();
        foreach (var (key, val) in schema.Fields)
            obj[key] = val is MailNull ? null : ToJsonNode(val);
        return obj;
    }
}
