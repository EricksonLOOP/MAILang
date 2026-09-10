using System.Collections.Immutable;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
                    HandleRun(root, writer, tracker, registry, outerCt);
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
                .Select(t => new ToolContractDto(t.Name, DeclToDto(t.Input), DeclToDto(t.Output)))
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

        var execId   = exEl.GetString()!;
        var path     = pathEl.GetString()!;
        var provider = root.TryGetProperty("provider", out var pv)
            ? pv.GetString() ?? "simulated" : "simulated";

        if (provider != "simulated" && provider != "deepseek")
        {
            writer.WriteMessage("run_error",
                new RunErrorMsg(execId,
                    $"Provider '{provider}' not supported in integration mode; use 'simulated' or 'deepseek'."));
            return;
        }

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
                        ToolRegistryFactory.DeclToContracts(toolDecl.Input),
                        ToolRegistryFactory.DeclToContracts(toolDecl.Output));
                }

                var providerReg = new ProviderRegistry();
                var bindings    = new ModelBindings();

                if (provider == "deepseek")
                {
                    string? apiKey  = null;
                    string? modelId = null;
                    if (cfgEl is { } cfg)
                    {
                        if (cfg.TryGetProperty("api_key", out var keyEl))  apiKey  = keyEl.GetString();
                        if (cfg.TryGetProperty("model",   out var modEl))  modelId = modEl.GetString();
                    }
                    apiKey  ??= Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
                    modelId ??= "deepseek-chat";
                    providerReg.Register("deepseek", new DeepSeekModelProvider(new HttpClient(), apiKey));
                    foreach (var logicalName in plan.Agents.Values.Select(a => a.LogicalModelName).Distinct())
                        bindings.Register(logicalName, "deepseek", modelId);
                }
                else
                {
                    IReadOnlyList<ScriptStep>? script = null;
                    if (cfgEl is { } cfg && cfg.TryGetProperty("script", out var scriptEl))
                        script = ScriptParser.Parse(scriptEl);
                    providerReg.Register("simulated", new AdaptiveIntegrationProvider(script));
                    foreach (var logicalName in plan.Agents.Values.Select(a => a.LogicalModelName).Distinct())
                        bindings.Register(logicalName, "simulated", "simulated");
                }

                var limits = ExecutionLimits.Validated(
                    maxToolCalls:  20,
                    maxModelCalls: 10,
                    timeout: TimeSpan.FromMinutes(5));

                writer.WriteMessage("run_started", new RunStartedMsg(execId));

                // AsyncLocal: all RemoteToolImplementation.ExecuteAsync calls in this
                // task's async context will read the correct execution_id.
                RemoteToolImplementation.CurrentExecutionId.Value = execId;

                var executor = new WorkflowExecutor(plan, toolReg, providerReg, bindings, limits);
                var result   = await executor
                    .RunAsync(ParseInputElement(inputClone, plan), provider, linked.Token, execId)
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
        IReadOnlyList<Mail.Compiler.Ast.FieldDecl> fields) =>
        ToolRegistryFactory.DeclToContracts(fields)
            .Select(fc => new FieldContractDto(fc.Name, fc.Kind.ToString(), fc.Required))
            .ToArray();

    private static MailValue ParseInputElement(JsonElement el, ValidatedPlan plan)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return new MailSchema("input", ImmutableDictionary<string, MailValue>.Empty);

        var typeName = plan.Workflow.InputType is Mail.Compiler.Ast.NamedTypeRef nr
            ? nr.Name : "input";

        var builder = ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);
        var seen    = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prop in el.EnumerateObject())
        {
            if (!seen.Add(prop.Name)) continue;
            builder[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => new MailString(prop.Value.GetString()!),
                JsonValueKind.True   => new MailBool(true),
                JsonValueKind.False  => new MailBool(false),
                JsonValueKind.Number => new MailInt(prop.Value.GetInt64()),
                _                   => new MailString(prop.Value.GetRawText()),
            };
        }

        return new MailSchema(typeName, builder.ToImmutable());
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
