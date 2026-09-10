using Mail.Compiler.Ast;
using Mail.Contracts;

namespace Mail.Runtime;

public sealed class AuthorizationChecker
{
    public void CheckAllowed(AgentDecl agent, string toolName, MailValue? agentInput = null)
    {
        var entry = agent.AllowedTools.FirstOrDefault(e =>
            string.Equals(e.ToolName, toolName, StringComparison.Ordinal));

        if (entry is null)
            throw new ToolNotAuthorizedException(agent.Name, toolName);

        if (entry.WhenGuard is null) return;

        // Evaluate the when guard with agent's input bound to "input"
        bool guardPassed;
        try
        {
            MailValue guardResult;
            if (agentInput is not null)
            {
                var guardState = ExecutionState.WithInput(agentInput);
                guardResult = ExprEvaluator.Eval(entry.WhenGuard, guardState);
            }
            else
            {
                // No input provided — can't evaluate guard; deny by default
                throw new ContractViolationException(
                    $"agent '{agent.Name}' tool '{toolName}'",
                    "Tool has a 'when' guard but no agent input was provided.");
            }
            guardPassed = guardResult is MailBool { Value: true };
        }
        catch (ContractViolationException)
        {
            throw new ToolNotAuthorizedException(agent.Name, toolName);
        }

        if (!guardPassed)
            throw new ToolNotAuthorizedException(agent.Name, toolName);
    }
}
