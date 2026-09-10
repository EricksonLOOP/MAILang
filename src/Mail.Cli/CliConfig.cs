namespace Mail.Cli;

public sealed class CliConfig
{
    public string Provider { get; set; } = "simulated";
    public Dictionary<string, ModelBindingConfig> ModelBindings { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();
}

public sealed class ModelBindingConfig
{
    public string Provider { get; set; } = "";
    public string ModelId { get; set; } = "";
}

public sealed class LimitsConfig
{
    public int MaxToolCallsPerAgent { get; set; } = 10;
    public int MaxModelCallsPerAgent { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 30;
}
