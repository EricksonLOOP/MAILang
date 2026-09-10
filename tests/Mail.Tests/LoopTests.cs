using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using System.Collections.Immutable;
using Xunit;

namespace Mail.Tests;

// ── Loop compiler tests ───────────────────────────────────────────────────────

public class LoopCompilerTests
{
    private static (ValidatedPlan? Plan, IReadOnlyList<Diagnostic> Diagnostics) Compile(string src) =>
        MailCompiler.Compile(src, "test.mail");

    private static bool HasError(IReadOnlyList<Diagnostic> d, string? code = null) =>
        d.Any(x => x.Severity == DiagnosticSeverity.Error && (code is null || x.Code == code));

    // ── Basic structure ───────────────────────────────────────────────────────

    [Fact]
    public void Basic_loop_with_break_compiles()
    {
        var src = """
            schema Draft { text: String accepted: Bool }
            tool ReviewDraft { input { text: String } output { text: String accepted: Bool } }
            workflow W {
              input Draft output Draft
              loop Review {
                params { current: Draft = input }
                output Draft
                max 3
                step check {
                  call ReviewDraft { text: current.text }
                  save as result
                }
                if result.accepted {
                  break result
                } else {
                  continue { current: result }
                }
                save as reviewed
              }
              finish with reviewed
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
        Assert.NotNull(plan);
    }

    [Fact]
    public void Loop_result_is_available_after_loop()
    {
        var src = """
            schema Item { val: String done: Bool }
            tool Process { input { val: String } output { val: String done: Bool } }
            workflow W {
              input Item output Item
              loop L {
                params { current: Item = input }
                output Item
                max 5
                step s { call Process { val: current.val } save as r }
                if r.done { break r } else { continue { current: r } }
                save as final
              }
              finish with final
            }
            """;
        var (plan, diag) = Compile(src);
        Assert.False(HasError(diag), string.Join("; ", diag.Select(d => d.Message)));
        Assert.NotNull(plan);
    }

    [Fact]
    public void Loop_body_binding_does_not_escape_to_outer_scope()
    {
        var src = """
            schema Item { val: String done: Bool }
            tool Process { input { val: String } output { val: String done: Bool } }
            workflow W {
              input Item output String
              loop L {
                params { current: Item = input }
                output Item
                max 3
                step s { call Process { val: current.val } save as r }
                if r.done { break r } else { continue { current: r } }
                save as final
              }
              finish with r
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag), "Expected error: 'r' is a loop-internal binding and must not escape.");
    }

    // ── Name shadowing (spec §3) ──────────────────────────────────────────────

    [Fact]
    public void Step_save_as_shadowing_outer_binding_is_error()
    {
        var src = """
            schema Req { n: Int }
            schema Resp { n: Int }
            tool T { input { n: Int } output { n: Int } }
            workflow W {
              input Req output Resp
              step first { call T { n: input.n } save as result }
              step second { call T { n: input.n } save as result }
              finish with result
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag), "Expected error: 'result' shadows an existing binding.");
    }

    [Fact]
    public void Loop_param_shadowing_outer_binding_is_error()
    {
        var src = """
            schema Item { val: String done: Bool }
            tool Process { input { val: String } output { val: String done: Bool } }
            tool Pre { input { val: String } output { val: String done: Bool } }
            workflow W {
              input Item output Item
              step pre { call Pre { val: input.val } save as current }
              loop L {
                params { current: Item = input }
                output Item
                max 3
                step s { call Process { val: current.val } save as r }
                if r.done { break r } else { continue { current: r } }
                save as final
              }
              finish with final
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag), "Expected error: loop param 'current' shadows outer binding.");
    }

    // ── Break / continue validation ───────────────────────────────────────────

    [Fact]
    public void Break_outside_loop_is_error()
    {
        var src = """
            schema Req { val: String }
            workflow W {
              input Req output String
              break input.val
              finish with input.val
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag), "Expected error: 'break' used outside a loop.");
    }

    [Fact]
    public void Continue_outside_loop_is_error()
    {
        var src = """
            schema Req { val: String }
            workflow W {
              input Req output String
              continue { }
              finish with input.val
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag), "Expected error: 'continue' used outside a loop.");
    }

    [Fact]
    public void Continue_missing_required_param_is_error()
    {
        var src = """
            schema Item { val: String count: Int done: Bool }
            tool Process { input { val: String } output { val: String count: Int done: Bool } }
            workflow W {
              input Item output Item
              loop L {
                params { current: Item = input, n: Int = input.count }
                output Item
                max 3
                step s { call Process { val: current.val } save as r }
                if r.done {
                  break r
                } else {
                  continue { current: r }
                }
                save as final
              }
              finish with final
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag), "Expected error: 'continue' missing argument for param 'n'.");
    }

    [Fact]
    public void Continue_with_undeclared_param_is_error()
    {
        var src = """
            schema Item { val: String done: Bool }
            tool Process { input { val: String } output { val: String done: Bool } }
            workflow W {
              input Item output Item
              loop L {
                params { current: Item = input }
                output Item
                max 3
                step s { call Process { val: current.val } save as r }
                if r.done {
                  break r
                } else {
                  continue { current: r, extra: r }
                }
                save as final
              }
              finish with final
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag), "Expected error: 'continue' provides undeclared argument 'extra'.");
    }

    // ── Spec §10 conformance scenarios ────────────────────────────────────────

    [Fact]
    public void Ref_to_future_step_result_is_error()
    {
        var src = """
            schema Req { n: Int }
            schema Resp { n: Int }
            tool A { input { n: Int } output { n: Int } }
            tool B { input { n: Int } output { n: Int } }
            workflow W {
              input Req output Resp
              step first { call A { n: second.n } save as result }
              step second { call B { n: input.n } save as r2 }
              finish with result
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag), "Expected static error: reference to 'second' before it is defined.");
    }

    [Fact]
    public void Branch_exclusive_binding_read_after_branch_is_error()
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
        Assert.True(HasError(diag), "Expected static error: 'only_in_then' not available on all paths.");
    }

    [Fact]
    public void Loop_save_as_shadowing_existing_binding_is_error()
    {
        var src = """
            schema Item { val: String done: Bool }
            tool Pre { input { val: String } output { val: String done: Bool } }
            tool Process { input { val: String } output { val: String done: Bool } }
            workflow W {
              input Item output Item
              step pre { call Pre { val: input.val } save as result }
              loop L {
                params { current: Item = input }
                output Item
                max 3
                step s { call Process { val: current.val } save as r }
                if r.done { break r } else { continue { current: r } }
                save as result
              }
              finish with result
            }
            """;
        var (_, diag) = Compile(src);
        Assert.True(HasError(diag), "Expected error: loop 'save as result' shadows existing binding.");
    }
}

// ── Loop runtime tests ────────────────────────────────────────────────────────

public class LoopRuntimeTests
{
    private static ToolRegistry BuildTools(params (string name, FieldContract[] input, FieldContract[] output, Func<MailSchema, MailSchema> impl)[] specs)
    {
        var reg = new ToolRegistry();
        foreach (var (name, input, output, impl) in specs)
            reg.Register(name, new LambdaTool(impl), input, output);
        return reg;
    }

    private static async Task<ExecutionResult> Run(string source, MailValue input, ToolRegistry tools)
    {
        var (plan, diag) = MailCompiler.Compile(source, "test.mail");
        var errors = diag.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException("Compile errors: " + string.Join("; ", errors.Select(e => e.Message)));
        Assert.NotNull(plan);

        var providers = new ProviderRegistry();
        var bindings = new ModelBindings();
        var executor = new WorkflowExecutor(plan!, tools, providers, bindings, ExecutionLimits.Default);
        return await executor.RunAsync(input, "sim", CancellationToken.None);
    }

    [Fact]
    public async Task Loop_breaks_on_first_iteration()
    {
        var callCount = 0;
        var tools = BuildTools(
            ("Review",
                [new FieldContract("text", MailTypeKind.String, true)],
                [new FieldContract("text", MailTypeKind.String, true), new FieldContract("accepted", MailTypeKind.Bool, true)],
                s =>
                {
                    callCount++;
                    return MkSchema(("text", new MailString("done")), ("accepted", new MailBool(true)));
                })
        );

        var src = """
            schema Draft { text: String accepted: Bool }
            tool Review {
              input { text: String }
              output { text: String accepted: Bool }
            }
            workflow W {
              input Draft output Draft
              loop ReviewLoop {
                params { current: Draft = input }
                output Draft
                max 5
                step s { call Review { text: current.text } save as result }
                if result.accepted {
                  break result
                } else {
                  continue { current: result }
                }
                save as final
              }
              finish with final
            }
            """;

        var input = MkSchema(("text", new MailString("hello")), ("accepted", new MailBool(false)));
        var result = await Run(src, input, tools);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(1, callCount);
        var output = Assert.IsType<MailSchema>(result.Output);
        Assert.Equal(new MailBool(true), output.Fields["accepted"]);
    }

    [Fact]
    public async Task Loop_continues_multiple_iterations_before_breaking()
    {
        var callCount = 0;
        var tools = BuildTools(
            ("Review",
                [new FieldContract("text", MailTypeKind.String, true)],
                [new FieldContract("text", MailTypeKind.String, true), new FieldContract("accepted", MailTypeKind.Bool, true)],
                s =>
                {
                    callCount++;
                    var accepted = callCount >= 3; // accept on 3rd call
                    return MkSchema(("text", new MailString($"rev{callCount}")), ("accepted", new MailBool(accepted)));
                })
        );

        var src = """
            schema Draft { text: String accepted: Bool }
            tool Review {
              input { text: String }
              output { text: String accepted: Bool }
            }
            workflow W {
              input Draft output Draft
              loop ReviewLoop {
                params { current: Draft = input }
                output Draft
                max 5
                step s { call Review { text: current.text } save as result }
                if result.accepted {
                  break result
                } else {
                  continue { current: result }
                }
                save as final
              }
              finish with final
            }
            """;

        var input = MkSchema(("text", new MailString("original")), ("accepted", new MailBool(false)));
        var result = await Run(src, input, tools);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(3, callCount);
        var output = Assert.IsType<MailSchema>(result.Output);
        Assert.Equal(new MailBool(true), output.Fields["accepted"]);
    }

    [Fact]
    public async Task Loop_exceeding_max_iterations_fails()
    {
        var callCount = 0;
        var tools = BuildTools(
            ("Review",
                [new FieldContract("text", MailTypeKind.String, true)],
                [new FieldContract("text", MailTypeKind.String, true), new FieldContract("accepted", MailTypeKind.Bool, true)],
                s =>
                {
                    callCount++;
                    return MkSchema(("text", new MailString("draft")), ("accepted", new MailBool(false)));
                })
        );

        var src = """
            schema Draft { text: String accepted: Bool }
            tool Review {
              input { text: String }
              output { text: String accepted: Bool }
            }
            workflow W {
              input Draft output Draft
              loop ReviewLoop {
                params { current: Draft = input }
                output Draft
                max 2
                step s { call Review { text: current.text } save as result }
                if result.accepted {
                  break result
                } else {
                  continue { current: result }
                }
                save as final
              }
              finish with final
            }
            """;

        var input = MkSchema(("text", new MailString("hello")), ("accepted", new MailBool(false)));
        var result = await Run(src, input, tools);

        Assert.False(result.Succeeded, "Expected failure when max iterations is exhausted.");
        Assert.Equal(2, callCount); // ran exactly max times
    }

    [Fact]
    public async Task Loop_receives_updated_params_on_each_iteration()
    {
        var receivedTexts = new List<string>();
        var callCount = 0;
        var tools = BuildTools(
            ("Process",
                [new FieldContract("text", MailTypeKind.String, true)],
                [new FieldContract("text", MailTypeKind.String, true), new FieldContract("done", MailTypeKind.Bool, true)],
                s =>
                {
                    callCount++;
                    var text = ((MailString)s.Fields["text"]).Value;
                    receivedTexts.Add(text);
                    var nextText = $"{text}!";
                    var done = callCount >= 3;
                    return MkSchema(("text", new MailString(nextText)), ("done", new MailBool(done)));
                })
        );

        var src = """
            schema Item { text: String done: Bool }
            tool Process {
              input { text: String }
              output { text: String done: Bool }
            }
            workflow W {
              input Item output Item
              loop L {
                params { current: Item = input }
                output Item
                max 5
                step s { call Process { text: current.text } save as r }
                if r.done { break r } else { continue { current: r } }
                save as final
              }
              finish with final
            }
            """;

        var input = MkSchema(("text", new MailString("start")), ("done", new MailBool(false)));
        var result = await Run(src, input, tools);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(["start", "start!", "start!!"], receivedTexts);
    }

    [Fact]
    public async Task Outer_bindings_are_readable_inside_loop_body()
    {
        var receivedPrefix = "";
        var callCount = 0;
        var tools = BuildTools(
            ("Pre",
                [new FieldContract("val", MailTypeKind.String, true)],
                [new FieldContract("prefix", MailTypeKind.String, true)],
                s => MkSchema(("prefix", new MailString("pre:")))),
            ("Process",
                [new FieldContract("text", MailTypeKind.String, true), new FieldContract("prefix", MailTypeKind.String, true)],
                [new FieldContract("text", MailTypeKind.String, true), new FieldContract("done", MailTypeKind.Bool, true)],
                s =>
                {
                    callCount++;
                    receivedPrefix = ((MailString)s.Fields["prefix"]).Value;
                    return MkSchema(("text", new MailString("out")), ("done", new MailBool(true)));
                })
        );

        var src = """
            schema Item { val: String done: Bool }
            schema Pre { prefix: String }
            tool Pre { input { val: String } output { prefix: String } }
            tool Process {
              input { text: String prefix: String }
              output { text: String done: Bool }
            }
            workflow W {
              input Item output Item
              step pre { call Pre { val: input.val } save as prefixResult }
              loop L {
                params { current: Item = input }
                output Item
                max 3
                step s { call Process { text: current.val, prefix: prefixResult.prefix } save as r }
                if r.done { break r } else { continue { current: input } }
                save as final
              }
              finish with final
            }
            """;

        var input = MkSchema(("val", new MailString("hello")), ("done", new MailBool(false)));
        var result = await Run(src, input, tools);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("pre:", receivedPrefix);
    }

    [Fact]
    public async Task Loop_result_is_published_as_binding_after_loop()
    {
        var loopCallCount = 0;
        var afterCallCount = 0;
        var tools = BuildTools(
            ("Review",
                [new FieldContract("text", MailTypeKind.String, true)],
                [new FieldContract("text", MailTypeKind.String, true), new FieldContract("accepted", MailTypeKind.Bool, true)],
                s =>
                {
                    loopCallCount++;
                    return MkSchema(("text", new MailString("approved")), ("accepted", new MailBool(true)));
                }),
            ("Publish",
                [new FieldContract("text", MailTypeKind.String, true)],
                [new FieldContract("ok", MailTypeKind.Bool, true)],
                s =>
                {
                    afterCallCount++;
                    return MkSchema(("ok", new MailBool(true)));
                })
        );

        var src = """
            schema Draft { text: String accepted: Bool }
            schema PubResult { ok: Bool }
            tool Review {
              input { text: String }
              output { text: String accepted: Bool }
            }
            tool Publish {
              input { text: String }
              output { ok: Bool }
            }
            workflow W {
              input Draft output PubResult
              loop ReviewLoop {
                params { current: Draft = input }
                output Draft
                max 3
                step s { call Review { text: current.text } save as result }
                if result.accepted { break result } else { continue { current: result } }
                save as approved
              }
              step pub { call Publish { text: approved.text } save as pubResult }
              finish with pubResult
            }
            """;

        var input = MkSchema(("text", new MailString("draft")), ("accepted", new MailBool(false)));
        var result = await Run(src, input, tools);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(1, loopCallCount);
        Assert.Equal(1, afterCallCount);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MailSchema MkSchema(params (string key, MailValue value)[] fields)
    {
        var dict = ImmutableDictionary<string, MailValue>.Empty;
        foreach (var (k, v) in fields)
            dict = dict.Add(k, v);
        return new MailSchema("Result", dict);
    }
}
