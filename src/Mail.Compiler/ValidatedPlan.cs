using Mail.Compiler.Ast;
using System.Collections.Immutable;

namespace Mail.Compiler;

public sealed record ValidatedPlan(
    ProgramNode Ast,
    ImmutableDictionary<string, SchemaDecl> Schemas,
    ImmutableDictionary<string, ToolDecl> Tools,
    ImmutableDictionary<string, AgentDecl> Agents,
    WorkflowDecl Workflow,
    string FilePath);
