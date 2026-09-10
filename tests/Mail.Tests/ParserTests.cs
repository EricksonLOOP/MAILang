using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Contracts;
using Xunit;

namespace Mail.Tests;

public class ParserTests
{
    private static readonly string OrderStatusSource = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "../../../../..", "examples", "order-status.mail"));

    [Fact]
    public void Parses_order_status_mail_without_errors()
    {
        var (plan, diagnostics) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        var errors = diagnostics.Where(d => d.Severity == Mail.Contracts.DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
        Assert.NotNull(plan);
    }

    [Fact]
    public void Plan_contains_expected_declarations()
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);
        Assert.True(plan.Schemas.ContainsKey("OrderStatusRequest"));
        Assert.True(plan.Schemas.ContainsKey("OrderStatusResponse"));
        Assert.True(plan.Tools.ContainsKey("GetOrderStatus"));
        Assert.True(plan.Agents.ContainsKey("StatusAgent"));
        Assert.Equal("OrderStatusFlow", plan.Workflow.Name);
    }

    [Fact]
    public void Agent_has_correct_allowed_tools()
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);
        var agent = plan.Agents["StatusAgent"];
        Assert.Contains(agent.AllowedTools, e => e.ToolName == "GetOrderStatus");
    }

    [Fact]
    public void Workflow_has_one_step_with_agent_body()
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);
        var item = Assert.Single(plan.Workflow.Items);
        var si = Assert.IsType<StepItem>(item);
        Assert.Equal("check_status", si.Step.Name);
        Assert.IsType<AgentBody>(si.Step.Body);
        var body = (AgentBody)si.Step.Body;
        Assert.Equal("StatusAgent", body.AgentName);
        Assert.Contains("input", body.ContextNames);
    }

    [Fact]
    public void Parser_reports_error_for_missing_closing_brace()
    {
        var source = "schema Foo { bar: String";
        var (plan, diagnostics) = MailCompiler.Compile(source, "test.mail");
        Assert.Null(plan);
        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    // ── system property ───────────────────────────────────────────────────────

    private const string AgentTemplate = """
        schema Req { q: String }
        schema Resp { a: String }
        tool T { input { q: String } output { a: String } }
        agent A {
          model GPT
          {SYSTEM}
          output Resp
          tools { allow T }
        }
        workflow W { input Req output Resp step s { agent A context { input } save as r } finish with r }
        """;

    private static (ValidatedPlan? Plan, IReadOnlyList<Diagnostic> Diagnostics) CompileAgent(string systemLine) =>
        MailCompiler.Compile(AgentTemplate.Replace("{SYSTEM}", systemLine), "test.mail");

    [Fact]
    public void Agent_without_system_has_null_prompt()
    {
        var (plan, _) = CompileAgent("");
        Assert.NotNull(plan);
        Assert.Null(plan.Agents["A"].SystemPrompt);
    }

    [Fact]
    public void Agent_with_single_line_system_captures_text()
    {
        var (plan, diag) = CompileAgent("system \"Custom instructions.\"");
        Assert.DoesNotContain(diag, d => d.Severity == DiagnosticSeverity.Error);
        Assert.NotNull(plan);
        Assert.Equal("Custom instructions.", plan.Agents["A"].SystemPrompt!.Text);
    }

    [Fact]
    public void Agent_with_triple_quoted_system_captures_text()
    {
        var (plan, diag) = CompileAgent("system \"\"\"\nLine one.\nLine two.\n\"\"\"");
        Assert.DoesNotContain(diag, d => d.Severity == DiagnosticSeverity.Error);
        Assert.NotNull(plan);
        Assert.Equal("\nLine one.\nLine two.\n", plan.Agents["A"].SystemPrompt!.Text);
    }

    [Fact]
    public void Duplicate_system_produces_error_and_parse_continues()
    {
        var (plan, diag) = CompileAgent("system \"first\" system \"second\"");
        var errors = diag.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Single(errors);
        Assert.Equal(DiagnosticCodes.PromptDuplicate, errors[0].Code);
        // plan is null due to error, but parser did not crash
    }

    [Fact]
    public void Three_system_properties_produce_two_duplicate_errors()
    {
        var (_, diag) = CompileAgent("system \"a\" system \"b\" system \"c\"");
        var errors = diag.Where(d => d.Code == DiagnosticCodes.PromptDuplicate).ToList();
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void System_without_literal_produces_parse_error()
    {
        var (plan, diag) = CompileAgent("system");
        var errors = diag.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.NotEmpty(errors);
        Assert.Null(plan);
    }

    [Fact]
    public void Empty_system_string_produces_prompt_empty_error()
    {
        var (_, diag) = CompileAgent("system \"\"");
        Assert.Contains(diag, d => d.Code == DiagnosticCodes.PromptEmpty);
    }

    [Fact]
    public void Whitespace_only_system_string_produces_prompt_empty_error()
    {
        var (_, diag) = CompileAgent("system \"   \"");
        Assert.Contains(diag, d => d.Code == DiagnosticCodes.PromptEmpty);
    }
}
