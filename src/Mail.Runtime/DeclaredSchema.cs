using System.Collections.Immutable;
using System.Text.Json;
using Mail.Compiler.Ast;
using Mail.Contracts;

namespace Mail.Runtime;

public static class DeclaredSchema
{
    public static string ToJson(
        TypeRef type,
        IReadOnlyDictionary<string, SchemaDecl> schemas,
        IReadOnlyDictionary<string, EnumDecl>? enums = null) =>
        JsonSerializer.Serialize(Build(type, schemas, enums ?? new Dictionary<string, EnumDecl>(), new HashSet<string>()));

    private static object Build(
        TypeRef type,
        IReadOnlyDictionary<string, SchemaDecl> schemas,
        IReadOnlyDictionary<string, EnumDecl> enums,
        HashSet<string> path)
    {
        switch (type)
        {
            case PrimitiveTypeRef p:
                return p.Kind switch
                {
                    PrimitiveKind.String  => new { type = "string" },
                    PrimitiveKind.Bool    => new { type = "boolean" },
                    PrimitiveKind.Int     => new { type = "integer" },
                    PrimitiveKind.Decimal => (object)new { type = "string", format = "decimal" },
                    _                     => new { type = "string" },
                };

            case ListTypeRef lr:
                return new { type = "array", items = Build(lr.ElementType, schemas, enums, path) };

            case NullableTypeRef nr:
                return new { oneOf = new[] { (object)new { type = "null" }, Build(nr.Inner, schemas, enums, path) } };

            case NamedTypeRef named:
                if (enums.TryGetValue(named.Name, out var enumDecl))
                    return new { type = "string", @enum = enumDecl.Symbols.ToArray() };

                if (!path.Add(named.Name)) throw new InvalidOperationException("Recursive schemas are unsupported.");
                var fields = schemas[named.Name].Fields;
                var properties = fields.ToDictionary(f => f.Name, f => Build(f.Type, schemas, enums, path));
                var required = fields.Where(f => !f.Optional).Select(f => f.Name).ToArray();
                path.Remove(named.Name);
                return new { type = "object", properties, required, additionalProperties = false };

            default:
                return new { type = "string" };
        }
    }

    public static MailValue Parse(
        JsonElement element,
        TypeRef type,
        IReadOnlyDictionary<string, SchemaDecl> schemas,
        IReadOnlyDictionary<string, EnumDecl>? enums = null)
    {
        var enumsMap = enums ?? new Dictionary<string, EnumDecl>();

        switch (type)
        {
            case NullableTypeRef nr:
                if (element.ValueKind == JsonValueKind.Null)
                    return new MailNull();
                return Parse(element, nr.Inner, schemas, enumsMap);

            case ListTypeRef lr:
                if (element.ValueKind != JsonValueKind.Array)
                    throw new InvalidProviderResponseException("Expected JSON array for List type.");
                var items = ImmutableList.CreateBuilder<MailValue>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Null)
                        throw new InvalidProviderResponseException("List elements cannot be null.");
                    items.Add(Parse(item, lr.ElementType, schemas, enumsMap));
                }
                return new MailList(items.ToImmutable());

            case NamedTypeRef named when enumsMap.TryGetValue(named.Name, out var enumDecl):
                if (element.ValueKind != JsonValueKind.String)
                    throw new InvalidProviderResponseException($"Expected string for enum '{named.Name}'.");
                var symbol = element.GetString()!;
                if (!enumDecl.Symbols.Contains(symbol, StringComparer.Ordinal))
                    throw new InvalidProviderResponseException($"'{symbol}' is not a valid symbol for enum '{named.Name}'.");
                return new MailEnum(named.Name, symbol);

            case NamedTypeRef named:
                if (element.ValueKind != JsonValueKind.Object)
                    throw new InvalidProviderResponseException("Expected schema object.");
                var schemaFields = schemas[named.Name].Fields;
                var values = ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var field = schemaFields.FirstOrDefault(f => f.Name == property.Name);
                    if (field is null) continue; // skip extra fields the model may include
                    if (values.ContainsKey(property.Name))
                        throw new InvalidProviderResponseException($"Duplicate schema field '{property.Name}'.");
                    values.Add(property.Name, Parse(property.Value, field.Type, schemas, enumsMap));
                }
                // Verify all required fields are present
                foreach (var f in schemaFields)
                    if (!f.Optional && !values.ContainsKey(f.Name))
                        throw new InvalidProviderResponseException($"Required schema field '{f.Name}' is missing.");
                return new MailSchema(named.Name, values.ToImmutable());

            case PrimitiveTypeRef pt:
                return pt.Kind switch
                {
                    PrimitiveKind.String when element.ValueKind == JsonValueKind.String =>
                        new MailString(element.GetString()!),

                    PrimitiveKind.Bool when element.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                        new MailBool(element.GetBoolean()),

                    PrimitiveKind.Int when element.ValueKind == JsonValueKind.Number &&
                        !element.GetRawText().Contains('.') &&
                        !element.GetRawText().Contains('e') &&
                        !element.GetRawText().Contains('E') &&
                        element.TryGetInt64(out var number) =>
                        new MailInt(number),

                    PrimitiveKind.Decimal when element.ValueKind == JsonValueKind.String =>
                        ParseDecimalElement(element),

                    _ => throw new InvalidProviderResponseException("Value does not match declared MAIL type.")
                };

            default:
                throw new InvalidProviderResponseException($"Unsupported TypeRef: {type.GetType().Name}.");
        }
    }

    private static MailDecimal ParseDecimalElement(JsonElement element)
    {
        var raw = element.GetString()!;
        if (raw.Contains('e') || raw.Contains('E'))
            throw new InvalidProviderResponseException("Decimal string must not use exponent notation.");
        if (!decimal.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign |
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new InvalidProviderResponseException($"'{raw}' is not a valid Decimal string.");
        return new MailDecimal(value);
    }
}
