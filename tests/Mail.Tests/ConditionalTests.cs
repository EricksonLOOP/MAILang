using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace Mail.Tests;

// ── Compile-time (semantic) tests ─────────────────────────────────────────────

public class ConditionalCompilerTests
{
    private static (ValidatedPlan? Plan, IReadOnlyList<Diagnostic> Diagnostics) Compile(string src) =>
        MailCompiler.Compile(src, "test.mail");

    private static bool HasError(IReadOnlyList<Diagnostic> d, string? code = null) =>
        d.Any(x => x.Severity == DiagnosticSeverity.Error && (code is null || x.Code == code));

    // ── Expressions ───────────────────────────────────────────────────────────

    [Fact]
    public void Integer_literal_parses_and_compiles()
    {
        var src = """
            schema Req { n: Int }
            schema Resp { v: Bool }
            workflow W {
              input Req output Resp
              finish with if input.n == 42 then true else false
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
        Assert.NotNull(plan);
    }

    [Fact]
    public void Bool_literal_parses_and_compiles()
    {
        var src = """
            schema Req { x: Bool }
            schema Resp { y: Bool }
            workflow W {
              input Req output Resp
              finish with if true then input.x else false
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
    }

    [Fact]
    public void String_literal_parses_and_compiles()
    {
        var src = """
            schema Req { s: String }
            schema Resp { t: String }
            workflow W {
              input Req output Resp
              finish with if input.s == "hello" then input.s else "world"
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
    }

    [Fact]
    public void Logical_operators_and_not_compile()
    {
        var src = """
            schema Req { a: Bool b: Bool }
            schema Resp { r: Bool }
            workflow W {
              input Req output Resp
              finish with if input.a and not input.b then true else false
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
    }

    [Fact]
    public void Comparison_operators_compile()
    {
        var src = """
            schema Req { n: Int m: Int }
            schema Resp { r: Bool }
            workflow W {
              input Req output Resp
              finish with if input.n >= input.m then true else false
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
    }

    [Fact]
    public void Non_Bool_condition_is_a_compile_error()
    {
        var src = """
            schema Req { s: String }
            schema Resp { r: Bool }
            workflow W {
              input Req output Resp
              finish with if input.s then true else false
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag));
    }

    [Fact]
    public void Comparing_different_types_is_a_compile_error()
    {
        var src = """
            schema Req { n: Int }
            schema Resp { r: Bool }
            workflow W {
              input Req output Resp
              finish with if input.n == "hello" then true else false
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag));
    }

    [Fact]
    public void Inline_conditional_branches_must_have_same_type()
    {
        var src = """
            schema Req { b: Bool }
            schema Resp { r: String }
            workflow W {
              input Req output Resp
              finish with if input.b then "text" else 42
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag));
    }

    [Fact]
    public void Chained_comparisons_are_a_parse_error()
    {
        var src = """
            schema Req { a: Int b: Int c: Int }
            schema Resp { r: Bool }
            workflow W {
              input Req output Resp
              finish with if input.a < input.b < input.c then true else false
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag));
    }

    // ── Workflow-level if/else ────────────────────────────────────────────────

    [Fact]
    public void Workflow_if_else_compiles_when_both_branches_publish_same_binding()
    {
        var src = """
            schema Req { urgent: Bool }
            schema Resp { status: String }
            tool Fast { input { } output { status: String } }
            tool Slow { input { } output { status: String } }
            workflow W {
              input Req output Resp
              if input.urgent {
                step run { call Fast { } save as r }
              } else {
                step run { call Slow { } save as r }
              }
              finish with r
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
        Assert.NotNull(plan);
    }

    [Fact]
    public void Binding_only_in_one_branch_is_not_available_after_if()
    {
        var src = """
            schema Req { flag: Bool }
            schema Resp { r: String }
            tool T { input { } output { val: String } }
            workflow W {
              input Req output Resp
              if input.flag {
                step s { call T { } save as only_in_then }
              }
              finish with only_in_then
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag));
    }

    [Fact]
    public void Binding_in_both_branches_is_available_after_if_else()
    {
        var src = """
            schema Req { flag: Bool }
            schema Resp { r: String }
            tool A { input { } output { val: String } }
            tool B { input { } output { val: String } }
            workflow W {
              input Req output Resp
              if input.flag {
                step s { call A { } save as both }
              } else {
                step s { call B { } save as both }
              }
              finish with both
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
        Assert.NotNull(plan);
    }

    [Fact]
    public void Nested_if_else_compiles()
    {
        var src = """
            schema Req { a: Bool b: Bool }
            schema Resp { r: String }
            tool X { input { } output { x: String } }
            tool Y { input { } output { y: String } }
            tool Z { input { } output { z: String } }
            workflow W {
              input Req output Resp
              if input.a {
                if input.b {
                  step s1 { call X { } save as result }
                } else {
                  step s2 { call Y { } save as result }
                }
              } else {
                step s3 { call Z { } save as result }
              }
              finish with result
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
        Assert.NotNull(plan);
    }

    // ── require blocks ────────────────────────────────────────────────────────

    [Fact]
    public void Tool_require_input_with_Bool_expr_compiles()
    {
        var src = """
            schema Req { n: Int }
            schema Resp { r: Int }
            tool Charge {
              input { amount: Int }
              require input { input.amount > 0 }
              output { receipt: String }
            }
            workflow W {
              input Req output Resp
              finish with input.n
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
    }

    [Fact]
    public void Tool_require_input_with_non_Bool_expr_is_error()
    {
        var src = """
            schema Req { n: Int }
            tool Bad {
              input { amount: Int }
              require input { input.amount }
              output { r: String }
            }
            workflow W {
              input Req output Req
              finish with input
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag));
    }

    // ── Agent when guards ─────────────────────────────────────────────────────

    [Fact]
    public void Agent_when_guard_compiles_with_typed_input()
    {
        var src = """
            schema Req { can_update: Bool }
            schema Resp { r: String }
            tool Lookup { input { id: String } output { name: String } }
            tool Update { input { name: String } output { ok: Bool } }
            agent Resolver {
              model GPT
              input Req
              output Resp
              tools {
                allow Lookup
                allow Update when input.can_update
              }
            }
            workflow W {
              input Req output Resp
              step s { agent Resolver input input context { input } save as r }
              finish with r
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
        Assert.NotNull(plan);
        var guard = plan!.Agents["Resolver"].AllowedTools.First(e => e.ToolName == "Update").WhenGuard;
        Assert.NotNull(guard);
    }

    [Fact]
    public void Agent_when_guard_without_typed_input_is_error()
    {
        var src = """
            schema Resp { r: String }
            tool T { input { x: String } output { y: String } }
            agent A {
              model GPT
              output Resp
              tools {
                allow T when input.x == "ok"
              }
            }
            schema Req { }
            workflow W {
              input Req output Resp
              step s { agent A context { } save as r }
              finish with r
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag));
    }
}

// ── Runtime tests ─────────────────────────────────────────────────────────────

public class ConditionalRuntimeTests
{
    private static ToolRegistry BuildTools(params (string name, FieldContract[] input, FieldContract[] output, Func<MailSchema, MailSchema> impl)[] specs)
    {
        var reg = new ToolRegistry();
        foreach (var (name, input, output, impl) in specs)
            reg.Register(name, new LambdaTool(impl), input, output);
        return reg;
    }

    private static (ProviderRegistry, ModelBindings) BuildProviders(string name, IModelProvider provider)
    {
        var pr = new ProviderRegistry();
        pr.Register(name, provider);
        var mb = new ModelBindings();
        return (pr, mb);
    }

    private static async Task<ExecutionResult> Run(string source, MailValue input, IModelProvider provider, ToolRegistry? tools = null)
    {
        var (plan, diag) = MailCompiler.Compile(source, "test.mail");
        var errors = diag.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException("Compile errors: " + string.Join("; ", errors.Select(e => e.Message)));
        Assert.NotNull(plan);

        tools ??= new ToolRegistry();
        var (providers, bindings) = BuildProviders("sim", provider);
        var executor = new WorkflowExecutor(plan!, tools, providers, bindings, ExecutionLimits.Default);
        return await executor.RunAsync(input, "sim", CancellationToken.None);
    }

    // ── Branch selection ──────────────────────────────────────────────────────

    [Fact]
    public async Task True_branch_executes_and_false_branch_is_skipped()
    {
        var trueCallCount  = 0;
        var falseCallCount = 0;

        var tools = BuildTools(
            ("TrueAction",
                [new FieldContract("x", MailTypeKind.String, true)],
                [new FieldContract("r", MailTypeKind.String, true)],
                s => { trueCallCount++;  return MkSchema("r", "true-result");  }),
            ("FalseAction",
                [new FieldContract("x", MailTypeKind.String, true)],
                [new FieldContract("r", MailTypeKind.String, true)],
                s => { falseCallCount++; return MkSchema("r", "false-result"); })
        );

        var src = """
            schema Req { flag: Bool x: String }
            schema Resp { r: String }
            tool TrueAction  { input { x: String } output { r: String } }
            tool FalseAction { input { x: String } output { r: String } }
            workflow W {
              input Req output Resp
              if input.flag {
                step s { call TrueAction  { x: input.x } save as result }
              } else {
                step s { call FalseAction { x: input.x } save as result }
              }
              finish with result
            }
            """;

        var input = MkSchema2("flag", new MailBool(true), "x", new MailString("val"));
        var result = await Run(src, input, new NoOpModelProvider(), tools);

        Assert.True(result.Succeeded);
        Assert.Equal(1, trueCallCount);
        Assert.Equal(0, falseCallCount);

        var events = result.Events.Select(e => e.Kind).ToList();
        Assert.Contains("branch.selected", events);
    }

    [Fact]
    public async Task False_branch_executes_when_condition_is_false()
    {
        var trueCallCount  = 0;
        var falseCallCount = 0;

        var tools = BuildTools(
            ("TrueAction",
                [new FieldContract("x", MailTypeKind.String, true)],
                [new FieldContract("r", MailTypeKind.String, true)],
                s => { trueCallCount++;  return MkSchema("r", "true"); }),
            ("FalseAction",
                [new FieldContract("x", MailTypeKind.String, true)],
                [new FieldContract("r", MailTypeKind.String, true)],
                s => { falseCallCount++; return MkSchema("r", "false"); })
        );

        var src = """
            schema Req { flag: Bool x: String }
            schema Resp { r: String }
            tool TrueAction  { input { x: String } output { r: String } }
            tool FalseAction { input { x: String } output { r: String } }
            workflow W {
              input Req output Resp
              if input.flag {
                step s { call TrueAction  { x: input.x } save as result }
              } else {
                step s { call FalseAction { x: input.x } save as result }
              }
              finish with result
            }
            """;

        var input = MkSchema2("flag", new MailBool(false), "x", new MailString("val"));
        var result = await Run(src, input, new NoOpModelProvider(), tools);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(0, trueCallCount);
        Assert.Equal(1, falseCallCount);
    }

    [Fact]
    public async Task No_else_and_condition_false_does_not_execute_branch()
    {
        var callCount = 0;
        var tools = BuildTools(
            ("OnlyInBranch",
                [new FieldContract("x", MailTypeKind.String, true)],
                [new FieldContract("r", MailTypeKind.String, true)],
                s => { callCount++; return MkSchema("r", "ran"); })
        );

        // finish with a primitive so we don't need the branch binding and avoid schema type check
        var src = """
            schema Req { flag: Bool x: String }
            tool OnlyInBranch { input { x: String } output { r: String } }
            workflow W {
              input Req output String
              if input.flag {
                step s { call OnlyInBranch { x: input.x } save as result }
              }
              finish with input.x
            }
            """;

        var input = MkSchema2("flag", new MailBool(false), "x", new MailString("hello"));
        var result = await Run(src, input, new NoOpModelProvider(), tools);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task Nested_if_else_selects_correct_branch()
    {
        var xCount = 0; var yCount = 0; var zCount = 0;

        var tools = BuildTools(
            ("X", [new FieldContract("v", MailTypeKind.String, true)], [new FieldContract("r", MailTypeKind.String, true)], s => { xCount++; return MkSchema("r", "x"); }),
            ("Y", [new FieldContract("v", MailTypeKind.String, true)], [new FieldContract("r", MailTypeKind.String, true)], s => { yCount++; return MkSchema("r", "y"); }),
            ("Z", [new FieldContract("v", MailTypeKind.String, true)], [new FieldContract("r", MailTypeKind.String, true)], s => { zCount++; return MkSchema("r", "z"); })
        );

        var src = """
            schema Req { a: Bool b: Bool v: String }
            schema Resp { r: String }
            tool X { input { v: String } output { r: String } }
            tool Y { input { v: String } output { r: String } }
            tool Z { input { v: String } output { r: String } }
            workflow W {
              input Req output Resp
              if input.a {
                if input.b {
                  step s1 { call X { v: input.v } save as result }
                } else {
                  step s2 { call Y { v: input.v } save as result }
                }
              } else {
                step s3 { call Z { v: input.v } save as result }
              }
              finish with result
            }
            """;

        // a=true, b=false → Y
        var input = new MailSchema("Req", ImmutableDictionary<string, MailValue>.Empty
            .Add("a", new MailBool(true)).Add("b", new MailBool(false)).Add("v", new MailString("val")));
        var result = await Run(src, input, new NoOpModelProvider(), tools);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(0, xCount);
        Assert.Equal(1, yCount);
        Assert.Equal(0, zCount);
    }

    // ── Short-circuit evaluation ──────────────────────────────────────────────
    // Tested by verifying the correct branch runs when `and`/`or` would short-circuit.

    [Fact]
    public async Task Short_circuit_and_does_not_execute_branch_when_left_is_false()
    {
        // a=false → (a and b) is false without evaluating b; else branch runs
        var trueBranchCount = 0;
        var tools = BuildTools(
            ("TrueOp",
                [new FieldContract("x", MailTypeKind.String, true)],
                [new FieldContract("r", MailTypeKind.String, true)],
                s => { trueBranchCount++; return MkSchema("r", "true"); }),
            ("FalseOp",
                [new FieldContract("x", MailTypeKind.String, true)],
                [new FieldContract("r", MailTypeKind.String, true)],
                s => MkSchema("r", "false"))
        );

        var src = """
            schema Req { a: Bool b: Bool x: String }
            schema Resp { r: String }
            tool TrueOp  { input { x: String } output { r: String } }
            tool FalseOp { input { x: String } output { r: String } }
            workflow W {
              input Req output Resp
              if input.a and input.b {
                step s { call TrueOp  { x: input.x } save as result }
              } else {
                step s { call FalseOp { x: input.x } save as result }
              }
              finish with result
            }
            """;

        var input = new MailSchema("Req", ImmutableDictionary<string, MailValue>.Empty
            .Add("a", new MailBool(false))
            .Add("b", new MailBool(true))
            .Add("x", new MailString("v")));

        var result = await Run(src, input, new NoOpModelProvider(), tools);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(0, trueBranchCount); // true-branch tool was never called
    }

    [Fact]
    public async Task Short_circuit_or_takes_true_branch_when_left_is_true()
    {
        var trueBranchCount = 0;

        var tools = BuildTools(
            ("TrueOp",
                [new FieldContract("x", MailTypeKind.String, true)],
                [new FieldContract("r", MailTypeKind.String, true)],
                s => { trueBranchCount++; return MkSchema("r", "true"); }),
            ("FalseOp",
                [new FieldContract("x", MailTypeKind.String, true)],
                [new FieldContract("r", MailTypeKind.String, true)],
                s => MkSchema("r", "false"))
        );

        var src = """
            schema Req { a: Bool b: Bool x: String }
            schema Resp { r: String }
            tool TrueOp  { input { x: String } output { r: String } }
            tool FalseOp { input { x: String } output { r: String } }
            workflow W {
              input Req output Resp
              if input.a or input.b {
                step s { call TrueOp  { x: input.x } save as result }
              } else {
                step s { call FalseOp { x: input.x } save as result }
              }
              finish with result
            }
            """;

        // a=true, b=false; (a or b) is true via short-circuit
        var input = new MailSchema("Req", ImmutableDictionary<string, MailValue>.Empty
            .Add("a", new MailBool(true))
            .Add("b", new MailBool(false))
            .Add("x", new MailString("v")));

        var result = await Run(src, input, new NoOpModelProvider(), tools);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(1, trueBranchCount);
    }

    // ── require input/output ──────────────────────────────────────────────────

    [Fact]
    public async Task Require_input_violation_prevents_tool_execution()
    {
        var toolCalled = false;
        var tools = BuildTools(
            ("Charge",
                [new FieldContract("amount", MailTypeKind.Int, true)],
                [new FieldContract("receipt", MailTypeKind.String, true)],
                s => { toolCalled = true; return MkSchema("receipt", "ok"); })
        );

        var src = """
            schema Req { amount: Int }
            schema Resp { receipt: String }
            tool Charge {
              input { amount: Int }
              require input { input.amount > 0 }
              output { receipt: String }
            }
            workflow W {
              input Req output Resp
              step s { call Charge { amount: input.amount } save as result }
              finish with result
            }
            """;

        var input = MkSchema("amount", new MailInt(-5));
        var result = await Run(src, input, new NoOpModelProvider(), tools);

        Assert.False(result.Succeeded);
        Assert.False(toolCalled);
        Assert.Contains("contract", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Require_input_satisfied_allows_tool_to_execute()
    {
        var toolCalled = false;
        var tools = BuildTools(
            ("Charge",
                [new FieldContract("amount", MailTypeKind.Int, true)],
                [new FieldContract("receipt", MailTypeKind.String, true)],
                s => { toolCalled = true; return MkSchema("receipt", "ok"); })
        );

        var src = """
            schema Req { amount: Int }
            schema Resp { receipt: String }
            tool Charge {
              input { amount: Int }
              require input { input.amount > 0 }
              output { receipt: String }
            }
            workflow W {
              input Req output Resp
              step s { call Charge { amount: input.amount } save as result }
              finish with result
            }
            """;

        var input = MkSchema("amount", new MailInt(100));
        var result = await Run(src, input, new NoOpModelProvider(), tools);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(toolCalled);
    }

    [Fact]
    public async Task Require_output_violation_fails_after_tool_executes()
    {
        var toolCalled = false;
        var tools = BuildTools(
            ("Charge",
                [new FieldContract("amount", MailTypeKind.Int, true)],
                [new FieldContract("receipt", MailTypeKind.String, true)],
                s => { toolCalled = true; return MkSchema("receipt", ""); /* empty receipt fails contract */ })
        );

        var src = """
            schema Req { amount: Int }
            schema Resp { receipt: String }
            tool Charge {
              input { amount: Int }
              output { receipt: String }
              require output { output.receipt != "" }
            }
            workflow W {
              input Req output Resp
              step s { call Charge { amount: input.amount } save as result }
              finish with result
            }
            """;

        var input = MkSchema("amount", new MailInt(100));
        var result = await Run(src, input, new NoOpModelProvider(), tools);

        Assert.False(result.Succeeded);
        Assert.True(toolCalled); // tool ran, but output contract failed
        Assert.Contains("contract", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Workflow_require_input_violation_prevents_execution()
    {
        var toolCalled = false;
        var tools = BuildTools(
            ("T",
                [new FieldContract("n", MailTypeKind.Int, true)],
                [new FieldContract("r", MailTypeKind.String, true)],
                s => { toolCalled = true; return MkSchema("r", "ok"); })
        );

        var src = """
            schema Req { n: Int }
            schema Resp { r: String }
            tool T { input { n: Int } output { r: String } }
            workflow W {
              input Req
              require input { input.n > 0 }
              output Resp
              step s { call T { n: input.n } save as result }
              finish with result
            }
            """;

        var input = MkSchema("n", new MailInt(0));
        var result = await Run(src, input, new NoOpModelProvider(), tools);

        Assert.False(result.Succeeded);
        Assert.False(toolCalled);
    }

    // ── when guards (agent authorization) ─────────────────────────────────────

    [Fact]
    public async Task When_guard_false_blocks_tool_call()
    {
        var updateCalled = false;

        var tools = BuildTools(
            ("Lookup",
                [new FieldContract("id", MailTypeKind.String, true)],
                [new FieldContract("name", MailTypeKind.String, true)],
                s => MkSchema("name", "Alice")),
            ("Update",
                [new FieldContract("name", MailTypeKind.String, true)],
                [new FieldContract("ok", MailTypeKind.Bool, true)],
                s => { updateCalled = true; return MkSchema("ok", new MailBool(true)); })
        );

        var src = """
            schema Req { id: String can_update: Bool }
            schema Resp { name: String }
            tool Lookup { input { id: String } output { name: String } }
            tool Update { input { name: String } output { ok: Bool } }
            agent Resolver {
              model GPT
              input Req
              output Resp
              tools {
                allow Lookup
                allow Update when input.can_update
              }
            }
            workflow W {
              input Req output Resp
              step s { agent Resolver input input context { input } save as result }
              finish with result
            }
            """;

        // Script: model tries to call Update (which is blocked by when=false)
        var callId = "call-1";
        var script = new List<ModelResponse>
        {
            new(ToolCalls: [new ToolCallRequest(callId, "Update",
                ImmutableDictionary<string, JsonElement>.Empty
                    .Add("name", JsonDocument.Parse("\"Bob\"").RootElement.Clone()))],
                Text: null),
        };

        var (providers, bindings) = BuildProviders("sim", new CapturingModelProvider(script));
        var (plan, _) = MailCompiler.Compile(src, "test.mail");
        Assert.NotNull(plan);

        var executor = new WorkflowExecutor(plan!, tools, providers, bindings, ExecutionLimits.Default);
        var input = new MailSchema("Req", ImmutableDictionary<string, MailValue>.Empty
            .Add("id", new MailString("u1"))
            .Add("can_update", new MailBool(false)));

        var result = await executor.RunAsync(input, "sim", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(updateCalled);
        var denied = result.Events.Where(e => e.Kind == "tool.denied").ToList();
        Assert.NotEmpty(denied);
    }

    [Fact]
    public async Task When_guard_true_allows_tool_call()
    {
        var updateCalled = false;

        var tools = BuildTools(
            ("Lookup",
                [new FieldContract("id", MailTypeKind.String, true)],
                [new FieldContract("name", MailTypeKind.String, true)],
                s => MkSchema("name", "Alice")),
            ("Update",
                [new FieldContract("name", MailTypeKind.String, true)],
                [new FieldContract("ok", MailTypeKind.Bool, true)],
                s => { updateCalled = true; return MkSchemaBool("ok", true); })
        );

        var src = """
            schema Req { id: String can_update: Bool }
            schema Resp { name: String }
            tool Lookup { input { id: String } output { name: String } }
            tool Update { input { name: String } output { ok: Bool } }
            agent Resolver {
              model GPT
              input Req
              output Resp
              tools {
                allow Lookup
                allow Update when input.can_update
              }
            }
            workflow W {
              input Req output Resp
              step s { agent Resolver input input context { input } save as result }
              finish with result
            }
            """;

        var script = new List<ModelResponse>
        {
            new(ToolCalls: [new ToolCallRequest("c1", "Update",
                ImmutableDictionary<string, JsonElement>.Empty
                    .Add("name", JsonDocument.Parse("\"Bob\"").RootElement.Clone()))],
                Text: null),
            new(ToolCalls: null, Text: "{\"name\":\"Bob\"}"),
        };

        var (providers, bindings) = BuildProviders("sim", new CapturingModelProvider(script));
        var (plan, _) = MailCompiler.Compile(src, "test.mail");
        Assert.NotNull(plan);

        var executor = new WorkflowExecutor(plan!, tools, providers, bindings, ExecutionLimits.Default);
        var input = new MailSchema("Req", ImmutableDictionary<string, MailValue>.Empty
            .Add("id", new MailString("u1"))
            .Add("can_update", new MailBool(true)));

        var result = await executor.RunAsync(input, "sim", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(updateCalled);
    }

    // ── Concurrent execution isolation ────────────────────────────────────────

    [Fact]
    public async Task Concurrent_executions_have_independent_state()
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<bool>();

        var src = """
            schema Req { flag: Bool x: String }
            schema Resp { r: String }
            tool A { input { x: String } output { r: String } }
            tool B { input { x: String } output { r: String } }
            workflow W {
              input Req output Resp
              if input.flag {
                step s { call A { x: input.x } save as result }
              } else {
                step s { call B { x: input.x } save as result }
              }
              finish with result
            }
            """;

        var (plan, _) = MailCompiler.Compile(src, "test.mail");
        Assert.NotNull(plan);

        var trueCount  = 0;
        var falseCount = 0;
        var tools = BuildTools(
            ("A", [new FieldContract("x", MailTypeKind.String, true)], [new FieldContract("r", MailTypeKind.String, true)],
                s => { Interlocked.Increment(ref trueCount);  return MkSchema("r", "A"); }),
            ("B", [new FieldContract("x", MailTypeKind.String, true)], [new FieldContract("r", MailTypeKind.String, true)],
                s => { Interlocked.Increment(ref falseCount); return MkSchema("r", "B"); })
        );

        var tasks = Enumerable.Range(0, 10).Select(i =>
        {
            var flag = i % 2 == 0;
            var input = MkSchema2("flag", new MailBool(flag), "x", new MailString($"x{i}"));
            var (providers, bindings) = BuildProviders("sim", new NoOpModelProvider());
            var executor = new WorkflowExecutor(plan!, tools, providers, bindings, ExecutionLimits.Default);
            return executor.RunAsync(input, "sim", CancellationToken.None);
        });

        var allResults = await Task.WhenAll(tasks);
        Assert.All(allResults, r => Assert.True(r.Succeeded, r.ErrorMessage));
        Assert.Equal(5, trueCount);
        Assert.Equal(5, falseCount);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MailSchema MkSchema(string key, string value) =>
        new("Result", ImmutableDictionary<string, MailValue>.Empty.Add(key, new MailString(value)));

    private static MailSchema MkSchema(string key, MailValue value) =>
        new("Result", ImmutableDictionary<string, MailValue>.Empty.Add(key, value));

    private static MailSchema MkSchemaBool(string key, bool value) =>
        new("Result", ImmutableDictionary<string, MailValue>.Empty.Add(key, new MailBool(value)));

    private static MailSchema MkSchema2(string k1, MailValue v1, string k2, MailValue v2) =>
        new("Req", ImmutableDictionary<string, MailValue>.Empty.Add(k1, v1).Add(k2, v2));
}

// ── Test helpers ──────────────────────────────────────────────────────────────

internal sealed class LambdaTool(Func<MailSchema, MailSchema> impl) : IToolImplementation
{
    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct) =>
        Task.FromResult(impl(input));
}

// A model provider that throws if called — used for tests that should not reach an agent
internal sealed class NoOpModelProvider : IModelProvider
{
    public Task<ModelResponse> CompleteAsync(ModelRequest req, CancellationToken ct) =>
        throw new InvalidOperationException("Model provider should not be called in this test.");
}
