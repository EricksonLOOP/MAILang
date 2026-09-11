using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Compiler.Lexer;
using Mail.Compiler.Parser;
using Mail.Contracts;
using Xunit;

namespace Mail.Tests;

public class ProviderParserTests
{
    // ── Lexer tests ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("provider", TokenKind.Provider)]
    [InlineData("env", TokenKind.Env)]
    [InlineData("response", TokenKind.Response)]
    [InlineData("method", TokenKind.Method)]
    [InlineData("headers", TokenKind.Headers)]
    [InlineData("body", TokenKind.Body)]
    [InlineData("finish_reason", TokenKind.FinishReason)]
    public void Lexer_recognizes_new_keywords(string word, TokenKind expected)
    {
        var (tokens, errors) = new Lexer(word, "test.mail").Tokenize();
        Assert.Empty(errors);
        Assert.Equal(expected, tokens[0].Kind);
    }

    [Fact]
    public void Lexer_recognizes_arrow()
    {
        var (tokens, errors) = new Lexer("->", "test.mail").Tokenize();
        Assert.Empty(errors);
        Assert.Equal(TokenKind.Arrow, tokens[0].Kind);
        Assert.Equal("->", tokens[0].Text);
    }

    [Fact]
    public void Lexer_recognizes_dollar()
    {
        var (tokens, errors) = new Lexer("$myvar", "test.mail").Tokenize();
        Assert.Empty(errors);
        Assert.Equal(TokenKind.Dollar, tokens[0].Kind);
        Assert.Equal(TokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("myvar", tokens[1].Text);
    }

    [Fact]
    public void Lexer_dollar_followed_by_keyword_preserves_keyword_token()
    {
        // $model is Dollar + Model keyword (not Identifier) — parser handles it via ExpectIdentifier which accepts keywords
        var (tokens, errors) = new Lexer("$model", "test.mail").Tokenize();
        Assert.Empty(errors);
        Assert.Equal(TokenKind.Dollar, tokens[0].Kind);
        Assert.Equal(TokenKind.Model, tokens[1].Kind);
        Assert.Equal("model", tokens[1].Text);
    }

    [Fact]
    public void Lexer_recognizes_plus()
    {
        var (tokens, errors) = new Lexer("+", "test.mail").Tokenize();
        Assert.Empty(errors);
        Assert.Equal(TokenKind.Plus, tokens[0].Kind);
    }

    // ── Simulated provider ────────────────────────────────────────────────────

    [Fact]
    public void Parser_parses_simulated_provider()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider Sim {
                type simulated
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        Assert.NotNull(program);

        var prov = Assert.Single(program!.Declarations.OfType<ProviderDecl>());
        Assert.Equal("Sim", prov.Name);
        Assert.True(prov.IsSimulated);
        Assert.Null(prov.BaseUrl);
        Assert.Null(prov.ApiKey);
        Assert.Null(prov.Call);
        Assert.Null(prov.Response);
    }

    // ── HTTP provider ─────────────────────────────────────────────────────────

    [Fact]
    public void Parser_parses_minimal_http_provider()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider DeepSeek {
                base_url "https://api.deepseek.com"
                api_key env("DEEPSEEK_KEY")
                call {
                    method POST
                    path "/v1/chat/completions"
                    headers {
                        "Content-Type" "application/json"
                    }
                    body {
                        "model" $model
                    }
                }
                response {
                    text "choices[0].message.content"
                }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        Assert.NotNull(program);

        var prov = Assert.Single(program!.Declarations.OfType<ProviderDecl>());
        Assert.Equal("DeepSeek", prov.Name);
        Assert.False(prov.IsSimulated);
        Assert.Equal("https://api.deepseek.com", prov.BaseUrl);
        Assert.NotNull(prov.ApiKey);
        Assert.Equal("DEEPSEEK_KEY", prov.ApiKey!.EnvVarName);
        Assert.NotNull(prov.Call);
        Assert.Equal("POST", prov.Call!.Method);
        Assert.Equal("/v1/chat/completions", prov.Call.Path);
        Assert.Single(prov.Call.Headers);
        Assert.Equal("Content-Type", prov.Call.Headers[0].Name);
        Assert.IsType<LiteralHeaderValue>(prov.Call.Headers[0].Value);
        Assert.NotNull(prov.Response);
        Assert.Equal("choices[0].message.content", prov.Response!.TextSelector);
        Assert.Null(prov.Response.ToolCalls);
        Assert.Null(prov.Response.FinishReason);
    }

    // ── env() expression ──────────────────────────────────────────────────────

    [Fact]
    public void Parser_parses_env_expression()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider P {
                base_url "https://x.com"
                api_key env("MY_SECRET_KEY")
                call {
                    method POST
                    path "/api"
                    headers { }
                    body { }
                }
                response {
                    text "result"
                }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        var prov = program!.Declarations.OfType<ProviderDecl>().Single();
        Assert.NotNull(prov.ApiKey);
        Assert.Equal("MY_SECRET_KEY", prov.ApiKey!.EnvVarName);
    }

    // ── Header value concatenation ────────────────────────────────────────────

    [Fact]
    public void Parser_parses_header_concatenation()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/api"
                    headers {
                        "Authorization" "Bearer " + api_key
                    }
                    body { }
                }
                response {
                    text "result"
                }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        var prov = program!.Declarations.OfType<ProviderDecl>().Single();
        var authHeader = prov.Call!.Headers.Single(h => h.Name == "Authorization");
        var concat = Assert.IsType<ConcatHeaderValue>(authHeader.Value);
        Assert.Equal(2, concat.Parts.Count);
        var lit = Assert.IsType<LiteralHeaderValue>(concat.Parts[0]);
        Assert.Equal("Bearer ", lit.Text);
        var field = Assert.IsType<FieldHeaderValue>(concat.Parts[1]);
        Assert.Equal("api_key", field.FieldName);
    }

    // ── finish_reason block ───────────────────────────────────────────────────

    [Fact]
    public void Parser_parses_finish_reason_block()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/v1/chat"
                    headers { }
                    body { }
                }
                response {
                    finish_reason {
                        path "choices[0].finish_reason"
                        stop "stop"
                        tool_calls "tool_calls"
                    }
                    text "choices[0].message.content"
                    tool_calls "choices[0].message.tool_calls[*]" {
                        id "id"
                        tool_name "function.name"
                        args "function.arguments"
                    }
                }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        var prov = program!.Declarations.OfType<ProviderDecl>().Single();
        var resp = prov.Response!;

        Assert.NotNull(resp.FinishReason);
        Assert.Equal("choices[0].finish_reason", resp.FinishReason!.Selector);
        Assert.Equal("stop", resp.FinishReason.StopValue);
        Assert.Equal("tool_calls", resp.FinishReason.ToolCallsValue);

        Assert.Equal("choices[0].message.content", resp.TextSelector);

        Assert.NotNull(resp.ToolCalls);
        Assert.Equal("choices[0].message.tool_calls[*]", resp.ToolCalls!.Selector);
        Assert.Equal("id", resp.ToolCalls.IdSelector);
        Assert.Equal("function.name", resp.ToolCalls.ToolNameSelector);
        Assert.Equal("function.arguments", resp.ToolCalls.ArgsSelector);
    }

    // ── $messages with all 4 types ────────────────────────────────────────────

    [Fact]
    public void Parser_parses_messages_body_value_with_four_types()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/v1/chat"
                    headers { }
                    body {
                        "messages" $messages {
                            system -> { "role" "system" "content" $text }
                            user -> { "role" "user" "content" $text }
                            assistant -> {
                                "role" "assistant"
                                "content" $text
                                "tool_calls" $calls {
                                    "id" $call_id
                                    "name" $tool_name
                                }
                            }
                            tool_result -> { "role" "tool" "content" $result_json }
                        }
                    }
                }
                response {
                    text "result"
                }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        var prov = program!.Declarations.OfType<ProviderDecl>().Single();

        var bodyField = prov.Call!.Body.Single(f => f.Key == "messages");
        var msgs = Assert.IsType<MessagesBodyValue>(bodyField.Value);
        Assert.Equal(4, msgs.Mappings.Count);

        var systemMapping = msgs.Mappings.Single(m => m.MessageType == "system");
        Assert.Equal(2, systemMapping.Template.Count);

        var assistantMapping = msgs.Mappings.Single(m => m.MessageType == "assistant");
        var callsField = assistantMapping.Template.Single(f => f.Key == "tool_calls");
        var calls = Assert.IsType<CallsBodyValue>(callsField.Value);
        Assert.Equal(2, calls.ItemTemplate.Count);
    }

    // ── Agent new syntax ──────────────────────────────────────────────────────

    [Fact]
    public void Parser_parses_agent_with_new_provider_and_model_syntax()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider Sim { type simulated }
            agent MyAgent {
                provider Sim
                model "deepseek-v3"
                output R
                tools { }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        Assert.NotNull(program);

        var agent = program!.Declarations.OfType<AgentDecl>().Single();
        Assert.Equal("Sim", agent.ProviderRef);
        Assert.Equal("deepseek-v3", agent.ModelId);
    }

    [Fact]
    public void Parser_parses_qualified_provider_ref_in_agent()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            agent MyAgent {
                provider ext.MyProvider
                model "gpt-4o"
                output R
                tools { }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        var agent = program!.Declarations.OfType<AgentDecl>().Single();
        Assert.Equal("ext.MyProvider", agent.ProviderRef);
        Assert.Equal("gpt-4o", agent.ModelId);
    }

    // ── Agent old syntax (backward compat, SemanticValidator handles P11) ─────

    [Fact]
    public void Parser_accepts_old_agent_model_syntax_for_migration()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            agent OldAgent {
                model GPT
                output R
                tools { }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        var agent = program!.Declarations.OfType<AgentDecl>().Single();
        Assert.Equal("", agent.ProviderRef);
        Assert.Equal("GPT", agent.ModelId);
        Assert.Equal("GPT", agent.LogicalModelName); // backward-compat alias
    }

    // ── Body interpolation values ─────────────────────────────────────────────

    [Fact]
    public void Parser_parses_interpolation_body_values()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/api"
                    headers { }
                    body {
                        "model" $model
                        "system_prompt" $system
                        "output" $output_schema
                        "max_tokens" 4096
                        "stream" false
                    }
                }
                response { text "result" }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        var prov = program!.Declarations.OfType<ProviderDecl>().Single();
        var body = prov.Call!.Body;

        Assert.Equal("model", ((InterpolationBodyValue)body[0].Value).VarName);
        Assert.Equal("system", ((InterpolationBodyValue)body[1].Value).VarName);
        Assert.Equal("output_schema", ((InterpolationBodyValue)body[2].Value).VarName);
        Assert.Equal(4096L, ((IntBodyValue)body[3].Value).Number);
        Assert.False(((BoolBodyValue)body[4].Value).Flag);
    }

    // ── Multiple providers ────────────────────────────────────────────────────

    [Fact]
    public void Parser_parses_multiple_providers_in_one_file()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider Sim { type simulated }
            provider Prod {
                base_url "https://api.example.com"
                api_key env("PROD_KEY")
                call {
                    method POST
                    path "/chat"
                    headers { }
                    body { }
                }
                response { text "result" }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        var providers = program!.Declarations.OfType<ProviderDecl>().ToList();
        Assert.Equal(2, providers.Count);
        Assert.Contains(providers, p => p.Name == "Sim" && p.IsSimulated);
        Assert.Contains(providers, p => p.Name == "Prod" && !p.IsSimulated);
    }

    // ── $tools body value ─────────────────────────────────────────────────────

    [Fact]
    public void Parser_parses_tools_body_value()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider P {
                base_url "https://x.com"
                api_key env("K")
                call {
                    method POST
                    path "/api"
                    headers { }
                    body {
                        "tools" $tools {
                            "name" $name
                            "description" "a tool"
                            "input_schema" $input_schema
                        }
                    }
                }
                response { text "result" }
            }
            """;

        var (program, errors) = Parse(source);
        Assert.Empty(errors);
        var prov = program!.Declarations.OfType<ProviderDecl>().Single();
        var toolsField = prov.Call!.Body.Single(f => f.Key == "tools");
        var tools = Assert.IsType<ToolsBodyValue>(toolsField.Value);
        Assert.Equal(3, tools.ItemTemplate.Count);
        Assert.Equal("name", ((InterpolationBodyValue)tools.ItemTemplate[0].Value).VarName);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static (ProgramNode? Program, IReadOnlyList<Diagnostic> Errors) Parse(string source)
    {
        var (tokens, lexErrors) = new Lexer(source, "test.mail").Tokenize();
        if (lexErrors.Any(e => e.Severity == DiagnosticSeverity.Error))
            return (null, lexErrors);
        return new Parser(tokens).Parse();
    }
}
