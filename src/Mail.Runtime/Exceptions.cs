namespace Mail.Runtime;

public sealed class ToolNotAuthorizedException(string agentName, string toolName)
    : Exception($"Agent '{agentName}' is not allowed to call tool '{toolName}'.")
{
    public string AgentName { get; } = agentName;
    public string ToolName { get; } = toolName;
}

public sealed class BudgetExceededException(string resource, int limit)
    : Exception($"Execution budget exceeded: {resource} limit of {limit} reached.")
{
    public string Resource { get; } = resource;
}

public sealed class InvalidProviderResponseException(string message)
    : Exception(message);

public sealed class ArgumentValidationException(string toolName, string field, string reason)
    : Exception($"Tool '{toolName}' argument validation failed for field '{field}': {reason}.")
{
    public string ToolName { get; } = toolName;
    public string Field { get; } = field;
}

public sealed class ToolContractMismatchException(string toolName, string reason)
    : Exception($"Tool '{toolName}' contract does not match declaration: {reason}.")
{
    public string ToolName { get; } = toolName;
}

public sealed class BindingNotFoundException(string name)
    : Exception($"Binding '{name}' is not available.")
{
    public string Name { get; } = name;
}

public sealed class ProviderNotFoundException(string providerName)
    : Exception($"Provider '{providerName}' is not registered.")
{
    public string ProviderName { get; } = providerName;
}

public sealed class ModelBindingNotFoundException(string logicalName, string providerName)
    : Exception($"No model binding found for logical name '{logicalName}' with provider '{providerName}'.")
{
    public string LogicalName { get; } = logicalName;
    public string ProviderName { get; } = providerName;
}

public sealed class DuplicateCallIdException(string callId)
    : Exception($"Provider returned duplicate call ID '{callId}'.");
