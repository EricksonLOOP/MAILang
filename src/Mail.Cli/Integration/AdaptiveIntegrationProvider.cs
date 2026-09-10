using System.Collections.Immutable;
using System.Text.Json;
using Mail.Contracts;
using Mail.Runtime;

namespace Mail.Cli.Integration;

// IModelProvider for integration mode. Supports an explicit script (from provider_config)
// or adaptive behaviour modelled after CliTokenEchoSimulator:
//   - First call: requests the first available (authorized) tool with args from context.
//   - Subsequent calls (after at least one ToolResultMessage): returns the last
//     tool result JSON as the final text response.
internal sealed class AdaptiveIntegrationProvider(IReadOnlyList<ScriptStep>? script = null)
    : IModelProvider
{
    private int    _stepIndex;
    private string _lastToolResultJson = "{}";

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Update last tool result from conversation history.
        var lastResult = request.Messages.OfType<ToolResultMessage>().LastOrDefault();
        if (lastResult is not null)
            _lastToolResultJson = lastResult.ResultJson;

        return Task.FromResult(script is { Count: > 0 }
            ? ExecuteScript(request)
            : ExecuteAdaptive(request));
    }

    private ModelResponse ExecuteScript(ModelRequest request)
    {
        if (_stepIndex >= script!.Count)
            throw new InvalidProviderResponseException(
                "AdaptiveIntegrationProvider: script exhausted.");

        return script[_stepIndex++] switch
        {
            ToolCallStep tc => new ModelResponse(
                [new ToolCallRequest(
                    Guid.NewGuid().ToString("N"),
                    tc.Tool,
                    tc.Args.EnumerateObject()
                        .ToImmutableDictionary(p => p.Name, p => p.Value.Clone()))],
                null),

            TextStep { Template: "last_tool_result" } =>
                new ModelResponse(null, _lastToolResultJson),

            var other =>
                throw new InvalidProviderResponseException(
                    $"Unknown script step type: {other.GetType().Name}"),
        };
    }

    private ModelResponse ExecuteAdaptive(ModelRequest request)
    {
        // Phase 1: no tool results yet — call the first available tool.
        if (!request.Messages.OfType<ToolResultMessage>().Any()
            && request.AvailableTools.Count > 0)
        {
            var tool = request.AvailableTools[0];
            var args = ExtractArgsFromContext(request.Messages);
            return new ModelResponse(
                [new ToolCallRequest(Guid.NewGuid().ToString("N"), tool.Name, args)],
                null);
        }

        // Phase 2: return last tool result as the final text response.
        return new ModelResponse(null, _lastToolResultJson);
    }

    private static ImmutableDictionary<string, JsonElement> ExtractArgsFromContext(
        IReadOnlyList<Message> messages)
    {
        var userMsg = messages.OfType<UserMessage>().LastOrDefault();
        if (userMsg is null) return ImmutableDictionary<string, JsonElement>.Empty;

        try
        {
            // Context format: "Context:\nname: {json}\n..."
            var start = userMsg.Content.IndexOf('{');
            var end   = userMsg.Content.LastIndexOf('}');
            if (start < 0 || end <= start) return ImmutableDictionary<string, JsonElement>.Empty;

            var snippet = userMsg.Content[start..(end + 1)];
            using var doc = JsonDocument.Parse(snippet);
            return doc.RootElement.EnumerateObject()
                .ToImmutableDictionary(p => p.Name, p => p.Value.Clone());
        }
        catch
        {
            return ImmutableDictionary<string, JsonElement>.Empty;
        }
    }
}

internal abstract record ScriptStep;
internal sealed record ToolCallStep(string Tool, JsonElement Args) : ScriptStep;
internal sealed record TextStep(string Template) : ScriptStep;

internal static class ScriptParser
{
    public static IReadOnlyList<ScriptStep> Parse(JsonElement scriptArray)
    {
        var steps = new List<ScriptStep>();
        foreach (var el in scriptArray.EnumerateArray())
        {
            var type = el.GetProperty("type").GetString();
            steps.Add(type switch
            {
                "tool_call" => new ToolCallStep(
                    el.GetProperty("tool").GetString()!,
                    el.GetProperty("args").Clone()),
                "text" => new TextStep(el.GetProperty("template").GetString()!),
                _ => throw new InvalidOperationException($"Unknown script step type: {type}"),
            });
        }
        return steps;
    }
}
