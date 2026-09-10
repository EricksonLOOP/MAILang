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

        var planId = plan.FilePath ?? plan.Workflow.Name;
        var ctx = executionId is not null
            ? new ExecutionContext(new RunId(executionId), limits, planId)
            : new ExecutionContext(limits, planId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(limits.Timeout);
        var linkedCt = timeoutCts.Token;

        ctx.Logger.Log(EventLogger.Kinds.ExecutionCreated,
            $"Execution '{ctx.RunId}' created for workflow '{plan.Workflow.Name}'.");

        ctx.Transition(RunStatus.Running);
        ctx.Logger.Log("execution.started",
            $"Workflow '{plan.Workflow.Name}' started.");

        var state = new ExecutionState(input);

        try
        {
            if (plan.Workflow.RequireInput is not null)
            {
                if (!ExprEvaluator.EvalBool(plan.Workflow.RequireInput, state, plan.Workflow.Name))
                {
                    ctx.Logger.Log("contract.rejected",
                        $"Workflow '{plan.Workflow.Name}' input contract rejected.");
                    throw new ContractViolationException(plan.Workflow.Name,
                        "Input contract (require input) was not satisfied.");
                }
            }

            state = await ExecuteWorkflowItemsAsync(plan.Workflow.Items, state, providerName, ctx, linkedCt);

            var output = state.Resolve(plan.Workflow.Finish);
            // Preserve the original entry-workflow boundary for existing programs.
            if (plan.Workflow.OutputType is NamedTypeRef && output is not MailSchema)
                throw new InvalidOperationException("Workflow output expected a schema.");

            if (plan.Workflow.RequireOutput is not null)
            {
                var outputState = state.Publish("output", output);
                if (!ExprEvaluator.EvalBool(plan.Workflow.RequireOutput, outputState, plan.Workflow.Name))
                {
                    ctx.Logger.Log("contract.rejected",
                        $"Workflow '{plan.Workflow.Name}' output contract rejected.");
                    throw new ContractViolationException(plan.Workflow.Name,
                        "Output contract (require output) was not satisfied.");
                }
            }

            ctx.Transition(RunStatus.Succeeded);
            ctx.Logger.Log("execution.succeeded", "Workflow completed successfully.");
            ctx.Logger.Log(EventLogger.Kinds.ExecutionEnded,
                $"Execution '{ctx.RunId}' ended: {RunStatus.Succeeded}.");

            return new ExecutionResult(true, output, ctx.Logger.Events, null,
                ctx.RunId, RunStatus.Succeeded);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            ctx.Transition(RunStatus.Cancelling);
            ctx.Logger.Log(EventLogger.Kinds.CancellationRequested, "Execution timed out.");
            ctx.Transition(RunStatus.Cancelled);
            ctx.Logger.Log("execution.timeout", "Execution timed out.");
            ctx.Logger.Log(EventLogger.Kinds.ExecutionEnded,
                $"Execution '{ctx.RunId}' ended: {RunStatus.Cancelled} (timeout).");

            return new ExecutionResult(false, null, ctx.Logger.Events, "Execution timed out.",
                ctx.RunId, RunStatus.Cancelled);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ctx.Transition(RunStatus.Cancelling);
            ctx.Logger.Log(EventLogger.Kinds.CancellationRequested, "Cancellation requested by caller.");
            ctx.Transition(RunStatus.Cancelled);
            ctx.Logger.Log(EventLogger.Kinds.ExecutionEnded,
                $"Execution '{ctx.RunId}' ended: {RunStatus.Cancelled}.");

            return new ExecutionResult(false, null, ctx.Logger.Events, "Execution was cancelled.",
                ctx.RunId, RunStatus.Cancelled);
        }
        catch (Exception ex)
        {
            ctx.Transition(RunStatus.Failed);
            ctx.Logger.Log("execution.failed", ex.Message);
            ctx.Logger.Log(EventLogger.Kinds.ExecutionEnded,
                $"Execution '{ctx.RunId}' ended: {RunStatus.Failed}.");

            return new ExecutionResult(false, null, ctx.Logger.Events, ex.Message,
                ctx.RunId, RunStatus.Failed);
        }
    }

    private async Task<ExecutionState> ExecuteWorkflowItemsAsync(
        IReadOnlyList<WorkflowItem> items,
        ExecutionState state,
        string providerName,
        ExecutionContext ctx,
        CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            switch (item)
            {
                case StepItem si:
                    var step = si.Step;
                    var actId = ActivationId.New();
                    var stepId = StepId.New();

                    ctx.Budget.TrackActivation();
                    ctx.Logger.Log("step.started",
                        $"Step '{step.Name}' started.",
                        stepId: stepId.Value, activationId: actId.Value);
                    ctx.Logger.Log(EventLogger.Kinds.ActivationStarted,
                        $"Activation of step '{step.Name}' started.",
                        stepId: stepId.Value, activationId: actId.Value);

                    var result = await ExecuteStepBodyAsync(step.Body, state, providerName, ctx, actId, ct);
                    state = state.Publish(step.SaveAs, result);

                    ctx.Logger.Log("step.completed",
                        $"Step '{step.Name}' completed, saved as '{step.SaveAs}'.",
                        stepId: stepId.Value, activationId: actId.Value);
                    ctx.Logger.Log(EventLogger.Kinds.ActivationConfirmed,
                        $"Activation of step '{step.Name}' confirmed.",
                        stepId: stepId.Value, activationId: actId.Value);
                    break;

                case IfItem ifItem:
                    ct.ThrowIfCancellationRequested();
                    var condValue = ExprEvaluator.EvalBool(ifItem.Condition, state, "if");
                    ctx.Logger.Log("branch.selected", $"Branch condition evaluated to {condValue}.");

                    if (condValue)
                    {
                        var branchState = await ExecuteWorkflowItemsAsync(ifItem.Then, state, providerName, ctx, ct);
                        state = state.MergeFrom(branchState);
                    }
                    else if (ifItem.Else is not null)
                    {
                        var branchState = await ExecuteWorkflowItemsAsync(ifItem.Else, state, providerName, ctx, ct);
                        state = state.MergeFrom(branchState);
                    }
                    break;

                case LoopItem loopItem:
                    ctx.Logger.Log("loop.started",
                        $"Loop '{loopItem.Name}' started (max {loopItem.MaxIterations} iterations).");
                    var loopResult = await ExecuteLoopAsync(loopItem, state, providerName, ctx, ct);
                    state = state.Publish(loopItem.SaveAs, loopResult);
                    ctx.Logger.Log("loop.completed",
                        $"Loop '{loopItem.Name}' completed, saved as '{loopItem.SaveAs}'.");
                    break;
            }
        }
        return state;
    }

    private async Task<MailValue> ExecuteLoopAsync(
        LoopItem loop,
        ExecutionState outerState,
        string providerName,
        ExecutionContext ctx,
        CancellationToken ct)
    {
        var paramBindings = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
        foreach (var param in loop.Params)
            paramBindings[param.Name] = outerState.Resolve(param.InitExpr);

        for (int iteration = 1; iteration <= loop.MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            ctx.Logger.Log("loop.iteration",
                $"Loop '{loop.Name}' iteration {iteration}/{loop.MaxIterations}.");

            var iterState = outerState;
            foreach (var (name, value) in paramBindings)
                iterState = iterState.Publish(name, value);

            try
            {
                await ExecuteLoopBodyAsync(loop.Body, iterState, providerName, ctx, ct);
                throw new InvalidOperationException(
                    $"Loop '{loop.Name}' body completed iteration {iteration} without 'break' or 'continue'.");
            }
            catch (LoopBreakSignal brk)
            {
                ctx.Logger.Log("loop.break",
                    $"Loop '{loop.Name}' exited via 'break' on iteration {iteration}.");
                return brk.Output;
            }
            catch (LoopContinueSignal cont)
            {
                if (iteration == loop.MaxIterations)
                    throw new InvalidOperationException(
                        $"Loop '{loop.Name}' reached maximum of {loop.MaxIterations} iterations without breaking.");

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

        throw new InvalidOperationException($"Loop '{loop.Name}' exited unexpectedly.");
    }

    private async Task<ExecutionState> ExecuteLoopBodyAsync(
        IReadOnlyList<WorkflowItem> items,
        ExecutionState state,
        string providerName,
        ExecutionContext ctx,
        CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            switch (item)
            {
                case StepItem si:
                    var step = si.Step;
                    var actId = ActivationId.New();
                    var stepId = StepId.New();

                    ctx.Budget.TrackActivation();
                    ctx.Logger.Log("step.started",
                        $"Step '{step.Name}' started.",
                        stepId: stepId.Value, activationId: actId.Value);
                    ctx.Logger.Log(EventLogger.Kinds.ActivationStarted,
                        $"Activation of step '{step.Name}' (loop body) started.",
                        stepId: stepId.Value, activationId: actId.Value);

                    var result = await ExecuteStepBodyAsync(step.Body, state, providerName, ctx, actId, ct);
                    state = state.Publish(step.SaveAs, result);

                    ctx.Logger.Log("step.completed",
                        $"Step '{step.Name}' completed, saved as '{step.SaveAs}'.",
                        stepId: stepId.Value, activationId: actId.Value);
                    ctx.Logger.Log(EventLogger.Kinds.ActivationConfirmed,
                        $"Activation of step '{step.Name}' (loop body) confirmed.",
                        stepId: stepId.Value, activationId: actId.Value);
                    break;

                case IfItem ifItem:
                    ct.ThrowIfCancellationRequested();
                    var condValue = ExprEvaluator.EvalBool(ifItem.Condition, state, "if");
                    ctx.Logger.Log("branch.selected", $"Branch condition evaluated to {condValue}.");

                    if (condValue)
                    {
                        var branchState = await ExecuteLoopBodyAsync(ifItem.Then, state, providerName, ctx, ct);
                        state = state.MergeFrom(branchState);
                    }
                    else if (ifItem.Else is not null)
                    {
                        var branchState = await ExecuteLoopBodyAsync(ifItem.Else, state, providerName, ctx, ct);
                        state = state.MergeFrom(branchState);
                    }
                    break;

                case LoopItem nestedLoop:
                    ctx.Logger.Log("loop.started",
                        $"Loop '{nestedLoop.Name}' started (max {nestedLoop.MaxIterations} iterations).");
                    var loopResult = await ExecuteLoopAsync(nestedLoop, state, providerName, ctx, ct);
                    state = state.Publish(nestedLoop.SaveAs, loopResult);
                    ctx.Logger.Log("loop.completed",
                        $"Loop '{nestedLoop.Name}' completed, saved as '{nestedLoop.SaveAs}'.");
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
        ExecutionContext ctx,
        ActivationId actId,
        CancellationToken ct)
    {
        switch (body)
        {
            case CallBody cb:
                return await ExecuteCallStepAsync(cb, state, ctx, actId, ct);

            case AgentBody ab:
                return await ExecuteAgentStepAsync(ab, state, providerName, ctx, actId, ct);

            case WorkflowCallBody wcb:
                return await ExecuteSubworkflowAsync(wcb, state, providerName, ctx, actId, ct);

            case ConditionalStepBody csb:
                var cond = ExprEvaluator.EvalBool(csb.Condition, state, "step if");
                ctx.Logger.Log("branch.selected",
                    $"Step branch condition evaluated to {cond}.",
                    activationId: actId.Value);
                return await ExecuteStepBodyAsync(
                    cond ? csb.Then : csb.Else, state, providerName, ctx, actId, ct);

            default:
                throw new InvalidOperationException($"Unknown step body type: {body.GetType().Name}");
        }
    }

    private async Task<MailValue> ExecuteSubworkflowAsync(
        WorkflowCallBody wcb,
        ExecutionState state,
        string providerName,
        ExecutionContext ctx,
        ActivationId actId,
        CancellationToken ct)
    {
        var key = wcb.TargetName ?? $"{wcb.Alias}.{wcb.WorkflowName}";
        if (!plan.Workflows.TryGetValue(key, out var childWorkflow))
            throw new InvalidOperationException($"Subworkflow '{key}' not found in plan.");

        ct.ThrowIfCancellationRequested();
        var childInput = state.Resolve(wcb.InputExpr);
        ValidateOutput(childInput, childWorkflow.InputType);
        var operationId = OperationId.New();

        ctx.Logger.Log("subworkflow.started",
            $"Subworkflow '{key}' started.",
            operationId: operationId.Value, activationId: actId.Value);

        var parentOperationId = ctx.Logger.ParentOperationId;
        ctx.Logger.ParentOperationId = operationId.Value;
        try
        {

            // Fresh state — child sees only its own input, not parent bindings.
            var childState = new ExecutionState(childInput);
            if (childWorkflow.RequireInput is not null && !ExprEvaluator.EvalBool(childWorkflow.RequireInput, childState, key))
                throw new ContractViolationException(key, "Input contract (require input) was not satisfied.");

            // Share parent ctx: same RunId, same deadline CancellationToken, same GlobalBudget.
            childState = await ExecuteWorkflowItemsAsync(
                childWorkflow.Items, childState, providerName, ctx, ct);

            ct.ThrowIfCancellationRequested();
            var output = childState.Resolve(childWorkflow.Finish);
            ValidateOutput(output, childWorkflow.OutputType);
            if (childWorkflow.RequireOutput is not null &&
                !ExprEvaluator.EvalBool(childWorkflow.RequireOutput, childState.Publish("output", output), key))
                throw new ContractViolationException(key, "Output contract (require output) was not satisfied.");

            ctx.Logger.ParentOperationId = parentOperationId;
            ctx.Logger.Log("subworkflow.completed",
                $"Subworkflow '{key}' completed.",
                operationId: operationId.Value, activationId: actId.Value);

            return output;
        }
        finally { ctx.Logger.ParentOperationId = parentOperationId; }
    }

    private async Task<MailValue> ExecuteCallStepAsync(
        CallBody cb,
        ExecutionState state,
        ExecutionContext ctx,
        ActivationId actId,
        CancellationToken ct)
    {
        var tool = plan.Tools[cb.ToolName];

        var resolvedArgs = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
        foreach (var (key, expr) in cb.Args)
            resolvedArgs[key] = state.Resolve(expr);
        var inputSchema = BuildSchemaFromValues(resolvedArgs.ToImmutable(), tools.InputContract(cb.ToolName), cb.ToolName);

        if (tool.RequireInput is not null)
        {
            var inputState = ExecutionState.WithInput(inputSchema);
            if (!ExprEvaluator.EvalBool(tool.RequireInput, inputState, cb.ToolName))
                throw new ContractViolationException(cb.ToolName,
                    "Input contract (require input) was not satisfied.");
        }

        var tracker = new BudgetTracker(ctx.Limits);
        tracker.ConsumeToolCall();

        ctx.Budget.TrackToolAttempt();

        var opId      = OperationId.New();
        var attemptId = AttemptId.New();

        ctx.Logger.Log(EventLogger.Kinds.DispatchIntent,
            $"Dispatching tool '{cb.ToolName}'.",
            operationId: opId.Value, activationId: actId.Value, attemptId: attemptId.Value);

        MailValue output;
        try
        {
            output = await tools.Resolve(cb.ToolName).ExecuteAsync(inputSchema, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.Log(EventLogger.Kinds.AttemptResult,
                $"Tool '{cb.ToolName}' failed: {ex.Message}",
                operationId: opId.Value, activationId: actId.Value, attemptId: attemptId.Value);
            throw;
        }

        ctx.Logger.Log(EventLogger.Kinds.AttemptResult,
            $"Tool '{cb.ToolName}' confirmed (effect: {EffectStatus.Confirmed}).",
            operationId: opId.Value, activationId: actId.Value, attemptId: attemptId.Value);

        if (tool.RequireOutput is not null)
        {
            var outputState = ExecutionState.WithInput(inputSchema).Publish("output", output);
            if (!ExprEvaluator.EvalBool(tool.RequireOutput, outputState, cb.ToolName))
                throw new ContractViolationException(cb.ToolName,
                    "Output contract (require output) was not satisfied.");
        }

        return output;
    }

    private async Task<MailValue> ExecuteAgentStepAsync(
        AgentBody ab,
        ExecutionState state,
        string providerName,
        ExecutionContext ctx,
        ActivationId actId,
        CancellationToken ct)
    {
        if (!plan.Agents.TryGetValue(ab.AgentName, out var agentDecl))
            throw new InvalidOperationException($"Agent '{ab.AgentName}' not found in plan.");

        var binding  = modelBindings.Resolve(agentDecl.LogicalModelName, providerName);
        var provider = providers.Resolve(binding.ProviderName);

        MailValue? agentInput = null;
        if (ab.InputExpr is not null)
            agentInput = state.Resolve(ab.InputExpr);

        if (agentDecl.RequireInput is not null && agentInput is not null)
        {
            var inputState = ExecutionState.WithInput(agentInput);
            if (!ExprEvaluator.EvalBool(agentDecl.RequireInput, inputState, ab.AgentName))
                throw new ContractViolationException(ab.AgentName,
                    "Input contract (require input) was not satisfied.");
        }

        var context = ab.ContextNames
            .Select(name => (name, state.ResolveBinding(name)))
            .ToList();

        var budget = new BudgetTracker(ctx.Limits);
        var runner = new AgentRunner(
            agentDecl, provider, binding.ModelId, _auth, tools, budget,
            ctx.Logger, plan.Schemas, agentInput, actId, ctx.Budget);

        var result = await runner.RunAsync(context, ct);

        if (agentDecl.RequireOutput is not null)
        {
            var outputState = agentInput is not null
                ? ExecutionState.WithInput(agentInput).Publish("output", result)
                : new ExecutionState(result);
            if (!ExprEvaluator.EvalBool(agentDecl.RequireOutput, outputState, ab.AgentName))
                throw new ContractViolationException(ab.AgentName,
                    "Output contract (require output) was not satisfied.");
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
        bool valid = expectedType switch
        {
            PrimitiveTypeRef p => p.Kind switch
            {
                PrimitiveKind.String => output is MailString,
                PrimitiveKind.Bool => output is MailBool,
                PrimitiveKind.Int => output is MailInt,
                PrimitiveKind.Decimal => output is MailDecimal,
                _ => false
            },
            NullableTypeRef n => output is MailNull || Valid(output, n.Inner),
            ListTypeRef l => output is MailList list && list.Elements.All(x => Valid(x, l.ElementType)),
            NamedTypeRef n when plan.Enums.TryGetValue(n.Name, out var e) =>
                output is MailEnum value && e.Symbols.Contains(value.Symbol),
            NamedTypeRef n when plan.Schemas.TryGetValue(n.Name, out var schema) =>
                output is MailSchema value && schema.Fields.All(f => value.Fields.TryGetValue(f.Name, out var v)
                    ? Valid(v, f.Type) : f.Optional),
            _ => false
        };
        if (!valid) throw new InvalidOperationException($"Workflow value does not match declared type '{expectedType}'.");
        bool Valid(MailValue value, TypeRef type) { ValidateOutput(value, type); return true; }
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
