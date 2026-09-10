using Mail.Contracts;

namespace Mail.Compiler.Ast;

// ── Type references ────────────────────────────────────────────────────────────

public enum PrimitiveKind { String, Bool, Int, Decimal }

public abstract record TypeRef;
public sealed record PrimitiveTypeRef(PrimitiveKind Kind) : TypeRef;
public sealed record NamedTypeRef(string Name, SourceLocation Location) : TypeRef;
public sealed record ListTypeRef(TypeRef ElementType, SourceLocation Location) : TypeRef;
public sealed record NullableTypeRef(TypeRef Inner, SourceLocation Location) : TypeRef;

// ── Expressions ───────────────────────────────────────────────────────────────

public abstract record Expr;
public sealed record NameExpr(string Name, SourceLocation Location) : Expr;
public sealed record FieldAccessExpr(Expr Target, string Field, SourceLocation Location) : Expr;
public sealed record StringLiteralExpr(string Value, SourceLocation Location) : Expr;
public sealed record BoolLiteralExpr(bool Value, SourceLocation Location) : Expr;
public sealed record IntLiteralExpr(long Value, SourceLocation Location) : Expr;
public sealed record NotExpr(Expr Operand, SourceLocation Location) : Expr;

public enum BinaryOp { And, Or, Eq, Ne, Lt, Le, Gt, Ge }
public sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right, SourceLocation Location) : Expr;

// Inline conditional: if Condition then Then else Else
public sealed record ConditionalExpr(Expr Condition, Expr Then, Expr Else, SourceLocation Location) : Expr;

// ── Declarations ──────────────────────────────────────────────────────────────

public sealed record FieldDecl(string Name, TypeRef Type, bool Optional = false);

public abstract record Declaration(string Name, SourceLocation Location);

public sealed record EnumDecl(
    string Name,
    IReadOnlyList<string> Symbols,
    SourceLocation Location) : Declaration(Name, Location);

public sealed record SchemaDecl(
    string Name,
    IReadOnlyList<FieldDecl> Fields,
    SourceLocation Location) : Declaration(Name, Location);

public sealed record ToolDecl(
    string Name,
    IReadOnlyList<FieldDecl> Input,
    IReadOnlyList<FieldDecl> Output,
    SourceLocation Location,
    Expr? RequireInput = null,
    Expr? RequireOutput = null) : Declaration(Name, Location);

public sealed record SystemPromptNode(string Text, SourceLocation Location);

// Entry in agent's tools { allow ... } block; WhenGuard is null for unconditional allow
public sealed record AllowedToolEntry(string ToolName, Expr? WhenGuard);

public sealed record AgentDecl(
    string Name,
    string LogicalModelName,
    TypeRef? InputType,
    TypeRef OutputType,
    IReadOnlyList<AllowedToolEntry> AllowedTools,
    SourceLocation Location,
    SystemPromptNode? SystemPrompt = null,
    Expr? RequireInput = null,
    Expr? RequireOutput = null) : Declaration(Name, Location);

// ── Step bodies ───────────────────────────────────────────────────────────────

public abstract record StepBody;

public sealed record CallBody(
    string ToolName,
    IReadOnlyDictionary<string, Expr> Args) : StepBody;

public sealed record AgentBody(
    string AgentName,
    Expr? InputExpr,
    IReadOnlyList<string> ContextNames) : StepBody;

// Step-level if/else — both branches required, each produces exactly one value
public sealed record ConditionalStepBody(
    Expr Condition,
    StepBody Then,
    StepBody Else,
    SourceLocation Location) : StepBody;

public sealed record StepDecl(
    string Name,
    StepBody Body,
    string SaveAs,
    SourceLocation Location);

// ── Workflow items ────────────────────────────────────────────────────────────

public abstract record WorkflowItem(SourceLocation Location);

public sealed record StepItem(StepDecl Step) : WorkflowItem(Step.Location);

public sealed record IfItem(
    Expr Condition,
    IReadOnlyList<WorkflowItem> Then,
    IReadOnlyList<WorkflowItem>? Else,
    SourceLocation Location) : WorkflowItem(Location);

// ── Loop construct ────────────────────────────────────────────────────────────

// One typed parameter with its initial-value expression
public sealed record LoopParam(string Name, TypeRef Type, Expr InitExpr, SourceLocation Location);

// continue { name: expr, ... } — advances to the next iteration
public sealed record LoopContinueItem(
    IReadOnlyDictionary<string, Expr> Args,
    SourceLocation Location) : WorkflowItem(Location);

// break expr — exits the loop and produces its output value
public sealed record LoopBreakItem(Expr OutputExpr, SourceLocation Location) : WorkflowItem(Location);

// The loop construct itself; SaveAs binds the break output in the outer scope
public sealed record LoopItem(
    string Name,
    IReadOnlyList<LoopParam> Params,
    TypeRef OutputType,
    int MaxIterations,
    IReadOnlyList<WorkflowItem> Body,
    string SaveAs,
    SourceLocation Location) : WorkflowItem(Location);

// ── Workflow ──────────────────────────────────────────────────────────────────

public sealed record WorkflowDecl(
    string Name,
    TypeRef InputType,
    TypeRef OutputType,
    IReadOnlyList<WorkflowItem> Items,
    Expr Finish,
    SourceLocation Location,
    Expr? RequireInput = null,
    Expr? RequireOutput = null) : Declaration(Name, Location);

// ── Program ───────────────────────────────────────────────────────────────────

public sealed record ProgramNode(IReadOnlyList<Declaration> Declarations);
