using Mail.Contracts;

namespace Mail.Runtime;

public sealed record ExecutionEvent(
    string Kind,
    string Message,
    DateTimeOffset Timestamp,
    string ExecutionId,
    string? OperationId  = null,
    string? CallId       = null,
    long?   DurationMs   = null,
    int     Sequence     = 0,
    string? StepId       = null,
    string? ActivationId = null,
    string? AttemptId    = null,
    string? ParentOperationId = null);

public sealed record ExecutionResult(
    bool Succeeded,
    MailValue? Output,
    IReadOnlyList<ExecutionEvent> Events,
    string? ErrorMessage,
    RunId RunId = default,
    RunStatus? TerminalStatus = null,
    FailureInfo? Failure = null)
{
    public RunStatus Status => TerminalStatus ?? (Succeeded ? RunStatus.Succeeded : RunStatus.Failed);
}
