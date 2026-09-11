using System.Collections.Immutable;
using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Contracts;

namespace Mail.Runtime;

public static class FieldContractBuilder
{
    public static FieldContract FromDecl(FieldDecl fd, ValidatedPlan plan)
    {
        var inner    = fd.Type is NullableTypeRef nr ? nr.Inner : fd.Type;
        var nullable = fd.Type is NullableTypeRef;

        return inner switch
        {
            PrimitiveTypeRef pt => new FieldContract(
                fd.Name, PrimKind(pt.Kind), Required: !fd.Optional, Nullable: nullable),

            ListTypeRef lr => BuildList(fd.Name, lr, nullable, !fd.Optional, plan),

            NamedTypeRef nt when plan.Enums.TryGetValue(nt.Name, out var ed) => new FieldContract(
                fd.Name, MailTypeKind.Enum, Required: !fd.Optional, Nullable: nullable,
                EnumTypeName: nt.Name,
                EnumSymbols: ed.Symbols.ToImmutableArray()),

            NamedTypeRef nt => new FieldContract(
                fd.Name, MailTypeKind.Schema, Required: !fd.Optional, Nullable: nullable,
                SchemaTypeName: nt.Name),

            QualifiedNameTypeRef qr => new FieldContract(
                fd.Name, MailTypeKind.Schema, Required: !fd.Optional, Nullable: nullable,
                SchemaTypeName: $"{qr.Alias}.{qr.Name}"),

            _ => throw new InvalidOperationException(
                $"Unsupported TypeRef '{inner.GetType().Name}' for field '{fd.Name}'.")
        };
    }

    public static FieldContract[] FromDecls(IReadOnlyList<FieldDecl> fds, ValidatedPlan plan) =>
        fds.Select(fd => FromDecl(fd, plan)).ToArray();

    private static FieldContract BuildList(
        string name, ListTypeRef lr, bool nullable, bool required, ValidatedPlan plan)
    {
        var (elemKind, elemTypeName, elemEnumSymbols) = ResolveElement(lr.ElementType, plan);
        return new FieldContract(
            name, MailTypeKind.List, Required: required, Nullable: nullable,
            ElementKind: elemKind,
            ElementTypeName: elemTypeName,
            ElementEnumSymbols: elemEnumSymbols);
    }

    private static (MailTypeKind kind, string? typeName, ImmutableArray<string>? enumSymbols)
        ResolveElement(TypeRef elemType, ValidatedPlan plan)
    {
        return elemType switch
        {
            PrimitiveTypeRef pt => (PrimKind(pt.Kind), null, null),

            NamedTypeRef nt when plan.Enums.TryGetValue(nt.Name, out var ed) =>
                (MailTypeKind.Enum, nt.Name, (ImmutableArray<string>?)ed.Symbols.ToImmutableArray()),

            NamedTypeRef nt => (MailTypeKind.Schema, nt.Name, null),

            ListTypeRef => throw new InvalidOperationException("List<List<T>> is not supported."),

            _ => throw new InvalidOperationException(
                $"Unsupported list element type '{elemType.GetType().Name}'.")
        };
    }

    private static MailTypeKind PrimKind(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.String  => MailTypeKind.String,
        PrimitiveKind.Bool    => MailTypeKind.Bool,
        PrimitiveKind.Int     => MailTypeKind.Int,
        PrimitiveKind.Decimal => MailTypeKind.Decimal,
        _ => throw new InvalidOperationException($"Unknown PrimitiveKind {kind}.")
    };
}
