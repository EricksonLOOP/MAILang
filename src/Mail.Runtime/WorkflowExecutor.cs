using Mail.Compiler;
using Mail.Compiler.Ast;
using Mail.Contracts;

namespace Mail.Runtime;

public sealed class WorkflowExecutor(
    ValidatedPlan plan,
    IToolRegistry tools,
    IProviderRegistry providers,
    IModelBindings modelBindings,
    ExecutionLimits limits)
{
    private readonly AuthorizationChecker _auth = new();
    private readonly ArgumentValidator _argValidator = new();

    public async Task<ExecutionResult> RunAsync(
        MailValue input,
        string providerName,
        CancellationToken ct,
        string? executionId = null)
    {
        ContractVerifier.Verify(plan, tools);

        var logger = new EventLogger(executionId ?? Guid.NewGuid().ToString("N"));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(limits.Timeout);
        var linkedCt = timeoutCts.Token;

        var state = new ExecutionState(input);
        logger.Log("execution.started", $"Workflow '{plan.Workflow.Name}' started.");

        try
        {
            foreach (var step in plan.Workflow.Steps)
            {
                logger.Log("step.started", $"Step '{step.Name}' started.");

                MailValue result = step.Body switch
                {
                    CallBody cb   => await ExecuteCallStep(cb, state, linkedCt),
                    AgentBody ab  => await ExecuteAgentStep(ab, state, providerName, logger, linkedCt),
                    _             => throw new InvalidOperationException($"Unknown step body type."),
                };

                state = state.Publish(step.SaveAs, result);
                logger.Log("step.completed", $"Step '{step.Name}' completed, saved as '{step.SaveAs}'.");
            }

            var output = state.Resolve(plan.Workflow.Finish);
            ValidateOutput(output, plan.Workflow.OutputType);

            logger.Log("execution.succeeded", "Workflow completed successfully.");
            return new ExecutionResult(true, output, logger.Events, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.Log("execution.timeout", "Execution timed out.");
            return new ExecutionResult(false, null, logger.Events, "Execution timed out.");
        }
        catch (Exception ex)
        {
            logger.Log("execution.failed", ex.Message);
            return new ExecutionResult(false, null, logger.Events, ex.Message);
        }
    }

    private async Task<MailValue> ExecuteCallStep(CallBody cb, ExecutionState state, CancellationToken ct)
    {
        // Resolve args from current state
        var resolvedArgs = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
        foreach (var (key, expr) in cb.Args)
            resolvedArgs[key] = state.Resolve(expr);

        // Validate args against tool input contract and convert to MailSchema
        var inputSchema = BuildSchemaFromValues(resolvedArgs.ToImmutable(), tools.InputContract(cb.ToolName), cb.ToolName);

        // Consume budget and dispatch
        var tracker = new BudgetTracker(limits);
        tracker.ConsumeToolCall();

        return await tools.Resolve(cb.ToolName).ExecuteAsync(inputSchema, ct);
    }

    private async Task<MailValue> ExecuteAgentStep(AgentBody ab, ExecutionState state, string providerName, EventLogger logger, CancellationToken ct)
    {
        if (!plan.Agents.TryGetValue(ab.AgentName, out var agentDecl))
            throw new InvalidOperationException($"Agent '{ab.AgentName}' not found in plan.");

        var binding = modelBindings.Resolve(agentDecl.LogicalModelName, providerName);
        var provider = providers.Resolve(binding.ProviderName);

        var context = ab.ContextNames
            .Select(name => (name, state.ResolveBinding(name)))
            .ToList();

        var budget = new BudgetTracker(limits);
        var runner = new AgentRunner(agentDecl, provider, binding.ModelId, _auth, tools, budget, logger, plan.Schemas);
        return await runner.RunAsync(context, ct);
    }

    private static MailSchema BuildSchemaFromValues(
        System.Collections.Immutable.ImmutableDictionary<string, MailValue> values,
        FieldContract[] contract,
        string toolName)
    {
        foreach (var fc in contract)
            if (fc.Required && !values.ContainsKey(fc.Name))
                throw new ArgumentValidationException(toolName, fc.Name, "Required argument missing.");

        return new MailSchema(toolName + "Input", values);
    }

    private void ValidateOutput(MailValue output, TypeRef expectedType)
    {
        // For the prototype: check that named type output is a MailSchema
        if (expectedType is NamedTypeRef && output is not MailSchema)
            throw new InvalidOperationException(
                $"Workflow output expected a schema but got {output.GetType().Name}.");
    }

}

// Extension helper used in agent step
file static class ExecutionStateExtensions
{
    public static MailValue ResolveBinding(this ExecutionState state, string name) =>
        name == "input" ? state.Input
        : state.Bindings.TryGetValue(name, out var v) ? v
        : throw new BindingNotFoundException(name);
}

