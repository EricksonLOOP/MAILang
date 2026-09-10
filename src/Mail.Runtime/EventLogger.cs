namespace Mail.Runtime;

internal sealed class EventLogger(string executionId)
{
    private readonly List<ExecutionEvent> _events = [];
    private int _sequence;

    public IReadOnlyList<ExecutionEvent> Events => _events;
    public string? ParentOperationId { get; set; }

    public void Log(
        string kind,
        string message,
        string? operationId = null,
        string? callId = null,
        long? durationMs = null,
        string? stepId = null,
        string? activationId = null,
        string? attemptId = null)
    {
        _events.Add(new ExecutionEvent(
            kind, message, DateTimeOffset.UtcNow, executionId,
            operationId, callId, durationMs,
            ++_sequence, stepId, activationId, attemptId, ParentOperationId));
    }

    // Spec §11 — required event kind identifiers
    internal static class Kinds
    {
        public const string ExecutionCreated       = "execution.created";
        public const string ExecutionStarted       = "execution.started";
        public const string ActivationStarted      = "activation.started";
        public const string ActivationConfirmed    = "activation.confirmed";
        public const string GuardDecision          = "guard.decision";
        public const string ApprovalRequested      = "approval.requested";
        public const string ApprovalResolved       = "approval.resolved";
        public const string DispatchIntent         = "dispatch.intent";
        public const string AttemptResult          = "attempt.result";
        public const string RetryScheduled         = "retry.scheduled";
        public const string ExecutionSuspended     = "execution.suspended";
        public const string ExecutionResumed       = "execution.resumed";
        public const string CancellationRequested  = "cancellation.requested";
        public const string ExecutionEnded         = "execution.ended";
        public const string Reconciliation         = "reconciliation";
    }
}
