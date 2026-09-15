namespace Mail.Mcp;

public sealed record McpServerConfig(
    string Command,
    string[] Args,
    IReadOnlyDictionary<string, string> Env,
    string Transport,
    IReadOnlyDictionary<string, string> Bindings);
