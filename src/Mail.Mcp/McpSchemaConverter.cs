using System.Collections.Immutable;
using System.Text.Json;
using Mail.Contracts;

namespace Mail.Mcp;

// Converts MCP tool JSON Schema (inputSchema / outputSchema from tools/list) into MAIL FieldContract[].
// Only the JSON Schema subset representable in MAIL is supported.
// Unsupported constructs (anyOf, oneOf, allOf, $ref, List<List<T>>) throw McpBindingException.
public static class McpSchemaConverter
{
    public static FieldContract[] FromJsonSchema(JsonElement objectSchema)
    {
        if (objectSchema.ValueKind != JsonValueKind.Object)
            throw new McpBindingException("MCP tool schema is not a JSON object.");

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (objectSchema.TryGetProperty("required", out var reqEl) &&
            reqEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in reqEl.EnumerateArray())
                if (r.GetString() is string s)
                    required.Add(s);
        }

        if (!objectSchema.TryGetProperty("properties", out var propsEl) ||
            propsEl.ValueKind != JsonValueKind.Object)
            return [];

        var contracts = new List<FieldContract>();
        foreach (var prop in propsEl.EnumerateObject())
            contracts.Add(ConvertField(prop.Name, prop.Value, required.Contains(prop.Name)));

        return contracts.ToArray();
    }

    private static FieldContract ConvertField(string name, JsonElement schema, bool required)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            throw new McpBindingException($"Field '{name}': schema must be an object.");

        RejectUnsupported(name, schema);

        // Enum (string values only)
        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            var symbols = ReadStringArray(enumEl, $"Field '{name}' enum");
            return new FieldContract(name, MailTypeKind.Enum, required,
                EnumSymbols: ImmutableArray.CreateRange(symbols));
        }

        var (typeName, nullable) = ResolveType(name, schema);

        return typeName switch
        {
            "string"  => new FieldContract(name, MailTypeKind.String,  required, nullable),
            "boolean" => new FieldContract(name, MailTypeKind.Bool,    required, nullable),
            "integer" => new FieldContract(name, MailTypeKind.Int,     required, nullable),
            "number"  => new FieldContract(name, MailTypeKind.Decimal, required, nullable),
            "array"   => ConvertArrayField(name, schema, required, nullable),
            "object"  => new FieldContract(name, MailTypeKind.Schema, required, nullable,
                             SchemaTypeName: $"Mcp{ToPascal(name)}"),
            _ => throw new McpBindingException($"Field '{name}': unsupported JSON Schema type '{typeName}'.")
        };
    }

    private static FieldContract ConvertArrayField(string name, JsonElement schema, bool required, bool nullable)
    {
        if (!schema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            throw new McpBindingException($"Field '{name}': array type requires an 'items' object schema.");

        RejectUnsupported($"{name}.items", items);

        // Reject List<List<T>>
        if (items.TryGetProperty("type", out var itemTypeEl) && itemTypeEl.GetString() == "array")
            throw new McpBindingException(
                $"Field '{name}': List<List<T>> is not supported in MAIL. Flatten the structure.");

        // Enum elements
        if (items.TryGetProperty("enum", out var itemEnum) && itemEnum.ValueKind == JsonValueKind.Array)
        {
            var symbols = ReadStringArray(itemEnum, $"Field '{name}.items' enum");
            return new FieldContract(name, MailTypeKind.List, required, nullable,
                ElementKind: MailTypeKind.Enum,
                ElementEnumSymbols: ImmutableArray.CreateRange(symbols));
        }

        var (itemTypeName, _) = ResolveType($"{name}.items", items);

        return itemTypeName switch
        {
            "string"  => new FieldContract(name, MailTypeKind.List, required, nullable, ElementKind: MailTypeKind.String),
            "boolean" => new FieldContract(name, MailTypeKind.List, required, nullable, ElementKind: MailTypeKind.Bool),
            "integer" => new FieldContract(name, MailTypeKind.List, required, nullable, ElementKind: MailTypeKind.Int),
            "number"  => new FieldContract(name, MailTypeKind.List, required, nullable, ElementKind: MailTypeKind.Decimal),
            "object"  => new FieldContract(name, MailTypeKind.List, required, nullable,
                             ElementKind: MailTypeKind.Schema,
                             ElementTypeName: $"Mcp{ToPascal(name)}Item"),
            _ => throw new McpBindingException(
                $"Field '{name}.items': unsupported array element type '{itemTypeName}'.")
        };
    }

    // Returns (typeName, isNullable). Handles both string type and ["type","null"] array.
    private static (string type, bool nullable) ResolveType(string fieldPath, JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var typeEl))
            throw new McpBindingException(
                $"Field '{fieldPath}': schema has no 'type' and no 'enum'. " +
                "Use a concrete type or provide an enum list.");

        if (typeEl.ValueKind == JsonValueKind.String)
            return (typeEl.GetString()!, false);

        if (typeEl.ValueKind == JsonValueKind.Array)
        {
            var types = typeEl.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => s != null)
                .Cast<string>()
                .ToList();

            var nonNull = types.Where(t => t != "null").ToList();
            bool hasNull = types.Contains("null");

            if (nonNull.Count != 1)
                throw new McpBindingException(
                    $"Field '{fieldPath}': type array must have exactly one non-null type. " +
                    $"Found: [{string.Join(", ", types)}]. Use anyOf for unions (not supported).");

            return (nonNull[0], hasNull);
        }

        throw new McpBindingException(
            $"Field '{fieldPath}': 'type' must be a string or an array of strings.");
    }

    private static void RejectUnsupported(string path, JsonElement schema)
    {
        foreach (var unsupported in new[] { "anyOf", "oneOf", "allOf", "$ref" })
        {
            if (schema.TryGetProperty(unsupported, out _))
                throw new McpBindingException(
                    $"Field '{path}': '{unsupported}' is not supported. " +
                    "Declare the MAIL type explicitly in the .mail file.");
        }
    }

    private static string[] ReadStringArray(JsonElement arrayEl, string context)
    {
        var items = new List<string>();
        foreach (var el in arrayEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
                throw new McpBindingException(
                    $"{context}: contains a non-string value (got {el.ValueKind}). Only string enums are supported.");
            items.Add(el.GetString()!);
        }
        return items.ToArray();
    }

    private static string ToPascal(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
