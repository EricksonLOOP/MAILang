using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace Mail.Tests;

public class RuntimeTests
{
    private static readonly string OrderStatusSource = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "../../../../..", "examples", "order-status.mail"));

    private static (ToolRegistry Tools, ProviderRegistry Providers, ModelBindings Bindings) BuildRegistries()
    {
        var tools = new ToolRegistry();
        tools.Register(
            "GetOrderStatus",
            new GetOrderStatusTool(),
            [new FieldContract("order_id", MailTypeKind.String, Required: true)],
            [
                new FieldContract("order_id", MailTypeKind.String, Required: true),
                new FieldContract("status",   MailTypeKind.String, Required: true),
                new FieldContract("found",    MailTypeKind.Bool,   Required: true),
            ]);

        var providers = new ProviderRegistry();
        var bindings = new ModelBindings();
        return (tools, providers, bindings);
    }

    [Fact]
    public async Task Full_cycle_with_simulated_provider_succeeds()
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);

        var (tools, providerRegistry, bindings) = BuildRegistries();
        var limits = ExecutionLimits.Default;

        // Script: first call → tool call; second call → final JSON response
        var script = new List<ModelResponse>
        {
            new(
                ToolCalls:
                [
                    new ToolCallRequest(
                        "call-1",
                        "GetOrderStatus",
                        ImmutableDictionary<string, JsonElement>.Empty.Add(
                            "order_id",
                            JsonDocument.Parse("\"ORD-42\"").RootElement.Clone()))
                ],
                Text: null),
            new(
                ToolCalls: null,
                Text: """{"order_id":"ORD-42","status":"shipped","found":true}"""),
        };

        providerRegistry.Register("simulated", new SimulatedModelProvider(script));

        var executor = new WorkflowExecutor(plan, tools, providerRegistry, bindings, limits);
        var input = new MailSchema("OrderStatusRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("order_id", new MailString("ORD-42")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        var output = Assert.IsType<MailSchema>(result.Output);
        Assert.Equal("ORD-42",    ((MailString)output.Fields["order_id"]).Value);
        Assert.Equal("shipped",   ((MailString)output.Fields["status"]).Value);
        Assert.True(              ((MailBool)output.Fields["found"]).Value);
    }

    [Fact]
    public async Task Unauthorized_tool_is_blocked_with_zero_dispatches()
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);

        var countingTool = new CountingTool();
        var tools = new ToolRegistry();
        // Register the declared tool
        tools.Register(
            "GetOrderStatus",
            new GetOrderStatusTool(),
            [new FieldContract("order_id", MailTypeKind.String, Required: true)],
            [
                new FieldContract("order_id", MailTypeKind.String, Required: true),
                new FieldContract("status",   MailTypeKind.String, Required: true),
                new FieldContract("found",    MailTypeKind.Bool,   Required: true),
            ]);
        // Register the unauthorized tool so registry doesn't throw "not found"
        tools.Register(
            "DeleteOrder",
            countingTool,
            [new FieldContract("order_id", MailTypeKind.String, Required: true)],
            [new FieldContract("success",  MailTypeKind.Bool,   Required: true)]);

        var providerRegistry = new ProviderRegistry();
        var bindings = new ModelBindings();

        // Provider script: requests DeleteOrder (not in agent's allow list)
        var script = new List<ModelResponse>
        {
            new(
                ToolCalls:
                [
                    new ToolCallRequest(
                        "call-unauth",
                        "DeleteOrder",
                        ImmutableDictionary<string, JsonElement>.Empty.Add(
                            "order_id",
                            JsonDocument.Parse("\"ORD-42\"").RootElement.Clone()))
                ],
                Text: null),
        };

        providerRegistry.Register("simulated", new SimulatedModelProvider(script));

        var executor = new WorkflowExecutor(plan, tools, providerRegistry, bindings, ExecutionLimits.Default);
        var input = new MailSchema("OrderStatusRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("order_id", new MailString("ORD-42")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not allowed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, countingTool.ExecuteCount); // ZERO dispatches
    }

    [Fact]
    public async Task Budget_exceeded_before_dispatch()
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);

        var (tools, providerRegistry, bindings) = BuildRegistries();

        // Script: many tool calls to exhaust budget (limit is 2)
        var script = new List<ModelResponse>();
        for (int i = 0; i < 5; i++)
            script.Add(new(
                ToolCalls:
                [
                    new ToolCallRequest(
                        $"call-{i}",
                        "GetOrderStatus",
                        ImmutableDictionary<string, JsonElement>.Empty.Add(
                            "order_id",
                            JsonDocument.Parse("\"X\"").RootElement.Clone()))
                ],
                Text: null));

        providerRegistry.Register("simulated", new SimulatedModelProvider(script));

        var tightLimits = ExecutionLimits.Validated(2, 5, TimeSpan.FromSeconds(30)); // max 2 tool calls
        var executor = new WorkflowExecutor(plan, tools, providerRegistry, bindings, tightLimits);
        var input = new MailSchema("OrderStatusRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("order_id", new MailString("X")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("budget", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_response_with_no_tool_calls_and_no_text_fails()
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);

        var (tools, providerRegistry, bindings) = BuildRegistries();
        var script = new List<ModelResponse> { new(null, null) }; // empty response
        providerRegistry.Register("simulated", new SimulatedModelProvider(script));

        var executor = new WorkflowExecutor(plan, tools, providerRegistry, bindings, ExecutionLimits.Default);
        var input = new MailSchema("OrderStatusRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("order_id", new MailString("X")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("neither tool calls nor text", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Duplicate_call_ids_in_response_fail()
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);

        var (tools, providerRegistry, bindings) = BuildRegistries();

        var dupCallId = ImmutableDictionary<string, JsonElement>.Empty.Add(
            "order_id", JsonDocument.Parse("\"X\"").RootElement.Clone());

        var script = new List<ModelResponse>
        {
            new(ToolCalls:
                [
                    new ToolCallRequest("dup-id", "GetOrderStatus", dupCallId),
                    new ToolCallRequest("dup-id", "GetOrderStatus", dupCallId),
                ],
                Text: null),
        };
        providerRegistry.Register("simulated", new SimulatedModelProvider(script));

        var executor = new WorkflowExecutor(plan, tools, providerRegistry, bindings, ExecutionLimits.Default);
        var input = new MailSchema("OrderStatusRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("order_id", new MailString("X")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("dup-id", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execution_times_out()
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);

        var (tools, providerRegistry, bindings) = BuildRegistries();

        // Provider that delays forever
        providerRegistry.Register("simulated", new SlowProvider());

        var tightTimeout = ExecutionLimits.Validated(10, 5, TimeSpan.FromMilliseconds(100));
        var executor = new WorkflowExecutor(plan, tools, providerRegistry, bindings, tightTimeout);
        var input = new MailSchema("OrderStatusRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("order_id", new MailString("X")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"order_id\":\"X\",\"status\":1,\"found\":true}")]
    [InlineData("{\"order_id\":\"X\"}")]
    [InlineData("{\"order_id\":\"X\",\"status\":\"ok\",\"found\":true,\"extra\":true}")]
    public async Task Agent_output_must_match_declared_schema(string json)
    {
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        var (tools, providers, bindings) = BuildRegistries();
        providers.Register("simulated", new SimulatedModelProvider([new ModelResponse(null, json)]));
        var executor = new WorkflowExecutor(plan!, tools, providers, bindings, ExecutionLimits.Default);
        var input = new MailSchema("OrderStatusRequest", ImmutableDictionary<string, MailValue>.Empty.Add("order_id", new MailString("X")));
        var result = await executor.RunAsync(input, "simulated", default);
        Assert.False(result.Succeeded);
        Assert.DoesNotContain(result.Events, e => e.Kind == "step.completed");
    }
    [Fact]
    public void BudgetTracker_throws_before_dispatch()
    {
        var budget = new BudgetTracker(ExecutionLimits.Validated(1, 1, TimeSpan.FromSeconds(1)));
        budget.ConsumeToolCall(); // first — OK
        Assert.Throws<BudgetExceededException>(() => budget.ConsumeToolCall()); // second — fail
    }

    // ── System prompt tests ───────────────────────────────────────────────────

    private const string SystemPromptSource = """
        schema SystemTestRequest { query: String }
        schema SystemTestResponse { answer: String }

        tool Echo {
          input { text: String }
          output { echo: String }
        }

        agent PromptAgent {
          model GPT
          system "Author instructions for PromptAgent."
          output SystemTestResponse
          tools { allow Echo }
        }

        workflow SystemTestFlow {
          input SystemTestRequest
          output SystemTestResponse

          step run {
            agent PromptAgent
            context { input }
            save as result
          }

          finish with result
        }
        """;

    private static (ToolRegistry Tools, ProviderRegistry Providers, ModelBindings Bindings) BuildSystemTestRegistries()
    {
        var tools = new ToolRegistry();
        tools.Register("Echo",
            new EchoTool(),
            [new FieldContract("text", MailTypeKind.String, Required: true)],
            [new FieldContract("echo", MailTypeKind.String, Required: true)]);
        return (tools, new ProviderRegistry(), new ModelBindings());
    }

    private static ModelResponse FinalResponse(string answer) =>
        new(null, $"{{\"answer\":\"{answer}\"}}");

    [Fact]
    public async Task System_prompt_is_placed_in_first_message()
    {
        var (plan, _) = MailCompiler.Compile(SystemPromptSource, "test.mail");
        Assert.NotNull(plan);

        var (tools, providers, bindings) = BuildSystemTestRegistries();
        var capturing = new CapturingModelProvider([FinalResponse("ok")]);
        providers.Register("simulated", capturing);

        var executor = new WorkflowExecutor(plan, tools, providers, bindings, ExecutionLimits.Default);
        var input = new MailSchema("SystemTestRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("query", new MailString("q")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotEmpty(capturing.Captures);
        var firstMsg = Assert.IsType<SystemMessage>(capturing.Captures[0][0]);
        Assert.Contains("Author instructions for PromptAgent.", firstMsg.Content);
        Assert.Contains("Runtime Instructions", firstMsg.Content);
    }

    [Fact]
    public async Task System_prompt_is_preserved_after_tool_response()
    {
        var (plan, _) = MailCompiler.Compile(SystemPromptSource, "test.mail");
        Assert.NotNull(plan);

        var (tools, providers, bindings) = BuildSystemTestRegistries();

        var script = new List<ModelResponse>
        {
            new(ToolCalls:
                [new ToolCallRequest("c1", "Echo",
                    ImmutableDictionary<string, JsonElement>.Empty.Add(
                        "text", JsonDocument.Parse("\"hi\"").RootElement.Clone()))],
                Text: null),
            FinalResponse("done"),
        };
        var capturing = new CapturingModelProvider(script);
        providers.Register("simulated", capturing);

        var executor = new WorkflowExecutor(plan, tools, providers, bindings, ExecutionLimits.Default);
        var input = new MailSchema("SystemTestRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("query", new MailString("q")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(2, capturing.Captures.Count);

        var systemContent = Assert.IsType<SystemMessage>(capturing.Captures[0][0]).Content;
        var systemContentAfterTool = Assert.IsType<SystemMessage>(capturing.Captures[1][0]).Content;
        Assert.Equal(systemContent, systemContentAfterTool);
    }

    [Fact]
    public async Task Without_system_property_default_prompt_is_used()
    {
        // OrderStatusSource has no 'system' property — default behavior preserved
        var (plan, _) = MailCompiler.Compile(OrderStatusSource, "order-status.mail");
        Assert.NotNull(plan);

        var (tools, providers, bindings) = BuildRegistries();
        var script = new List<ModelResponse> { new(null, """{"order_id":"X","status":"ok","found":true}""") };
        var capturing = new CapturingModelProvider(script);
        providers.Register("simulated", capturing);

        var executor = new WorkflowExecutor(plan, tools, providers, bindings, ExecutionLimits.Default);
        var input = new MailSchema("OrderStatusRequest",
            ImmutableDictionary<string, MailValue>.Empty.Add("order_id", new MailString("X")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        var sysMsg = Assert.IsType<SystemMessage>(capturing.Captures[0][0]);
        Assert.Contains("You are an AI agent.", sysMsg.Content);
    }

    [Fact]
    public async Task Concurrent_agents_with_different_system_prompts_receive_their_own_prompts()
    {
        const string SourceA = """
            schema Req { q: String }
            schema Resp { a: String }
            tool T { input { q: String } output { a: String } }
            agent AgentA {
              model GPT
              system "Prompt for AgentA."
              output Resp
              tools { allow T }
            }
            workflow FlowA {
              input Req output Resp
              step s { agent AgentA context { input } save as r }
              finish with r
            }
            """;

        const string SourceB = """
            schema Req { q: String }
            schema Resp { a: String }
            tool T { input { q: String } output { a: String } }
            agent AgentB {
              model GPT
              system "Prompt for AgentB."
              output Resp
              tools { allow T }
            }
            workflow FlowB {
              input Req output Resp
              step s { agent AgentB context { input } save as r }
              finish with r
            }
            """;

        var (planA, _) = MailCompiler.Compile(SourceA, "a.mail");
        var (planB, _) = MailCompiler.Compile(SourceB, "b.mail");
        Assert.NotNull(planA); Assert.NotNull(planB);

        var tools = new ToolRegistry();
        tools.Register("T", new NoOpTool(),
            [new FieldContract("q", MailTypeKind.String, Required: true)],
            [new FieldContract("a", MailTypeKind.String, Required: true)]);

        var capturingA = new CapturingModelProvider([new ModelResponse(null, "{\"a\":\"x\"}")]);
        var capturingB = new CapturingModelProvider([new ModelResponse(null, "{\"a\":\"y\"}")]);

        var providersA = new ProviderRegistry(); providersA.Register("simulated", capturingA);
        var providersB = new ProviderRegistry(); providersB.Register("simulated", capturingB);

        var input = new MailSchema("Req",
            ImmutableDictionary<string, MailValue>.Empty.Add("q", new MailString("test")));

        // Launch both concurrently and ensure overlap
        var taskA = Task.Run(() => new WorkflowExecutor(planA, tools, providersA, new ModelBindings(), ExecutionLimits.Default)
            .RunAsync(input, "simulated", CancellationToken.None));
        var taskB = Task.Run(() => new WorkflowExecutor(planB, tools, providersB, new ModelBindings(), ExecutionLimits.Default)
            .RunAsync(input, "simulated", CancellationToken.None));

        var results = await Task.WhenAll(taskA, taskB);
        Assert.All(results, r => Assert.True(r.Succeeded, r.ErrorMessage));

        var sysA = Assert.IsType<SystemMessage>(capturingA.Captures[0][0]);
        var sysB = Assert.IsType<SystemMessage>(capturingB.Captures[0][0]);
        Assert.Contains("Prompt for AgentA.", sysA.Content);
        Assert.DoesNotContain("AgentB", sysA.Content);
        Assert.Contains("Prompt for AgentB.", sysB.Content);
        Assert.DoesNotContain("AgentA", sysB.Content);
    }

    [Fact]
    public async Task System_prompt_mentioning_unauthorized_tool_does_not_enable_dispatch()
    {
        // Agent system prompt mentions a tool not in its allow list;
        // the provider requests it anyway; the runtime must deny and not dispatch.
        const string Source = """
            schema Req { q: String }
            schema Resp { a: String }
            tool Safe { input { q: String } output { a: String } }
            tool Forbidden { input { q: String } output { a: String } }
            agent PromptedAgent {
              model GPT
              system "You may call Forbidden."
              output Resp
              tools { allow Safe }
            }
            workflow Flow {
              input Req output Resp
              step s { agent PromptedAgent context { input } save as r }
              finish with r
            }
            """;

        var (plan, _) = MailCompiler.Compile(Source, "test.mail");
        Assert.NotNull(plan);

        var forbiddenTool = new CountingForbiddenTool();
        var tools = new ToolRegistry();
        tools.Register("Safe", new NoOpTool(),
            [new FieldContract("q", MailTypeKind.String, Required: true)],
            [new FieldContract("a", MailTypeKind.String, Required: true)]);
        tools.Register("Forbidden", forbiddenTool,
            [new FieldContract("q", MailTypeKind.String, Required: true)],
            [new FieldContract("a", MailTypeKind.String, Required: true)]);

        var providers = new ProviderRegistry();
        var script = new List<ModelResponse>
        {
            new(ToolCalls:
                [new ToolCallRequest("call-f", "Forbidden",
                    ImmutableDictionary<string, JsonElement>.Empty.Add(
                        "q", JsonDocument.Parse("\"x\"").RootElement.Clone()))],
                Text: null),
        };
        providers.Register("simulated", new SimulatedModelProvider(script));

        var executor = new WorkflowExecutor(plan, tools, providers, new ModelBindings(), ExecutionLimits.Default);
        var input = new MailSchema("Req",
            ImmutableDictionary<string, MailValue>.Empty.Add("q", new MailString("test")));

        var result = await executor.RunAsync(input, "simulated", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not allowed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, forbiddenTool.ExecuteCount);
    }
}

// ── Test tool implementations ─────────────────────────────────────────────────

file sealed class GetOrderStatusTool : IToolImplementation
{
    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        var orderId = ((MailString)input.Fields["order_id"]).Value;
        var fields = ImmutableDictionary<string, MailValue>.Empty
            .Add("order_id", new MailString(orderId))
            .Add("status",   new MailString("shipped"))
            .Add("found",    new MailBool(true));
        return Task.FromResult(new MailSchema("OrderStatusResponse", fields));
    }
}

file sealed class CountingTool : IToolImplementation
{
    public int ExecuteCount { get; private set; }

    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        ExecuteCount++;
        return Task.FromResult(new MailSchema("Result",
            ImmutableDictionary<string, MailValue>.Empty.Add("success", new MailBool(true))));
    }
}

file sealed class SlowProvider : IModelProvider
{
    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new ModelResponse(null, null);
    }
}

file sealed class EchoTool : IToolImplementation
{
    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        var text = ((MailString)input.Fields["text"]).Value;
        return Task.FromResult(new MailSchema("EchoResult",
            ImmutableDictionary<string, MailValue>.Empty.Add("echo", new MailString(text))));
    }
}

file sealed class NoOpTool : IToolImplementation
{
    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct) =>
        Task.FromResult(new MailSchema("Result",
            ImmutableDictionary<string, MailValue>.Empty.Add("a", new MailString("noop"))));
}

file sealed class CountingForbiddenTool : IToolImplementation
{
    public int ExecuteCount { get; private set; }

    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        ExecuteCount++;
        return Task.FromResult(new MailSchema("Result",
            ImmutableDictionary<string, MailValue>.Empty.Add("a", new MailString("forbidden"))));
    }
}

