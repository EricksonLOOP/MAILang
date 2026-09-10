using System.Collections.Immutable;
using System.Text.Json;
using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;
using Mail.Cli;

if (args.Length >= 1 && args[0].ToLowerInvariant() == "integrate")
{
    await Mail.Cli.Integration.IntegrationMode.RunAsync(CancellationToken.None);
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: mail <validate|run|integrate> <file.mail> [--provider <name>] [--input <json>]");
    return 2;
}

var command  = args[0].ToLowerInvariant();
var filePath = args[1];

if (!File.Exists(filePath))
{
    Console.Error.WriteLine($"File not found: {filePath}");
    return 2;
}

// ── Parse CLI args ────────────────────────────────────────────────────────────
string? providerOverride = null;
string? inputJson = null;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--provider" && i + 1 < args.Length) providerOverride = args[++i];
    else if (args[i] == "--input" && i + 1 < args.Length) inputJson = args[++i];
}

// ── Load config ──────────────────────────────────────────────────────────────
var config = LoadConfig();
var providerName = providerOverride ?? config.Provider;

// ── Compile ───────────────────────────────────────────────────────────────────
var source = await File.ReadAllTextAsync(filePath);
var (plan, diagnostics) = Mail.Compiler.Compiler.Compile(source, filePath);

foreach (var d in diagnostics)
    Console.Error.WriteLine(d.ToString());

if (command == "validate")
{
    if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        return 1;
    Console.WriteLine("No errors found.");
    return 0;
}

if (command != "run")
{
    Console.Error.WriteLine($"Unknown command '{command}'. Use 'validate' or 'run'.");
    return 2;
}

if (plan is null)
{
    Console.Error.WriteLine("Compilation failed. Cannot run.");
    return 1;
}

// ── Parse input ───────────────────────────────────────────────────────────────
var input = ParseInput(inputJson, plan);

// ── Build registries ──────────────────────────────────────────────────────────
var toolRegistry     = ToolRegistryFactory.Build(plan, out var tokenTool);
var providerRegistry = new ProviderRegistry();
var modelBindings    = BuildModelBindings(config, providerName, plan);
var limits = ExecutionLimits.Validated(
    config.Limits.MaxToolCallsPerAgent,
    config.Limits.MaxModelCallsPerAgent,
    TimeSpan.FromSeconds(config.Limits.TimeoutSeconds));

using var providerHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
providerHttpClient.Timeout = Timeout.InfiniteTimeSpan;
try { RegisterProviders(providerRegistry, providerName, plan, toolRegistry, input, providerHttpClient); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"[MAIL-CONFIG-001] {ex.Message}");
    return 1;
}

// ── Execute ───────────────────────────────────────────────────────────────────
var executor = new WorkflowExecutor(plan, toolRegistry, providerRegistry, modelBindings, limits);
var result = await executor.RunAsync(input, providerName, CancellationToken.None);

foreach (var ev in result.Events)
{
    var sb = new System.Text.StringBuilder(
        $"[{ev.Timestamp:HH:mm:ss.fff}] [{ev.ExecutionId[..8]}] {ev.Kind}");
    if (ev.OperationId is not null) sb.Append($" op:{ev.OperationId[..8]}");
    if (ev.CallId is not null)      sb.Append($" call:{ev.CallId}");
    if (ev.DurationMs is not null)  sb.Append($" ({ev.DurationMs}ms)");
    sb.Append($": {ev.Message}");
    Console.Error.WriteLine(sb.ToString());
}

if (!result.Succeeded)
{
    Console.Error.WriteLine($"[MAIL-EXEC-001] {result.ErrorMessage}");
    return 1;
}

// ── Token cycle verification ───────────────────────────────────────────────────
if (tokenTool is not null)
{
    if (tokenTool.LastToken is null)
    {
        Console.Error.WriteLine("[MAIL-VERIFY-001] GenerateToken was never dispatched — tool cycle not proven.");
        return 1;
    }

    var outSchema = result.Output as MailSchema;
    var outToken  = outSchema?.Fields.GetValueOrDefault("token") is MailString ms ? ms.Value : null;

    if (outToken is null || outToken != tokenTool.LastToken)
    {
        Console.Error.WriteLine(
            $"[MAIL-VERIFY-002] Token mismatch: generated={tokenTool.LastToken[..8]}…, output={outToken ?? "(missing)"}.");
        return 1;
    }

    Console.Error.WriteLine($"[MAIL-VERIFY-OK] Token verified: {tokenTool.LastToken[..8]}… matches agent output.");
}

Console.WriteLine(SchemaConverter.ToJson(result.Output!));
return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────

static CliConfig LoadConfig()
{
    const string configPath = "appsettings.json";
    if (!File.Exists(configPath)) return new CliConfig();
    var json = File.ReadAllText(configPath);
    return JsonSerializer.Deserialize<CliConfig>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    }) ?? new CliConfig();
}

static ModelBindings BuildModelBindings(CliConfig config, string providerName, ValidatedPlan plan)
{
    var bindings = new ModelBindings();
    foreach (var logicalName in plan.Agents.Values.Select(a => a.LogicalModelName).Distinct())
    {
        if (providerName == "simulated") bindings.Register(logicalName, providerName, "simulated");
        else if (config.ModelBindings.TryGetValue(logicalName, out var configured) && configured.Provider == providerName)
            bindings.Register(logicalName, providerName, configured.ModelId);
        else if (providerName == "deepseek")
            bindings.Register(logicalName, providerName, Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-flash");
    }
    return bindings;
}

static void RegisterProviders(ProviderRegistry registry, string providerName, ValidatedPlan plan,
    ToolRegistry tools, MailValue input, HttpClient client)
{
    if (providerName == "simulated")
    {
        // When the plan includes GenerateToken, use an adaptive provider that propagates
        // the actual generated token from the tool result into the final response.
        IModelProvider provider = plan.Tools.ContainsKey("GenerateToken")
            ? new CliTokenEchoSimulator()
            : new SimulatedModelProvider(BuildSimulatedScript(plan, tools, input));

        registry.Register(providerName, provider);
    }
    else if (providerName == "deepseek")
        registry.Register(providerName, new DeepSeekModelProvider(client, Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? ""));
    else
        throw new ArgumentException($"Provider '{providerName}' is not registered. Use simulated or deepseek.");
}
static List<ModelResponse> BuildSimulatedScript(ValidatedPlan plan, ToolRegistry tools, MailValue input)
{
    // Find the first agent step and its first allowed tool to build a realistic script.
    var script = new List<ModelResponse>();

    foreach (var step in plan.Workflow.Steps)
    {
        if (step.Body is not Mail.Compiler.Ast.AgentBody ab) continue;
        if (!plan.Agents.TryGetValue(ab.AgentName, out var agent)) continue;
        if (agent.AllowedTools.Count == 0) continue;

        var firstTool = agent.AllowedTools[0];
        if (!plan.Tools.TryGetValue(firstTool, out var toolDecl)) continue;

        // Build args from input schema fields that match tool input fields
        var args = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        if (input is MailSchema inputSchema)
        {
            foreach (var field in toolDecl.Input)
            {
                if (inputSchema.Fields.TryGetValue(field.Name, out var val))
                {
                    var json = SchemaConverter.ToJson(val);
                    args[field.Name] = JsonDocument.Parse(json).RootElement.Clone();
                }
            }
        }

        // First response: call the first tool
        script.Add(new ModelResponse(
            ToolCalls: [new ToolCallRequest("sim-call-1", firstTool, args.ToImmutable())],
            Text: null));

        // Second response: return a JSON that satisfies the agent's output type
        if (plan.Schemas.TryGetValue(
            agent.OutputType is Mail.Compiler.Ast.NamedTypeRef nr ? nr.Name : "", out var outputSchema))
        {
            var outputFields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var f in outputSchema.Fields)
            {
                outputFields[f.Name] = f.Type switch
                {
                    Mail.Compiler.Ast.PrimitiveTypeRef { Kind: Mail.Compiler.Ast.PrimitiveKind.String } =>
                        input is MailSchema s && s.Fields.TryGetValue(f.Name, out var sv)
                            ? ((MailString)sv).Value
                            : "simulated-value",
                    Mail.Compiler.Ast.PrimitiveTypeRef { Kind: Mail.Compiler.Ast.PrimitiveKind.Bool } => true,
                    Mail.Compiler.Ast.PrimitiveTypeRef { Kind: Mail.Compiler.Ast.PrimitiveKind.Int }  => 0,
                    _ => "simulated-value",
                };
            }
            script.Add(new ModelResponse(null, JsonSerializer.Serialize(outputFields)));
        }
        else
        {
            script.Add(new ModelResponse(null, "{\"result\":\"simulated\"}"));
        }

        break; // Only handle the first agent step for the simulated script
    }

    if (script.Count == 0)
        script.Add(new ModelResponse(null, "{\"result\":\"simulated\"}"));

    return script;
}

static MailValue ParseInput(string? inputJson, ValidatedPlan plan)
{
    if (inputJson is null)
        return new MailSchema("input", ImmutableDictionary<string, MailValue>.Empty);

    using var doc = JsonDocument.Parse(inputJson);
    if (doc.RootElement.ValueKind != JsonValueKind.Object)
        throw new InvalidOperationException("--input must be a JSON object.");

    // Check for duplicate properties
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var prop in doc.RootElement.EnumerateObject())
        if (!seen.Add(prop.Name))
            throw new InvalidOperationException($"Duplicate property '{prop.Name}' in --input JSON.");

    var builder = ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
    foreach (var prop in doc.RootElement.EnumerateObject())
    {
        MailValue val = prop.Value.ValueKind switch
        {
            JsonValueKind.String => new MailString(prop.Value.GetString()!),
            JsonValueKind.True   => new MailBool(true),
            JsonValueKind.False  => new MailBool(false),
            JsonValueKind.Number => new MailInt(prop.Value.GetInt64()),
            _ => new MailString(prop.Value.GetRawText()),
        };
        builder[prop.Name] = val;
    }

    var inputTypeName = plan.Workflow.InputType is Mail.Compiler.Ast.NamedTypeRef nr ? nr.Name : "input";
    return new MailSchema(inputTypeName, builder.ToImmutable());
}

// ── CliTokenEchoSimulator ─────────────────────────────────────────────────────
// Simulates the model for token-echo.mail: first call requests GenerateToken,
// second call reads the actual token from the tool result and echoes it back.

file sealed class CliTokenEchoSimulator : IModelProvider
{
    private int _callCount;

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _callCount++;

        if (_callCount == 1)
        {
            return Task.FromResult(new ModelResponse(
                ToolCalls:
                [
                    new ToolCallRequest(
                        "cli-token-1",
                        "GenerateToken",
                        ImmutableDictionary<string, JsonElement>.Empty.Add(
                            "prompt", JsonDocument.Parse("\"test\"").RootElement.Clone()))
                ],
                Text: null));
        }

        var toolResult = request.Messages
            .OfType<ToolResultMessage>()
            .First(m => m.ToolName == "GenerateToken");

        using var doc = JsonDocument.Parse(toolResult.ResultJson);
        var token = doc.RootElement.GetProperty("token").GetString()!;

        return Task.FromResult(new ModelResponse(
            ToolCalls: null,
            Text: JsonSerializer.Serialize(new { token })));
    }
}

