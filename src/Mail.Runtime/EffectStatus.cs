namespace Mail.Runtime;

public enum EffectStatus { NotStarted, Confirmed, Unknown }

public sealed record FailureInfo(
    string Category,
    string Origin,
    string OperationId,
    string AttemptId,
    EffectStatus Effect);
