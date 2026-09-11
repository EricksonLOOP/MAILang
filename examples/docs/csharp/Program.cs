using System.Collections.Immutable;
using Mail.Compiler;
using Mail.Contracts;
using Mail.Runtime;
using Mail.Runtime.Providers;

var path = Path.GetFullPath(args.Length > 0 ? args[0] : "examples/docs/tools.mail");
var (plan, diagnostics) = Compiler.CompileFile(path, new FilesystemSourceResolver());
foreach (var diagnostic in diagnostics) Console.Error.WriteLine(diagnostic);
if (plan is null) return 1;

var tools = new ToolRegistry();
FieldContract[] fields = [new("text", MailTypeKind.String, Required: true)];
tools.Register("Echo", new EchoTool(), fields, fields);

var providers = new ProviderRegistry();
// "Sim" matches the provider name declared in agent.mail; tools.mail has no agents, so unused here.
// Hosts register providers directly — Mail.Cli.ProviderRegistrar is not required for embedding.
providers.Register("Sim", new FixedProvider());

var executor = new WorkflowExecutor(plan, tools, providers, ExecutionLimits.Default);
var input = new MailSchema("Request", ImmutableDictionary<string, MailValue>.Empty
    .Add("text", new MailString("hello")));
var result = await executor.RunAsync(input, CancellationToken.None);
if (!result.Succeeded) { Console.Error.WriteLine(result.ErrorMessage); return 1; }
Console.WriteLine(SchemaConverter.ToJson(result.Output!));
return 0;

sealed class EchoTool : IToolImplementation
{
    public Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new MailSchema("Echo_output", input.Fields));
    }
}

// Used when this example is run against agent.mail; no external model call.
// Registered under the name "Sim" to match the provider declared in agent.mail.
sealed class FixedProvider : IModelProvider
{
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ModelResponse(null, "{\"text\":\"hello\"}"));
    }
}
