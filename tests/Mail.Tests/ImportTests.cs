using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using Xunit;

namespace Mail.Tests;

public class ImportTests
{
    internal static (ValidatedPlan? Plan, IReadOnlyList<Diagnostic> Diagnostics) Compile(string main, params (string Path, string Source)[] files)
    {
        var sources = files.ToDictionary(f => Path.GetFullPath(f.Path), f => f.Source);
        sources[Path.GetFullPath("main.mail")] = main;
        return MailCompiler.CompileFile(Path.GetFullPath("main.mail"), new InMemorySourceResolver(sources));
    }
    internal static ValidatedPlan Good(string main, params (string Path, string Source)[] files)
    {
        var (plan, errors) = Compile(main, files);
        Assert.True(plan is not null, string.Join("\n", errors));
        return plan!;
    }
    private const string Identity = "workflow Echo { input String output String finish with input }";
    private const string Main = "import \"child.mail\" as C workflow Main { input String output String step s { workflow C.Echo input input save as result } finish with result }";

    [Fact] public void SingleFileCompatibility()
    {
        var plan = Good(Identity);
        Assert.Equal(MailCompiler.Compile(Identity, "main.mail").Plan!.Workflow.Name, plan.Workflow.Name);
    }
    [Fact] public void ImportRequiresResolver() => Assert.Null(MailCompiler.Compile(Main, "main.mail").Plan);
    [Fact] public void ImportedWorkflowsDoNotCompeteForEntry()
    {
        var p = Good(Main, ("child.mail", Identity + " workflow Other { input Int output Int finish with input }"));
        Assert.Equal(3, p.Workflows.Count);
        Assert.Same(p.Workflow, p.EntryWorkflow);
        Assert.IsType<WorkflowCallBody>(((StepItem)p.Workflow.Items[0]).Step.Body);
    }
    [Theory]
    [InlineData("workflow Main { input String output String finish with input }", "MAIL-IMPORT-NOT-FOUND")]
    [InlineData("import \"main.mail\" as M", "MAIL-IMPORT-CYCLE")]
    public void MissingOrCycle(string child, string code)
    {
        var result = code == "MAIL-IMPORT-NOT-FOUND" ? Compile(Main) : Compile(Main, ("child.mail", child));
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }
    [Theory]
    [InlineData("import \"child.mail\" as C import \"child.mail\" as C", "MAIL-IMPORT-ALIAS")]
    [InlineData("import \"child.mail\" as Main", "MAIL-IMPORT-ALIAS")]
    [InlineData("import \"https://example.com/a.mail\" as X", "MAIL-IMPORT-NOT-FOUND")]
    public void InvalidImports(string prefix, string code)
    {
        var r = Compile(prefix + " workflow Main { input String output String finish with input }", ("child.mail", Identity));
        Assert.Contains(r.Diagnostics, d => d.Code == code);
    }
    [Theory]
    [InlineData("")]
    [InlineData("workflow A { input String output String finish with input } workflow B { input String output String finish with input }")]
    public void EntryCardinality(string source) => Assert.Contains(Compile(source).Diagnostics, d => d.Code == DiagnosticCodes.EntryWorkflow);
    [Fact] public void AllImportedWorkflowsAreValidated()
    {
        var r = Compile(Main, ("child.mail", Identity + " workflow Bad { input String output String finish with parentBinding }"));
        Assert.Contains(r.Diagnostics, d => d.Code == DiagnosticCodes.BindingNotAvailable);
    }
    [Fact] public void InputTypeMismatch()
    {
        var r = Compile(Main, ("child.mail", "workflow Echo { input Int output Int finish with input }"));
        Assert.Contains(r.Diagnostics, d => d.Code == DiagnosticCodes.ArgumentTypeMismatch);
    }
    [Fact] public void NominalSchemasCannotCrossModules()
    {
        var r = Compile("import \"child.mail\" as C schema R { value: String } workflow Main { input R output C.R step s { workflow C.Echo input input save as r } finish with r }",
            ("child.mail", "schema R { value: String } workflow Echo { input R output R finish with input }"));
        Assert.Contains(r.Diagnostics, d => d.Code == DiagnosticCodes.ArgumentTypeMismatch);
    }
    [Fact] public void DiamondPreservesNominalIdentity()
    {
        var p = Good("import \"types.mail\" as T import \"child.mail\" as C workflow Main { input T.R output T.R step s { workflow C.Echo input input save as r } finish with r }",
            ("types.mail", "schema R { value: String }"),
            ("child.mail", "import \"./types.mail\" as Different workflow Echo { input Different.R output Different.R finish with input }"));
        Assert.Single(p.Schemas);
    }
    [Fact] public void MalformedImportedAgentReturnsDiagnostics()
    {
        var r = Compile(Main, ("child.mail", "agent A { model GPT output String system \"misplaced\" tools {} }"));
        Assert.Null(r.Plan); Assert.NotEmpty(r.Diagnostics);
    }
    [Fact] public void DiamondReadsEachSourceOnce()
    {
        var resolver = new CountingResolver(new Dictionary<string, string>
        {
            ["main.mail"] = "import \"a.mail\" as A import \"b.mail\" as B " + Identity,
            ["a.mail"] = "import \"shared.mail\" as S",
            ["b.mail"] = "import \"./shared.mail\" as Different",
            ["shared.mail"] = "schema R { x: String }"
        });
        var r = MailCompiler.CompileFile("main.mail", resolver);
        Assert.NotNull(r.Plan);
        Assert.Equal(4, resolver.Reads.Count);
        Assert.All(resolver.Reads.Values, count => Assert.Equal(1, count));
    }
    private sealed class CountingResolver(Dictionary<string, string> files) : ISourceResolver
    {
        private readonly InMemorySourceResolver inner = new(files);
        public Dictionary<string, int> Reads { get; } = [];
        public string? Resolve(string importer, string relative) => inner.Resolve(importer, relative);
        public string ReadSource(string path)
        {
            Reads[path] = Reads.GetValueOrDefault(path) + 1;
            return inner.ReadSource(path);
        }
    }
    [Fact] public void QualifiedToolsAndAgentsResolveInDeclaringModule()
    {
        var p = Good("import \"child.mail\" as C workflow Main { input String output String step s { agent C.A input input context {} save as r } finish with input }",
            ("child.mail", "import \"tools.mail\" as T agent A { model Test input String output String tools { allow T.Echo } }"),
            ("tools.mail", "tool Echo { input { x: String } output { x: String } }"));
        Assert.Equal("Echo", Assert.Single(p.Agents.Values).AllowedTools[0].ToolName);
    }
    [Fact] public void ToolsCannotLeakAcrossModules()
    {
        var r = Compile("import \"tools.mail\" as T workflow Main { input String output String step s { call Echo { x: input } save as r } finish with input }",
            ("tools.mail", "tool Echo { input { x: String } output { x: String } }"));
        Assert.Contains(r.Diagnostics, d => d.Code == DiagnosticCodes.SymbolNotFound);
    }
    [Fact] public void ToolCollisionShowsBothLocations()
    {
        var r = Compile("import \"child.mail\" as C tool T { input {} output {} } workflow Main { input String output String finish with input }",
            ("child.mail", "tool T { input {} output {} }"));
        var error = Assert.Single(r.Diagnostics, d => d.Code == DiagnosticCodes.ToolNameCollision);
        Assert.Contains("main.mail", error.Message); Assert.Contains("child.mail", error.Message);
    }
    [Fact] public void LimitsAreConfigurable()
    {
        var files = new InMemorySourceResolver(new Dictionary<string, string> { ["main.mail"] = Main, ["child.mail"] = Identity });
        var r = MailCompiler.CompileFile("main.mail", files, new CompilationOptions(MaxModules: 1));
        Assert.Contains(r.Diagnostics, d => d.Code == "MAIL-IMPORT-LIMIT");
    }
}

public class SubworkflowRuntimeTests
{
    private static Task<ExecutionResult> Run(ValidatedPlan plan, ExecutionLimits? limits = null, CancellationToken ct = default) =>
        new WorkflowExecutor(plan, new ToolRegistry(), new ProviderRegistry(), new ModelBindings(), limits ?? ExecutionLimits.Default)
            .RunAsync(new MailString("hello"), "simulated", ct);

    [Fact] public async Task NestedCallsAndReusedAliasesRemainDistinct()
    {
        var p = ImportTests.Good("import \"a.mail\" as A import \"b.mail\" as B workflow Main { input String output String step a { workflow A.Go input input save as a } step b { workflow B.Go input a save as b } finish with b }",
            ("a.mail", "import \"x.mail\" as C workflow Go { input String output String step s { workflow C.Go input input save as r } finish with r }"),
            ("b.mail", "import \"y.mail\" as C workflow Go { input String output String step s { workflow C.Go input input save as r } finish with r }"),
            ("x.mail", "workflow Go { input String output String finish with \"x\" }"),
            ("y.mail", "workflow Go { input String output String finish with \"y\" }"));
        var r = await Run(p);
        Assert.True(r.Succeeded, r.ErrorMessage);
        Assert.Equal(new MailString("y"), r.Output);
        var starts = r.Events.Where(e => e.Kind == "subworkflow.started").ToArray();
        Assert.Equal(4, starts.Length);
        Assert.Equal(4, starts.Select(e => e.OperationId).Distinct().Count());
        Assert.Equal(2, starts.Count(e => e.ParentOperationId is not null));
        Assert.All(starts, e => { Assert.NotNull(e.OperationId); Assert.NotNull(e.ActivationId); });
        Assert.Single(r.Events.Select(e => e.ExecutionId).Distinct());
    }
    private static ValidatedPlan Plan(string child) => ImportTests.Good("import \"child.mail\" as C workflow Main { input String output String step s { workflow C.Go input input save as r } finish with r }", ("child.mail", child));
    [Fact] public async Task OutputValidatedBeforePublishing()
    {
        var r = await Run(Plan("workflow Go { input String output String finish with 42 }"));
        Assert.False(r.Succeeded); Assert.Null(r.Output);
        Assert.DoesNotContain(r.Events, e => e.Kind == "subworkflow.completed" || e.Kind == "step.completed");
    }
    [Fact] public async Task ChildContractPropagates()
    {
        var r = await Run(Plan("workflow Go { input String require input { false } output String finish with input }"));
        Assert.False(r.Succeeded); Assert.Contains("contract", r.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task SharedActivationBudget()
    {
        var p = ImportTests.Good("import \"child.mail\" as C workflow Main { input String output String step a { workflow C.Go input input save as a } step b { workflow C.Go input a save as b } finish with b }",
            ("child.mail", "workflow Go { input String output String finish with input }"));
        var r = await Run(p, ExecutionLimits.Default with { MaxTotalActivations = 1 });
        Assert.False(r.Succeeded);
        Assert.Contains("total activations", r.ErrorMessage!);
        Assert.Single(r.Events, e => e.Kind == "subworkflow.completed");
    }    [Fact] public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var r = await Run(Plan("workflow Go { input String output String finish with input }"), ct: cts.Token);
        Assert.Equal(RunStatus.Cancelled, r.Status); Assert.Null(r.Output);
    }
}


