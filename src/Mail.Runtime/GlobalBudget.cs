using Mail.Contracts;

namespace Mail.Runtime;

internal sealed class GlobalBudget(ExecutionLimits limits)
{
    private int _totalModelCalls;
    private int _totalToolAttempts;
    private int _totalActivations;
    public int TotalModelCalls => _totalModelCalls;
    public int TotalToolAttempts => _totalToolAttempts;
    public int TotalActivations => _totalActivations;
    private static void Track(ref int counter, int limit, string resource)
    {
        if (Interlocked.Increment(ref counter) > limit)
            throw new BudgetExceededException(resource, limit);
    }
    internal void TrackModelCall() => Track(ref _totalModelCalls, limits.MaxTotalModelCalls, "total model calls");
    internal void TrackToolAttempt() => Track(ref _totalToolAttempts, limits.MaxTotalToolAttempts, "total tool attempts");
    internal void TrackActivation() => Track(ref _totalActivations, limits.MaxTotalActivations, "total activations");
}
