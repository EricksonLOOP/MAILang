using System.Collections.Immutable;
using System.Text.Json;
using Mail.Compiler.Ast;
using Mail.Contracts;

namespace Mail.Runtime;

internal static class DeclaredSchema
{
    public static string ToJson(TypeRef type, IReadOnlyDictionary<string, SchemaDecl> schemas) =>
        JsonSerializer.Serialize(Build(type, schemas, new HashSet<string>()));

    private static object Build(TypeRef type, IReadOnlyDictionary<string, SchemaDecl> schemas, HashSet<string> path)
    {
        if (type is PrimitiveTypeRef p)
            return new { type = p.Kind switch { PrimitiveKind.String => "string", PrimitiveKind.Bool => "boolean", _ => "integer" } };
        var name = ((NamedTypeRef)type).Name;
        if (!path.Add(name)) throw new InvalidOperationException("Recursive schemas are unsupported.");
        var fields = schemas[name].Fields;
        var properties = fields.ToDictionary(f => f.Name, f => Build(f.Type, schemas, path));
        path.Remove(name);
        return new { type = "object", properties, required = fields.Select(f => f.Name).ToArray(), additionalProperties = false };
    }

    public static MailValue Parse(JsonElement element, TypeRef type, IReadOnlyDictionary<string, SchemaDecl> schemas)
    {
        if (type is NamedTypeRef named)
        {
            if (element.ValueKind != JsonValueKind.Object) throw new InvalidProviderResponseException("Expected schema object.");
            var fields = schemas[named.Name].Fields;
            var values = ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var field = fields.FirstOrDefault(f => f.Name == property.Name);
                if (field is null || values.ContainsKey(property.Name)) throw new InvalidProviderResponseException("Unknown or duplicate schema field.");
                values.Add(property.Name, Parse(property.Value, field.Type, schemas));
            }
            if (values.Count != fields.Count) throw new InvalidProviderResponseException("Required schema field missing.");
            return new MailSchema(named.Name, values.ToImmutable());
        }
        return ((PrimitiveTypeRef)type).Kind switch
        {
            PrimitiveKind.String when element.ValueKind == JsonValueKind.String => new MailString(element.GetString()!),
            PrimitiveKind.Bool when element.ValueKind is JsonValueKind.True or JsonValueKind.False => new MailBool(element.GetBoolean()),
            PrimitiveKind.Int when element.ValueKind == JsonValueKind.Number && !element.GetRawText().Contains('.') && !element.GetRawText().Contains('e') && !element.GetRawText().Contains('E') && element.TryGetInt64(out var number) => new MailInt(number),
            _ => throw new InvalidProviderResponseException("Value does not match declared MAIL type.")
        };
    }
}
