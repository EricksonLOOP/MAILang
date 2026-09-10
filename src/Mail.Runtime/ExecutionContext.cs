using Mail.Contracts;

namespace Mail.Runtime;

internal sealed class ExecutionContext
{
    // Allowed transitions from spec §4
    private static readonly IReadOnlyDictionary<RunStatus, RunStatus[]> Allowed =
        new Dictionary<RunStatus, RunStatus[]>
        {
            [RunStatus.Created]    = [RunStatus.Running, RunStatus.Cancelled, RunStatus.Failed],
            [RunStatus.Running]    = [RunStatus.Suspended, RunStatus.Succeeded, RunStatus.Failed, RunStatus.Cancelling],
            [RunStatus.Suspended]  = [RunStatus.Running, RunStatus.Failed, RunStatus.Cancelling],
            [RunStatus.Cancelling] = [RunStatus.Cancelled],
            [RunStatus.Succeeded]  = [],
            [RunStatus.Failed]     = [],
            [RunStatus.Cancelled]  = [],
        };

    public RunId           RunId    { get; }
    public string          PlanHash { get; }
    public RunStatus       Status   { get; private set; }
    public ExecutionLimits Limits   { get; }
    public GlobalBudget    Budget   { get; }
    public EventLogger     Logger   { get; }

    public bool IsTerminal =>
        Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled;

    public ExecutionContext(ExecutionLimits limits, string planHash)
        : this(RunId.New(), limits, planHash) { }

    public ExecutionContext(RunId runId, ExecutionLimits limits, string planHash)
    {
        RunId    = runId;
        PlanHash = planHash;
        Status   = RunStatus.Created;
        Limits   = limits;
        Budget   = new GlobalBudget(limits);
        Logger   = new EventLogger(runId.Value);
    }

    public void Transition(RunStatus to)
    {
        if (!Allowed.TryGetValue(Status, out var targets) || !targets.Contains(to))
            throw new InvalidStatusTransitionException(Status, to);
        Status = to;
    }
}
