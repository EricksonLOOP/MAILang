using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace Mail.Tests;

public class EventCorrelationTests
{
    private static readonly string TokenEchoSource = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "../../../../..", "examples", "token-echo.mail"));

    private static async Task<ExecutionResult> RunTokenEchoWithAdaptiveProvider()
    {
        var (plan, _) = MailCompiler.Compile(TokenEchoSource, "token-echo.mail");

        var tools = new ToolRegistry();
        tools.Register(
            "GenerateToken",
            new CorrelationTokenTool(),
            [new FieldContract("prompt", MailTypeKind.String, Required: true)],
            [new FieldContract("token",  MailTypeKind.String, Required: true)]);

        var providers = new ProviderRegistry();
        providers.Register("Sim", new AdaptiveTokenProvider());

        var executor = new WorkflowExecutor(plan!, tools, providers, ExecutionLimits.Default);
        var input = new MailSchema("EchoRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("prompt", new MailString("corr-test")));

        return await executor.RunAsync(input, CancellationToken.None);
    }

    [Fact]
    public async Task Full_cycle_event_sequence_is_correct()
    {
        var result = await RunTokenEchoWithAdaptiveProvider();
        Assert.True(result.Succeeded, result.ErrorMessage);

        var kinds = result.Events.Select(e => e.Kind).ToList();

        int Idx(string kind) => kinds.IndexOf(kind);

        Assert.True(Idx("execution.started") >= 0,    "execution.started missing");
        Assert.True(Idx("step.started")      >= 0,    "step.started missing");
        Assert.True(Idx("model.requested")   >= 0,    "model.requested missing");
        Assert.True(Idx("model.tool_requested") >= 0, "model.tool_requested missing");
        Assert.True(Idx("tool.authorized")   >= 0,    "tool.authorized missing");
        Assert.True(Idx("tool.started")      >= 0,    "tool.started missing");
        Assert.True(Idx("tool.completed")    >= 0,    "tool.completed missing");
        Assert.True(Idx("agent.output_validated") >= 0, "agent.output_validated missing");
        Assert.True(Idx("step.completed")    >= 0,    "step.completed missing");
        Assert.True(Idx("execution.succeeded") >= 0,  "execution.succeeded missing");

        Assert.True(Idx("execution.started")   < Idx("step.started"));
        Assert.True(Idx("step.started")        < Idx("model.requested"));
        Assert.True(Idx("model.requested")     < Idx("model.tool_requested"));
        Assert.True(Idx("model.tool_requested")< Idx("tool.authorized"));
        Assert.True(Idx("tool.authorized")     < Idx("tool.started"));
        Assert.True(Idx("tool.started")        < Idx("tool.completed"));
        Assert.True(Idx("tool.completed")      < Idx("agent.output_validated"));
        Assert.True(Idx("agent.output_validated") < Idx("step.completed"));
        Assert.True(Idx("step.completed")      < Idx("execution.succeeded"));
    }

    [Fact]
    public async Task OperationId_is_consistent_for_model_requested_and_tool_requested()
    {
        var result = await RunTokenEchoWithAdaptiveProvider();
        Assert.True(result.Succeeded, result.ErrorMessage);

        // Find events from the first model call (the one that requested the tool)
        var modelReq = result.Events.First(e => e.Kind == "model.requested");
        var toolReq  = result.Events.First(e => e.Kind == "model.tool_requested");

        Assert.NotNull(modelReq.OperationId);
        Assert.Equal(modelReq.OperationId, toolReq.OperationId);
    }

    [Fact]
    public async Task CallId_is_consistent_across_tool_events()
    {
        var result = await RunTokenEchoWithAdaptiveProvider();
        Assert.True(result.Succeeded, result.ErrorMessage);

        var toolAuth  = result.Events.First(e => e.Kind == "tool.authorized");
        var toolStart = result.Events.First(e => e.Kind == "tool.started");
        var toolDone  = result.Events.First(e => e.Kind == "tool.completed");

        Assert.NotNull(toolAuth.CallId);
        Assert.Equal(toolAuth.CallId, toolStart.CallId);
        Assert.Equal(toolAuth.CallId, toolDone.CallId);
    }

    [Fact]
    public async Task ExecutionId_is_consistent_across_all_events()
    {
        var result = await RunTokenEchoWithAdaptiveProvider();
        Assert.True(result.Succeeded, result.ErrorMessage);

        var ids = result.Events.Select(e => e.ExecutionId).Distinct().ToList();
        Assert.Single(ids);
        Assert.NotEmpty(ids[0]);
    }

    [Fact]
    public async Task Tool_completed_has_duration()
    {
        var result = await RunTokenEchoWithAdaptiveProvider();
        Assert.True(result.Succeeded, result.ErrorMessage);

        var toolDone = result.Events.First(e => e.Kind == "tool.completed");
        Assert.NotNull(toolDone.DurationMs);
        Assert.True(toolDone.DurationMs >= 0);
    }

    [Fact]
    public async Task Second_model_call_has_different_operation_id()
    {
        var result = await RunTokenEchoWithAdaptiveProvider();
        Assert.True(result.Succeeded, result.ErrorMessage);

        var modelOps = result.Events
            .Where(e => e.Kind == "model.requested")
            .Select(e => e.OperationId)
            .ToList();

        // Exactly two model calls: one that returned tool call, one that returned final text
        Assert.Equal(2, modelOps.Count);
        Assert.NotEqual(modelOps[0], modelOps[1]);
    }
}

// ── Test doubles (file-scoped) ────────────────────────────────────────────────

file sealed class CorrelationTokenTool : IToolImplementation
{
    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        return Task.FromResult(new MailSchema("GenerateTokenOutput",
            ImmutableDictionary<string, MailValue>.Empty
                .Add("token", new MailString(token))));
    }
}

