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
            if (ad.InputType is not null)
                ValidateTypeRef(ad.InputType, schemas);
            ValidateTypeRef(ad.OutputType, schemas);
            foreach (var entry in ad.AllowedTools)
                if (!tools.ContainsKey(entry.ToolName))
                    Error(DiagnosticCodes.ToolNotDeclaredInAllow,
                        $"Tool '{entry.ToolName}' referenced in 'allow' is not declared.", ad.Location);
        }

        ValidateTypeRef(workflow.InputType, schemas);
        ValidateTypeRef(workflow.OutputType, schemas);

        // ── Pass 3: validate agent when-guards and require blocks ─────────────
        foreach (var ad in agents.Values)
        {
            var agentInputEnv = BuildAgentInputEnv(ad, schemas);

            foreach (var entry in ad.AllowedTools)
            {
                if (entry.WhenGuard is null) continue;
                if (ad.InputType is null)
                    Error("MAIL-SEM", $"Agent '{ad.Name}' has a 'when' guard but no typed 'input'. Declare 'input TypeRef' in the agent.", entry.WhenGuard is NameExpr ne ? ne.Location : ad.Location);
                var cat = InferType(entry.WhenGuard, agentInputEnv, schemas);
                if (cat != ExprCategory.Bool)
                    Error("MAIL-SEM", $"'when' guard on tool '{entry.ToolName}' must be a Bool expression.", ad.Location);
            }

            if (ad.RequireInput is not null)
            {
                if (ad.InputType is null)
                    Error("MAIL-SEM", $"Agent '{ad.Name}' has 'require input' but no typed 'input'.", ad.Location);
                var cat = InferType(ad.RequireInput, agentInputEnv, schemas);
                if (cat != ExprCategory.Bool)
                    Error("MAIL-SEM", $"'require input' expression in agent '{ad.Name}' must be Bool.", ad.Location);
            }

            if (ad.RequireOutput is not null)
            {
                var agentOutputEnv = BuildAgentOutputEnv(ad, agentInputEnv, schemas);
                var cat = InferType(ad.RequireOutput, agentOutputEnv, schemas);
                if (cat != ExprCategory.Bool)
                    Error("MAIL-SEM", $"'require output' expression in agent '{ad.Name}' must be Bool.", ad.Location);
            }
        }

        // validate tool require blocks
        foreach (var td in tools.Values)
        {
            if (td.RequireInput is not null)
            {
                var env = BuildToolInputEnv(td, schemas);
                var cat = InferType(td.RequireInput, env, schemas);
                if (cat != ExprCategory.Bool)
                    Error("MAIL-SEM", $"'require input' expression in tool '{td.Name}' must be Bool.", td.Location);
            }
            if (td.RequireOutput is not null)
            {
                var env = BuildToolOutputEnv(td, schemas);
                var cat = InferType(td.RequireOutput, env, schemas);
                if (cat != ExprCategory.Bool)
                    Error("MAIL-SEM", $"'require output' expression in tool '{td.Name}' must be Bool.", td.Location);
            }
        }

        // ── Pass 4: validate workflow items ───────────────────────────────────
        var initialEnv = BuildWorkflowInputEnv(workflow, schemas);

        if (workflow.RequireInput is not null)
        {
            var cat = InferType(workflow.RequireInput, initialEnv, schemas);
            if (cat != ExprCategory.Bool)
                Error("MAIL-SEM", $"'require input' expression in workflow '{workflow.Name}' must be Bool.", workflow.Location);
        }

        var envAfterItems = ValidateWorkflowItems(workflow.Items, initialEnv, tools, agents, schemas);

        // ── Pass 5: validate finish expression ────────────────────────────────
        InferType(workflow.Finish, envAfterItems, schemas);

        if (workflow.RequireOutput is not null)
        {
            // output binding represents the finish value — we can't name it here without knowing type,
            // so validate in same env as finish (output binding validation is a runtime concern for require output)
            var cat = InferType(workflow.RequireOutput, envAfterItems, schemas);
            if (cat != ExprCategory.Bool)
                Error("MAIL-SEM", $"'require output' expression in workflow '{workflow.Name}' must be Bool.", workflow.Location);
        }

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

    // ── Type environment builders ──────────────────────────────────────────────

    private static TypeEnv BuildWorkflowInputEnv(WorkflowDecl workflow, Dictionary<string, SchemaDecl> schemas)
    {
        var info = TypeRefToBindingInfo(workflow.InputType, schemas);
        return TypeEnv.Empty().Extend("input", info);
    }

    private static TypeEnv BuildAgentInputEnv(AgentDecl agent, Dictionary<string, SchemaDecl> schemas)
    {
        if (agent.InputType is null) return TypeEnv.Empty();
        var info = TypeRefToBindingInfo(agent.InputType, schemas);
        return TypeEnv.Empty().Extend("input", info);
    }

    private static TypeEnv BuildAgentOutputEnv(AgentDecl agent, TypeEnv inputEnv, Dictionary<string, SchemaDecl> schemas)
    {
        var info = TypeRefToBindingInfo(agent.OutputType, schemas);
        return inputEnv.Extend("output", info);
    }

    private static TypeEnv BuildToolInputEnv(ToolDecl tool, Dictionary<string, SchemaDecl> schemas)
    {
        var schema = SyntheticSchema($"_{tool.Name}_input", tool.Input);
        return TypeEnv.Empty().Extend("input", new BindingInfo(ExprCategory.Schema, schema));
    }

    private static TypeEnv BuildToolOutputEnv(ToolDecl tool, Dictionary<string, SchemaDecl> schemas)
    {
        var inSchema  = SyntheticSchema($"_{tool.Name}_input",  tool.Input);
        var outSchema = SyntheticSchema($"_{tool.Name}_output", tool.Output);
        return TypeEnv.Empty()
            .Extend("input",  new BindingInfo(ExprCategory.Schema, inSchema))
            .Extend("output", new BindingInfo(ExprCategory.Schema, outSchema));
    }

    private static SchemaDecl SyntheticSchema(string name, IReadOnlyList<FieldDecl> fields) =>
        new(name, fields, new SourceLocation("synthetic", 0, 0));

    private static BindingInfo TypeRefToBindingInfo(TypeRef typeRef, Dictionary<string, SchemaDecl> schemas)
    {
        if (typeRef is PrimitiveTypeRef pt)
        {
            return pt.Kind switch
            {
                PrimitiveKind.String => new BindingInfo(ExprCategory.String, null),
                PrimitiveKind.Bool   => new BindingInfo(ExprCategory.Bool,   null),
                PrimitiveKind.Int    => new BindingInfo(ExprCategory.Int,    null),
                _                    => new BindingInfo(ExprCategory.String, null),
            };
        }
        if (typeRef is NamedTypeRef nr && schemas.TryGetValue(nr.Name, out var sd))
            return new BindingInfo(ExprCategory.Schema, sd);

        return new BindingInfo(ExprCategory.Schema, null); // unknown schema — allow gracefully
    }

    // ── Workflow item validation ───────────────────────────────────────────────

    private TypeEnv ValidateWorkflowItems(
        IReadOnlyList<WorkflowItem> items,
        TypeEnv env,
        Dictionary<string, ToolDecl> tools,
        Dictionary<string, AgentDecl> agents,
        Dictionary<string, SchemaDecl> schemas)
    {
        var stepNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            switch (item)
            {
                case StepItem si:
                    var step = si.Step;
                    if (!stepNames.Add(step.Name))
                        Error(DiagnosticCodes.DuplicateInScope,
                            $"Duplicate step name '{step.Name}'.", step.Location);

                    var outInfo = ValidateStepBody(step.Body, env, tools, agents, schemas, step.Location);
                    env = env.Extend(step.SaveAs, outInfo);
                    break;

                case IfItem ifItem:
                    var condCat = InferType(ifItem.Condition, env, schemas);
                    if (condCat != ExprCategory.Bool)
                        Error("MAIL-SEM", "Condition in 'if' must be a Bool expression.", ifItem.Location);

                    var envThen = ValidateWorkflowItems(ifItem.Then, env, tools, agents, schemas);

                    if (ifItem.Else is not null)
                    {
                        var envElse = ValidateWorkflowItems(ifItem.Else, env, tools, agents, schemas);
                        // Only bindings defined in both branches escape
                        env = envThen.IntersectWith(envElse);
                    }
                    // No else: bindings from Then do not escape; env unchanged
                    break;
            }
        }
        return env;
    }

    // Returns the binding info of the step's result value
    private BindingInfo ValidateStepBody(
        StepBody body,
        TypeEnv env,
        Dictionary<string, ToolDecl> tools,
        Dictionary<string, AgentDecl> agents,
        Dictionary<string, SchemaDecl> schemas,
        SourceLocation loc)
    {
        switch (body)
        {
            case CallBody cb:
                if (!tools.TryGetValue(cb.ToolName, out var calledTool))
                {
                    Error(DiagnosticCodes.ToolNotDeclaredInCall,
                        $"Tool '{cb.ToolName}' referenced in 'call' is not declared.", loc);
                    return new BindingInfo(ExprCategory.Schema, null);
                }
                ValidateCallArgs(cb.Args, calledTool.Input, env, schemas, loc);
                var toolOutSchema = SyntheticSchema($"_{cb.ToolName}_output", calledTool.Output);
                return new BindingInfo(ExprCategory.Schema, toolOutSchema);

            case AgentBody ab:
                if (!agents.TryGetValue(ab.AgentName, out var agent))
                {
                    Error(DiagnosticCodes.AgentNotDeclared,
                        $"Agent '{ab.AgentName}' is not declared.", loc);
                    return new BindingInfo(ExprCategory.Schema, null);
                }
                if (ab.InputExpr is not null)
                {
                    if (agent.InputType is null)
                        Error("MAIL-SEM", $"Agent '{ab.AgentName}' does not declare a typed 'input', but an input expression was provided.", loc);
                    else
                        InferType(ab.InputExpr, env, schemas);
                }
                foreach (var ctx in ab.ContextNames)
                    if (!env.TryGet(ctx, out _))
                        Error(DiagnosticCodes.BindingNotAvailable,
                            $"Binding '{ctx}' is not available at this step.", loc);
                return TypeRefToBindingInfo(agent.OutputType, schemas);

            case ConditionalStepBody csb:
                var cat = InferType(csb.Condition, env, schemas);
                if (cat != ExprCategory.Bool)
                    Error("MAIL-SEM", "Condition in step-level 'if' must be a Bool expression.", csb.Location);
                var thenInfo = ValidateStepBody(csb.Then, env, tools, agents, schemas, csb.Location);
                var elseInfo = ValidateStepBody(csb.Else, env, tools, agents, schemas, csb.Location);
                if (thenInfo.Category != elseInfo.Category)
                    Error("MAIL-SEM", "Both branches of a step-level 'if' must produce the same type.", csb.Location);
                return thenInfo;

            default:
                Error("MAIL-SEM", $"Unknown step body type: {body.GetType().Name}.", loc);
                return new BindingInfo(ExprCategory.Schema, null);
        }
    }

    // ── Expression type inference ──────────────────────────────────────────────

    private ExprCategory InferType(Expr expr, TypeEnv env, Dictionary<string, SchemaDecl> schemas)
    {
        switch (expr)
        {
            case StringLiteralExpr:
                return ExprCategory.String;

            case BoolLiteralExpr:
                return ExprCategory.Bool;

            case IntLiteralExpr:
                return ExprCategory.Int;

            case NameExpr ne:
                if (!env.TryGet(ne.Name, out var info))
                {
                    Error(DiagnosticCodes.BindingNotAvailable,
                        $"Binding '{ne.Name}' is not available at this position.", ne.Location);
                    return ExprCategory.String;
                }
                return info.Category;

            case FieldAccessExpr fa:
                var targetCat = InferType(fa.Target, env, schemas);
                if (targetCat != ExprCategory.Schema)
                {
                    Error("MAIL-SEM", $"Cannot access field '{fa.Field}' on a non-schema value.", fa.Location);
                    return ExprCategory.String;
                }
                // Resolve field type
                if (!TryGetFieldType(fa, env, schemas, out var fieldCat))
                    return ExprCategory.String; // error already emitted
                return fieldCat;

            case NotExpr ne:
                var opCat = InferType(ne.Operand, env, schemas);
                if (opCat != ExprCategory.Bool)
                    Error("MAIL-SEM", "'not' requires a Bool operand.", ne.Location);
                return ExprCategory.Bool;

            case BinaryExpr be:
                return InferBinaryType(be, env, schemas);

            case ConditionalExpr ce:
                var condCat = InferType(ce.Condition, env, schemas);
                if (condCat != ExprCategory.Bool)
                    Error("MAIL-SEM", "Condition in 'if/then/else' expression must be Bool.", ce.Location);
                var thenCat = InferType(ce.Then, env, schemas);
                var elseCat = InferType(ce.Else, env, schemas);
                if (thenCat != elseCat)
                    Error("MAIL-SEM", "Both branches of 'if/then/else' expression must have the same type.", ce.Location);
                return thenCat;

            default:
                Error("MAIL-SEM", $"Unknown expression type: {expr.GetType().Name}.", new SourceLocation(filePath, 0, 0));
                return ExprCategory.String;
        }
    }

    private ExprCategory InferBinaryType(BinaryExpr be, TypeEnv env, Dictionary<string, SchemaDecl> schemas)
    {
        var leftCat  = InferType(be.Left,  env, schemas);
        var rightCat = InferType(be.Right, env, schemas);

        switch (be.Op)
        {
            case BinaryOp.And:
            case BinaryOp.Or:
                if (leftCat  != ExprCategory.Bool) Error("MAIL-SEM", $"Left side of '{OpName(be.Op)}' must be Bool.", be.Location);
                if (rightCat != ExprCategory.Bool) Error("MAIL-SEM", $"Right side of '{OpName(be.Op)}' must be Bool.", be.Location);
                return ExprCategory.Bool;

            case BinaryOp.Eq:
            case BinaryOp.Ne:
                if (leftCat == ExprCategory.Schema || rightCat == ExprCategory.Schema)
                    Error("MAIL-SEM", $"Equality operators cannot be applied to schema types.", be.Location);
                else if (leftCat != rightCat)
                    Error("MAIL-SEM", $"Both sides of '{OpName(be.Op)}' must have the same type.", be.Location);
                return ExprCategory.Bool;

            case BinaryOp.Lt:
            case BinaryOp.Le:
            case BinaryOp.Gt:
            case BinaryOp.Ge:
                if (leftCat  != ExprCategory.Int) Error("MAIL-SEM", $"Left side of '{OpName(be.Op)}' must be Int.", be.Location);
                if (rightCat != ExprCategory.Int) Error("MAIL-SEM", $"Right side of '{OpName(be.Op)}' must be Int.", be.Location);
                return ExprCategory.Bool;

            default:
                return ExprCategory.Bool;
        }
    }

    private bool TryGetFieldType(FieldAccessExpr fa, TypeEnv env, Dictionary<string, SchemaDecl> schemas, out ExprCategory cat)
    {
        // Walk to root and get schema
        var schema = ResolveExprSchema(fa.Target, env, schemas);
        if (schema is null)
        {
            cat = ExprCategory.String;
            return true; // error already emitted by InferType on target
        }
        var field = schema.Fields.FirstOrDefault(f => f.Name == fa.Field);
        if (field is null)
        {
            Error("MAIL-SEM", $"Field '{fa.Field}' does not exist on schema '{schema.Name}'.", fa.Location);
            cat = ExprCategory.String;
            return false;
        }
        cat = field.Type is PrimitiveTypeRef pt
            ? pt.Kind switch { PrimitiveKind.Bool => ExprCategory.Bool, PrimitiveKind.Int => ExprCategory.Int, _ => ExprCategory.String }
            : ExprCategory.Schema;
        return true;
    }

    private SchemaDecl? ResolveExprSchema(Expr expr, TypeEnv env, Dictionary<string, SchemaDecl> schemas)
    {
        switch (expr)
        {
            case NameExpr ne:
                if (env.TryGet(ne.Name, out var info)) return info.Schema;
                return null;
            case FieldAccessExpr fa:
                var parentSchema = ResolveExprSchema(fa.Target, env, schemas);
                if (parentSchema is null) return null;
                var fieldDecl = parentSchema.Fields.FirstOrDefault(f => f.Name == fa.Field);
                if (fieldDecl?.Type is NamedTypeRef nr && schemas.TryGetValue(nr.Name, out var sd))
                    return sd;
                return null;
            default:
                return null;
        }
    }

    private static string OpName(BinaryOp op) => op switch
    {
        BinaryOp.And => "and", BinaryOp.Or => "or",
        BinaryOp.Eq  => "==",  BinaryOp.Ne => "!=",
        BinaryOp.Lt  => "<",   BinaryOp.Le => "<=",
        BinaryOp.Gt  => ">",   BinaryOp.Ge => ">=",
        _ => op.ToString(),
    };

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
        TypeEnv env,
        Dictionary<string, SchemaDecl> schemas,
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
            InferType(expr, env, schemas);
        }

        foreach (var key in args.Keys)
            if (!inputFields.Any(f => f.Name == key))
                Error(DiagnosticCodes.ArgumentTypeMismatch,
                    $"Argument '{key}' is not a field of the tool input.", loc);
    }

    private void Error(string code, string message, SourceLocation loc) =>
        _errors.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, loc));
}

// ── Type environment ──────────────────────────────────────────────────────────

internal enum ExprCategory { String, Bool, Int, Schema }

internal sealed record BindingInfo(ExprCategory Category, SchemaDecl? Schema);

internal sealed class TypeEnv
{
    private readonly IReadOnlyDictionary<string, BindingInfo> _bindings;

    private TypeEnv(IReadOnlyDictionary<string, BindingInfo> bindings) => _bindings = bindings;

    public static TypeEnv Empty() =>
        new(new Dictionary<string, BindingInfo>(StringComparer.Ordinal));

    public TypeEnv Extend(string name, BindingInfo info)
    {
        var d = new Dictionary<string, BindingInfo>(_bindings, StringComparer.Ordinal) { [name] = info };
        return new TypeEnv(d);
    }

    // Returns env containing only bindings present in BOTH envs with the same category.
    // Schema identity is not required to match — branches may call different tools that
    // both produce a Schema result. The merged binding keeps the schema if both agree,
    // otherwise null (field access on the merged binding would fail at compile time).
    public TypeEnv IntersectWith(TypeEnv other)
    {
        var d = new Dictionary<string, BindingInfo>(StringComparer.Ordinal);
        foreach (var (name, info) in _bindings)
        {
            if (other._bindings.TryGetValue(name, out var otherInfo) && info.Category == otherInfo.Category)
            {
                var schema = ReferenceEquals(info.Schema, otherInfo.Schema) ? info.Schema : null;
                d[name] = new BindingInfo(info.Category, schema);
            }
        }
        return new TypeEnv(d);
    }

    public bool TryGet(string name, out BindingInfo info)
    {
        var found = _bindings.TryGetValue(name, out var v);
        info = v!;
        return found;
    }
}
