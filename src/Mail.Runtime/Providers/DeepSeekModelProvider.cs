using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mail.Contracts;

namespace Mail.Runtime.Providers;

/// <summary>DeepSeek Chat Completions, non-streaming and non-thinking mode.</summary>
public sealed class DeepSeekModelProvider : IModelProvider
{
    private readonly HttpClient _client;
    private readonly string _apiKey;
    public DeepSeekModelProvider(HttpClient client, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Set DEEPSEEK_API_KEY before using the DeepSeek provider.");
        _client = client;
        _apiKey = apiKey;
    }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var messages = request.Messages.Select(ToWireMessage).ToList();
        messages.Insert(0, new { role = "system", content =
            "Return your final answer as JSON matching this schema: " + request.ExpectedOutputSchemaJson });
        using var schema = JsonDocument.Parse(request.ExpectedOutputSchemaJson);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["stream"] = false,
            ["thinking"] = new { type = "disabled" },
            ["max_tokens"] = 2048
        };
        // JSON object mode cannot represent primitive outputs.
        if (schema.RootElement.TryGetProperty("type", out var type) && type.GetString() == "object")
            payload["response_format"] = new { type = "json_object" };
        if (request.AvailableTools.Count > 0)
            payload["tools"] = request.AvailableTools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, parameters = ParseClone(t.InputSchemaJson) }
            }).ToArray();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Content = JsonContent.Create(payload);
        HttpResponseMessage response;
        try { response = await _client.SendAsync(httpRequest, ct); }
        catch (HttpRequestException) { throw new InvalidProviderResponseException("DeepSeek network request failed."); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidProviderResponseException($"DeepSeek HTTP {(int)response.StatusCode}. Check credentials, balance, model and service availability.");
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                return ParseResponse(doc.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new InvalidProviderResponseException("DeepSeek returned an invalid response payload.");
            }
        }
    }

    private static ModelResponse ParseResponse(JsonElement root)
    {
        RejectDuplicates(root);
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() != 1)
            throw new InvalidProviderResponseException("DeepSeek must return exactly one choice.");
        var choice = choices[0];
        var finish = choice.GetProperty("finish_reason").GetString();
        if (finish is not ("stop" or "tool_calls"))
            throw new InvalidProviderResponseException("DeepSeek response was incomplete or filtered.");
        var message = choice.GetProperty("message");
        if (message.GetProperty("role").GetString() != "assistant")
            throw new InvalidProviderResponseException("DeepSeek returned an unexpected message role.");
        var calls = new List<ToolCallRequest>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (message.TryGetProperty("tool_calls", out var rawCalls) && rawCalls.ValueKind != JsonValueKind.Null)
        {
            foreach (var call in rawCalls.EnumerateArray())
            {
                var id = call.GetProperty("id").GetString();
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !ids.Add(id) || call.GetProperty("type").GetString() != "function")
                    throw new InvalidProviderResponseException("DeepSeek returned invalid or duplicate tool call identifiers.");
                using var arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!);
                RejectDuplicates(arguments.RootElement);
                var fields = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in arguments.RootElement.EnumerateObject()) fields.Add(p.Name, p.Value.Clone());
                calls.Add(new ToolCallRequest(id, name, fields.ToImmutable()));
            }
        }
        var text = message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null ? content.GetString() : null;
        if ((finish == "tool_calls") != (calls.Count > 0) || (calls.Count == 0 && string.IsNullOrWhiteSpace(text)))
            throw new InvalidProviderResponseException("DeepSeek returned inconsistent or empty content.");
        return new ModelResponse(calls.Count > 0 ? calls : null, text);
    }

    private static object ToWireMessage(Message message) => message switch
    {
        SystemMessage m => new { role = "system", content = m.Content },
        UserMessage m => new { role = "user", content = m.Content },
        ToolResultMessage m => new { role = "tool", tool_call_id = m.CallId, content = m.ResultJson },
        AssistantMessage m when m.ToolCalls is { Count: > 0 } => new
        {
            role = "assistant", content = m.Text,
            tool_calls = m.ToolCalls.Select(c => new
            {
                id = c.CallId, type = "function",
                function = new { name = c.ToolName, arguments = JsonSerializer.Serialize(c.Arguments) }
            }).ToArray()
        },
        AssistantMessage m => new { role = "assistant", content = m.Text },
        _ => throw new InvalidOperationException("Unsupported DeepSeek message type.")
    };

    private static JsonElement ParseClone(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new InvalidProviderResponseException("Duplicate JSON property in DeepSeek response.");
                RejectDuplicates(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
