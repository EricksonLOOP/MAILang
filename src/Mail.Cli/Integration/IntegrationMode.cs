using System.Collections.Immutable;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Mail.Cli;
using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;

namespace Mail.Cli.Integration;

internal static class IntegrationMode
{
    private const int ProtocolVersion = 1;
    private const int MaxLineBytes    = 4 * 1024 * 1024; // 4 MB

    public static async Task RunAsync(CancellationToken outerCt)
    {
        Console.InputEncoding  = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        // Snapshot OS env + .env once per process; shared across all executions.
        var dotEnv = DotEnvLoader.Load();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };

        await using var writer  = new MessageWriter();
        var tracker  = new PendingCallTracker();
        var registry = new ExecutionRegistry();
        bool handshakeDone = false;

        while (true)
        {
            string? line;
            try
            {
                line = await ReadLineLimitedAsync(Console.In, MaxLineBytes, outerCt)
                    .ConfigureAwait(false);
            }
            catch (ProtocolLineLimitException ex)
            {
                writer.WriteMessage("protocol_error",
                    new ProtocolErrorMsg(ex.Message, true));
                break;
            }
            catch (OperationCanceledException) { break; }

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement root;
            try { root = JsonDocument.Parse(line).RootElement; }
            catch (JsonException)
            {
                writer.WriteMessage("protocol_error",
                    new ProtocolErrorMsg("Invalid JSON.", !handshakeDone));
                if (!handshakeDone) break;
                continue;
            }

            if (!root.TryGetProperty("type", out var typeEl) ||
                typeEl.ValueKind != JsonValueKind.String)
            {
                writer.WriteMessage("protocol_error",
                    new ProtocolErrorMsg("Missing or non-string 'type' field.", !handshakeDone));
                if (!handshakeDone) break;
                continue;
            }

            var msgType = typeEl.GetString()!;

            if (!handshakeDone)
            {
                if (msgType == "handshake")
                    HandleHandshake(root, writer, ref handshakeDone);
                else
                    writer.WriteMessage("protocol_error",
                        new ProtocolErrorMsg(
                            $"Expected 'handshake' as first message, got '{msgType}'.", true));
                if (!handshakeDone) break;
                continue;
            }

            switch (msgType)
            {
                case "handshake":
                    writer.WriteMessage("protocol_error",
                        new ProtocolErrorMsg("Handshake already completed.", true));
                    break;

                case "load":
                    if (!root.TryGetProperty("load_id", out var lidEl) ||
                        !root.TryGetProperty("path",    out var lpEl))
                    {
                        writer.WriteMessage("protocol_error",
                            new ProtocolErrorMsg("'load' requires 'load_id' and 'path'.", false));
                        break;
                    }
                    await HandleLoadAsync(lidEl.GetString()!, lpEl.GetString()!, writer)
                        .ConfigureAwait(false);
                    break;

                case "run":
                    HandleRun(root, writer, tracker, registry, dotEnv, http, outerCt);
                    break;

                case "tool_response":
                    HandleToolResponse(root, tracker);
                    break;

                case "tool_error":
                    HandleToolError(root, tracker);
                    break;

                case "cancel":
                    if (root.TryGetProperty("execution_id", out var cExEl))
                        registry.Cancel(cExEl.GetString()!);
                    break;

                default:
                    writer.WriteMessage("protocol_error",
                        new ProtocolErrorMsg($"Unknown message type '{msgType}'.", false));
                    break;
            }
        }

        // Ordered shutdown:
        await registry.CancelAllAndWaitAsync().ConfigureAwait(false);
        tracker.CancelAll();
        await writer.CompleteAsync().ConfigureAwait(false);
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private static void HandleHandshake(JsonElement root, MessageWriter writer, ref bool done)
    {
        int version = root.TryGetProperty("protocol_version", out var vEl) ? vEl.GetInt32() : 0;
        if (version != ProtocolVersion)
        {
            writer.WriteMessage("handshake_error",
                new HandshakeErrorMsg(
                    $"version {version} not supported; supported: [{ProtocolVersion}]"));
            return;
        }
        done = true;
        writer.WriteMessage("handshake_ok", new HandshakeOkMsg(ProtocolVersion));
    }

    private static async Task HandleLoadAsync(string loadId, string path, MessageWriter writer)
    {
        try
        {
            var absPath = Path.GetFullPath(path);
            var (plan, diagnostics) = Mail.Compiler.Compiler.CompileFile(
                absPath, new Mail.Compiler.FilesystemSourceResolver());

            if (plan is null || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                writer.WriteMessage("load_error", new LoadErrorMsg(loadId,
                    diagnostics.Select(d =>
                        new DiagnosticDto(d.Severity.ToString(), null, d.ToString())).ToArray()));
                return;
            }

            var contracts = plan.Tools.Values
                .Select(t => new ToolContractDto(t.Name, DeclToDto(t.Input, plan), DeclToDto(t.Output, plan)))
                .ToArray();

            writer.WriteMessage("loaded", new LoadedMsg(loadId, contracts));
        }
        catch (Exception ex)
        {
            writer.WriteMessage("load_error", new LoadErrorMsg(loadId,
                [new DiagnosticDto("Error", null, ex.Message)]));
        }
    }

    private static void HandleRun(
        JsonElement root,
        MessageWriter writer,
        PendingCallTracker tracker,
        ExecutionRegistry registry,
        DotEnvLoader dotEnv,
        HttpClient http,
        CancellationToken outerCt)
    {
        if (!root.TryGetProperty("execution_id", out var exEl) ||
            !root.TryGetProperty("path",         out var pathEl) ||
            !root.TryGetProperty("input",         out var inputEl))
        {
            var partialId = root.TryGetProperty("execution_id", out var pid)
                ? pid.GetString()! : "(unknown)";
            writer.WriteMessage("run_error",
                new RunErrorMsg(partialId, "'run' requires 'execution_id', 'path', and 'input'."));
            return;
        }

        var execId = exEl.GetString()!;
        var path   = pathEl.GetString()!;

        JsonElement? cfgEl = root.TryGetProperty("provider_config", out var cv) ? cv : null;

        // Register BEFORE Task.Run so a concurrent 'cancel' can fire the CTS
        // even if the task has not started yet.
        if (!registry.TryRegister(execId, out var cts))
        {
            writer.WriteMessage("run_error",
                new RunErrorMsg(execId, $"Duplicate execution_id '{execId}'."));
            return;
        }

        // Do NOT use 'using' here: the linked CTS must outlive HandleRun.
        // It is disposed inside the background task's finally block.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, outerCt);

        // Clone input element before handing off to the background task.
        var inputClone = inputEl.Clone();

        var task = Task.Run(async () =>
        {
            try
            {
                var absPath      = Path.GetFullPath(path);
                var (plan, diag) = Mail.Compiler.Compiler.CompileFile(
                    absPath, new Mail.Compiler.FilesystemSourceResolver());

                if (plan is null || diag.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    writer.WriteMessage("run_error", new RunErrorMsg(execId,
                        string.Join("; ", diag.Select(d => d.ToString()))));
                    return;
                }

                // All declared tools become remote implementations.
                var toolReg = new ToolRegistry();
                foreach (var (toolName, toolDecl) in plan.Tools)
                {
                    toolReg.Register(toolName,
                        new RemoteToolImplementation(toolName, tracker, writer),
                        FieldContractBuilder.FromDecls(toolDecl.Input,  plan),
                        FieldContractBuilder.FromDecls(toolDecl.Output, plan));
                }

                // MAIL-PROTO-001: reject run.provider when plan declares providers
                if (plan.Providers.Count > 0 && root.TryGetProperty("provider", out _))
                {
                    writer.WriteMessage("run_error", new RunErrorMsg(execId,
                        "[MAIL-PROTO-001] The 'provider' field is not accepted for plans that declare providers in .mail. " +
                        "Remove 'provider' from the run message."));
                    return;
                }

                // Resolve per-provider scripts from provider_config
                IReadOnlyList<ScriptStep>? flatScript = null;
                Dictionary<string, IReadOnlyList<ScriptStep>>? scriptsByProvider = null;

                if (cfgEl is { } cfg)
                {
                    if (cfg.TryGetProperty("providers", out var providersEl))
                    {
                        scriptsByProvider = new Dictionary<string, IReadOnlyList<ScriptStep>>(StringComparer.Ordinal);
                        foreach (var prop in providersEl.EnumerateObject())
                        {
                            // Validate ALL names, not only those that carry a script.
                            if (!plan.Providers.ContainsKey(prop.Name))
                            {
                                writer.WriteMessage("run_error", new RunErrorMsg(execId,
                                    $"[MAIL-PROTO-003] provider_config.providers.{prop.Name} is not declared in the .mail file."));
                                return;
                            }
                            if (prop.Value.TryGetProperty("script", out var s))
                                scriptsByProvider[prop.Name] = ScriptParser.Parse(s);
                        }
                    }
                    else if (cfg.TryGetProperty("script", out var scriptEl))
                    {
                        // Legacy flat format — valid only when plan has exactly one simulated provider
                        var simCount = plan.Providers.Values.Count(p => p.IsSimulated);
                        if (simCount != 1)
                        {
                            writer.WriteMessage("run_error", new RunErrorMsg(execId,
                                $"[MAIL-PROTO-002] Legacy 'provider_config.script' is ambiguous: plan has {simCount} simulated providers. " +
                                $"Use 'provider_config.providers.{{ProviderName}}.script'."));
                            return;
                        }
                        flatScript = ScriptParser.Parse(scriptEl);
                    }
                }

                var providerReg = new ProviderRegistry();

                // Simulated providers use AdaptiveIntegrationProvider (stateful per execution);
                // real providers use the shared DotEnvLoader snapshot and HttpClient.
                Func<string, IModelProvider> simulatedFactory = name =>
                {
                    IReadOnlyList<ScriptStep>? providerScript = null;
                    if (scriptsByProvider?.TryGetValue(name, out var s) == true)
                        providerScript = s;
                    else if (flatScript is not null)
                        providerScript = flatScript;
                    return new AdaptiveIntegrationProvider(providerScript);
                };

                try
                {
                    ProviderRegistrar.RegisterDeclaredProviders(providerReg, plan, dotEnv, http, simulatedFactory);
                }
                catch (ProviderConfigurationException ex)
                {
                    writer.WriteMessage("run_error", new RunErrorMsg(execId, ex.Message));
                    return;
                }

                var limits = ParseLimits(root);

                writer.WriteMessage("run_started", new RunStartedMsg(execId));

                // AsyncLocal: all RemoteToolImplementation.ExecuteAsync calls in this
                // task's async context will read the correct execution_id.
                RemoteToolImplementation.CurrentExecutionId.Value = execId;

                var executor = new WorkflowExecutor(plan, toolReg, providerReg, limits);
                var result   = await executor
                    .RunAsync(ParseInputElement(inputClone, plan), linked.Token, execId)
                    .ConfigureAwait(false);

                // Emit all events (batch — streaming is a v1 limitation).
                foreach (var ev in result.Events)
                    writer.WriteMessage("event", new EventMsg(execId, new EventDto(
                        ev.Kind, ev.Message,
                        ev.Timestamp.ToString("O"),
                        ev.OperationId, ev.CallId, ev.DurationMs, ev.ParentOperationId, ev.ActivationId)));

                if (result.Succeeded && result.Output is not null)
                {
                    var outJson = SchemaConverter.ToJson(result.Output);
                    var outEl   = JsonDocument.Parse(outJson).RootElement.Clone();
                    writer.WriteMessage("result", new ResultMsg(execId, outEl));
                }
                else
                {
                    writer.WriteMessage("run_error",
                        new RunErrorMsg(execId, result.ErrorMessage ?? "Execution failed."));
                }
            }
            catch (OperationCanceledException)
            {
                writer.WriteMessage("run_error", new RunErrorMsg(execId, "Cancelled"));
            }
            catch (Exception ex)
            {
                writer.WriteMessage("run_error", new RunErrorMsg(execId, ex.Message));
            }
            finally
            {
                registry.Remove(execId);
                linked.Dispose();
            }
        }, outerCt);

        registry.SetTask(execId, task);
    }

    private static void HandleToolResponse(JsonElement root, PendingCallTracker tracker)
    {
        if (!root.TryGetProperty("request_id", out var ridEl) ||
            !root.TryGetProperty("output",     out var outEl))
        {
            Console.Error.WriteLine("[integrate] Warning: tool_response missing 'request_id' or 'output'.");
            return;
        }
        if (!tracker.CompleteCall(ridEl.GetString()!, outEl.GetRawText()))
            Console.Error.WriteLine(
                $"[integrate] Warning: tool_response for unknown request_id '{ridEl.GetString()}'.");
    }

    private static void HandleToolError(JsonElement root, PendingCallTracker tracker)
    {
        if (!root.TryGetProperty("request_id", out var ridEl) ||
            !root.TryGetProperty("error",       out var errEl))
        {
            Console.Error.WriteLine("[integrate] Warning: tool_error missing 'request_id' or 'error'.");
            return;
        }
        if (!tracker.FailCall(ridEl.GetString()!, errEl.GetString()!))
            Console.Error.WriteLine(
                $"[integrate] Warning: tool_error for unknown request_id '{ridEl.GetString()}'.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FieldContractDto[] DeclToDto(
        IReadOnlyList<Mail.Compiler.Ast.FieldDecl> fields, ValidatedPlan plan) =>
        FieldContractBuilder.FromDecls(fields, plan)
            .Select(fc => new FieldContractDto(
                fc.Name,
                fc.Kind.ToString(),
                fc.Required,
                Nullable:          fc.Nullable ? true : null,
                EnumSymbols:       fc.EnumSymbols?.ToArray(),
                ElementKind:       fc.ElementKind?.ToString(),
                ElementType:       fc.ElementTypeName,
                ElementEnumSymbols: fc.ElementEnumSymbols?.ToArray()))
            .ToArray();

    private static MailValue ParseInputElement(JsonElement el, ValidatedPlan plan)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return new MailSchema(InputTypeName(plan), ImmutableDictionary<string, MailValue>.Empty);

        // Reject duplicate keys
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
            if (!seen.Add(prop.Name))
                throw new InvalidOperationException($"Duplicate property '{prop.Name}' in input.");

        return Mail.Runtime.DeclaredSchema.Parse(el, plan.Workflow.InputType, plan.Schemas, plan.Enums);
    }

    private static string InputTypeName(ValidatedPlan plan) =>
        plan.Workflow.InputType is Mail.Compiler.Ast.NamedTypeRef nr ? nr.Name : "input";

    private static ExecutionLimits ParseLimits(JsonElement root)
    {
        const int DefaultToolCalls  = 20;
        const int DefaultModelCalls = 10;
        const int DefaultTimeoutSec = 300; // 5 minutes

        int maxToolCalls  = DefaultToolCalls;
        int maxModelCalls = DefaultModelCalls;
        int timeoutSec    = DefaultTimeoutSec;

        if (root.TryGetProperty("limits", out var lim))
        {
            if (lim.TryGetProperty("max_tool_calls_per_agent",  out var tc)  && tc.ValueKind == JsonValueKind.Number)
                maxToolCalls  = Math.Clamp(tc.GetInt32(), 1, 1000);
            if (lim.TryGetProperty("max_model_calls_per_agent", out var mc)  && mc.ValueKind == JsonValueKind.Number)
                maxModelCalls = Math.Clamp(mc.GetInt32(), 1, 1000);
            if (lim.TryGetProperty("timeout_seconds",           out var ts)  && ts.ValueKind == JsonValueKind.Number)
                timeoutSec    = Math.Clamp(ts.GetInt32(), 1, 3600);
        }

        return ExecutionLimits.Validated(maxToolCalls, maxModelCalls, TimeSpan.FromSeconds(timeoutSec));
    }

    // Reads one line, enforcing a byte limit to prevent unbounded memory use.
    // Throws ProtocolLineLimitException if exceeded. Returns null on EOF.
    private static async Task<string?> ReadLineLimitedAsync(
        TextReader reader, int maxBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null) return null;
        if (Encoding.UTF8.GetByteCount(line) > maxBytes)
            throw new ProtocolLineLimitException(
                $"Message exceeds {maxBytes / 1024 / 1024} MB line limit.");
        return line;
    }
}

internal sealed class ProtocolLineLimitException(string message) : Exception(message);
