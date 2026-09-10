using System.Collections.Immutable;
using System.Text.Json;

namespace Mail.Contracts;

public enum MessageRole { System, User, Assistant, Tool }

public abstract record Message(MessageRole Role);

public sealed record SystemMessage(string Content) : Message(MessageRole.System);

public sealed record UserMessage(string Content) : Message(MessageRole.User);

public sealed record AssistantMessage(string? Text, IReadOnlyList<ToolCallRequest>? ToolCalls)
    : Message(MessageRole.Assistant);

public sealed record ToolResultMessage(string CallId, string ToolName, string ResultJson)
    : Message(MessageRole.Tool);

public sealed record ToolCallRequest(
    string CallId,
    string ToolName,
    ImmutableDictionary<string, JsonElement> Arguments);
