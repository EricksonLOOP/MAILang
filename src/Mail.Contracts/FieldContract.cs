using System.Collections.Immutable;

namespace Mail.Contracts;

public enum MailTypeKind { String, Bool, Int, Decimal, List, Schema, Enum }

public sealed record FieldContract(
    string Name,
    MailTypeKind Kind,
    bool Required,
    bool Nullable = false,
    string? SchemaTypeName = null,
    string? EnumTypeName = null,
    ImmutableArray<string>? EnumSymbols = null,
    MailTypeKind? ElementKind = null,
    string? ElementTypeName = null);
