using Mail.Compiler.Ast;
using Mail.Contracts;
using System.Collections.Immutable;

namespace Mail.Compiler.Semantics;

// Bind names in their declaring module before running the shared semantic validator.
// Aliases are lexical names, never identities in the executable plan.
internal sealed class ModuleGraphBuilder(ISourceResolver resolver, CompilationOptions options)
{
    private readonly Dictionary<string, ProgramNode> modules = new(PathIdentity.Comparer);
    private readonly Dictionary<string, Dictionary<string, string>> imports = new(PathIdentity.Comparer);
    private readonly List<Diagnostic> errors = [];
    private readonly List<string> active = [];
    private string entry = "";
    private bool Failed => errors.Any(d => d.Severity == DiagnosticSeverity.Error);
    private void Error(string code, string message, SourceLocation loc) =>
        errors.Add(new(DiagnosticSeverity.Error, code, message, loc));

    public static (ValidatedPlan? Plan, IReadOnlyList<Diagnostic> Diagnostics) Build(
        string path, ISourceResolver resolver, CompilationOptions? options = null)
    {
        var b = new ModuleGraphBuilder(resolver, options ?? new());
        return b.Build(path);
    }

    private (ValidatedPlan?, IReadOnlyList<Diagnostic>) Build(string path)
    {
        try { entry = resolver.Canonicalize(path); }
        catch (Exception ex) { Error(DiagnosticCodes.ImportNotFound, ex.Message, new(path, 1, 1)); return (null, errors); }
        Load(entry, new(entry, 1, 1));
        if (Failed) return (null, errors);
        var entries = modules[entry].Declarations.OfType<WorkflowDecl>().ToArray();
        if (entries.Length != 1)
            Error(DiagnosticCodes.EntryWorkflow, $"Entry file must declare exactly one workflow; found {entries.Length}.", new(entry, 1, 1));
        foreach (var (file, program) in modules)
        {
            var names = new Dictionary<string, Declaration>(StringComparer.Ordinal);
            foreach (var decl in program.Declarations)
                if (!names.TryAdd(decl.Name, decl))
                    Error(DiagnosticCodes.DuplicateName, $"Duplicate '{decl.Name}'; first declared at {names[decl.Name].Location}.", decl.Location);
            foreach (var imp in program.Imports)
                if (names.ContainsKey(imp.Alias))
                    Error(DiagnosticCodes.ImportAlias, $"Alias '{imp.Alias}' conflicts with a declaration.", imp.Location);
        }
        var tools = new Dictionary<string, ToolDecl>(StringComparer.Ordinal);
        foreach (var tool in modules.Values.SelectMany(p => p.Declarations).OfType<ToolDecl>())
            if (!tools.TryAdd(tool.Name, tool))
                Error(DiagnosticCodes.ToolNameCollision, $"Tool '{tool.Name}' is declared at both {tools[tool.Name].Location} and {tool.Location}.", tool.Location);
        if (Failed) return (null, errors);
        var declarations = modules.SelectMany(m => m.Value.Declarations.Select(d => BindDeclaration(m.Key, d))).ToArray();
        if (Failed) return (null, errors);
        var workflows = declarations.OfType<WorkflowDecl>().ToImmutableDictionary(w => w.Name);
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        void Visit(WorkflowDecl wf)
        {
            if (visited.Contains(wf.Name)) return;
            if (!visiting.Add(wf.Name)) { Error(DiagnosticCodes.WorkflowRecursion, $"Recursive workflow call involving '{wf.Name}'.", wf.Location); return; }
            foreach (var call in Calls(wf.Items)) Visit(workflows[call.TargetName ?? $"{call.Alias}.{call.WorkflowName}"]);
            visiting.Remove(wf.Name);
            visited.Add(wf.Name);
        }
        foreach (var wf in workflows.Values) Visit(wf);
        if (Failed) return (null, errors);
        ValidatedPlan? plan = null;
        // Every workflow receives exactly the same declaration checks, contracts, and
        // binding checks as the original single-file compiler.
        var common = declarations.Where(d => d is not WorkflowDecl).ToArray();
        foreach (var wf in workflows.Values)
        {
            var (validated, diagnostics) = new SemanticValidator(wf.Location.File, workflows)
                .Validate(new ProgramNode([], common.Append(wf).ToArray()));
            errors.AddRange(diagnostics);
            if (wf.Name == entries[0].Name) plan = validated;
        }
        if (Failed || plan is null) return (null, errors.Distinct().ToArray());
        return (plan with { Ast = modules[entry], Workflows = workflows, FilePath = entry }, errors);
    }

    private void Load(string path, SourceLocation location)
    {
        if (active.Contains(path, PathIdentity.Comparer))
        { Error(DiagnosticCodes.ImportCycle, $"Import cycle: {string.Join(" -> ", active.Append(path))}", location); return; }
        if (active.Count > options.MaxImportDepth)
        { Error("MAIL-IMPORT-LIMIT", $"Import depth exceeds {options.MaxImportDepth}.", location); return; }
        if (modules.ContainsKey(path)) return;
        if (modules.Count >= options.MaxModules)
        { Error("MAIL-IMPORT-LIMIT", $"Module count exceeds {options.MaxModules}.", location); return; }
        try
        {
            var (tokens, lexErrors) = new Lexer.Lexer(resolver.ReadSource(path), path).Tokenize();
            errors.AddRange(lexErrors);
            var (program, parseErrors) = new Parser.Parser(tokens).Parse();
            errors.AddRange(parseErrors);
            if (program is null) return;
            modules[path] = program;
            imports[path] = new(StringComparer.Ordinal);
            active.Add(path);
            foreach (var imp in program.Imports)
            {
                try
                {
                    if (Path.IsPathRooted(imp.Path) || imp.Path.Contains(':') || imp.Path.IndexOfAny(['*', '?']) >= 0 ||
                        !imp.Path.EndsWith(".mail", StringComparison.OrdinalIgnoreCase))
                    { Error(DiagnosticCodes.ImportNotFound, "Imports must be relative local .mail paths.", imp.Location); continue; }
                    var target = resolver.Resolve(path, imp.Path);
                    if (target is null) { Error(DiagnosticCodes.ImportNotFound, $"Cannot resolve '{imp.Path}'.", imp.Location); continue; }
                    target = resolver.Canonicalize(target);
                    if (!imports[path].TryAdd(imp.Alias, target))
                        Error(DiagnosticCodes.ImportAlias, $"Duplicate alias '{imp.Alias}'.", imp.Location);
                    Load(target, imp.Location);
                }
                catch (Exception ex) { Error(DiagnosticCodes.ImportNotFound, ex.Message, imp.Location); }
            }
            active.RemoveAt(active.Count - 1);
        }
        catch (Exception ex) { Error(DiagnosticCodes.ImportNotFound, $"Cannot read '{path}': {ex.Message}", location); }
    }

    private string Key(string file, Declaration decl) => decl is ToolDecl || PathIdentity.Comparer.Equals(file, entry)
        ? decl.Name : $"{file}::{decl.Name}";
    private string Resolve(string file, string name, SourceLocation loc, params Type[] kinds)
    {
        var parts = name.Split('.');
        var target = file;
        if (parts.Length == 2 && imports[file].TryGetValue(parts[0], out var imported)) target = imported;
        else if (parts.Length != 1) { Error(DiagnosticCodes.SymbolNotFound, $"Unknown qualified name '{name}'.", loc); return name; }
        var decl = modules[target].Declarations.FirstOrDefault(d => d.Name == parts[^1] && kinds.Contains(d.GetType()));
        if (decl is null) { Error(DiagnosticCodes.SymbolNotFound, $"Symbol '{name}' not found in this module's scope.", loc); return name; }
        return Key(target, decl);
    }
    private TypeRef BindType(string file, TypeRef type) => type switch
    {
        NamedTypeRef n => n with { Name = Resolve(file, n.Name, n.Location, typeof(SchemaDecl), typeof(EnumDecl)) },
        QualifiedNameTypeRef q => new NamedTypeRef(Resolve(file, $"{q.Alias}.{q.Name}", q.Location, typeof(SchemaDecl), typeof(EnumDecl)), q.Location),
        ListTypeRef l => l with { ElementType = BindType(file, l.ElementType) },
        NullableTypeRef n => n with { Inner = BindType(file, n.Inner) },
        _ => type
    };
    private IReadOnlyList<FieldDecl> Fields(string file, IReadOnlyList<FieldDecl> fields) => fields.Select(f => f with { Type = BindType(file, f.Type) }).ToArray();
    private Declaration BindDeclaration(string file, Declaration d) => d switch
    {
        SchemaDecl s => s with { Name = Key(file, s), Fields = Fields(file, s.Fields) },
        EnumDecl e => e with { Name = Key(file, e) },
        ToolDecl t => t with { Input = Fields(file, t.Input), Output = Fields(file, t.Output) },
        ProviderDecl p => p with { Name = Key(file, p) },
        AgentDecl a => a with
        {
            Name = Key(file, a),
            ProviderRef = a.ProviderRef == "" ? "" : Resolve(file, a.ProviderRef, a.Location, typeof(ProviderDecl)),
            InputType = a.InputType is null ? null : BindType(file, a.InputType),
            OutputType = BindType(file, a.OutputType),
            AllowedTools = a.AllowedTools.Select(t => t with { ToolName = Resolve(file, t.ToolName, a.Location, typeof(ToolDecl)) }).ToArray()
        },
        WorkflowDecl w => w with { Name = Key(file, w), InputType = BindType(file, w.InputType), OutputType = BindType(file, w.OutputType), Items = Items(file, w.Items) },
        _ => d
    };
    private IReadOnlyList<WorkflowItem> Items(string file, IReadOnlyList<WorkflowItem> items) => items.Select<WorkflowItem, WorkflowItem>(i => i switch
    {
        StepItem s => s with { Step = s.Step with { Body = Body(file, s.Step.Body, s.Location) } },
        IfItem x => x with { Then = Items(file, x.Then), Else = x.Else is null ? null : Items(file, x.Else) },
        LoopItem l => l with { OutputType = BindType(file, l.OutputType), Params = l.Params.Select(p => p with { Type = BindType(file, p.Type) }).ToArray(), Body = Items(file, l.Body) },
        _ => i
    }).ToArray();
    private StepBody Body(string file, StepBody body, SourceLocation loc) => body switch
    {
        CallBody c => c with { ToolName = Resolve(file, c.ToolName, loc, typeof(ToolDecl)) },
        AgentBody a => a with { AgentName = Resolve(file, a.AgentName, loc, typeof(AgentDecl)) },
        WorkflowCallBody w => w with { TargetName = Resolve(file, $"{w.Alias}.{w.WorkflowName}", w.Location, typeof(WorkflowDecl)) },
        ConditionalStepBody c => c with { Then = Body(file, c.Then, loc), Else = Body(file, c.Else, loc) },
        _ => body
    };
    private static IEnumerable<WorkflowCallBody> Calls(IReadOnlyList<WorkflowItem> items) => items.SelectMany(i => i switch
    {
        StepItem s => Calls(s.Step.Body),
        IfItem x => Calls(x.Then).Concat(Calls(x.Else ?? [])),
        LoopItem l => Calls(l.Body),
        _ => Enumerable.Empty<WorkflowCallBody>()
    });
    private static IEnumerable<WorkflowCallBody> Calls(StepBody b) => b switch
    {
        WorkflowCallBody w => [w],
        ConditionalStepBody c => Calls(c.Then).Concat(Calls(c.Else)),
        _ => []
    };
}

