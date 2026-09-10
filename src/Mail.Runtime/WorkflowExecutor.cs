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
            // Validate workflow input contract
            if (plan.Workflow.RequireInput is not null)
            {
                if (!ExprEvaluator.EvalBool(plan.Workflow.RequireInput, state, plan.Workflow.Name))
                {
                    logger.Log("contract.rejected", $"Workflow '{plan.Workflow.Name}' input contract rejected.");
                    throw new ContractViolationException(plan.Workflow.Name, "Input contract (require input) was not satisfied.");
                }
            }

            state = await ExecuteWorkflowItemsAsync(plan.Workflow.Items, state, providerName, logger, linkedCt);

            var output = state.Resolve(plan.Workflow.Finish);
            ValidateOutput(output, plan.Workflow.OutputType);

            // Validate workflow output contract
            if (plan.Workflow.RequireOutput is not null)
            {
                var outputState = state.Publish("output", output);
                if (!ExprEvaluator.EvalBool(plan.Workflow.RequireOutput, outputState, plan.Workflow.Name))
                {
                    logger.Log("contract.rejected", $"Workflow '{plan.Workflow.Name}' output contract rejected.");
                    throw new ContractViolationException(plan.Workflow.Name, "Output contract (require output) was not satisfied.");
                }
            }

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

    private async Task<ExecutionState> ExecuteWorkflowItemsAsync(
        IReadOnlyList<WorkflowItem> items,
        ExecutionState state,
        string providerName,
        EventLogger logger,
        CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            switch (item)
            {
                case StepItem si:
                    var step = si.Step;
                    logger.Log("step.started", $"Step '{step.Name}' started.");
                    var result = await ExecuteStepBodyAsync(step.Body, state, providerName, logger, ct);
                    state = state.Publish(step.SaveAs, result);
                    logger.Log("step.completed", $"Step '{step.Name}' completed, saved as '{step.SaveAs}'.");
                    break;

                case IfItem ifItem:
                    ct.ThrowIfCancellationRequested();
                    var condValue = ExprEvaluator.EvalBool(ifItem.Condition, state, "if");
                    logger.Log("branch.selected", $"Branch condition evaluated to {condValue}.");

                    if (condValue)
                    {
                        var branchState = await ExecuteWorkflowItemsAsync(ifItem.Then, state, providerName, logger, ct);
                        state = state.MergeFrom(branchState);
                    }
                    else if (ifItem.Else is not null)
                    {
                        var branchState = await ExecuteWorkflowItemsAsync(ifItem.Else, state, providerName, logger, ct);
                        state = state.MergeFrom(branchState);
                    }
                    // No else and condition false: no state change
                    break;

                case LoopItem loopItem:
                    logger.Log("loop.started", $"Loop '{loopItem.Name}' started (max {loopItem.MaxIterations} iterations).");
                    var loopResult = await ExecuteLoopAsync(loopItem, state, providerName, logger, ct);
                    state = state.Publish(loopItem.SaveAs, loopResult);
                    logger.Log("loop.completed", $"Loop '{loopItem.Name}' completed, saved as '{loopItem.SaveAs}'.");
                    break;
            }
        }
        return state;
    }

    private async Task<MailValue> ExecuteLoopAsync(
        LoopItem loop,
        ExecutionState outerState,
        string providerName,
        EventLogger logger,
        CancellationToken ct)
    {
        // Build initial loop-param bindings from init expressions evaluated against outer state
        var paramBindings = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
        foreach (var param in loop.Params)
            paramBindings[param.Name] = outerState.Resolve(param.InitExpr);

        for (int iteration = 1; iteration <= loop.MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            logger.Log("loop.iteration", $"Loop '{loop.Name}' iteration {iteration}/{loop.MaxIterations}.");

            // Build iteration state: outer bindings + current param values
            var iterState = outerState;
            foreach (var (name, value) in paramBindings)
                iterState = iterState.Publish(name, value);

            try
            {
                await ExecuteLoopBodyAsync(loop.Body, iterState, providerName, logger, ct);
                throw new InvalidOperationException(
                    $"Loop '{loop.Name}' body completed iteration {iteration} without 'break' or 'continue'.");
            }
            catch (LoopBreakSignal brk)
            {
                logger.Log("loop.break", $"Loop '{loop.Name}' exited via 'break' on iteration {iteration}.");
                return brk.Output;
            }
            catch (LoopContinueSignal cont)
            {
                if (iteration == loop.MaxIterations)
                    throw new InvalidOperationException(
                        $"Loop '{loop.Name}' reached maximum of {loop.MaxIterations} iterations without breaking.");

                // Update param bindings for the next iteration
                paramBindings.Clear();
                foreach (var param in loop.Params)
                {
                    if (cont.Args.TryGetValue(param.Name, out var nextValue))
                        paramBindings[param.Name] = nextValue;
                    else
                        throw new InvalidOperationException(
                            $"Loop '{loop.Name}' 'continue' did not provide a value for parameter '{param.Name}'.");
                }
            }
        }

        // Should be unreachable: the catch(LoopContinueSignal) above throws when iteration == MaxIterations
        throw new InvalidOperationException($"Loop '{loop.Name}' exited unexpectedly.");
    }

    // Returns the updated ExecutionState after the items complete normally.
    // Throws LoopBreakSignal or LoopContinueSignal when those statements are reached.
    private async Task<ExecutionState> ExecuteLoopBodyAsync(
        IReadOnlyList<WorkflowItem> items,
        ExecutionState state,
        string providerName,
        EventLogger logger,
        CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            switch (item)
            {
                case StepItem si:
                    var step = si.Step;
                    logger.Log("step.started", $"Step '{step.Name}' started.");
                    var result = await ExecuteStepBodyAsync(step.Body, state, providerName, logger, ct);
                    state = state.Publish(step.SaveAs, result);
                    logger.Log("step.completed", $"Step '{step.Name}' completed, saved as '{step.SaveAs}'.");
                    break;

                case IfItem ifItem:
                    ct.ThrowIfCancellationRequested();
                    var condValue = ExprEvaluator.EvalBool(ifItem.Condition, state, "if");
                    logger.Log("branch.selected", $"Branch condition evaluated to {condValue}.");

                    if (condValue)
                    {
                        // Signals from within the branch bubble up naturally
                        var branchState = await ExecuteLoopBodyAsync(ifItem.Then, state, providerName, logger, ct);
                        state = state.MergeFrom(branchState);
                    }
                    else if (ifItem.Else is not null)
                    {
                        var branchState = await ExecuteLoopBodyAsync(ifItem.Else, state, providerName, logger, ct);
                        state = state.MergeFrom(branchState);
                    }
                    break;

                case LoopItem nestedLoop:
                    logger.Log("loop.started", $"Loop '{nestedLoop.Name}' started (max {nestedLoop.MaxIterations} iterations).");
                    var loopResult = await ExecuteLoopAsync(nestedLoop, state, providerName, logger, ct);
                    state = state.Publish(nestedLoop.SaveAs, loopResult);
                    logger.Log("loop.completed", $"Loop '{nestedLoop.Name}' completed, saved as '{nestedLoop.SaveAs}'.");
                    break;

                case LoopBreakItem brk:
                    throw new LoopBreakSignal(state.Resolve(brk.OutputExpr));

                case LoopContinueItem cont:
                    var args = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
                    foreach (var (key, expr) in cont.Args)
                        args[key] = state.Resolve(expr);
                    throw new LoopContinueSignal(args.ToImmutable());
            }
        }
        return state;
    }

    private async Task<MailValue> ExecuteStepBodyAsync(
        StepBody body,
        ExecutionState state,
        string providerName,
        EventLogger logger,
        CancellationToken ct)
    {
        switch (body)
        {
            case CallBody cb:
                return await ExecuteCallStepAsync(cb, state, ct);

            case AgentBody ab:
                return await ExecuteAgentStepAsync(ab, state, providerName, logger, ct);

            case ConditionalStepBody csb:
                var cond = ExprEvaluator.EvalBool(csb.Condition, state, "step if");
                logger.Log("branch.selected", $"Step branch condition evaluated to {cond}.");
                return await ExecuteStepBodyAsync(cond ? csb.Then : csb.Else, state, providerName, logger, ct);

            default:
                throw new InvalidOperationException($"Unknown step body type: {body.GetType().Name}");
        }
    }

    private async Task<MailValue> ExecuteCallStepAsync(CallBody cb, ExecutionState state, CancellationToken ct)
    {
        var tool = plan.Tools[cb.ToolName];

        // Resolve args
        var resolvedArgs = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
        foreach (var (key, expr) in cb.Args)
            resolvedArgs[key] = state.Resolve(expr);
        var inputSchema = BuildSchemaFromValues(resolvedArgs.ToImmutable(), tools.InputContract(cb.ToolName), cb.ToolName);

        // Validate tool input contract
        if (tool.RequireInput is not null)
        {
            var inputState = ExecutionState.WithInput(inputSchema);
            if (!ExprEvaluator.EvalBool(tool.RequireInput, inputState, cb.ToolName))
                throw new ContractViolationException(cb.ToolName, "Input contract (require input) was not satisfied.");
        }

        var tracker = new BudgetTracker(limits);
        tracker.ConsumeToolCall();

        var output = await tools.Resolve(cb.ToolName).ExecuteAsync(inputSchema, ct);

        // Validate tool output contract
        if (tool.RequireOutput is not null)
        {
            var outputState = ExecutionState.WithInput(inputSchema).Publish("output", output);
            if (!ExprEvaluator.EvalBool(tool.RequireOutput, outputState, cb.ToolName))
                throw new ContractViolationException(cb.ToolName, "Output contract (require output) was not satisfied.");
        }

        return output;
    }

    private async Task<MailValue> ExecuteAgentStepAsync(AgentBody ab, ExecutionState state, string providerName, EventLogger logger, CancellationToken ct)
    {
        if (!plan.Agents.TryGetValue(ab.AgentName, out var agentDecl))
            throw new InvalidOperationException($"Agent '{ab.AgentName}' not found in plan.");

        var binding = modelBindings.Resolve(agentDecl.LogicalModelName, providerName);
        var provider = providers.Resolve(binding.ProviderName);

        // Resolve explicit input expression if provided
        MailValue? agentInput = null;
        if (ab.InputExpr is not null)
            agentInput = state.Resolve(ab.InputExpr);

        // Validate agent input contract
        if (agentDecl.RequireInput is not null && agentInput is not null)
        {
            var inputState = ExecutionState.WithInput(agentInput);
            if (!ExprEvaluator.EvalBool(agentDecl.RequireInput, inputState, ab.AgentName))
                throw new ContractViolationException(ab.AgentName, "Input contract (require input) was not satisfied.");
        }

        var context = ab.ContextNames
            .Select(name => (name, state.ResolveBinding(name)))
            .ToList();

        var budget = new BudgetTracker(limits);
        var runner = new AgentRunner(agentDecl, provider, binding.ModelId, _auth, tools, budget, logger, plan.Schemas, agentInput);
        var result = await runner.RunAsync(context, ct);

        // Validate agent output contract
        if (agentDecl.RequireOutput is not null)
        {
            var outputState = agentInput is not null
                ? ExecutionState.WithInput(agentInput).Publish("output", result)
                : new ExecutionState(result);
            if (!ExprEvaluator.EvalBool(agentDecl.RequireOutput, outputState, ab.AgentName))
                throw new ContractViolationException(ab.AgentName, "Output contract (require output) was not satisfied.");
        }

        return result;
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

// Internal control-flow signals for loop break/continue — never cross public API boundaries
file sealed class LoopBreakSignal(MailValue output) : Exception
{
    public MailValue Output { get; } = output;
}

file sealed class LoopContinueSignal(System.Collections.Immutable.ImmutableDictionary<string, MailValue> args) : Exception
{
    public System.Collections.Immutable.ImmutableDictionary<string, MailValue> Args { get; } = args;
}
