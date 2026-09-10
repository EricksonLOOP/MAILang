using Mail.Contracts;

namespace Mail.Compiler.Ast;

// ── Type references ────────────────────────────────────────────────────────────

public enum PrimitiveKind { String, Bool, Int }

public abstract record TypeRef;
public sealed record PrimitiveTypeRef(PrimitiveKind Kind) : TypeRef;
public sealed record NamedTypeRef(string Name, SourceLocation Location) : TypeRef;

// ── Expressions ───────────────────────────────────────────────────────────────

public abstract record Expr;
public sealed record NameExpr(string Name, SourceLocation Location) : Expr;
public sealed record FieldAccessExpr(Expr Target, string Field, SourceLocation Location) : Expr;

// ── Declarations ──────────────────────────────────────────────────────────────

public sealed record FieldDecl(string Name, TypeRef Type);

public abstract record Declaration(string Name, SourceLocation Location);

public sealed record SchemaDecl(
    string Name,
    IReadOnlyList<FieldDecl> Fields,
    SourceLocation Location) : Declaration(Name, Location);

public sealed record ToolDecl(
    string Name,
    IReadOnlyList<FieldDecl> Input,
    IReadOnlyList<FieldDecl> Output,
    SourceLocation Location) : Declaration(Name, Location);

public sealed record SystemPromptNode(string Text, SourceLocation Location);

public sealed record AgentDecl(
    string Name,
    string LogicalModelName,
    TypeRef OutputType,
    IReadOnlyList<string> AllowedTools,
    SourceLocation Location,
    SystemPromptNode? SystemPrompt = null) : Declaration(Name, Location);

// ── Step bodies ───────────────────────────────────────────────────────────────

public abstract record StepBody;

public sealed record CallBody(
    string ToolName,
    IReadOnlyDictionary<string, Expr> Args) : StepBody;

public sealed record AgentBody(
    string AgentName,
    IReadOnlyList<string> ContextNames) : StepBody;

public sealed record StepDecl(
    string Name,
    StepBody Body,
    string SaveAs,
    SourceLocation Location);

// ── Workflow ──────────────────────────────────────────────────────────────────

public sealed record WorkflowDecl(
    string Name,
    TypeRef InputType,
    TypeRef OutputType,
    IReadOnlyList<StepDecl> Steps,
    Expr Finish,
    SourceLocation Location) : Declaration(Name, Location);

// ── Program ───────────────────────────────────────────────────────────────────

public sealed record ProgramNode(IReadOnlyList<Declaration> Declarations);
