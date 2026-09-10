namespace Mail.Runtime;

public enum RunStatus
{
    Created,
    Running,
    Suspended,
    Cancelling,
    Succeeded,
    Failed,
    Cancelled
}

public enum SuspendReason
{
    ApprovalRequired,
    ExplicitPause,
    EffectUnknown
}
