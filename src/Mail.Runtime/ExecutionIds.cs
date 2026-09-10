namespace Mail.Runtime;

public readonly record struct RunId(string Value)
{
    public static RunId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}

public readonly record struct StepId(string Value)
{
    public static StepId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}

public readonly record struct ActivationId(string Value)
{
    public static ActivationId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}

public readonly record struct OperationId(string Value)
{
    public static OperationId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}

public readonly record struct AttemptId(string Value)
{
    public static AttemptId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}
