using Mail.Compiler.Ast;
using Mail.Contracts;
using System.Collections.Immutable;

namespace Mail.Compiler.Semantics;

public sealed class SemanticValidator(string filePath)
{
    private readonly List<Diagnostic> _errors = [];

    public (ValidatedPlan? Plan, IReadOnlyList<Diagnostic> Errors) Validate(ProgramNode program)
    {
        var schemas  = new Dictionary<string, SchemaDecl>(StringComparer.Ordinal);
        var tools    = new Dictionary<string, ToolDecl>(StringComparer.Ordinal);
        var agents   = new Dictionary<string, AgentDecl>(StringComparer.Ordinal);
        WorkflowDecl? workflow = null;

        // ── Pass 1: collect top-level declarations ────────────────────────────
        foreach (var decl in program.Declarations)
        {
            switch (decl)
            {
                case SchemaDecl sd:
                    CheckDuplicate(schemas, sd.Name, sd.Location);
                    schemas[sd.Name] = sd;
                    CheckDuplicateFields(sd.Fields, sd.Location);
                    break;
                case ToolDecl td:
                    CheckDuplicate(tools, td.Name, td.Location);
                    tools[td.Name] = td;
                    CheckDuplicateFields(td.Input, td.Location);
                    CheckDuplicateFields(td.Output, td.Location);
                    break;
                case AgentDecl ad:
                    CheckDuplicate(agents, ad.Name, ad.Location);
                    agents[ad.Name] = ad;
                    break;
                case WorkflowDecl wd:
                    if (workflow is not null)
                        Error(DiagnosticCodes.DuplicateName, $"Duplicate workflow '{wd.Name}'.", wd.Location);
                    else
                        workflow = wd;
                    break;
            }
        }

        if (workflow is null)
        {
            Error(DiagnosticCodes.DuplicateName, "No workflow declared.", new SourceLocation(filePath, 1, 1));
            return (null, _errors);
        }

        // ── Pass 2: validate type references ─────────────────────────────────
        foreach (var sd in schemas.Values)
            foreach (var f in sd.Fields)
                ValidateTypeRef(f.Type, schemas);

        foreach (var td in tools.Values)
        {
            foreach (var f in td.Input)  ValidateTypeRef(f.Type, schemas);
            foreach (var f in td.Output) ValidateTypeRef(f.Type, schemas);
        }

        foreach (var ad in agents.Values)
        {
            ValidateTypeRef(ad.OutputType, schemas);
            foreach (var toolName in ad.AllowedTools)
                if (!tools.ContainsKey(toolName))
                    Error(DiagnosticCodes.ToolNotDeclaredInAllow,
                        $"Tool '{toolName}' referenced in 'allow' is not declared.", ad.Location);
        }

        ValidateTypeRef(workflow.InputType, schemas);
        ValidateTypeRef(workflow.OutputType, schemas);

        // ── Pass 3: validate steps ────────────────────────────────────────────
        var published = new HashSet<string>(StringComparer.Ordinal) { "input" };
        var stepNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in workflow.Steps)
        {
            if (!stepNames.Add(step.Name))
                Error(DiagnosticCodes.DuplicateInScope,
                    $"Duplicate step name '{step.Name}'.", step.Location);

            switch (step.Body)
            {
                case CallBody cb:
                    if (!tools.TryGetValue(cb.ToolName, out var calledTool))
                        Error(DiagnosticCodes.ToolNotDeclaredInCall,
                            $"Tool '{cb.ToolName}' referenced in 'call' is not declared.", step.Location);
                    else
                        ValidateCallArgs(cb.Args, calledTool.Input, published, step.Location);
                    break;

                case AgentBody ab:
                    if (!agents.ContainsKey(ab.AgentName))
                        Error(DiagnosticCodes.AgentNotDeclared,
                            $"Agent '{ab.AgentName}' is not declared.", step.Location);
                    foreach (var ctx in ab.ContextNames)
                        if (!published.Contains(ctx))
                            Error(DiagnosticCodes.BindingNotAvailable,
                                $"Binding '{ctx}' is not available at step '{step.Name}'.", step.Location);
                    break;
            }

            published.Add(step.SaveAs);
        }

        // ── Pass 4: validate finish expression ───────────────────────────────
        ValidateExpr(workflow.Finish, published);

        if (_errors.Any(d => d.Severity == DiagnosticSeverity.Error))
            return (null, _errors);

        return (new ValidatedPlan(
            program,
            schemas.ToImmutableDictionary(),
            tools.ToImmutableDictionary(),
            agents.ToImmutableDictionary(),
            workflow,
            filePath), _errors);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void CheckDuplicate<T>(Dictionary<string, T> dict, string name, SourceLocation loc)
    {
        if (dict.ContainsKey(name))
            Error(DiagnosticCodes.DuplicateName, $"Duplicate declaration '{name}'.", loc);
    }

    private void CheckDuplicateFields(IReadOnlyList<FieldDecl> fields, SourceLocation loc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fields)
            if (!seen.Add(f.Name))
                Error(DiagnosticCodes.DuplicateInScope, $"Duplicate field name '{f.Name}'.", loc);
    }

    private void ValidateTypeRef(TypeRef type, Dictionary<string, SchemaDecl> schemas)
    {
        if (type is NamedTypeRef nr && !schemas.ContainsKey(nr.Name))
            Error(DiagnosticCodes.SchemaNotDeclared,
                $"Schema '{nr.Name}' is not declared.", nr.Location);
    }

    private void ValidateCallArgs(
        IReadOnlyDictionary<string, Expr> args,
        IReadOnlyList<FieldDecl> inputFields,
        HashSet<string> published,
        SourceLocation loc)
    {
        foreach (var f in inputFields)
        {
            if (!args.TryGetValue(f.Name, out var expr))
            {
                Error(DiagnosticCodes.ArgumentTypeMismatch,
                    $"Missing required argument '{f.Name}'.", loc);
                continue;
            }
            ValidateExpr(expr, published);
        }

        foreach (var key in args.Keys)
            if (!inputFields.Any(f => f.Name == key))
                Error(DiagnosticCodes.ArgumentTypeMismatch,
                    $"Argument '{key}' is not a field of the tool input.", loc);
    }

    private void ValidateExpr(Expr expr, HashSet<string> published)
    {
        switch (expr)
        {
            case NameExpr ne:
                if (!published.Contains(ne.Name))
                    Error(DiagnosticCodes.BindingNotAvailable,
                        $"Binding '{ne.Name}' is not available at this position.", ne.Location);
                break;
            case FieldAccessExpr fa:
                ValidateExpr(fa.Target, published);
                break;
        }
    }

    private void Error(string code, string message, SourceLocation loc) =>
        _errors.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, loc));
}
