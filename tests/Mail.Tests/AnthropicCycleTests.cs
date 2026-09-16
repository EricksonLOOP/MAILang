using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using Xunit;

namespace Mail.Tests;

// Tests for the Anthropic tool cycle using the new array template syntax.
// All tests use simulated HTTP — no real API calls are made.
public class AnthropicCycleTests
{
    private const string AnthropicSource = """
        schema Req  { text: String }
        schema Resp { result: String }
        tool Echo { input { text: String } output { result: String } }
        provider Anthropic {
          base_url "https://api.anthropic.com"
          api_key  env("ANTHROPIC_API_KEY")
          call {
            method POST
            path   "/v1/messages"
            headers {
              "Content-Type"      "application/json"
              "x-api-key"         api_key
              "anthropic-version" "2023-06-01"
            }
            body {
              "model"      $model
              "max_tokens" 1024
              "system"     $system
              "messages"   $messages {
                user -> { "role" "user" "content" $text }
                assistant -> {
                  "role"    "assistant"
                  "content" [
                    ?$text { "type" "text" "text" $text }
                    ...$calls {
                      "type"  "tool_use"
                      "id"    $call_id
                      "name"  $tool_name
                      "input" $args
                    }
                  ]
                }
                tool_result -> {
                  "role"    "user"
                  "content" [
                    {
                      "type"        "tool_result"
                      "tool_use_id" $call_id
                      "content"     $result_json
                    }
                  ]
                }
              }
              "tools" $tools {
                "name"         $name
                "input_schema" $input_schema
              }
            }
          }
          response {
            finish_reason {
              path       "stop_reason"
              stop       "end_turn"
              tool_calls "tool_use"
            }
            text "content[type=text].text"
            tool_calls "content[type=tool_use]" {
              id        "id"
              tool_name "name"
              args      "input"
            }
          }
        }
        agent Writer {
          provider Anthropic
          model    "claude-3-5-sonnet-20241022"
          system   "Call Echo with the supplied text and return its result as JSON."
          input    Req
          output   Resp
          tools    { allow Echo }
        }
        workflow EchoViaAgent {
          input  Req
          output Resp
          step write {
            agent Writer input input context { input }
            save as reply
          }
          finish with reply
        }
        """;

    // Builds a response containing a single tool_use block (Anthropic format).
    private static string ToolCallResponse(string id, string name, string inputJson) =>
        $$"""{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"{{id}}","name":"{{name}}","input":{{inputJson}}}]}""";

    // Builds a response containing a text block (Anthropic format).
    // Uses JsonSerializer so that textContent is properly JSON-encoded.
    private static string TextResponse(string textContent) =>
        JsonSerializer.Serialize(new
        {
            stop_reason = "end_turn",
            content = new object[] { new { type = "text", text = textContent } }
        });

    private static ValidatedPlan CompileSource()
    {
        var (plan, diag) = MailCompiler.Compile(AnthropicSource, "test.mail");
        Assert.DoesNotContain(diag, d => d.Severity == DiagnosticSeverity.Error);
        return plan!;
    }

    private static HttpModelProvider MakeProvider(ValidatedPlan plan, HttpMessageHandler handler) =>
        new(plan.Providers["Anthropic"], "test-key", new HttpClient(handler));

    private static (ToolRegistry tools, ProviderRegistry providers) BuildRegistries(
        ValidatedPlan plan, HttpModelProvider provider)
    {
        FieldContract[] textIn  = [new("text",   MailTypeKind.String, Required: true)];
        FieldContract[] textOut = [new("result", MailTypeKind.String, Required: true)];
        var tools = new ToolRegistry();
        tools.Register("Echo", new EchoToolImpl(), textIn, textOut);
        var providers = new ProviderRegistry();
        providers.Register("Anthropic", provider);
        return (tools, providers);
    }

    // Builds a ToolCallRequest with a single string argument.
    private static ToolCallRequest MakeCall(string id, string argValue) =>
        new(id, "Echo",
            ImmutableDictionary<string, JsonElement>.Empty.Add("text",
                JsonDocument.Parse($"\"{argValue}\"").RootElement));

    // ── Single call + result ──────────────────────────────────────────────────

    [Fact]
    public async Task Single_call_and_result_second_request_has_correct_structure()
    {
        var responses = new Queue<string>([
            ToolCallResponse("c1", "Echo", """{"text":"hello"}"""),
            TextResponse("""{"result":"hello"}""")
        ]);

        var plan = CompileSource();
        var capturer = new CapturingQueuedHttpHandler(responses);
        var provider = MakeProvider(plan, capturer);
        var (tools, providers) = BuildRegistries(plan, provider);

        var executor = new WorkflowExecutor(plan, tools, providers, ExecutionLimits.Default);
        var input = new MailSchema("Req",
            ImmutableDictionary<string, MailValue>.Empty.Add("text", new MailString("hello")));

        var result = await executor.RunAsync(input, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(2, capturer.CapturedBodies.Count);

        var body2 = JsonDocument.Parse(capturer.CapturedBodies[1]).RootElement;
        var msgs = body2.GetProperty("messages");

        var assistantMsg = FindMessage(msgs, "assistant");
        Assert.NotNull(assistantMsg);
        var assistantContent = assistantMsg!.Value.GetProperty("content");
        Assert.Equal(1, assistantContent.GetArrayLength()); // no text block — call only
        Assert.Equal("tool_use", assistantContent[0].GetProperty("type").GetString());
        Assert.Equal("c1", assistantContent[0].GetProperty("id").GetString());
        Assert.Equal("Echo", assistantContent[0].GetProperty("name").GetString());

        var toolResultMsg = FindLastMessage(msgs, "user");
        Assert.NotNull(toolResultMsg);
        var toolResultContent = toolResultMsg!.Value.GetProperty("content")[0];
        Assert.Equal("tool_result", toolResultContent.GetProperty("type").GetString());
        Assert.Equal("c1", toolResultContent.GetProperty("tool_use_id").GetString());
    }

    // ── Text + call in assistant content ─────────────────────────────────────

    [Fact]
    public async Task Text_and_call_in_assistant_produces_text_block_followed_by_tool_use()
    {
        var plan = CompileSource();
        var call = MakeCall("c1", "hello");

        var messages = new List<Message>
        {
            new UserMessage("hello"),
            new AssistantMessage("thinking aloud", [call]),
            new ToolResultMessage("c1", "Echo", """{"result":"hello"}"""),
        };
        var request = new ModelRequest(
            "claude-3-5-sonnet-20241022", messages, [],
            """{"type":"object","properties":{}}""");

        var handler = new FakeHttpHandler(HttpStatusCode.OK, TextResponse("done"));
        var provider = MakeProvider(plan, handler);

        await provider.CompleteAsync(request, CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        var assistantMsg = FindMessage(body.GetProperty("messages"), "assistant")!.Value;
        var content = assistantMsg.GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("thinking aloud", content[0].GetProperty("text").GetString());
        Assert.Equal("tool_use", content[1].GetProperty("type").GetString());
        Assert.Equal("c1", content[1].GetProperty("id").GetString());
    }

    // ── Call-only assistant — no text block emitted ───────────────────────────

    [Fact]
    public async Task Call_only_assistant_has_no_text_block_in_content_array()
    {
        var plan = CompileSource();
        var call = MakeCall("c1", "hello");

        var messages = new List<Message>
        {
            new UserMessage("hello"),
            new AssistantMessage(null, [call]),
            new ToolResultMessage("c1", "Echo", """{"result":"hello"}"""),
        };
        var request = new ModelRequest(
            "claude-3-5-sonnet-20241022", messages, [],
            """{"type":"object","properties":{}}""");

        var handler = new FakeHttpHandler(HttpStatusCode.OK, TextResponse("done"));
        var provider = MakeProvider(plan, handler);

        await provider.CompleteAsync(request, CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        var assistantMsg = FindMessage(body.GetProperty("messages"), "assistant")!.Value;
        var content = assistantMsg.GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("tool_use", content[0].GetProperty("type").GetString());
        Assert.DoesNotContain(
            content.EnumerateArray(),
            block => block.GetProperty("type").GetString() == "text");
    }

    // ── Multiple calls + all results ──────────────────────────────────────────

    [Fact]
    public async Task Multiple_calls_all_ids_present_results_directly_follow_assistant()
    {
        var plan = CompileSource();
        var call1 = MakeCall("c1", "alpha");
        var call2 = MakeCall("c2", "beta");

        var messages = new List<Message>
        {
            new UserMessage("hello"),
            new AssistantMessage(null, [call1, call2]),
            new ToolResultMessage("c1", "Echo", """{"result":"alpha"}"""),
            new ToolResultMessage("c2", "Echo", """{"result":"beta"}"""),
        };
        var request = new ModelRequest(
            "claude-3-5-sonnet-20241022", messages, [],
            """{"type":"object","properties":{}}""");

        var handler = new FakeHttpHandler(HttpStatusCode.OK, TextResponse("done"));
        var provider = MakeProvider(plan, handler);
        await provider.CompleteAsync(request, CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        var msgs = body.GetProperty("messages");
        // user(0), assistant(1), tool_result_c1(2), tool_result_c2(3)
        Assert.Equal(4, msgs.GetArrayLength());

        var assistantContent = msgs[1].GetProperty("content");
        Assert.Equal(2, assistantContent.GetArrayLength());
        Assert.Equal("c1", assistantContent[0].GetProperty("id").GetString());
        Assert.Equal("c2", assistantContent[1].GetProperty("id").GetString());

        // Results directly follow assistant — no gaps
        var r1 = msgs[2].GetProperty("content")[0];
        Assert.Equal("tool_result", r1.GetProperty("type").GetString());
        Assert.Equal("c1", r1.GetProperty("tool_use_id").GetString());

        var r2 = msgs[3].GetProperty("content")[0];
        Assert.Equal("tool_result", r2.GetProperty("type").GetString());
        Assert.Equal("c2", r2.GetProperty("tool_use_id").GetString());
    }

    // ── Text-only assistant (direct provider test) ────────────────────────────

    [Fact]
    public async Task Text_only_assistant_content_is_single_text_block()
    {
        var plan = CompileSource();

        var messages = new List<Message>
        {
            new UserMessage("hello"),
            new AssistantMessage("I can help with that.", null),
            new UserMessage("go ahead"),
        };
        var request = new ModelRequest(
            "claude-3-5-sonnet-20241022", messages, [],
            """{"type":"object","properties":{}}""");

        var handler = new FakeHttpHandler(HttpStatusCode.OK, TextResponse("done"));
        var provider = MakeProvider(plan, handler);
        await provider.CompleteAsync(request, CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        var assistantMsg = FindMessage(body.GetProperty("messages"), "assistant")!.Value;
        var content = assistantMsg.GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("I can help with that.", content[0].GetProperty("text").GetString());
    }

    // ── Result with special characters ────────────────────────────────────────

    [Fact]
    public async Task Result_with_special_chars_is_preserved_verbatim_in_tool_result_content()
    {
        var plan = CompileSource();
        const string specialResult = """{"text":"line1\nline2","quote":"say \"hi\"","val":42}""";
        var call = MakeCall("c1", "x");

        var messages = new List<Message>
        {
            new UserMessage("hello"),
            new AssistantMessage(null, [call]),
            new ToolResultMessage("c1", "Echo", specialResult),
        };
        var request = new ModelRequest(
            "claude-3-5-sonnet-20241022", messages, [],
            """{"type":"object","properties":{}}""");

        var handler = new FakeHttpHandler(HttpStatusCode.OK, TextResponse("done"));
        var provider = MakeProvider(plan, handler);
        await provider.CompleteAsync(request, CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        var msgs = body.GetProperty("messages");
        var toolResultMsg = FindLastMessage(msgs, "user")!.Value;
        var contentBlock = toolResultMsg.GetProperty("content")[0];
        Assert.Equal("tool_result", contentBlock.GetProperty("type").GetString());
        Assert.Equal(specialResult, contentBlock.GetProperty("content").GetString());
    }

    // ── Empty-text assistant does not emit text block ─────────────────────────

    [Fact]
    public async Task Empty_text_assistant_emits_no_text_block()
    {
        var plan = CompileSource();
        var call = MakeCall("c1", "hello");

        var messages = new List<Message>
        {
            new UserMessage("hello"),
            new AssistantMessage("", [call]), // empty string — should not produce text block
            new ToolResultMessage("c1", "Echo", """{"result":"hello"}"""),
        };
        var request = new ModelRequest(
            "claude-3-5-sonnet-20241022", messages, [],
            """{"type":"object","properties":{}}""");

        var handler = new FakeHttpHandler(HttpStatusCode.OK, TextResponse("done"));
        var provider = MakeProvider(plan, handler);
        await provider.CompleteAsync(request, CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        var assistantMsg = FindMessage(body.GetProperty("messages"), "assistant")!.Value;
        var content = assistantMsg.GetProperty("content");
        Assert.DoesNotContain(
            content.EnumerateArray(),
            block => block.GetProperty("type").GetString() == "text");
    }

    // ── Full workflow cycle (end-to-end via WorkflowExecutor) ─────────────────

    [Fact]
    public async Task Full_anthropic_cycle_via_workflow_executor_succeeds()
    {
        var responses = new Queue<string>([
            ToolCallResponse("c1", "Echo", """{"text":"hello"}"""),
            TextResponse("""{"result":"hello"}""")
        ]);

        var plan = CompileSource();
        var capturer = new CapturingQueuedHttpHandler(responses);
        var provider = MakeProvider(plan, capturer);
        var (tools, providers) = BuildRegistries(plan, provider);

        var executor = new WorkflowExecutor(plan, tools, providers, ExecutionLimits.Default);
        var input = new MailSchema("Req",
            ImmutableDictionary<string, MailValue>.Empty.Add("text", new MailString("hello")));

        var result = await executor.RunAsync(input, CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Contains(result.Events, e => e.Kind == "tool.started");
        Assert.Equal(2, capturer.CapturedBodies.Count);

        // Second request: last message is tool_result wrapping the Echo result
        var body2 = JsonDocument.Parse(capturer.CapturedBodies[1]).RootElement;
        var msgs = body2.GetProperty("messages");
        var lastMsg = msgs[msgs.GetArrayLength() - 1];
        Assert.Equal("user", lastMsg.GetProperty("role").GetString());
        var lastContent = lastMsg.GetProperty("content")[0];
        Assert.Equal("tool_result", lastContent.GetProperty("type").GetString());
        Assert.Equal("c1", lastContent.GetProperty("tool_use_id").GetString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonElement? FindMessage(JsonElement messages, string role)
    {
        foreach (var msg in messages.EnumerateArray())
            if (msg.GetProperty("role").GetString() == role)
                return msg;
        return null;
    }

    private static JsonElement? FindLastMessage(JsonElement messages, string role)
    {
        JsonElement? last = null;
        foreach (var msg in messages.EnumerateArray())
            if (msg.GetProperty("role").GetString() == role)
                last = msg;
        return last;
    }
}

// ── CapturingQueuedHttpHandler ─────────────────────────────────────────────

internal sealed class CapturingQueuedHttpHandler(Queue<string> responses) : HttpMessageHandler
{
    public List<string> CapturedBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        CapturedBodies.Add(await request.Content!.ReadAsStringAsync(ct));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json")
        };
    }
}
