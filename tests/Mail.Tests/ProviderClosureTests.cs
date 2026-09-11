using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Mail.Cli;
using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using Xunit;

namespace Mail.Tests;

/// <summary>
/// Acceptance tests for VALIDATION_provider-declaration_2026-09-11 closure work.
/// Covers: finish_reason branching, tool call validation, selector cardinality,
/// body limit, non-POST rejection, redirect rejection, DotEnvLoader snapshot,
/// and ProviderRegistrar simulator factory.
/// </summary>
public class ProviderClosureTests
{
    // ── Shared helpers ─────────────────────────────────────────────────────────

    private const string Scaffold = """
        schema R { x: String }
        workflow W { input R output R finish with input }
        """;

    private static ProviderDecl CompileProvider(string providerSource)
    {
        var source = Scaffold + "\n" + providerSource;
        var (plan, diagnostics) = MailCompiler.Compile(source, "test.mail");
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return plan!.Providers.Values.Single();
    }

    private static HttpModelProvider MakeProvider(
        ProviderDecl decl, HttpMessageHandler handler, string apiKey = "test-key")
        => new(decl, apiKey, new HttpClient(handler));

    private static ModelRequest SimpleRequest() =>
        new("m", [new UserMessage("hi")], [], """{"type":"object"}""");

    private static readonly string OpenAiSource = """
        provider P {
            base_url "https://api.example.com"
            api_key env("KEY")
            call {
                method POST
                path "/v1/chat/completions"
                headers { "Content-Type" "application/json" "Authorization" "Bearer " + api_key }
                body { "model" $model "messages" $messages { user -> { "role" "user" "content" $text } } }
            }
            response {
                finish_reason { path "choices[0].finish_reason" stop "stop" tool_calls "tool_calls" }
                text "choices[0].message.content"
                tool_calls "choices[0].message.tool_calls[*]" {
                    id "id"
                    tool_name "function.name"
                    args "function.arguments"
                }
            }
        }
        """;

    // Anthropic-style provider: filter-based selector for tool_calls.
    private static readonly string AnthropicSource = """
        provider P {
            base_url "https://api.anthropic.com"
            api_key env("KEY")
            call {
                method POST
                path "/v1/messages"
                headers { "Content-Type" "application/json" "x-api-key" api_key }
                body { "model" $model "messages" $messages { user -> { "role" "user" "content" $text } } }
            }
            response {
                finish_reason { path "stop_reason" stop "end_turn" tool_calls "tool_use" }
                text "content[type=text].text"
                tool_calls "content[type=tool_use]" {
                    id "id"
                    tool_name "name"
                    args "input"
                }
            }
        }
        """;

    // ── finish_reason: stop discards tool_calls ────────────────────────────────

    [Fact]
    public async Task Stop_with_tool_calls_present_discards_tool_calls_returns_text()
    {
        const string response = """
            {
                "choices": [{
                    "message": {
                        "role": "assistant",
                        "content": "Final answer",
                        "tool_calls": [{"id":"c1","function":{"name":"T","arguments":"{}"}}]
                    },
                    "finish_reason": "stop"
                }]
            }
            """;

        var decl = CompileProvider(OpenAiSource);
        var result = await MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
            .CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.Equal("Final answer", result.Text);
        Assert.Null(result.ToolCalls);
    }

    // ── finish_reason: tool_calls preserves text ───────────────────────────────

    [Fact]
    public async Task Tool_calls_branch_preserves_text_for_history()
    {
        const string response = """
            {
                "choices": [{
                    "message": {
                        "role": "assistant",
                        "content": "I will call a tool.",
                        "tool_calls": [{"id":"c1","function":{"name":"T","arguments":"{}"}}]
                    },
                    "finish_reason": "tool_calls"
                }]
            }
            """;

        var decl = CompileProvider(OpenAiSource);
        var result = await MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
            .CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.Equal("I will call a tool.", result.Text);
        Assert.NotNull(result.ToolCalls);
        Assert.Single(result.ToolCalls!);
        Assert.Equal("c1", result.ToolCalls![0].CallId);
    }

    // ── finish_reason: tool_calls with empty list throws ──────────────────────

    [Fact]
    public async Task Tool_calls_finish_reason_with_empty_list_throws()
    {
        const string response = """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[]},"finish_reason":"tool_calls"}]}
            """;

        var decl = CompileProvider(OpenAiSource);
        await Assert.ThrowsAsync<InvalidProviderResponseException>(
            () => MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
                .CompleteAsync(SimpleRequest(), CancellationToken.None));
    }

    // ── finish_reason: omitted, inferred from presence/absence of tool_calls ──

    [Fact]
    public async Task Omitted_finish_reason_with_non_empty_tool_calls_uses_tool_calls_branch()
    {
        const string providerSource = """
            provider P {
                base_url "https://api.example.com"
                api_key env("KEY")
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

        const string response = """
            {
                "choices": [{
                    "message": {
                        "content": "calling",
                        "tool_calls": [{"id":"c1","function":{"name":"T","arguments":"{}"}}]
                    }
                }]
            }
            """;

        var decl = CompileProvider(providerSource);
        var result = await MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
            .CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.NotNull(result.ToolCalls);
        Assert.Equal("c1", result.ToolCalls![0].CallId);
    }

    [Fact]
    public async Task Omitted_finish_reason_with_empty_tool_calls_uses_stop_branch()
    {
        const string providerSource = """
            provider P {
                base_url "https://api.example.com"
                api_key env("KEY")
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

        const string response = """
            {"choices":[{"message":{"content":"done","tool_calls":[]}}]}
            """;

        var decl = CompileProvider(providerSource);
        var result = await MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
            .CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.Equal("done", result.Text);
        Assert.Null(result.ToolCalls);
    }

    // ── Tool call validation errors ────────────────────────────────────────────

    [Fact]
    public async Task Tool_call_missing_id_throws_InvalidProviderResponseException()
    {
        const string response = """
            {
                "choices": [{
                    "message": {
                        "tool_calls": [{"function":{"name":"T","arguments":"{}"}}]
                    },
                    "finish_reason": "tool_calls"
                }]
            }
            """;

        var decl = CompileProvider(OpenAiSource);
        await Assert.ThrowsAsync<InvalidProviderResponseException>(
            () => MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
                .CompleteAsync(SimpleRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task Tool_call_missing_name_throws_InvalidProviderResponseException()
    {
        const string response = """
            {
                "choices": [{
                    "message": {
                        "tool_calls": [{"id":"c1","function":{"arguments":"{}"}}]
                    },
                    "finish_reason": "tool_calls"
                }]
            }
            """;

        var decl = CompileProvider(OpenAiSource);
        await Assert.ThrowsAsync<InvalidProviderResponseException>(
            () => MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
                .CompleteAsync(SimpleRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task Tool_call_duplicate_ids_throws_DuplicateCallIdException()
    {
        const string response = """
            {
                "choices": [{
                    "message": {
                        "tool_calls": [
                            {"id":"same","function":{"name":"A","arguments":"{}"}},
                            {"id":"same","function":{"name":"B","arguments":"{}"}}
                        ]
                    },
                    "finish_reason": "tool_calls"
                }]
            }
            """;

        var decl = CompileProvider(OpenAiSource);
        await Assert.ThrowsAsync<DuplicateCallIdException>(
            () => MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
                .CompleteAsync(SimpleRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task Tool_call_unparseable_json_args_throws_ProviderResponseException()
    {
        const string response = """
            {
                "choices": [{
                    "message": {
                        "tool_calls": [{"id":"c1","function":{"name":"T","arguments":"not-json"}}]
                    },
                    "finish_reason": "tool_calls"
                }]
            }
            """;

        var decl = CompileProvider(OpenAiSource);
        await Assert.ThrowsAsync<ProviderResponseException>(
            () => MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
                .CompleteAsync(SimpleRequest(), CancellationToken.None));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    public async Task Tool_call_non_object_json_args_throws_ProviderResponseException(string args)
    {
        // arguments is a raw JSON value (array/null/number), not a JSON string or object.
        var rawJson = $$$"""
            {
                "choices": [{
                    "message": {
                        "tool_calls": [{"id":"c1","function":{"name":"T","arguments":{{{args}}}}}]
                    },
                    "finish_reason": "tool_calls"
                }]
            }
            """;

        var decl = CompileProvider(OpenAiSource);
        await Assert.ThrowsAsync<ProviderResponseException>(
            () => MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, rawJson))
                .CompleteAsync(SimpleRequest(), CancellationToken.None));
    }

    // ── Selector cardinality: filter returns all matches ──────────────────────

    [Fact]
    public async Task Filter_returns_all_matching_tool_use_elements()
    {
        const string response = """
            {
                "stop_reason": "tool_use",
                "content": [
                    {"type": "tool_use", "id": "c1", "name": "Tool1", "input": {"x": 1}},
                    {"type": "text",     "text": "thinking..."},
                    {"type": "tool_use", "id": "c2", "name": "Tool2", "input": {"y": 2}}
                ]
            }
            """;

        var decl = CompileProvider(AnthropicSource);
        var result = await MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
            .CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.NotNull(result.ToolCalls);
        Assert.Equal(2, result.ToolCalls!.Count);
        Assert.Equal("c1", result.ToolCalls[0].CallId);
        Assert.Equal("Tool1", result.ToolCalls[0].ToolName);
        Assert.Equal("c2", result.ToolCalls[1].CallId);
        Assert.Equal("Tool2", result.ToolCalls[1].ToolName);
    }

    [Fact]
    public async Task Filter_with_zero_matches_produces_empty_result()
    {
        const string response = """
            {
                "stop_reason": "end_turn",
                "content": [
                    {"type": "text", "text": "No tools needed."}
                ]
            }
            """;

        var decl = CompileProvider(AnthropicSource);
        var result = await MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
            .CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.Equal("No tools needed.", result.Text);
        Assert.Null(result.ToolCalls);
    }

    // ── Multiple text nodes concatenated ──────────────────────────────────────

    [Fact]
    public async Task Multiple_text_nodes_are_concatenated_with_newline()
    {
        const string providerSource = """
            provider P {
                base_url "https://api.example.com"
                api_key env("KEY")
                call { method POST path "/v1" headers { } body { } }
                response { text "content[type=text].text" }
            }
            """;

        const string response = """
            {
                "content": [
                    {"type": "text", "text": "Hello"},
                    {"type": "thinking", "text": "..."},
                    {"type": "text", "text": "World"}
                ]
            }
            """;

        var decl = CompileProvider(providerSource);
        var result = await MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
            .CompleteAsync(SimpleRequest(), CancellationToken.None);

        Assert.Equal("Hello\nWorld", result.Text);
    }

    // ── Body size limit ───────────────────────────────────────────────────────

    [Fact]
    public async Task Body_exceeding_4MiB_throws_ProviderResponseException_with_code()
    {
        var oversizedBody = new string('x', 4 * 1024 * 1024 + 10); // > 4 MiB

        var decl = CompileProvider(OpenAiSource);
        var ex = await Assert.ThrowsAsync<ProviderResponseException>(
            () => MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, oversizedBody))
                .CompleteAsync(SimpleRequest(), CancellationToken.None));

        Assert.Contains("MAIL-PROVIDER-003", ex.Message);
    }

    // ── Non-POST method rejection ──────────────────────────────────────────────

    [Fact]
    public async Task Non_POST_method_throws_ProviderConfigurationException()
    {
        const string providerSource = """
            provider P {
                base_url "https://api.example.com"
                api_key env("KEY")
                call {
                    method GET
                    path "/v1"
                    headers { }
                    body { }
                }
                response { text "result" }
            }
            """;

        var decl = CompileProvider(providerSource);
        var ex = await Assert.ThrowsAsync<ProviderConfigurationException>(
            () => MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, "{}"))
                .CompleteAsync(SimpleRequest(), CancellationToken.None));

        Assert.Contains("GET", ex.Message);
        Assert.Contains("MAIL-CONFIG-003", ex.Message);
    }

    // ── finish_reason=tool_calls with no tool_calls selector ─────────────────

    [Fact]
    public async Task Tool_calls_branch_without_selector_declared_throws_InvalidProviderResponseException()
    {
        // Provider has finish_reason configured but no tool_calls block in response.
        const string providerSource = """
            provider P {
                base_url "https://api.example.com"
                api_key env("KEY")
                call { method POST path "/v1" headers { } body { } }
                response {
                    finish_reason { path "finish_reason" stop "stop" tool_calls "tool_calls" }
                    text "content"
                }
            }
            """;

        const string response = """{"finish_reason":"tool_calls","content":"no selector"}""";

        var decl = CompileProvider(providerSource);
        await Assert.ThrowsAsync<InvalidProviderResponseException>(
            () => MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
                .CompleteAsync(SimpleRequest(), CancellationToken.None));
    }

    // ── Tool call with absent args field ──────────────────────────────────────

    [Fact]
    public async Task Tool_call_absent_args_field_throws_ProviderResponseException()
    {
        const string response = """
            {
                "choices": [{
                    "message": {
                        "tool_calls": [{"id":"c1","function":{"name":"T"}}]
                    },
                    "finish_reason": "tool_calls"
                }]
            }
            """;

        var decl = CompileProvider(OpenAiSource);
        await Assert.ThrowsAsync<ProviderResponseException>(
            () => MakeProvider(decl, new FakeHttpHandler(HttpStatusCode.OK, response))
                .CompleteAsync(SimpleRequest(), CancellationToken.None));
    }

    // ── 302 redirect rejected by SocketsHttpHandler with AllowAutoRedirect=false

    // Plain-HTTP provider for the redirect test — SocketsHttpHandler uses the ConnectCallback
    // stream directly for HTTP (no TLS), so the fake stream works without a real server.
    private static readonly string RedirectTestSource = """
        provider P {
            base_url "http://api.example.local"
            api_key env("KEY")
            call { method POST path "/v1" headers { } body { } }
            response { text "choices[0].message.content" }
        }
        """;

    [Fact]
    public async Task Real_SocketsHttpHandler_AllowAutoRedirect_false_does_not_follow_302()
    {
        // Uses the real SocketsHttpHandler (same as production) with AllowAutoRedirect = false.
        // ConnectCallback intercepts the TCP layer so no real network call is made.
        int connectionAttempts = 0;
        const string fakeRedirectResponse =
            "HTTP/1.1 302 Found\r\n" +
            "Location: http://should-not-be-followed.example.local/\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";

        using var sockHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = (_, ct) =>
            {
                Interlocked.Increment(ref connectionAttempts);
                return ValueTask.FromResult<Stream>(new FakeHttpResponseStream(fakeRedirectResponse));
            }
        };

        var decl = CompileProvider(RedirectTestSource);
        using var client = new HttpClient(sockHandler);
        var ex = await Assert.ThrowsAsync<ProviderHttpException>(
            () => new HttpModelProvider(decl, "key", client)
                .CompleteAsync(SimpleRequest(), CancellationToken.None));

        Assert.Equal(302, ex.StatusCode);
        Assert.Equal(1, connectionAttempts); // redirect was not followed; exactly one connection
    }

    // ── DotEnvLoader: snapshot captured at Load() time ────────────────────────

    [Fact]
    public void DotEnvLoader_snapshot_stable_across_subsequent_env_mutations()
    {
        var key = $"MAIL_TEST_CLOSURE_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(key, "before-load");
        try
        {
            // Load() snapshots the OS env — "before-load" captured.
            var loader = DotEnvLoader.Load();
            Assert.Equal("before-load", loader.Resolve(key));

            // Mutate OS env after load — snapshot must not change.
            Environment.SetEnvironmentVariable(key, "after-load");
            Assert.Equal("before-load", loader.Resolve(key));
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public void DotEnvLoader_ForTesting_file_override_wins_when_os_snapshot_is_empty()
    {
        var key = $"MAIL_TEST_CLOSURE_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(key, "os-live-value");
        try
        {
            // ForTesting has an empty OS snapshot regardless of live OS env.
            var loader = DotEnvLoader.ForTesting(new Dictionary<string, string> { [key] = "file-override" });
            Assert.Equal("file-override", loader.Resolve(key));
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    // ── ProviderRegistrar: simulator factory ──────────────────────────────────

    [Fact]
    public void RegisterDeclaredProviders_uses_simulator_factory_for_simulated_providers()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider Sim { type simulated }
            """;
        var (plan, _) = MailCompiler.Compile(source, "test.mail");
        var registry = new ProviderRegistry();
        var env = DotEnvLoader.ForTesting(new Dictionary<string, string>());
        using var client = new HttpClient();
        var factoryCalled = false;
        var sentinel = new SimulatedModelProvider([]);

        ProviderRegistrar.RegisterDeclaredProviders(registry, plan!, env, client,
            name => { factoryCalled = true; return sentinel; });

        Assert.True(factoryCalled);
        Assert.Same(sentinel, registry.Resolve("Sim"));
    }

    [Fact]
    public void RegisterDeclaredProviders_without_factory_uses_SimulatedModelProvider()
    {
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider Sim { type simulated }
            """;
        var (plan, _) = MailCompiler.Compile(source, "test.mail");
        var registry = new ProviderRegistry();
        var env = DotEnvLoader.ForTesting(new Dictionary<string, string>());
        using var client = new HttpClient();

        ProviderRegistrar.RegisterDeclaredProviders(registry, plan!, env, client);

        Assert.IsType<SimulatedModelProvider>(registry.Resolve("Sim"));
    }

    // ── validate does not require credentials ─────────────────────────────────

    [Fact]
    public void Validate_compiles_http_provider_without_credentials_present()
    {
        // The compiler must not check whether the env var actually exists.
        const string source = """
            schema R { x: String }
            workflow W { input R output R finish with input }
            provider P {
                base_url "https://api.example.com"
                api_key env("CREDENTIAL_NOT_SET_IN_TEST_ENV")
                call { method POST path "/v1" headers { } body { "model" $model } }
                response { text "result" }
            }
            """;

        var (plan, diagnostics) = MailCompiler.Compile(source, "test.mail");
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.NotNull(plan);
    }

    // ── 3-turn fixture through WorkflowExecutor with real tool dispatch ──────────

    private const string ThreeTurnSource = """
        schema Req  { text: String }
        schema Resp { result: String }
        tool Echo { input { text: String } output { result: String } }
        provider Http {
            base_url "https://api.example.com"
            api_key env("KEY")
            call { method POST path "/v1" headers { } body { } }
            response {
                finish_reason { path "choices[0].finish_reason" stop "stop" tool_calls "tool_calls" }
                text "choices[0].message.content"
                tool_calls "choices[0].message.tool_calls[*]" {
                    id "id"
                    tool_name "function.name"
                    args "function.arguments"
                }
            }
        }
        agent A {
            provider Http
            model "gpt-4"
            output Resp
            tools { allow Echo }
        }
        workflow W {
            input Req
            output Resp
            step s { agent A context { input } save as result }
            finish with result
        }
        """;

    [Fact]
    public async Task Three_turn_tool_dispatch_via_WorkflowExecutor_end_to_end()
    {
        // Turn 1: model calls Echo with {"text":"hello"}
        // Turn 2: after receiving Echo result, model returns final JSON output
        var responses = new Queue<string>(
        [
            """{"choices":[{"message":{"content":"calling echo","tool_calls":[{"id":"c1","function":{"name":"Echo","arguments":"{\"text\":\"hello\"}"}}]},"finish_reason":"tool_calls"}]}""",
            """{"choices":[{"message":{"content":"{\"result\":\"hello\"}"},"finish_reason":"stop"}]}"""
        ]);

        var (plan, diag) = MailCompiler.Compile(ThreeTurnSource, "test.mail");
        Assert.DoesNotContain(diag, d => d.Severity == DiagnosticSeverity.Error);

        FieldContract[] textFields   = [new("text",   MailTypeKind.String, Required: true)];
        FieldContract[] resultFields = [new("result", MailTypeKind.String, Required: true)];
        var tools = new ToolRegistry();
        tools.Register("Echo", new EchoToolImpl(), textFields, resultFields);

        var provider  = new HttpModelProvider(plan!.Providers["Http"], "test-key",
                            new HttpClient(new QueuedHttpHandler(responses)));
        var providers = new ProviderRegistry();
        providers.Register("Http", provider);

        var executor = new WorkflowExecutor(plan!, tools, providers, ExecutionLimits.Default);
        var input = new MailSchema("Req",
            ImmutableDictionary<string, MailValue>.Empty.Add("text", new MailString("hello")));

        var result = await executor.RunAsync(input, CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        // Confirm tool was dispatched (turn 1 → Echo invoked → turn 2 with result → text)
        Assert.Contains(result.Events, e => e.Kind == "tool.started");
        // Final output matches what the Echo tool returned
        var output = Assert.IsType<MailSchema>(result.Output);
        Assert.Equal("hello", ((MailString)output.Fields["result"]).Value);
    }
}

// ── Test infrastructure ────────────────────────────────────────────────────────

internal sealed class EchoToolImpl : IToolImplementation
{
    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        var text = ((MailString)input.Fields["text"]).Value;
        return Task.FromResult(new MailSchema("Echo_output",
            ImmutableDictionary<string, MailValue>.Empty.Add("result", new MailString(text))));
    }
}

// ── Test infrastructure ────────────────────────────────────────────────────────

internal sealed class QueuedHttpHandler(Queue<string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var body = responses.Dequeue();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}

// Readable/writable stream that feeds back a fixed HTTP/1.1 response string.
// Used with SocketsHttpHandler.ConnectCallback to intercept transport without a real server.
// Writes are silently discarded (simulates the server reading the request).
internal sealed class FakeHttpResponseStream(string response) : Stream
{
    private readonly byte[] _data = Encoding.ASCII.GetBytes(response);
    private int _pos;

    public override bool CanRead  => true;
    public override bool CanWrite => true;
    public override bool CanSeek  => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position { get => _pos; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(count, _data.Length - _pos);
        if (n <= 0) return 0;
        Array.Copy(_data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = Math.Min(buffer.Length, _data.Length - _pos);
        if (n > 0)
        {
            _data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
        }
        return ValueTask.FromResult(n);
    }

    public override void Write(byte[] buffer, int offset, int count) { }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => ValueTask.CompletedTask;
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => Task.CompletedTask;
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value)                => throw new NotSupportedException();
}
