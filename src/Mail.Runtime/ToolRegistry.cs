using Mail.Contracts;

namespace Mail.Runtime;

public sealed class ToolRegistry : IToolRegistry
{
    private sealed record ToolEntry(
        IToolImplementation Implementation,
        FieldContract[] InputContract,
        FieldContract[] OutputContract,
        bool HostExecuted);

    private readonly Dictionary<string, ToolEntry> _tools = new(StringComparer.Ordinal);

    public void Register(
        string name,
        IToolImplementation implementation,
        FieldContract[] inputContract,
        FieldContract[] outputContract,
        bool hostExecuted = false) =>
        _tools[name] = new ToolEntry(implementation, inputContract, outputContract, hostExecuted);

    public bool IsHostExecuted(string toolName) =>
        _tools.TryGetValue(toolName, out var e) && e.HostExecuted;

    public IEnumerable<string> HostExecutedNames =>
        _tools.Where(kv => kv.Value.HostExecuted).Select(kv => kv.Key);

    public IEnumerable<string> ClientExecutedNames =>
        _tools.Where(kv => !kv.Value.HostExecuted).Select(kv => kv.Key);

    public IToolImplementation Resolve(string toolName)
    {
        if (_tools.TryGetValue(toolName, out var entry)) return entry.Implementation;
        throw new InvalidOperationException($"Tool '{toolName}' is not registered.");
    }

    public FieldContract[] InputContract(string toolName) =>
        _tools.TryGetValue(toolName, out var e)
            ? e.InputContract
            : throw new InvalidOperationException($"Tool '{toolName}' is not registered.");

    public FieldContract[] OutputContract(string toolName) =>
        _tools.TryGetValue(toolName, out var e)
            ? e.OutputContract
            : throw new InvalidOperationException($"Tool '{toolName}' is not registered.");

    public bool IsRegistered(string toolName) => _tools.ContainsKey(toolName);
}
