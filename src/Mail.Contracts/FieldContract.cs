namespace Mail.Contracts;

public enum MailTypeKind { String, Bool, Int, Schema }

public sealed record FieldContract(
    string Name,
    MailTypeKind Kind,
    bool Required,
    string? SchemaTypeName = null);
