using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Contracts;
using Xunit;

namespace Mail.Tests;

public class ProviderValidationTests
{
    // Shared minimal scaffolding for provider validation tests
    private const string MinWorkflow = """
        schema R { x: String }
        workflow W { input R output R finish with input }
        """;

    private static (ValidatedPlan? Plan, IReadOnlyList<Diagnostic> Diagnostics) Compile(string source) =>
        MailCompiler.Compile(source, "test.mail");

    private static IReadOnlyList<Diagnostic> Errors(string source) =>
        Compile(source).Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    private static IReadOnlyList<Diagnostic> Warnings(string source) =>
        Compile(source).Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

    // ── P11 — old model syntax is now an error ────────────────────────────────

    [Fact]
    public void P11_old_model_syntax_is_now_error()
    {
        const string source = """
            schema R { x: String }
            tool T { input { x: String } output { x: String } }
            agent OldStyle {
                model GPT
                output R
                tools { allow T }
            }
            workflow W { input R output R finish with input }
            """;

        var (plan, _) = Compile(source);
        Assert.Null(plan); // error blocks compilation

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P11" && e.Message.Contains("OldStyle"));
    }

    [Fact]
    public void P11_new_syntax_produces_no_p11_warning()
    {
        const string source = """
            schema R { x: String }
            tool T { input { x: String } output { x: String } }
            provider Sim { type simulated }
            agent NewStyle {
                provider Sim
                model "deepseek-v3"
                output R
                tools { allow T }
            }
            workflow W { input R output R finish with input }
            """;

        var (plan, _) = Compile(source);
        Assert.NotNull(plan);

        var warnings = Warnings(source);
        Assert.DoesNotContain(warnings, w => w.Code == "MAIL-SEM-P11");
    }

    // ── P01 — provider reference must resolve ─────────────────────────────────

    [Fact]
    public void P01_agent_referencing_undeclared_provider_is_error()
    {
        const string source = """
            schema R { x: String }
            agent BadAgent {
                provider NonExistent
                model "gpt-4"
                output R
                tools { }
            }
            workflow W { input R output R finish with input }
            """;

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P01" && e.Message.Contains("NonExistent"));
    }

    [Fact]
    public void P01_agent_referencing_declared_provider_passes()
    {
        const string source = """
            schema R { x: String }
            provider Sim { type simulated }
            agent GoodAgent {
                provider Sim
                model "deepseek-v3"
                output R
                tools { }
            }
            workflow W { input R output R finish with input }
            """;

        var errors = Errors(source);
        Assert.DoesNotContain(errors, e => e.Code == "MAIL-SEM-P01");
    }

    // ── P03 — non-simulated provider requires all HTTP fields ─────────────────

    [Fact]
    public void P03_http_provider_missing_base_url_is_error()
    {
        const string source = MinWorkflow + """
            provider P {
                api_key env("KEY")
                call {
                    method POST
                    path "/v1"
                    headers { }
                    body { }
                }
                response { text "result" }
            }
            """;

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P03" && e.Message.Contains("base_url"));
    }

    [Fact]
    public void P03_http_provider_missing_response_block_is_error()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://example.com"
                api_key env("KEY")
                call {
                    method POST
                    path "/v1"
                    headers { }
                    body { }
                }
            }
            """;

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P03" && e.Message.Contains("response"));
    }

    [Fact]
    public void P03_simulated_provider_passes_without_http_fields()
    {
        const string source = MinWorkflow + "provider Sim { type simulated }";

        var errors = Errors(source);
        Assert.DoesNotContain(errors, e => e.Code == "MAIL-SEM-P03");
    }

    // ── P04 — duplicate header names ─────────────────────────────────────────

    [Fact]
    public void P04_duplicate_header_name_is_error()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/v1"
                    headers {
                        "Authorization" "value1"
                        "Authorization" "value2"
                    }
                    body { }
                }
                response { text "result" }
            }
            """;

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P04" && e.Message.Contains("Authorization"));
    }

    // ── P05 — duplicate body keys ─────────────────────────────────────────────

    [Fact]
    public void P05_duplicate_body_key_is_error()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/v1"
                    headers { }
                    body {
                        "model" $model
                        "model" "duplicate"
                    }
                }
                response { text "result" }
            }
            """;

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P05" && e.Message.Contains("model"));
    }

    // ── P06 — duplicate message types in $messages ────────────────────────────

    [Fact]
    public void P06_duplicate_message_type_is_error()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/v1"
                    headers { }
                    body {
                        "messages" $messages {
                            user -> { "role" "user" "content" $text }
                            user -> { "role" "user" "content" $text }
                        }
                    }
                }
                response { text "result" }
            }
            """;

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P06" && e.Message.Contains("user"));
    }

    // ── P07 — unknown interpolation variable ──────────────────────────────────

    [Fact]
    public void P07_unknown_interpolation_at_body_root_is_error()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/v1"
                    headers { }
                    body {
                        "bad" $unknown_var
                    }
                }
                response { text "result" }
            }
            """;

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P07");
    }

    [Fact]
    public void P07_known_body_root_vars_pass()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/v1"
                    headers { }
                    body {
                        "model" $model
                        "system" $system
                        "schema" $output_schema
                    }
                }
                response { text "result" }
            }
            """;

        var errors = Errors(source);
        Assert.DoesNotContain(errors, e => e.Code == "MAIL-SEM-P07");
    }

    // ── P08 — response must have text or tool_calls ───────────────────────────

    [Fact]
    public void P08_response_with_neither_text_nor_tool_calls_is_error()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/v1"
                    headers { }
                    body { }
                }
                response {
                    finish_reason {
                        path "choices[0].finish_reason"
                        stop "stop"
                        tool_calls "tool_calls"
                    }
                }
            }
            """;

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P08");
    }

    [Fact]
    public void P08_response_with_only_text_passes()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call { method POST path "/v1" headers { } body { } }
                response { text "choices[0].message.content" }
            }
            """;

        var errors = Errors(source);
        Assert.DoesNotContain(errors, e => e.Code == "MAIL-SEM-P08");
    }

    // ── P09 — selector syntax validation ─────────────────────────────────────

    [Fact]
    public void P09_invalid_selector_syntax_is_error()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call { method POST path "/v1" headers { } body { } }
                response { text "choices[0.bad" }
            }
            """;

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P09");
    }

    [Fact]
    public void P09_valid_complex_selector_passes()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call { method POST path "/v1" headers { } body { } }
                response {
                    text "choices[0].message.content"
                    tool_calls "choices[0].message.tool_calls[*]" {
                        id "id"
                        tool_name "function.name"
                        args "function.arguments"
                    }
                }
            }
            """;

        var errors = Errors(source);
        Assert.DoesNotContain(errors, e => e.Code == "MAIL-SEM-P09");
    }

    [Fact]
    public void P09_filter_selector_key_equals_value_passes()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call { method POST path "/v1" headers { } body { } }
                response { text "content[type=text].text" }
            }
            """;

        var errors = Errors(source);
        Assert.DoesNotContain(errors, e => e.Code == "MAIL-SEM-P09");
    }

    // ── P12 — $calls only valid inside assistant mapping ──────────────────────

    [Fact]
    public void P12_calls_outside_assistant_mapping_is_error()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/v1"
                    headers { }
                    body {
                        "messages" $messages {
                            user -> {
                                "role" "user"
                                "calls" $calls {
                                    "id" $call_id
                                }
                            }
                        }
                    }
                }
                response { text "result" }
            }
            """;

        var errors = Errors(source);
        Assert.Contains(errors, e => e.Code == "MAIL-SEM-P12");
    }

    [Fact]
    public void P12_calls_inside_assistant_mapping_passes()
    {
        const string source = MinWorkflow + """
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/v1"
                    headers { }
                    body {
                        "messages" $messages {
                            assistant -> {
                                "role" "assistant"
                                "content" $text
                                "tool_calls" $calls {
                                    "id" $call_id
                                    "name" $tool_name
                                }
                            }
                        }
                    }
                }
                response { text "result" }
            }
            """;

        var errors = Errors(source);
        Assert.DoesNotContain(errors, e => e.Code == "MAIL-SEM-P12");
    }

    // ── Phase 3 — ValidatedPlan.Providers is populated ───────────────────────

    [Fact]
    public void Phase3_compiled_plan_contains_provider()
    {
        const string source = MinWorkflow + """
            provider Sim { type simulated }
            provider Prod {
                base_url "https://api.example.com"
                api_key env("KEY")
                call { method POST path "/v1/chat" headers { } body { "model" $model } }
                response { text "choices[0].message.content" }
            }
            """;

        var (plan, _) = Compile(source);
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Providers.Count);

        Assert.True(plan.Providers["Sim"].IsSimulated);
        Assert.False(plan.Providers["Prod"].IsSimulated);
        Assert.Equal("https://api.example.com", plan.Providers["Prod"].BaseUrl);
    }

    [Fact]
    public void Plan_with_no_provider_declarations_has_empty_providers_dict()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            """;

        var (plan, _) = Compile(source);
        Assert.NotNull(plan);
        Assert.Empty(plan!.Providers);
    }
}
