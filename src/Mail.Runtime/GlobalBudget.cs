using System.Threading;

namespace Mail.Runtime;

internal sealed class GlobalBudget
{
    private int _totalModelCalls;
    private int _totalToolAttempts;
    private int _totalActivations;

    public int TotalModelCalls    => _totalModelCalls;
    public int TotalToolAttempts  => _totalToolAttempts;
    public int TotalActivations   => _totalActivations;

    internal void TrackModelCall()    => Interlocked.Increment(ref _totalModelCalls);
    internal void TrackToolAttempt()  => Interlocked.Increment(ref _totalToolAttempts);
    internal void TrackActivation()   => Interlocked.Increment(ref _totalActivations);
}
