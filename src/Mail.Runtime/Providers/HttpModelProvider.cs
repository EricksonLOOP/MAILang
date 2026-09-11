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
    private const int MaxBodyBytes    = 4 * 1024 * 1024;
    private const int MaxErrorExcerpt = 256;

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        // Only POST is allowed; other methods are a configuration error.
        if (!string.Equals(decl.Call!.Method, "POST", StringComparison.OrdinalIgnoreCase))
            throw new ProviderConfigurationException(decl.Name,
                $"[MAIL-CONFIG-003] HTTP method '{decl.Call.Method}' is not supported; only POST is allowed.");

        var url = decl.BaseUrl!.TrimEnd('/') + decl.Call.Path;
        using var req = new HttpRequestMessage(HttpMethod.Post, url);

        foreach (var h in decl.Call.Headers)
            req.Headers.TryAddWithoutValidation(h.Name, EvaluateHeaderValue(h.Value));

        var bodyJson = BuildBodyJson(request);
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try { resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ProviderHttpException(decl.Name, 0, Truncate(RedactSecrets(ex.Message), MaxErrorExcerpt)); }

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

            // Determine finish_reason value first — before any tool_calls interpretation.
            string? finishReason = null;
            if (decl.Response!.FinishReason is { } fr)
            {
                finishReason = NavigateTo(root, fr.Selector)?.GetString();
                if (finishReason != fr.StopValue && finishReason != fr.ToolCallsValue)
                    throw new ProviderResponseException(decl.Name,
                        "Unexpected finish_reason.",
                        Truncate(RedactSecrets(finishReason ?? "(null)"), MaxErrorExcerpt));
            }

            // Choose branch before parsing tool_calls.
            bool useToolCallsBranch;
            if (decl.Response.FinishReason is { } frDecl)
                useToolCallsBranch = finishReason == frDecl.ToolCallsValue;
            else
                // No finish_reason configured — infer from presence of tool_calls in response.
                useToolCallsBranch = decl.Response.ToolCalls is not null &&
                                     HasRawToolCalls(root, decl.Response.ToolCalls);

            if (useToolCallsBranch)
            {
                // tool_calls branch: validate and extract; text is preserved for history.
                List<ToolCallRequest> toolCalls = [];
                if (decl.Response.ToolCalls is { } tcDecl)
                    toolCalls = EvaluateToolCalls(root, tcDecl, decl.Name);
                // Empty list is an error regardless of whether a selector was declared.
                if (toolCalls.Count == 0)
                    throw new InvalidProviderResponseException(
                        $"Provider '{decl.Name}': finish_reason indicates tool_calls but response contains none.");
                var text = ExtractText(root, decl.Response.TextSelector);
                return new ModelResponse(toolCalls, text);
            }
            else
            {
                // stop branch: return text only; discard tool_calls entirely (no parsing, no validation).
                var text = ExtractText(root, decl.Response.TextSelector);
                if (string.IsNullOrEmpty(text))
                    throw new InvalidProviderResponseException(
                        $"Provider '{decl.Name}' returned a response with neither text nor tool calls.");
                return new ModelResponse(null, text);
            }
        }
    }

    // ── Header evaluation ──────────────────────────────────────────────────────

    private string EvaluateHeaderValue(HeaderValue value) => value switch
    {
        LiteralHeaderValue lit => lit.Text,
        FieldHeaderValue f => f.FieldName switch
        {
            "api_key"  => resolvedApiKey,
            "base_url" => decl.BaseUrl ?? "",
            _          => ""
        },
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
        // Agent output_schema is serialized as a JSON string (not embedded object).
        "output_schema" when tool is null => JsonValue.Create(request.ExpectedOutputSchemaJson),

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

    // ── Response helpers ───────────────────────────────────────────────────────

    private bool HasRawToolCalls(JsonElement root, ProviderToolCallsDecl tcDecl)
    {
        var candidates = NavigateToMany(root, tcDecl.Selector);
        if (candidates.Count == 0) return false;
        if (candidates.Count == 1 && candidates[0].ValueKind == JsonValueKind.Array)
            return candidates[0].GetArrayLength() > 0;
        return true;
    }

    private string? ExtractText(JsonElement root, string? textSel)
    {
        if (textSel is null) return null;
        var elems = NavigateToMany(root, textSel);
        var texts = elems
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
        if (texts.Count > 1)
            Console.Error.WriteLine(
                $"[MAIL-WARN] Provider '{decl.Name}': selector '{textSel}' matched {texts.Count} text nodes; concatenating with newline.");
        return texts.Count > 0 ? string.Join("\n", texts) : null;
    }

    // ── Selector evaluation ────────────────────────────────────────────────────

    private static List<ToolCallRequest> EvaluateToolCalls(
        JsonElement root, ProviderToolCallsDecl tcDecl, string providerName)
    {
        var candidates = NavigateToMany(root, tcDecl.Selector);

        // Selector may return either a single array element or multiple individual elements (filter result).
        IEnumerable<JsonElement> items;
        if (candidates.Count == 1 && candidates[0].ValueKind == JsonValueKind.Array)
            items = candidates[0].EnumerateArray();
        else
            items = candidates;

        var results = new List<ToolCallRequest>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var id   = NavigateTo(item, tcDecl.IdSelector)?.GetString();
            var name = NavigateTo(item, tcDecl.ToolNameSelector)?.GetString();

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                throw new InvalidProviderResponseException(
                    $"Provider '{providerName}' returned a tool call with missing id or name.");

            if (!ids.Add(id!))
                throw new DuplicateCallIdException(id!);

            var argsElem = NavigateTo(item, tcDecl.ArgsSelector);
            results.Add(new ToolCallRequest(id!, name!, ParseToolArgs(argsElem, providerName)));
        }
        return results;
    }

    private static ImmutableDictionary<string, JsonElement> ParseToolArgs(
        JsonElement? argsElem, string providerName)
    {
        if (argsElem is null)
            throw new ProviderResponseException(providerName,
                "Tool call args field is absent from the response.");

        var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);

        if (argsElem.Value.ValueKind == JsonValueKind.String)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(argsElem.Value.GetString()!); }
            catch { throw new ProviderResponseException(providerName, "Tool call has unparseable JSON args."); }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ProviderResponseException(providerName,
                        $"Tool call args must be a JSON object, got {doc.RootElement.ValueKind}.");
                foreach (var p in doc.RootElement.EnumerateObject())
                    builder[p.Name] = p.Value.Clone();
            }
        }
        else if (argsElem.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in argsElem.Value.EnumerateObject())
                builder[p.Name] = p.Value.Clone();
        }
        else
        {
            throw new ProviderResponseException(providerName,
                $"Tool call args must be a JSON object, got {argsElem.Value.ValueKind}.");
        }

        return builder.ToImmutable();
    }

    // ── Navigation engine ──────────────────────────────────────────────────────

    // Returns all matching elements for the selector, preserving cardinality.
    // Filter and wildcard segments fan out over collections; field/index segments reduce.
    private static List<JsonElement> NavigateToMany(JsonElement root, string selector)
    {
        var current = new List<JsonElement> { root };
        foreach (var seg in ParseSelector(selector))
        {
            var next = new List<JsonElement>();
            foreach (var el in current)
                next.AddRange(ApplySegmentMany(el, seg));
            current = next;
            if (current.Count == 0) return current;
        }
        return current;
    }

    // Convenience wrapper for callers that expect a single scalar value.
    private static JsonElement? NavigateTo(JsonElement root, string selector)
    {
        var many = NavigateToMany(root, selector);
        return many.Count > 0 ? many[0] : null;
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

    private static IEnumerable<JsonElement> ApplySegmentMany(JsonElement el, SelectorSegment seg)
    {
        switch (seg)
        {
            case FieldSegment f:
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(f.Name, out var fv))
                    yield return fv;
                break;

            case IndexSegment i:
                if (el.ValueKind == JsonValueKind.Object &&
                    el.TryGetProperty(i.Field, out var arr) &&
                    arr.ValueKind == JsonValueKind.Array &&
                    i.Index < arr.GetArrayLength())
                    yield return arr[i.Index];
                break;

            case WildcardSegment w:
                if (el.ValueKind == JsonValueKind.Object &&
                    el.TryGetProperty(w.Field, out var warr) &&
                    warr.ValueKind == JsonValueKind.Array)
                    foreach (var item in warr.EnumerateArray())
                        yield return item;
                break;

            case FilterSegment filter:
                if (el.ValueKind == JsonValueKind.Object &&
                    el.TryGetProperty(filter.Field, out var farr) &&
                    farr.ValueKind == JsonValueKind.Array)
                    foreach (var item in farr.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object &&
                            item.TryGetProperty(filter.Key, out var kv) &&
                            kv.GetString() == filter.Value)
                            yield return item;
                break;
        }
    }

    // ── I/O helpers ────────────────────────────────────────────────────────────

    // Reads the response body up to MaxBodyBytes. Reading even one extra byte triggers MAIL-PROVIDER-003.
    private async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct);
        var buf = new byte[MaxBodyBytes + 1];
        var totalRead = 0;
        int read;
        while (totalRead < buf.Length &&
               (read = await stream.ReadAsync(buf.AsMemory(totalRead), ct)) > 0)
            totalRead += read;

        if (totalRead > MaxBodyBytes)
            throw new ProviderResponseException(decl.Name,
                "[MAIL-PROVIDER-003] Response body exceeds 4 MiB limit.");

        return Encoding.UTF8.GetString(buf, 0, totalRead);
    }

    private string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(resolvedApiKey) || string.IsNullOrEmpty(text)) return text;
        return text.Replace(resolvedApiKey, "[redacted]", StringComparison.Ordinal);
    }

    // Sanitize secrets first, then truncate so total UTF-8 byte count ≤ maxBytes (including "…").
    private static string Truncate(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        const int EllipsisBytes = 3; // "…" = U+2026 = E2 80 A6 in UTF-8
        var bytes = Encoding.UTF8.GetBytes(text);
        var limit = maxBytes - EllipsisBytes;
        if (limit <= 0) return "…";
        // Scan back to a valid UTF-8 character boundary (skip continuation bytes 10xxxxxx).
        while (limit > 0 && (bytes[limit] & 0xC0) == 0x80)
            limit--;
        return Encoding.UTF8.GetString(bytes, 0, limit) + "…";
    }

    private static JsonNode ParseCloneNode(string json)
        => JsonNode.Parse(json) ?? JsonValue.Create<object?>(null)!;
}
