namespace Mail.Contracts;

public interface IModelProvider
{
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct);
}

public sealed record ToolDefinition(string Name, string InputSchemaJson, string OutputSchemaJson);

public sealed record ModelRequest(
    string ModelId,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ToolDefinition> AvailableTools,
    string ExpectedOutputSchemaJson);

public sealed record ModelResponse(
    IReadOnlyList<ToolCallRequest>? ToolCalls,
    string? Text);
