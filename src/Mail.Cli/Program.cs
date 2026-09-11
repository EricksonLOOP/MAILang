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
    Console.Error.WriteLine("Usage: mail <validate|run|integrate> <file.mail> [--input <json>]");
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
string? inputJson = null;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--provider" && i + 1 < args.Length)
    {
        Console.Error.WriteLine("[MAIL-CONFIG-003] --provider flag is no longer supported. Declare providers in the .mail file.");
        return 1;
    }
    if (args[i] == "--input" && i + 1 < args.Length) inputJson = args[++i];
}

// ── Load config ──────────────────────────────────────────────────────────────
CliConfig config;
try { config = LoadConfig(); }
catch (MailConfigurationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var dotEnv = DotEnvLoader.Load();

// ── Compile ───────────────────────────────────────────────────────────────────
var absFilePath = Path.GetFullPath(filePath);
var (plan, diagnostics) = Mail.Compiler.Compiler.CompileFile(
    absFilePath, new Mail.Compiler.FilesystemSourceResolver());

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
var limits = ExecutionLimits.Validated(
    config.Limits.MaxToolCallsPerAgent,
    config.Limits.MaxModelCallsPerAgent,
    TimeSpan.FromSeconds(config.Limits.TimeoutSeconds));

using var providerHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
providerHttpClient.Timeout = Timeout.InfiniteTimeSpan;
try { RegisterProviders(providerRegistry, plan, providerHttpClient, dotEnv); }
catch (Exception ex) when (ex is ArgumentException or ProviderConfigurationException)
{
    Console.Error.WriteLine($"[MAIL-CONFIG-001] {ex.Message}");
    return 1;
}

// ── Execute ───────────────────────────────────────────────────────────────────
var executor = new WorkflowExecutor(plan, toolRegistry, providerRegistry, limits);
var result = await executor.RunAsync(input, CancellationToken.None);

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

    // Detect removed fields and reject at startup
    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.TryGetProperty("Provider", out _) ||
        doc.RootElement.TryGetProperty("ModelBindings", out _))
        throw new MailConfigurationException(
            "[MAIL-CONFIG-004] appsettings.json 'Provider' and 'ModelBindings' are no longer supported. " +
            "Declare providers in the .mail file instead.");

    return JsonSerializer.Deserialize<CliConfig>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    }) ?? new CliConfig();
}

static void RegisterProviders(ProviderRegistry registry, ValidatedPlan plan,
    HttpClient client, DotEnvLoader env)
{
    if (plan.Providers.Count > 0)
        ProviderRegistrar.RegisterDeclaredProviders(registry, plan, env, client);
}

static MailValue ParseInput(string? inputJson, ValidatedPlan plan)
{
    if (inputJson is null)
        return new MailSchema(InputTypeName(plan), ImmutableDictionary<string, MailValue>.Empty);

    using var doc = JsonDocument.Parse(inputJson);
    if (doc.RootElement.ValueKind != JsonValueKind.Object)
        throw new InvalidOperationException("--input must be a JSON object.");

    // Reject duplicate keys before parsing
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var prop in doc.RootElement.EnumerateObject())
        if (!seen.Add(prop.Name))
            throw new InvalidOperationException($"Duplicate property '{prop.Name}' in --input JSON.");

    return Mail.Runtime.DeclaredSchema.Parse(
        doc.RootElement, plan.Workflow.InputType, plan.Schemas, plan.Enums);
}

static string InputTypeName(ValidatedPlan plan) =>
    plan.Workflow.InputType is Mail.Compiler.Ast.NamedTypeRef nr ? nr.Name : "input";
