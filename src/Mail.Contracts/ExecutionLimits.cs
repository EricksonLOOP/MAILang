namespace Mail.Contracts;

public sealed record ExecutionLimits(
    int MaxToolCallsPerAgent,
    int MaxModelCallsPerAgent,
    TimeSpan Timeout)
{
    public int MaxTotalModelCalls { get; init; } = 100;
    public int MaxTotalToolAttempts { get; init; } = 1000;
    public int MaxTotalActivations { get; init; } = 1000;

    public static ExecutionLimits Default => new(10, 5, TimeSpan.FromSeconds(30));

    public static ExecutionLimits Validated(int maxToolCalls, int maxModelCalls, TimeSpan timeout)
    {
        if (maxToolCalls <= 0) throw new ArgumentOutOfRangeException(nameof(maxToolCalls), "Must be positive.");
        if (maxModelCalls <= 0) throw new ArgumentOutOfRangeException(nameof(maxModelCalls), "Must be positive.");
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "Must be positive.");
        return new(maxToolCalls, maxModelCalls, timeout);
    }
}
