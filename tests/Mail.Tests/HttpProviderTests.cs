using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using Xunit;

namespace Mail.Tests;

public class HttpProviderTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private const string Scaffold = """
        schema R { x: String }
        workflow W { input R output R finish with input }
        """;

    private static ProviderDecl CompileProvider(string providerSource)
    {
        var source = Scaffold + "\n" + providerSource;
        var (plan, diagnostics) = MailCompiler.Compile(source, "test.mail");
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.NotNull(plan);
        return plan!.Providers.Values.Single();
    }

    private static HttpModelProvider MakeProvider(
        ProviderDecl decl,
        FakeHttpHandler handler,
        string apiKey = "test-api-key")
        => new(decl, apiKey, new HttpClient(handler));

    private static ModelRequest SimpleRequest(
        string modelId = "gpt-4",
        string? system = null,
        string? userText = "Hello") =>
        new(modelId,
            BuildMessages(system, userText),
            [],
            """{"type":"object","properties":{}}""");

    private static IReadOnlyList<Message> BuildMessages(string? system, string? userText)
    {
        var msgs = new List<Message>();
        if (system is not null) msgs.Add(new SystemMessage(system));
        if (userText is not null) msgs.Add(new UserMessage(userText));
        return msgs;
    }

    private static string OpenAiTextResponse(string text, string finishReason = "stop") => $$"""
        {"choices":[{"message":{"role":"assistant","content":"{{text}}"},"finish_reason":"{{finishReason}}"}]}
        """;

    private static string OpenAiToolCallsResponse(string finishReason = "tool_calls") => """
        {
            "choices": [{
                "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                        {"id":"call1","function":{"name":"MyTool","arguments":"{\"x\":42}"}}
                    ]
                },
                "finish_reason": "tool_calls"
            }]
        }
        """;

    private static readonly string OpenAiProviderSource = """
        provider P {
            base_url "https://api.example.com"
            api_key env("KEY")
            call {
                method POST
                path "/v1/chat/completions"
                headers {
                    "Content-Type" "application/json"
                    "Authorization" "Bearer " + api_key
                }
                body {
                    "model" $model
                    "messages" $messages {
                        system -> { "role" "system" "content" $text }
                        user   -> { "role" "user"   "content" $text }
                        assistant -> {
                            "role" "assistant"
                            "content" $text
                            "tool_calls" $calls {
                                "id" $call_id
                                "type" "function"
                            }
                        }
                        tool_result -> { "role" "tool" "tool_call_id" $call_id "content" $result_json }
                    }
                }
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

    // ── Text response extraction ───────────────────────────────────────────────

    [Fact]
    public async Task Text_response_extracts_text()
    {
        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, OpenAiTextResponse("Hello, world!"));
        var provider = MakeProvider(decl, handler);

        var result = await provider.CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.Equal("Hello, world!", result.Text);
        Assert.Null(result.ToolCalls);
    }

    // ── Tool calls extraction ─────────────────────────────────────────────────

    [Fact]
    public async Task ToolCalls_response_extracts_tool_calls()
    {
        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, OpenAiToolCallsResponse());
        var provider = MakeProvider(decl, handler);

        var result = await provider.CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.NotNull(result.ToolCalls);
        Assert.Single(result.ToolCalls!);
        Assert.Equal("call1", result.ToolCalls![0].CallId);
        Assert.Equal("MyTool", result.ToolCalls[0].ToolName);
        Assert.Equal(42, result.ToolCalls[0].Arguments["x"].GetInt32());
    }

    // ── Adjustment A: both text and tool_calls preserved simultaneously ────────

    [Fact]
    public async Task Both_text_and_tool_calls_preserved_simultaneously()
    {
        const string bothResponse = """
            {
                "choices": [{
                    "message": {
                        "role": "assistant",
                        "content": "Calling a tool",
                        "tool_calls": [
                            {"id":"c1","function":{"name":"T","arguments":"{}"}}
                        ]
                    },
                    "finish_reason": "tool_calls"
                }]
            }
            """;

        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, bothResponse);
        var provider = MakeProvider(decl, handler);

        var result = await provider.CompleteAsync(SimpleRequest(), CancellationToken.None);

        // finish_reason is "tool_calls" but text is still extracted
        Assert.Equal("Calling a tool", result.Text);
        Assert.NotNull(result.ToolCalls);
        Assert.Single(result.ToolCalls!);
        Assert.Equal("c1", result.ToolCalls![0].CallId);
    }

    // ── HTTP errors ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Http_4xx_throws_ProviderHttpException()
    {
        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.Unauthorized, """{"error":"invalid_api_key"}""");
        var provider = MakeProvider(decl, handler);

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(
            () => provider.CompleteAsync(SimpleRequest(), CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("P", ex.ProviderName);
    }

    [Fact]
    public async Task Http_error_body_redacts_api_key()
    {
        var apiKey = "sk-supersecret-abc123";
        var errorBody = $$$"""{"error":"Forbidden: key {{{apiKey}}} rejected"}""";

        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.Forbidden, errorBody);
        var provider = MakeProvider(decl, handler, apiKey);

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(
            () => provider.CompleteAsync(SimpleRequest(), CancellationToken.None));

        Assert.DoesNotContain(apiKey, ex.Message);
        Assert.Contains("[redacted]", ex.Message);
    }

    [Fact]
    public async Task Redaction_before_truncation_prevents_secret_leak()
    {
        // Secret appears near the 256-byte boundary; without redact-before-truncate it would leak
        var apiKey = "MYSECRETKEY";
        var padding = new string('A', 250);
        var longErrorBody = padding + apiKey + new string('B', 200); // secret at position 250

        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.BadRequest, longErrorBody);
        var provider = MakeProvider(decl, handler, apiKey);

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(
            () => provider.CompleteAsync(SimpleRequest(), CancellationToken.None));

        Assert.DoesNotContain(apiKey, ex.Message);
    }

    // ── Response parsing errors ───────────────────────────────────────────────

    [Fact]
    public async Task Invalid_json_response_throws_ProviderResponseException()
    {
        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "not json at all");
        var provider = MakeProvider(decl, handler);

        await Assert.ThrowsAsync<ProviderResponseException>(
            () => provider.CompleteAsync(SimpleRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task No_text_no_tool_calls_throws_InvalidProviderResponseException()
    {
        // Valid JSON but selectors return null / empty
        const string emptyResponse = """{"choices":[{"message":{"role":"assistant"},"finish_reason":"stop"}]}""";

        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, emptyResponse);
        var provider = MakeProvider(decl, handler);

        await Assert.ThrowsAsync<InvalidProviderResponseException>(
            () => provider.CompleteAsync(SimpleRequest(), CancellationToken.None));
    }

    // ── finish_reason validation ──────────────────────────────────────────────

    [Fact]
    public async Task FinishReason_unexpected_value_throws_ProviderResponseException()
    {
        // "length" is not declared as stop or tool_calls value
        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, OpenAiTextResponse("truncated", "length"));
        var provider = MakeProvider(decl, handler);

        var ex = await Assert.ThrowsAsync<ProviderResponseException>(
            () => provider.CompleteAsync(SimpleRequest(), CancellationToken.None));

        Assert.Contains("length", ex.Message);
    }

    [Fact]
    public async Task FinishReason_stop_does_not_throw()
    {
        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, OpenAiTextResponse("OK", "stop"));
        var provider = MakeProvider(decl, handler);

        var result = await provider.CompleteAsync(SimpleRequest(), CancellationToken.None);
        Assert.Equal("OK", result.Text);
    }

    // ── Header injection ──────────────────────────────────────────────────────

    [Fact]
    public async Task Auth_header_concat_sends_bearer_token()
    {
        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, OpenAiTextResponse("hi"));
        var provider = MakeProvider(decl, handler, apiKey: "mykey123");

        await provider.CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.TryGetValues("Authorization", out var vals));
        Assert.Equal("Bearer mykey123", vals.Single());
    }

    // ── Body template engine ──────────────────────────────────────────────────

    [Fact]
    public async Task Model_interpolation_in_body()
    {
        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, OpenAiTextResponse("ok"));
        var provider = MakeProvider(decl, handler);

        await provider.CompleteAsync(SimpleRequest(modelId: "claude-sonnet-4-6"), CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal("claude-sonnet-4-6", body.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Messages_body_serialized_per_mapping()
    {
        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, OpenAiTextResponse("pong"));
        var provider = MakeProvider(decl, handler);

        var request = new ModelRequest(
            "gpt-4",
            [new SystemMessage("Be helpful"), new UserMessage("Ping")],
            [],
            """{"type":"object"}""");

        await provider.CompleteAsync(request, CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        var msgs = body.GetProperty("messages");
        Assert.Equal(2, msgs.GetArrayLength());
        Assert.Equal("system", msgs[0].GetProperty("role").GetString());
        Assert.Equal("Be helpful", msgs[0].GetProperty("content").GetString());
        Assert.Equal("user", msgs[1].GetProperty("role").GetString());
        Assert.Equal("Ping", msgs[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Tools_omitted_when_empty_included_when_present()
    {
        const string providerWithTools = """
            provider P {
                base_url "https://api.example.com"
                api_key env("KEY")
                call {
                    method POST
                    path "/v1/chat"
                    headers { }
                    body {
                        "model" $model
                        "tools" $tools {
                            "name" $name
                            "parameters" $input_schema
                        }
                    }
                }
                response { text "choices[0].message.content" }
            }
            """;

        var decl = CompileProvider(providerWithTools);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, OpenAiTextResponse("ok"));
        var provider = MakeProvider(decl, handler);

        // No tools: "tools" field omitted
        await provider.CompleteAsync(SimpleRequest(), CancellationToken.None);
        var bodyNoTools = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.False(bodyNoTools.TryGetProperty("tools", out _));

        // With tools: "tools" field present
        var requestWithTool = new ModelRequest(
            "gpt-4",
            [new UserMessage("hi")],
            [new ToolDefinition("DoWork", """{"type":"object"}""", """{"type":"string"}""")],
            """{"type":"object"}""");

        await provider.CompleteAsync(requestWithTool, CancellationToken.None);
        var bodyWithTools = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.True(bodyWithTools.TryGetProperty("tools", out var toolsElem));
        Assert.Equal(1, toolsElem.GetArrayLength());
        Assert.Equal("DoWork", toolsElem[0].GetProperty("name").GetString());
    }

    // ── Tool call args parsing (OpenAI JSON-string format) ────────────────────

    [Fact]
    public async Task ToolCalls_with_json_string_arguments_parsed()
    {
        // OpenAI returns arguments as a JSON-encoded string, not an object
        const string response = """
            {
                "choices": [{
                    "message": {
                        "role": "assistant",
                        "content": null,
                        "tool_calls": [
                            {"id":"c1","function":{"name":"Search","arguments":"{\"query\":\"test\",\"n\":5}"}}
                        ]
                    },
                    "finish_reason": "tool_calls"
                }]
            }
            """;

        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, response);
        var provider = MakeProvider(decl, handler);

        var result = await provider.CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.NotNull(result.ToolCalls);
        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("Search", call.ToolName);
        Assert.Equal("test", call.Arguments["query"].GetString());
        Assert.Equal(5, call.Arguments["n"].GetInt32());
    }

    // ── Network error ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Network_error_throws_ProviderHttpException_with_status_0()
    {
        var decl = CompileProvider(OpenAiProviderSource);
        var handler = new FakeHttpHandler(null, "");
        var provider = MakeProvider(decl, handler);

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(
            () => provider.CompleteAsync(SimpleRequest(), CancellationToken.None));

        Assert.Equal(0, ex.StatusCode);
        Assert.Equal("P", ex.ProviderName);
    }

    // ── Provider without finish_reason block ──────────────────────────────────

    [Fact]
    public async Task Provider_without_finish_reason_block_does_not_validate_finish_reason()
    {
        const string providerSource = """
            provider P {
                base_url "https://api.example.com"
                api_key env("KEY")
                call { method POST path "/v1" headers { } body { "model" $model } }
                response { text "choices[0].message.content" }
            }
            """;

        // Response has finish_reason "length" — no validation should occur
        var decl = CompileProvider(providerSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"length"}]}""");
        var provider = MakeProvider(decl, handler);

        var result = await provider.CompleteAsync(SimpleRequest(), CancellationToken.None);
        Assert.Equal("ok", result.Text);
    }

    // ── Selector edge cases ───────────────────────────────────────────────────

    [Fact]
    public async Task Filter_selector_extracts_correct_element()
    {
        const string providerSource = """
            provider P {
                base_url "https://api.example.com"
                api_key env("KEY")
                call { method POST path "/v1" headers { } body { } }
                response { text "content[type=text].text" }
            }
            """;

        const string anthropicStyleResponse = """
            {
                "content": [
                    {"type": "thinking", "text": "reasoning"},
                    {"type": "text", "text": "Hello there!"}
                ]
            }
            """;

        var decl = CompileProvider(providerSource);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, anthropicStyleResponse);
        var provider = MakeProvider(decl, handler);

        var result = await provider.CompleteAsync(SimpleRequest(), CancellationToken.None);
        Assert.Equal("Hello there!", result.Text);
    }
}

// ── Fake HTTP handler ──────────────────────────────────────────────────────────

internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode? _statusCode;
    private readonly string _body;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public FakeHttpHandler(HttpStatusCode? statusCode, string body)
    {
        _statusCode = statusCode;
        _body = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastRequestBody = request.Content is not null
            ? await request.Content.ReadAsStringAsync(ct)
            : null;

        if (_statusCode is null)
            throw new HttpRequestException("Simulated network failure.");

        return new HttpResponseMessage(_statusCode.Value)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        };
    }
}
