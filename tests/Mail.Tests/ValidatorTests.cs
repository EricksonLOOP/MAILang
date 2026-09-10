using Mail.Compiler;
using Mail.Contracts;
using Xunit;

namespace Mail.Tests;

public class ValidatorTests
{
    private static IReadOnlyList<Diagnostic> Errors(string source)
    {
        var (_, diagnostics) = MailCompiler.Compile(source, "test.mail");
        return diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    }

    [Fact]
    public void Rejects_undeclared_schema_in_tool()
    {
        var source = """
            tool Foo {
              input { id: UndeclaredSchema }
              output { result: String }
            }
            workflow W {
              input String
              output String
              step s { call Foo { id: input } save as r }
              finish with r
            }
            """;
        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == DiagnosticCodes.SchemaNotDeclared);
    }

    [Fact]
    public void Rejects_tool_not_declared_in_allow()
    {
        var source = """
            schema Out { result: String }
            agent A {
              model GPT
              output Out
              tools { allow NonExistentTool }
            }
            workflow W {
              input String
              output Out
              step s { agent A context { input } save as r }
              finish with r
            }
            """;
        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == DiagnosticCodes.ToolNotDeclaredInAllow);
    }

    [Fact]
    public void Rejects_undeclared_agent_in_step()
    {
        var source = """
            schema Out { result: String }
            workflow W {
              input String
              output Out
              step s { agent NoSuchAgent context { input } save as r }
              finish with r
            }
            """;
        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == DiagnosticCodes.AgentNotDeclared);
    }

    [Fact]
    public void Rejects_binding_referenced_before_published()
    {
        var source = """
            schema In { id: String }
            schema Out { result: String }
            tool T {
              input { id: String }
              output { result: String }
            }
            agent A {
              model GPT
              output Out
              tools { allow T }
            }
            workflow W {
              input In
              output Out
              step first { agent A context { second } save as r }
              step second { agent A context { input } save as s }
              finish with r
            }
            """;
        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == DiagnosticCodes.BindingNotAvailable);
    }

    [Fact]
    public void Rejects_duplicate_top_level_declaration()
    {
        var source = """
            schema Foo { x: String }
            schema Foo { y: String }
            workflow W {
              input Foo
              output Foo
              finish with input
            }
            """;
        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == DiagnosticCodes.DuplicateName);
    }

    [Fact]
    public void Accepts_valid_order_status_program()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "../../../../..", "examples", "order-status.mail"));
        var errors = Errors(source);
        Assert.Empty(errors);
    }
}
