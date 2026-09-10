using Mail.Contracts;

namespace Mail.Runtime;

public sealed record ExecutionEvent(
    string Kind,
    string Message,
    DateTimeOffset Timestamp,
    string ExecutionId,
    string? OperationId = null,
    string? CallId = null,
    long? DurationMs = null);

public sealed record ExecutionResult(
    bool Succeeded,
    MailValue? Output,
    IReadOnlyList<ExecutionEvent> Events,
    string? ErrorMessage);
