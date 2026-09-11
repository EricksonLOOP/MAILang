using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Contracts;

namespace Mail.Runtime;

public static class MailValueValidator
{
    public static void Validate(MailValue value, TypeRef expectedType, ValidatedPlan plan) =>
        ValidateWithDicts(value, expectedType, plan.Schemas, plan.Enums);

    public static void ValidateToolOutput(
        MailSchema output,
        IReadOnlyList<FieldDecl> outputDecl,
        ValidatedPlan plan,
        string toolName)
        => ValidateToolOutputCore(output, outputDecl, plan.Schemas, plan.Enums, toolName);

    public static void ValidateToolOutput(
        MailSchema output,
        IReadOnlyList<FieldDecl> outputDecl,
        IReadOnlyDictionary<string, SchemaDecl> schemas,
        IReadOnlyDictionary<string, EnumDecl> enums,
        string toolName)
        => ValidateToolOutputCore(output, outputDecl, schemas, enums, toolName);

    private static void ValidateToolOutputCore(
        MailSchema output,
        IReadOnlyList<FieldDecl> outputDecl,
        IReadOnlyDictionary<string, SchemaDecl> schemas,
        IReadOnlyDictionary<string, EnumDecl> enums,
        string toolName)
    {
        foreach (var fd in outputDecl)
        {
            if (!output.Fields.TryGetValue(fd.Name, out var val))
            {
                if (!fd.Optional)
                    throw new ArgumentValidationException(toolName, fd.Name,
                        "Required output field is missing.");
                continue;
            }
            try { ValidateWithDicts(val, fd.Type, schemas, enums); }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentValidationException(toolName, fd.Name,
                    $"Output field type mismatch: {ex.Message}");
            }
        }
    }

    private static void ValidateWithDicts(
        MailValue value,
        TypeRef expectedType,
        IReadOnlyDictionary<string, SchemaDecl> schemas,
        IReadOnlyDictionary<string, EnumDecl> enums)
    {
        switch (expectedType)
        {
            case PrimitiveTypeRef p:
                bool primOk = p.Kind switch
                {
                    PrimitiveKind.String  => value is MailString,
                    PrimitiveKind.Bool    => value is MailBool,
                    PrimitiveKind.Int     => value is MailInt,
                    PrimitiveKind.Decimal => value is MailDecimal,
                    _ => false
                };
                if (!primOk)
                    throw new InvalidOperationException(
                        $"Expected {p.Kind} but got {value.GetType().Name}.");
                return;

            case NullableTypeRef n:
                if (value is MailNull) return;
                ValidateWithDicts(value, n.Inner, schemas, enums);
                return;

            case ListTypeRef l:
                if (value is not MailList list)
                    throw new InvalidOperationException(
                        $"Expected List but got {value.GetType().Name}.");
                foreach (var elem in list.Elements)
                    ValidateWithDicts(elem, l.ElementType, schemas, enums);
                return;

            case NamedTypeRef named when enums.TryGetValue(named.Name, out var enumDecl):
                if (value is not MailEnum ev ||
                    !enumDecl.Symbols.Contains(ev.Symbol, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"Expected enum '{named.Name}' with valid symbol, got " +
                        $"{value.GetType().Name}" +
                        (value is MailEnum me ? $" '{me.Symbol}'" : "") + ".");
                return;

            case NamedTypeRef named when schemas.TryGetValue(named.Name, out var schemaDecl):
                if (value is not MailSchema schema)
                    throw new InvalidOperationException(
                        $"Expected Schema '{named.Name}' but got {value.GetType().Name}.");
                foreach (var field in schemaDecl.Fields)
                {
                    if (!schema.Fields.TryGetValue(field.Name, out var fieldVal))
                    {
                        if (!field.Optional)
                            throw new InvalidOperationException(
                                $"Required field '{field.Name}' missing in schema '{named.Name}'.");
                        continue;
                    }
                    ValidateWithDicts(fieldVal, field.Type, schemas, enums);
                }
                return;

            default:
                throw new InvalidOperationException(
                    $"Unresolvable TypeRef '{expectedType.GetType().Name}'.");
        }
    }
}
