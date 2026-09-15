namespace Mail.Mcp;

// Thrown at runtime when an MCP tool call fails or its response cannot be parsed.
public sealed class McpToolException(string message, Exception? inner = null)
    : Exception(message, inner);
