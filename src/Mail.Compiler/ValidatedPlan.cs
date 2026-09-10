using Mail.Compiler.Ast;
using System.Collections.Immutable;

namespace Mail.Compiler;

public sealed record ValidatedPlan(
    ProgramNode Ast,
    ImmutableDictionary<string, SchemaDecl>   Schemas,
    ImmutableDictionary<string, EnumDecl>     Enums,
    ImmutableDictionary<string, ToolDecl>     Tools,
    ImmutableDictionary<string, AgentDecl>    Agents,
    WorkflowDecl                               EntryWorkflow,
    ImmutableDictionary<string, WorkflowDecl> Workflows,
    string FilePath)
{
    // Backward-compat alias — all existing callers continue to work unchanged.
    public WorkflowDecl Workflow => EntryWorkflow;
};
