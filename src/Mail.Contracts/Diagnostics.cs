namespace Mail.Contracts;

public enum DiagnosticSeverity { Error, Warning }

public sealed record SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => $"{File}:{Line}:{Column}";
}

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    SourceLocation Location)
{
    public override string ToString() => $"[{Code}] {Message} ({Location})";
}

public static class DiagnosticCodes
{
    public const string DuplicateName = "MAIL-E001";
    public const string SchemaNotDeclared = "MAIL-E002";
    public const string ToolNotDeclaredInAllow = "MAIL-E003";
    public const string ToolNotDeclaredInCall = "MAIL-E004";
    public const string AgentNotDeclared = "MAIL-E005";
    public const string FieldNotFound = "MAIL-E006";
    public const string BindingNotAvailable = "MAIL-E007";
    public const string DuplicateInScope = "MAIL-E008";
    public const string ArgumentTypeMismatch = "MAIL-E009";
    public const string FinishTypeMismatch = "MAIL-E010";

    public const string StringUnterminated = "MAIL-STRING-UNTERMINATED";
    public const string PromptDuplicate    = "MAIL-PROMPT-DUPLICATE";
    public const string PromptEmpty        = "MAIL-PROMPT-EMPTY";
}
