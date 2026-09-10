using Mail.Contracts;
using System.Collections.Immutable;
using System.Text.Json;

namespace Mail.Tests;

// Captures a copy of Messages from each ModelRequest; returns responses from a sequential script.
internal sealed class CapturingModelProvider(IReadOnlyList<ModelResponse> script) : IModelProvider
{
    private int _index;
    private readonly object _lock = new();
    public List<IReadOnlyList<Message>> Captures { get; } = [];

    public Task<ModelResponse> CompleteAsync(ModelRequest req, CancellationToken ct)
    {
        lock (_lock)
        {
            Captures.Add(req.Messages.ToList()); // snapshot — not a reference
            var resp = script[_index];
            if (_index < script.Count - 1) _index++;
            return Task.FromResult(resp);
        }
    }
}

// Shared adaptive provider for token-echo tests.
// First call requests GenerateToken; second call reads the actual token
// from the tool result in message history and returns it as the final output.
internal sealed class AdaptiveTokenProvider : IModelProvider
{
    private int _callCount;

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _callCount++;

        if (_callCount == 1)
        {
            return Task.FromResult(new ModelResponse(
                ToolCalls:
                [
                    new ToolCallRequest(
                        "atp-call-1",
                        "GenerateToken",
                        ImmutableDictionary<string, JsonElement>.Empty.Add(
                            "prompt", JsonDocument.Parse("\"test\"").RootElement.Clone()))
                ],
                Text: null));
        }

        var toolResult = request.Messages
            .OfType<ToolResultMessage>()
            .First(m => m.ToolName == "GenerateToken");

        using var doc = JsonDocument.Parse(toolResult.ResultJson);
        var token = doc.RootElement.GetProperty("token").GetString()!;

        return Task.FromResult(new ModelResponse(
            ToolCalls: null,
            Text: JsonSerializer.Serialize(new { token })));
    }
}
