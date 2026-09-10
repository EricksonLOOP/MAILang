using Mail.Contracts;

namespace Mail.Runtime;

public sealed class ToolRegistry : IToolRegistry
{
    private sealed record ToolEntry(
        IToolImplementation Implementation,
        FieldContract[] InputContract,
        FieldContract[] OutputContract);

    private readonly Dictionary<string, ToolEntry> _tools = new(StringComparer.Ordinal);

    public void Register(
        string name,
        IToolImplementation implementation,
        FieldContract[] inputContract,
        FieldContract[] outputContract) =>
        _tools[name] = new ToolEntry(implementation, inputContract, outputContract);

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
