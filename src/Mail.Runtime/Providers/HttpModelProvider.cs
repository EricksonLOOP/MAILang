using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mail.Compiler.Ast;
using Mail.Contracts;

namespace Mail.Runtime.Providers;

public sealed class HttpModelProvider(
    ProviderDecl decl,
    string resolvedApiKey,
    HttpClient client) : IModelProvider
{
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private const int MaxErrorExcerpt = 256;

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var url = decl.BaseUrl!.TrimEnd('/') + decl.Call!.Path;
        using var req = new HttpRequestMessage(new HttpMethod(decl.Call.Method), url);

        foreach (var h in decl.Call.Headers)
            req.Headers.TryAddWithoutValidation(h.Name, EvaluateHeaderValue(h.Value));

        var bodyJson = BuildBodyJson(request);
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try { resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ProviderHttpException(decl.Name, 0, Truncate(ex.Message, MaxErrorExcerpt)); }

        using (resp)
        {
            var raw = await ReadBodyAsync(resp.Content, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var excerpt = Truncate(RedactSecrets(raw), MaxErrorExcerpt);
                throw new ProviderHttpException(decl.Name, (int)resp.StatusCode, excerpt);
            }

            JsonElement root;
            try { root = JsonDocument.Parse(raw).RootElement; }
            catch
            {
                throw new ProviderResponseException(decl.Name, "Response is not valid JSON.",
                    Truncate(RedactSecrets(raw), MaxErrorExcerpt));
            }

            if (decl.Response!.FinishReason is { } fr)
            {
                var finishVal = NavigateTo(root, fr.Selector)?.GetString();
                if (finishVal != fr.StopValue && finishVal != fr.ToolCallsValue)
                    throw new ProviderResponseException(decl.Name,
                        $"Unexpected finish_reason: '{finishVal ?? "(null)"}'.");
            }

            // Always extract both text and tool_calls — finish_reason only validates completeness
            string? text = null;
            List<ToolCallRequest>? toolCalls = null;

            if (decl.Response.TextSelector is { } textSel)
            {
                var elem = NavigateTo(root, textSel);
                if (elem is { ValueKind: JsonValueKind.String })
                    text = elem.Value.GetString();
            }

            if (decl.Response.ToolCalls is { } tcDecl)
                toolCalls = EvaluateToolCalls(root, tcDecl);

            if (string.IsNullOrEmpty(text) && (toolCalls is null || toolCalls.Count == 0))
                throw new InvalidProviderResponseException(
                    $"Provider '{decl.Name}' returned a response with neither text nor tool calls.");

            return new ModelResponse(toolCalls is { Count: > 0 } ? toolCalls : null, text);
        }
    }

    // ── Header evaluation ──────────────────────────────────────────────────────

    private string EvaluateHeaderValue(HeaderValue value) => value switch
    {
        LiteralHeaderValue lit => lit.Text,
        FieldHeaderValue f => f.FieldName == "api_key" ? resolvedApiKey : "",
        ConcatHeaderValue cat => string.Concat(cat.Parts.Select(EvaluateHeaderValue)),
        _ => ""
    };

    // ── Body template engine ───────────────────────────────────────────────────

    private string BuildBodyJson(ModelRequest request)
    {
        var obj = EvaluateBodyFields(decl.Call!.Body, request, null, null, null);
        return obj?.ToJsonString() ?? "{}";
    }

    private JsonObject? EvaluateBodyFields(
        IReadOnlyList<BodyFieldDecl> fields,
        ModelRequest request,
        Message? message,
        ToolDefinition? tool,
        ToolCallRequest? call)
    {
        var obj = new JsonObject();
        foreach (var f in fields)
        {
            var val = EvaluateBodyValue(f.Value, request, message, tool, call);
            if (val is not null)
                obj[f.Key] = val;
        }
        return obj;
    }

    private JsonNode? EvaluateBodyValue(
        BodyValue value,
        ModelRequest request,
        Message? message,
        ToolDefinition? tool,
        ToolCallRequest? call) => value switch
    {
        StringBodyValue s  => JsonValue.Create(s.Text),
        IntBodyValue i     => JsonValue.Create(i.Number),
        BoolBodyValue b    => JsonValue.Create(b.Flag),
        ObjectBodyValue o  => EvaluateBodyFields(o.Fields, request, message, tool, call),
        ArrayBodyValue a   => EvaluateArray(a.Items, request, message, tool, call),
        InterpolationBodyValue interp => EvaluateInterpolation(interp.VarName, request, message, tool, call),
        MessagesBodyValue msgs => EvaluateMessages(msgs, request),
        ToolsBodyValue ts  => EvaluateTools(ts, request),
        CallsBodyValue cs  => EvaluateCalls(cs, message as AssistantMessage, request),
        _                  => null
    };

    private JsonArray EvaluateArray(
        IReadOnlyList<BodyValue> items,
        ModelRequest request,
        Message? message,
        ToolDefinition? tool,
        ToolCallRequest? call)
    {
        var arr = new JsonArray();
        foreach (var item in items)
            arr.Add(EvaluateBodyValue(item, request, message, tool, call));
        return arr;
    }

    private JsonNode? EvaluateInterpolation(
        string varName,
        ModelRequest request,
        Message? message,
        ToolDefinition? tool,
        ToolCallRequest? call) => varName switch
    {
        // Root context
        "model"         => JsonValue.Create(request.ModelId),
        "system"        => request.Messages.OfType<SystemMessage>().FirstOrDefault() is { } sm
                               ? JsonValue.Create(sm.Content) : null,
        "output_schema" when tool is null => ParseCloneNode(request.ExpectedOutputSchemaJson),

        // Message context
        "text" => message switch
        {
            SystemMessage  m => JsonValue.Create(m.Content),
            UserMessage    m => JsonValue.Create(m.Content),
            AssistantMessage m => string.IsNullOrEmpty(m.Text) ? null : JsonValue.Create(m.Text),
            ToolResultMessage m => JsonValue.Create(m.ResultJson),
            _ => null
        },
        "call_id"     when message is ToolResultMessage tr => JsonValue.Create(tr.CallId),
        "tool_name"   when message is ToolResultMessage tr => JsonValue.Create(tr.ToolName),
        "result"      when message is ToolResultMessage tr => ParseCloneNode(tr.ResultJson),
        "result_json" when message is ToolResultMessage tr => JsonValue.Create(tr.ResultJson),

        // Tool context
        "name"          when tool is not null => JsonValue.Create(tool.Name),
        "input_schema"  when tool is not null => ParseCloneNode(tool.InputSchemaJson),
        "output_schema" when tool is not null => ParseCloneNode(tool.OutputSchemaJson),

        // Call context (inside $calls in assistant mapping)
        "call_id"   when call is not null => JsonValue.Create(call.CallId),
        "tool_name" when call is not null => JsonValue.Create(call.ToolName),
        "args"      when call is not null => BuildArgsObject(call),
        "args_json" when call is not null => JsonValue.Create(JsonSerializer.Serialize(call.Arguments)),

        _ => null
    };

    private static JsonObject BuildArgsObject(ToolCallRequest call)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in call.Arguments)
            obj[k] = JsonNode.Parse(v.GetRawText());
        return obj;
    }

    private JsonArray EvaluateMessages(MessagesBodyValue msgs, ModelRequest request)
    {
        var mappings = msgs.Mappings.ToDictionary(m => m.MessageType, StringComparer.Ordinal);
        var arr = new JsonArray();
        foreach (var msg in request.Messages)
        {
            var key = msg switch
            {
                SystemMessage    => "system",
                UserMessage      => "user",
                AssistantMessage => "assistant",
                ToolResultMessage => "tool_result",
                _ => null
            };
            if (key is null || !mappings.TryGetValue(key, out var mapping)) continue;
            var obj = EvaluateBodyFields(mapping.Template, request, msg, null, null);
            if (obj is not null) arr.Add(obj);
        }
        return arr;
    }

    private JsonArray? EvaluateTools(ToolsBodyValue ts, ModelRequest request)
    {
        if (request.AvailableTools.Count == 0) return null;
        var arr = new JsonArray();
        foreach (var tool in request.AvailableTools)
        {
            var obj = EvaluateBodyFields(ts.ItemTemplate, request, null, tool, null);
            if (obj is not null) arr.Add(obj);
        }
        return arr;
    }

    private JsonArray? EvaluateCalls(CallsBodyValue cs, AssistantMessage? asst, ModelRequest request)
    {
        if (asst?.ToolCalls is not { Count: > 0 } toolCalls) return null;
        var arr = new JsonArray();
        foreach (var call in toolCalls)
        {
            var obj = EvaluateBodyFields(cs.ItemTemplate, request, null, null, call);
            if (obj is not null) arr.Add(obj);
        }
        return arr;
    }

    // ── Selector evaluation ────────────────────────────────────────────────────

    private static List<ToolCallRequest> EvaluateToolCalls(JsonElement root, ProviderToolCallsDecl tcDecl)
    {
        var arrayElem = NavigateTo(root, tcDecl.Selector);
        if (arrayElem is null || arrayElem.Value.ValueKind != JsonValueKind.Array) return [];

        var results = new List<ToolCallRequest>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in arrayElem.Value.EnumerateArray())
        {
            var id   = NavigateTo(item, tcDecl.IdSelector)?.GetString();
            var name = NavigateTo(item, tcDecl.ToolNameSelector)?.GetString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || !ids.Add(id!)) continue;

            var argsElem = NavigateTo(item, tcDecl.ArgsSelector);
            results.Add(new ToolCallRequest(id!, name!, ParseToolArgs(argsElem)));
        }
        return results;
    }

    private static ImmutableDictionary<string, JsonElement> ParseToolArgs(JsonElement? argsElem)
    {
        if (argsElem is null) return ImmutableDictionary<string, JsonElement>.Empty;
        var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        if (argsElem.Value.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsElem.Value.GetString()!);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        builder[p.Name] = p.Value.Clone();
            }
            catch { /* invalid JSON args — skip */ }
        }
        else if (argsElem.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in argsElem.Value.EnumerateObject())
                builder[p.Name] = p.Value.Clone();
        }
        return builder.ToImmutable();
    }

    private static JsonElement? NavigateTo(JsonElement root, string selector)
    {
        var current = root;
        foreach (var seg in ParseSelector(selector))
        {
            var next = ApplySegment(current, seg);
            if (next is null) return null;
            current = next.Value;
        }
        return current;
    }

    private abstract record SelectorSegment;
    private sealed record FieldSegment(string Name) : SelectorSegment;
    private sealed record IndexSegment(string Field, int Index) : SelectorSegment;
    private sealed record WildcardSegment(string Field) : SelectorSegment;
    private sealed record FilterSegment(string Field, string Key, string Value) : SelectorSegment;

    private static List<SelectorSegment> ParseSelector(string selector)
    {
        var parts = selector.Split('.');
        var result = new List<SelectorSegment>(parts.Length);
        foreach (var part in parts)
        {
            var bi = part.IndexOf('[');
            if (bi < 0) { result.Add(new FieldSegment(part)); continue; }
            var field   = part[..bi];
            var content = part[(bi + 1)..part.IndexOf(']')];
            if (content == "*")
                result.Add(new WildcardSegment(field));
            else if (int.TryParse(content, out var idx))
                result.Add(new IndexSegment(field, idx));
            else
            {
                var eq = content.IndexOf('=');
                result.Add(new FilterSegment(field, content[..eq], content[(eq + 1)..]));
            }
        }
        return result;
    }

    private static JsonElement? ApplySegment(JsonElement current, SelectorSegment seg)
    {
        switch (seg)
        {
            case FieldSegment f:
                return current.ValueKind == JsonValueKind.Object &&
                       current.TryGetProperty(f.Name, out var fv) ? fv : null;

            case IndexSegment i:
                if (current.ValueKind != JsonValueKind.Object) return null;
                if (!current.TryGetProperty(i.Field, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
                return i.Index < arr.GetArrayLength() ? arr[i.Index] : null;

            case WildcardSegment w:
                if (current.ValueKind != JsonValueKind.Object) return null;
                return current.TryGetProperty(w.Field, out var warr) &&
                       warr.ValueKind == JsonValueKind.Array ? warr : null;

            case FilterSegment filter:
                if (current.ValueKind != JsonValueKind.Object) return null;
                if (!current.TryGetProperty(filter.Field, out var farr) || farr.ValueKind != JsonValueKind.Array) return null;
                foreach (var elem in farr.EnumerateArray())
                    if (elem.ValueKind == JsonValueKind.Object &&
                        elem.TryGetProperty(filter.Key, out var kv) &&
                        kv.GetString() == filter.Value)
                        return elem;
                return null;

            default: return null;
        }
    }

    // ── I/O helpers ────────────────────────────────────────────────────────────

    private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var stream = await content.ReadAsStreamAsync(ct);
        var buf = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, read);
            if (ms.Length >= MaxBodyBytes) break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)Math.Min(ms.Length, MaxBodyBytes));
    }

    private string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(resolvedApiKey) || string.IsNullOrEmpty(text)) return text;
        return text.Replace(resolvedApiKey, "[redacted]", StringComparison.Ordinal);
    }

    private static string Truncate(string text, int maxBytes)
    {
        if (text.Length <= maxBytes) return text;
        var bytes = Encoding.UTF8.GetBytes(text);
        return bytes.Length <= maxBytes ? text : Encoding.UTF8.GetString(bytes, 0, maxBytes) + "…";
    }

    private static JsonNode ParseCloneNode(string json)
        => JsonNode.Parse(json) ?? JsonValue.Create<object?>(null)!;
}
