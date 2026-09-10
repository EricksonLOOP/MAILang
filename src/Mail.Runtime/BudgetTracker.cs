using Mail.Contracts;

namespace Mail.Runtime;

public sealed class BudgetTracker(ExecutionLimits limits)
{
    private int _toolCalls;
    private int _modelCalls;

    public void ConsumeToolCall()
    {
        if (_toolCalls >= limits.MaxToolCallsPerAgent)
            throw new BudgetExceededException("tool calls", limits.MaxToolCallsPerAgent);
        _toolCalls++;
    }

    public void ConsumeModelCall()
    {
        if (_modelCalls >= limits.MaxModelCallsPerAgent)
            throw new BudgetExceededException("model calls", limits.MaxModelCallsPerAgent);
        _modelCalls++;
    }

    public int ToolCallsUsed => _toolCalls;
    public int ModelCallsUsed => _modelCalls;
}
