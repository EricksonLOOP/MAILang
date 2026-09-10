using Mail.Compiler.Ast;
using Mail.Contracts;
using System.Diagnostics;
using System.Text.Json;
using Ast = Mail.Compiler.Ast;

namespace Mail.Runtime;

internal sealed class AgentRunner(
    AgentDecl agent,
    IModelProvider provider,
    string modelId,
    AuthorizationChecker auth,
    IToolRegistry tools,
    BudgetTracker budget,
    EventLogger logger,
    IReadOnlyDictionary<string, SchemaDecl>? schemas = null,
    MailValue? agentInput = null,
    ActivationId activationId = default,
    GlobalBudget? globalBudget = null)
{
    private readonly ArgumentValidator _argValidator = new();

    public async Task<MailValue> RunAsync(
        IReadOnlyList<(string Name, MailValue Value)> context,
        CancellationToken ct)
    {
        var messages = new List<Message>();
        messages.Add(new SystemMessage(BuildSystemPrompt()));
        messages.Add(new UserMessage(BuildContextMessage(context)));

        var toolDefs = BuildToolDefinitions();

        while (true)
        {
            budget.ConsumeModelCall();
            globalBudget?.TrackModelCall();

            var opId     = OperationId.New();
            var attemptId = AttemptId.New();

            logger.Log("model.requested",
                $"Agent '{agent.Name}' calling model '{modelId}'.",
                operationId: opId.Value, activationId: activationId.Value);

            logger.Log(EventLogger.Kinds.DispatchIntent,
                $"Agent '{agent.Name}' dispatching model call '{modelId}'.",
                operationId: opId.Value, activationId: activationId.Value, attemptId: attemptId.Value);

            ModelResponse response;
            try
            {
                response = await provider.CompleteAsync(new ModelRequest(
                    modelId, messages, toolDefs, BuildOutputSchema()), ct);
            }
            catch (Exception ex)
            {
                logger.Log(EventLogger.Kinds.AttemptResult,
                    $"Model call failed: {ex.Message}",
                    operationId: opId.Value, activationId: activationId.Value, attemptId: attemptId.Value);
                throw;
            }

            logger.Log(EventLogger.Kinds.AttemptResult,
                $"Model call returned ({(response.ToolCalls?.Count > 0 ? "tool calls" : "text")}).",
                operationId: opId.Value, activationId: activationId.Value, attemptId: attemptId.Value);

            if (response.ToolCalls is { Count: > 0 })
            {
                logger.Log("model.tool_requested",
                    $"{response.ToolCalls.Count} tool call(s) requested.", opId.Value);

                ValidateNoDuplicateCallIds(response.ToolCalls);

                messages.Add(new AssistantMessage(response.Text, response.ToolCalls));

                foreach (var toolCall in response.ToolCalls)
                {
                    var toolAttemptId = AttemptId.New();

                    try
                    {
                        auth.CheckAllowed(agent, toolCall.ToolName, agentInput);
                    }
                    catch (ToolNotAuthorizedException)
                    {
                        logger.Log("tool.denied",
                            $"Tool '{toolCall.ToolName}' denied for agent '{agent.Name}'.",
                            operationId: opId.Value, callId: toolCall.CallId);
                        throw;
                    }

                    logger.Log("tool.authorized",
                        $"Tool '{toolCall.ToolName}' authorized.",
                        operationId: opId.Value, callId: toolCall.CallId);

                    budget.ConsumeToolCall();
                    globalBudget?.TrackToolAttempt();

                    var input = _argValidator.Validate(
                        toolCall.Arguments,
                        tools.InputContract(toolCall.ToolName),
                        toolCall.ToolName);

                    logger.Log(EventLogger.Kinds.DispatchIntent,
                        $"Dispatching tool '{toolCall.ToolName}'.",
                        operationId: opId.Value, callId: toolCall.CallId,
                        activationId: activationId.Value, attemptId: toolAttemptId.Value);

                    logger.Log("tool.started",
                        $"Tool '{toolCall.ToolName}' starting.",
                        operationId: opId.Value, callId: toolCall.CallId);

                    var sw = Stopwatch.StartNew();
                    MailSchema output;
                    try
                    {
                        output = await tools.Resolve(toolCall.ToolName).ExecuteAsync(input, ct);
                        sw.Stop();
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        logger.Log("tool.failed",
                            $"Tool '{toolCall.ToolName}' failed.",
                            operationId: opId.Value, callId: toolCall.CallId, durationMs: sw.ElapsedMilliseconds);
                        logger.Log(EventLogger.Kinds.AttemptResult,
                            $"Tool '{toolCall.ToolName}' attempt failed: {ex.Message}",
                            operationId: opId.Value, callId: toolCall.CallId,
                            activationId: activationId.Value, attemptId: toolAttemptId.Value,
                            durationMs: sw.ElapsedMilliseconds);
                        throw;
                    }

                    logger.Log("tool.completed",
                        $"Tool '{toolCall.ToolName}' completed.",
                        operationId: opId.Value, callId: toolCall.CallId, durationMs: sw.ElapsedMilliseconds);

                    logger.Log(EventLogger.Kinds.AttemptResult,
                        $"Tool '{toolCall.ToolName}' confirmed (effect: {EffectStatus.Confirmed}).",
                        operationId: opId.Value, callId: toolCall.CallId,
                        activationId: activationId.Value, attemptId: toolAttemptId.Value,
                        durationMs: sw.ElapsedMilliseconds);

                    ValidateOutput(output, tools.OutputContract(toolCall.ToolName), toolCall.ToolName);

                    var resultJson = SchemaConverter.ToJson(output);
                    messages.Add(new ToolResultMessage(toolCall.CallId, toolCall.ToolName, resultJson));
                }

                continue;
            }

            if (response.Text is not null)
            {
                var result = ParseFinalResponse(response.Text);
                logger.Log("agent.output_validated",
                    $"Agent '{agent.Name}' output validated.", opId.Value);
                return result;
            }

            throw new InvalidProviderResponseException(
                "Provider returned a response with neither tool calls nor text.");
        }
    }

    private static void ValidateNoDuplicateCallIds(IReadOnlyList<ToolCallRequest> calls)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls)
            if (!seen.Add(call.CallId))
                throw new DuplicateCallIdException(call.CallId);
    }

    private static void ValidateOutput(MailSchema output, FieldContract[] contract, string toolName)
    {
        foreach (var fc in contract)
        {
            if (!output.Fields.ContainsKey(fc.Name) && fc.Required)
                throw new ArgumentValidationException(toolName, fc.Name,
                    "Required output field is missing.");
        }
    }

    private MailValue ParseFinalResponse(string text)
    {
        if (schemas is not null)
        {
            using var parsed = JsonDocument.Parse(text);
            return DeclaredSchema.Parse(parsed.RootElement, agent.OutputType, schemas);
        }
        if (agent.OutputType is not Ast.NamedTypeRef namedType)
            return new MailString(text.Trim());

        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidProviderResponseException(
                $"Agent output expected JSON object for schema '{namedType.Name}', got {doc.RootElement.ValueKind}.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!seen.Add(prop.Name))
                throw new InvalidProviderResponseException(
                    $"Duplicate property '{prop.Name}' in agent response JSON.");

            builder[prop.Name] = ConvertElement(prop.Value, prop.Name);
        }

        return new MailSchema(namedType.Name, builder.ToImmutable());
    }

    private static MailValue ConvertElement(JsonElement element, string fieldName) => element.ValueKind switch
    {
        JsonValueKind.String => new MailString(element.GetString()!),
        JsonValueKind.True   => new MailBool(true),
        JsonValueKind.False  => new MailBool(false),
        JsonValueKind.Number => new MailInt(element.GetInt64()),
        _ => throw new InvalidProviderResponseException(
            $"Unsupported value kind '{element.ValueKind}' for field '{fieldName}' in agent response."),
    };

    private List<ToolDefinition> BuildToolDefinitions()
    {
        var defs = new List<ToolDefinition>();
        foreach (var entry in agent.AllowedTools)
        {
            if (entry.WhenGuard is not null && agentInput is not null)
            {
                try
                {
                    var guardState = ExecutionState.WithInput(agentInput);
                    var guardResult = ExprEvaluator.Eval(entry.WhenGuard, guardState);
                    if (guardResult is MailBool { Value: false }) continue;
                }
                catch
                {
                    continue;
                }
            }

            var inputSchema  = FieldContractsToJsonSchema(tools.InputContract(entry.ToolName));
            var outputSchema = FieldContractsToJsonSchema(tools.OutputContract(entry.ToolName));
            defs.Add(new ToolDefinition(entry.ToolName, inputSchema, outputSchema));
        }
        return defs;
    }

    private static string FieldContractsToJsonSchema(FieldContract[] contracts)
    {
        var props = new System.Text.StringBuilder("{\"type\":\"object\",\"properties\":{");
        var required = new List<string>();
        var first = true;

        foreach (var fc in contracts)
        {
            if (!first) props.Append(',');
            first = false;
            props.Append($"\"{fc.Name}\":{KindToJsonSchemaType(fc.Kind)}");
            if (fc.Required) required.Add(fc.Name);
        }

        props.Append("},\"required\":[");
        props.Append(string.Join(",", required.Select(r => $"\"{r}\"")));
        props.Append("]}");
        return props.ToString();
    }

    private static string KindToJsonSchemaType(MailTypeKind kind) => kind switch
    {
        MailTypeKind.String => "{\"type\":\"string\"}",
        MailTypeKind.Bool   => "{\"type\":\"boolean\"}",
        MailTypeKind.Int    => "{\"type\":\"integer\"}",
        MailTypeKind.Schema => "{\"type\":\"object\"}",
        _ => "{\"type\":\"string\"}",
    };

    private string BuildOutputSchema()
    {
        if (schemas is not null) return DeclaredSchema.ToJson(agent.OutputType, schemas);
        if (agent.OutputType is Ast.NamedTypeRef named)
            return $"{{\"type\":\"object\",\"description\":\"Schema {named.Name}\"}}";
        return "{\"type\":\"string\"}";
    }

    private const string RuntimeInstructions =
        "Use the available tools to complete the user's request. " +
        "When you have all the information needed, respond with a JSON object matching the expected output schema.";

    private string BuildSystemPrompt()
    {
        if (agent.SystemPrompt is null)
            return "You are an AI agent. " + RuntimeInstructions;

        return agent.SystemPrompt.Text + "\n\n--- Runtime Instructions ---\n" + RuntimeInstructions;
    }

    private static string BuildContextMessage(IReadOnlyList<(string Name, MailValue Value)> context)
    {
        var sb = new System.Text.StringBuilder("Context:\n");
        foreach (var (name, value) in context)
            sb.AppendLine($"{name}: {SchemaConverter.ToJson(value)}");
        return sb.ToString();
    }
}
