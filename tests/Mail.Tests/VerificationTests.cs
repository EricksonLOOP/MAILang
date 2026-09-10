using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace Mail.Tests;

public class VerificationTests
{
    private static readonly string TokenEchoSource = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "../../../../..", "examples", "token-echo.mail"));

    private static readonly string OrderStatusSource = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "../../../../..", "examples", "order-status.mail"));

    private static (ToolRegistry Tools, ProviderRegistry Providers, ModelBindings Bindings)
        BuildTokenRegistries(IToolImplementation tool)
    {
        var tools = new ToolRegistry();
        tools.Register(
            "GenerateToken",
            tool,
            [new FieldContract("prompt", MailTypeKind.String, Required: true)],
            [new FieldContract("token",  MailTypeKind.String, Required: true)]);

        return (tools, new ProviderRegistry(), new ModelBindings());
    }

    [Fact]
    public async Task Tool_result_propagates_to_agent_output()
    {
        var (plan, _) = MailCompiler.Compile(TokenEchoSource, "token-echo.mail");
        Assert.NotNull(plan);

        var tokenTool = new TokenEchoTool();
        var (tools, providers, bindings) = BuildTokenRegistries(tokenTool);
        providers.Register("simulated", new AdaptiveTokenProvider());

        var executor = new WorkflowExecutor(plan, tools, providers, bindings, ExecutionLimits.Default);
        var input = new MailSchema("EchoRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("prompt", new MailString("verify-cycle")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);

        // Prove tool was dispatched
        Assert.NotNull(tokenTool.LastToken);

        // The critical check: token in agent output must equal the one generated during dispatch
        var output = Assert.IsType<MailSchema>(result.Output);
        var outputToken = ((MailString)output.Fields["token"]).Value;
        Assert.Equal(tokenTool.LastToken, outputToken);
    }

    [Fact]
    public async Task Verification_fails_when_tool_not_dispatched()
    {
        // Provider fabricates a token without calling the tool
        var (plan, _) = MailCompiler.Compile(TokenEchoSource, "token-echo.mail");
        Assert.NotNull(plan);

        var tokenTool = new TokenEchoTool();
        var (tools, providers, bindings) = BuildTokenRegistries(tokenTool);
        providers.Register("simulated", new SimulatedModelProvider([
            new ModelResponse(null, """{"token":"fabricated-token-not-from-tool"}""")
        ]));

        var executor = new WorkflowExecutor(plan, tools, providers, bindings, ExecutionLimits.Default);
        var input = new MailSchema("EchoRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("prompt", new MailString("test")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        // Structurally valid — execution succeeds
        Assert.True(result.Succeeded, result.ErrorMessage);

        // But the tool was never dispatched
        Assert.Null(tokenTool.LastToken);

        // Verification detects the broken cycle: output token ≠ generated token
        var output = Assert.IsType<MailSchema>(result.Output);
        var outputToken = ((MailString)output.Fields["token"]).Value;
        Assert.NotEqual(tokenTool.LastToken, outputToken);
    }

    [Fact]
    public async Task Denied_tool_emits_event_and_blocks_dispatch()
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);

        var countingTool = new CountingTool2();
        var tools = new ToolRegistry();
        tools.Register(
            "GetOrderStatus",
            new EchoOrderStatusTool(),
            [new FieldContract("order_id", MailTypeKind.String, Required: true)],
            [
                new FieldContract("order_id", MailTypeKind.String, Required: true),
                new FieldContract("status",   MailTypeKind.String, Required: true),
                new FieldContract("found",    MailTypeKind.Bool,   Required: true),
            ]);
        tools.Register(
            "DeleteOrder",
            countingTool,
            [new FieldContract("order_id", MailTypeKind.String, Required: true)],
            [new FieldContract("success",  MailTypeKind.Bool,   Required: true)]);

        var providers = new ProviderRegistry();
        var bindings  = new ModelBindings();
        providers.Register("simulated", new SimulatedModelProvider([
            new ModelResponse(
                ToolCalls:
                [
                    new ToolCallRequest(
                        "call-unauth",
                        "DeleteOrder",
                        ImmutableDictionary<string, JsonElement>.Empty.Add(
                            "order_id", JsonDocument.Parse("\"ORD-99\"").RootElement.Clone()))
                ],
                Text: null)
        ]));

        var executor = new WorkflowExecutor(plan, tools, providers, bindings, ExecutionLimits.Default);
        var input = new MailSchema("OrderStatusRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("order_id", new MailString("ORD-99")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Events, e => e.Kind == "tool.denied");
        Assert.DoesNotContain(result.Events, e => e.Kind == "tool.started");
        Assert.DoesNotContain(result.Events, e => e.Kind == "tool.completed");
        Assert.Equal(0, countingTool.ExecuteCount);
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

file sealed class TokenEchoTool : IToolImplementation
{
    public string? LastToken { get; private set; }

    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        LastToken = token;
        return Task.FromResult(new MailSchema("GenerateTokenOutput",
            ImmutableDictionary<string, MailValue>.Empty
                .Add("token", new MailString(token))));
    }
}


file sealed class EchoOrderStatusTool : IToolImplementation
{
    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        var orderId = ((MailString)input.Fields["order_id"]).Value;
        return Task.FromResult(new MailSchema("OrderStatusResponse",
            ImmutableDictionary<string, MailValue>.Empty
                .Add("order_id", new MailString(orderId))
                .Add("status",   new MailString("ok"))
                .Add("found",    new MailBool(true))));
    }
}

file sealed class CountingTool2 : IToolImplementation
{
    public int ExecuteCount { get; private set; }

    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        ExecuteCount++;
        return Task.FromResult(new MailSchema("DeleteOrderOutput",
            ImmutableDictionary<string, MailValue>.Empty.Add("success", new MailBool(true))));
    }
}
