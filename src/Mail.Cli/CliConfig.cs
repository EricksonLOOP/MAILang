namespace Mail.Cli;

public sealed class CliConfig
{
    public LimitsConfig Limits { get; set; } = new();
}

public sealed class LimitsConfig
{
    public int MaxToolCallsPerAgent { get; set; } = 10;
    public int MaxModelCallsPerAgent { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 30;
}
