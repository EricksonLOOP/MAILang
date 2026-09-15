namespace Mail.Mcp;

// Thrown at startup/configuration time: unsupported JSON Schema constructs,
// tool name collisions, tools declared in bindings but absent from the plan.
public sealed class McpBindingException(string message) : Exception(message);
