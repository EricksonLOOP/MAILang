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


public sealed class DuplicateCallIdException(string callId)
    : Exception($"Provider returned duplicate call ID '{callId}'.");

public sealed class ContractViolationException(string context, string detail)
    : Exception($"Contract violation in '{context}': {detail}.")
{
    public string Context { get; } = context;
    public string Detail  { get; } = detail;
}

public sealed class InvalidStatusTransitionException(RunStatus from, RunStatus to)
    : Exception($"Invalid execution status transition: {from} → {to}.")
{
    public RunStatus From { get; } = from;
    public RunStatus To   { get; } = to;
}

public sealed class ExecutionAlreadyTerminatedException(string runId)
    : Exception($"Execution '{runId}' is in a terminal state and cannot be resumed.")
{
    public string RunId { get; } = runId;
}

public sealed class ApprovalDeniedException(string operationId)
    : Exception($"Approval was denied for operation '{operationId}'.")
{
    public string OperationId { get; } = operationId;
}

public sealed class ApprovalExpiredException(string operationId)
    : Exception($"Approval expired for operation '{operationId}'.")
{
    public string OperationId { get; } = operationId;
}

public sealed class ProviderHttpException(string providerName, int statusCode, string excerpt)
    : Exception(statusCode > 0
        ? $"Provider '{providerName}' returned HTTP {statusCode}: {excerpt}"
        : $"Provider '{providerName}' network error: {excerpt}")
{
    public string ProviderName { get; } = providerName;
    public int StatusCode { get; } = statusCode;
}

public sealed class ProviderResponseException(string providerName, string reason, string? excerpt = null)
    : Exception($"Provider '{providerName}' response error: {reason}" + (excerpt != null ? $" — {excerpt}" : ""))
{
    public string ProviderName { get; } = providerName;
}

public sealed class ProviderConfigurationException(string providerName, string reason)
    : Exception($"Provider '{providerName}' configuration error: {reason}")
{
    public string ProviderName { get; } = providerName;
}
