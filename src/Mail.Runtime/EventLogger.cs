namespace Mail.Runtime;

internal sealed class EventLogger(string executionId)
{
    private readonly List<ExecutionEvent> _events = [];

    public IReadOnlyList<ExecutionEvent> Events => _events;

    public void Log(string kind, string message,
        string? operationId = null, string? callId = null, long? durationMs = null) =>
        _events.Add(new ExecutionEvent(kind, message,
            DateTimeOffset.UtcNow, executionId, operationId, callId, durationMs));
}
