using Mail.Compiler.Ast;

namespace Mail.Runtime;

public sealed class AuthorizationChecker
{
    public void CheckAllowed(AgentDecl agent, string toolName)
    {
        if (!agent.AllowedTools.Contains(toolName, StringComparer.Ordinal))
            throw new ToolNotAuthorizedException(agent.Name, toolName);
    }
}
