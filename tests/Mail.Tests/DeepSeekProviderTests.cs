using System.Net;
using System.Text;
using System.Text.Json;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;

namespace Mail.Tests;

public class DeepSeekProviderTests
{
    private static ModelRequest Request(IReadOnlyList<Message>? history = null) => new(
        "deepseek-v4-flash", history ?? [new UserMessage("Check order")],
        [new ToolDefinition("GetOrderStatus", """{"type":"object","properties":{"order_id":{"type":"string"}}}""", "{}")],
        """{"type":"object","properties":{"status":{"type":"string"}}}""");

    [Fact]
    public async Task Tool_cycle_preserves_arguments_ids_and_schema_without_executing_tools()
    {
        var count = 0;
        using var client = new HttpClient(new Handler(async (req, ct) =>
        {
            Assert.Equal("https://api.deepseek.com/chat/completions", req.RequestUri!.ToString());
            Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", req.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("deepseek-v4-flash", root.GetProperty("model").GetString());
            Assert.Contains("status", root.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("GetOrderStatus", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
            if (count++ == 0) return Json("""{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":"Checking","tool_calls":[{"id":"call-1","type":"function","function":{"name":"GetOrderStatus","arguments":"{\"order_id\":\"ORD-42\"}"}}]}}]}""");
            var messages = root.GetProperty("messages");
            Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
            Assert.Equal("Checking", messages[2].GetProperty("content").GetString());
            Assert.Equal("call-1", messages[3].GetProperty("tool_call_id").GetString());
            return Json("""{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{\"status\":\"shipped\"}"}}]}""");
        }));
        var provider = new DeepSeekModelProvider(client, "test-key");
        var first = await provider.CompleteAsync(Request(), default);
        Assert.Equal("ORD-42", first.ToolCalls![0].Arguments["order_id"].GetString());
        var second = await provider.CompleteAsync(Request([
            new UserMessage("Check order"), new AssistantMessage(first.Text, first.ToolCalls),
            new ToolResultMessage("call-1", "GetOrderStatus", """{"status":"shipped"}""")]), default);
        Assert.Contains("shipped", second.Text);
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    public async Task Incomplete_response_is_rejected(string reason)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new
        { choices = new[] { new { finish_reason = reason, message = new { role = "assistant", content = "{}" } } } })))));
        await Assert.ThrowsAsync<InvalidProviderResponseException>(() => new DeepSeekModelProvider(client, "key").CompleteAsync(Request(), default));
    }

    [Fact]
    public async Task Duplicate_arguments_are_rejected()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json("""{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","tool_calls":[{"id":"x","type":"function","function":{"name":"GetOrderStatus","arguments":"{\"order_id\":1,\"order_id\":2}"}}]}}]}"""))));
        await Assert.ThrowsAsync<InvalidProviderResponseException>(() => new DeepSeekModelProvider(client, "key").CompleteAsync(Request(), default));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Http_errors_do_not_leak_body_or_retry(int status)
    {
        var count = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            count++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private response key") });
        }));
        var error = await Assert.ThrowsAsync<InvalidProviderResponseException>(() => new DeepSeekModelProvider(client, "secret-key").CompleteAsync(Request(), default));
        Assert.Contains(status.ToString(), error.Message);
        Assert.DoesNotContain("private", error.Message);
        Assert.DoesNotContain("secret-key", error.Message);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Cancellation_reaches_http_transport()
    {
        using var client = new HttpClient(new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeepSeekModelProvider(client, "key").CompleteAsync(Request(), cts.Token));
    }

    [Fact]
    public void Missing_key_fails_before_http()
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() => new DeepSeekModelProvider(client, ""));
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
