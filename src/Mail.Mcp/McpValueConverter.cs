using System.Collections.Immutable;
using System.Text.Json;
using Mail.Contracts;

namespace Mail.Mcp;

// Converts between MAIL runtime values and JSON (System.Text.Json) for MCP calls.
//
// ToArguments:      MailSchema  → argument dict for client.CallToolAsync()
// FromJsonElement:  JsonElement → MailSchema using declared output FieldContract[]
// FromTextJson:     string(JSON text) → MailSchema, same rules as FromJsonElement
//
// FromJsonElement throws McpToolException when:
//   - A required output field is absent from the JSON
//   - A field value has an incompatible JSON kind
//   - The JSON is not an object
//
// Schema-typed (Kind.Schema) fields are recursively converted using heuristic type inference.
// Downstream MailValueValidator enforces the declared field contracts.
public static class McpValueConverter
{
    public static Dictionary<string, object?> ToArguments(MailSchema input)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in input.Fields)
            dict[name] = ToJsonValue(value);
        return dict;
    }

    public static MailSchema FromJsonElement(JsonElement json, FieldContract[] outputContracts, string toolName)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new McpToolException(
                $"MCP tool '{toolName}' returned a non-object JSON value ({json.ValueKind}).");

        var fields = ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);

        foreach (var contract in outputContracts)
        {
            if (json.TryGetProperty(contract.Name, out var fieldEl))
                fields[contract.Name] = ConvertField(fieldEl, contract, toolName);
            else if (contract.Required)
                throw new McpToolException(
                    $"MCP tool '{toolName}' response is missing required field '{contract.Name}'.");
            // Optional absent fields: omitted from dict, consistent with MailValueValidator
        }

        return new MailSchema($"Mcp{ToPascal(toolName)}Output", fields.ToImmutable());
    }

    public static MailSchema FromTextJson(string textContent, FieldContract[] outputContracts, string toolName)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(textContent); }
        catch (JsonException ex)
        {
            throw new McpToolException(
                $"MCP tool '{toolName}' text content is not valid JSON: {ex.Message}");
        }

        using (doc)
            return FromJsonElement(doc.RootElement.Clone(), outputContracts, toolName);
    }

    private static MailValue ConvertField(JsonElement el, FieldContract contract, string toolName)
    {
        if (el.ValueKind == JsonValueKind.Null)
        {
            if (!contract.Nullable)
                throw new McpToolException(
                    $"MCP tool '{toolName}' returned null for non-nullable field '{contract.Name}'.");
            return new MailNull();
        }

        return contract.Kind switch
        {
            MailTypeKind.String  => ConvertString(el,   contract.Name, toolName),
            MailTypeKind.Bool    => ConvertBool(el,     contract.Name, toolName),
            MailTypeKind.Int     => ConvertInt(el,      contract.Name, toolName),
            MailTypeKind.Decimal => ConvertDecimal(el,  contract.Name, toolName),
            MailTypeKind.Enum    => ConvertEnum(el,     contract,      toolName),
            MailTypeKind.List    => ConvertList(el,     contract,      toolName),
            MailTypeKind.Schema  => ConvertSchema(el,   contract,      toolName),
            _ => throw new McpToolException(
                $"MCP tool '{toolName}' field '{contract.Name}': unsupported kind {contract.Kind}.")
        };
    }

    private static MailValue ConvertString(JsonElement el, string name, string toolName) =>
        el.ValueKind == JsonValueKind.String
            ? new MailString(el.GetString()!)
            : throw new McpToolException(
                $"MCP tool '{toolName}' field '{name}': expected string, got {el.ValueKind}.");

    private static MailValue ConvertBool(JsonElement el, string name, string toolName) =>
        el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? new MailBool(el.GetBoolean())
            : throw new McpToolException(
                $"MCP tool '{toolName}' field '{name}': expected boolean, got {el.ValueKind}.");

    private static MailValue ConvertInt(JsonElement el, string name, string toolName)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var i))
            return new MailInt(i);
        throw new McpToolException(
            $"MCP tool '{toolName}' field '{name}': expected integer, got {el.ValueKind}.");
    }

    private static MailValue ConvertDecimal(JsonElement el, string name, string toolName) =>
        el.ValueKind == JsonValueKind.Number
            ? new MailDecimal(el.GetDecimal())
            : throw new McpToolException(
                $"MCP tool '{toolName}' field '{name}': expected number, got {el.ValueKind}.");

    private static MailValue ConvertEnum(JsonElement el, FieldContract contract, string toolName)
    {
        if (el.ValueKind != JsonValueKind.String)
            throw new McpToolException(
                $"MCP tool '{toolName}' field '{contract.Name}': expected string for enum, got {el.ValueKind}.");
        // Symbol validation against EnumSymbols is enforced by MailValueValidator downstream
        return new MailEnum(contract.EnumTypeName ?? contract.Name, el.GetString()!);
    }

    private static MailValue ConvertList(JsonElement el, FieldContract contract, string toolName)
    {
        if (el.ValueKind != JsonValueKind.Array)
            throw new McpToolException(
                $"MCP tool '{toolName}' field '{contract.Name}': expected array, got {el.ValueKind}.");

        var elements = ImmutableList.CreateBuilder<MailValue>();
        int index = 0;
        foreach (var item in el.EnumerateArray())
        {
            elements.Add(ConvertElement(item, contract, index, toolName));
            index++;
        }
        return new MailList(elements.ToImmutable());
    }

    private static MailValue ConvertElement(JsonElement el, FieldContract contract, int index, string toolName)
    {
        string path = $"{contract.Name}[{index}]";

        if (el.ValueKind == JsonValueKind.Null)
            return new MailNull();

        return contract.ElementKind switch
        {
            MailTypeKind.String  => el.ValueKind == JsonValueKind.String
                ? new MailString(el.GetString()!)
                : throw new McpToolException($"MCP tool '{toolName}' field '{path}': expected string."),

            MailTypeKind.Bool    => el.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new MailBool(el.GetBoolean())
                : throw new McpToolException($"MCP tool '{toolName}' field '{path}': expected boolean."),

            MailTypeKind.Int     => el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var iv)
                ? new MailInt(iv)
                : throw new McpToolException($"MCP tool '{toolName}' field '{path}': expected integer."),

            MailTypeKind.Decimal => el.ValueKind == JsonValueKind.Number
                ? new MailDecimal(el.GetDecimal())
                : throw new McpToolException($"MCP tool '{toolName}' field '{path}': expected number."),

            MailTypeKind.Enum    => el.ValueKind == JsonValueKind.String
                ? new MailEnum(contract.ElementTypeName ?? "unknown", el.GetString()!)
                : throw new McpToolException($"MCP tool '{toolName}' field '{path}': expected string for enum."),

            MailTypeKind.Schema  => el.ValueKind == JsonValueKind.Object
                ? HeuristicSchema(el, contract.ElementTypeName ?? $"Mcp{ToPascal(contract.Name)}Item", path, toolName)
                : throw new McpToolException($"MCP tool '{toolName}' field '{path}': expected object for schema."),

            null or _ => throw new McpToolException(
                $"MCP tool '{toolName}' field '{path}': unsupported element kind {contract.ElementKind}.")
        };
    }

    private static MailValue ConvertSchema(JsonElement el, FieldContract contract, string toolName)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new McpToolException(
                $"MCP tool '{toolName}' field '{contract.Name}': expected object for schema, got {el.ValueKind}.");

        var typeName = contract.SchemaTypeName ?? $"Mcp{ToPascal(contract.Name)}";
        return HeuristicSchema(el, typeName, contract.Name, toolName);
    }

    // Heuristically converts a JSON object to MailSchema when no field-level FieldContract[] is available.
    // Used for Schema-typed fields and nested objects within lists.
    // MailValueValidator enforces the declared schema contracts after this conversion.
    private static MailSchema HeuristicSchema(JsonElement el, string typeName, string path, string toolName)
    {
        var fields = el.EnumerateObject()
            .ToImmutableDictionary(
                p => p.Name,
                p => HeuristicValue(p.Value, $"{path}.{p.Name}", toolName));
        return new MailSchema(typeName, fields);
    }

    private static MailValue HeuristicValue(JsonElement el, string path, string toolName) => el.ValueKind switch
    {
        JsonValueKind.String  => new MailString(el.GetString()!),
        JsonValueKind.True    => new MailBool(true),
        JsonValueKind.False   => new MailBool(false),
        JsonValueKind.Null    => new MailNull(),
        JsonValueKind.Number  => el.TryGetInt64(out var i) ? new MailInt(i) : new MailDecimal(el.GetDecimal()),
        JsonValueKind.Array   => new MailList(ImmutableList.Create(
                                    el.EnumerateArray()
                                      .Select(e => HeuristicValue(e, path, toolName))
                                      .ToArray())),
        JsonValueKind.Object  => HeuristicSchema(el, "mcp_nested", path, toolName),
        _ => throw new McpToolException(
            $"MCP tool '{toolName}' field '{path}': unsupported JSON value kind {el.ValueKind}.")
    };

    private static object? ToJsonValue(MailValue value) => value switch
    {
        MailString s  => s.Value,
        MailBool b    => b.Value,
        MailInt i     => i.Value,
        MailDecimal d => d.Value,
        MailEnum e    => e.Symbol,
        MailNull      => null,
        MailList l    => l.Elements.Select(ToJsonValue).ToArray(),
        MailSchema s  => s.Fields.ToDictionary(kv => kv.Key, kv => ToJsonValue(kv.Value)),
        _ => throw new NotSupportedException($"Unsupported MailValue type: {value.GetType().Name}")
    };

    private static string ToPascal(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
