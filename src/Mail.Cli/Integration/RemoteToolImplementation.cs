using System.Collections.Immutable;
using System.Text.Json;
using Mail.Contracts;
using Mail.Runtime;

namespace Mail.Cli.Integration;

// Implements IToolImplementation by forwarding tool calls to the external client
// via the protocol. Authorization and argument validation happen in AgentRunner
// BEFORE ExecuteAsync is called. This adapter never retries on failure.
internal sealed class RemoteToolImplementation(
    string toolName,
    PendingCallTracker tracker,
    MessageWriter writer) : IToolImplementation
{
    // Set by IntegrationMode before each RunAsync call so all async continuations
    // within that execution context inherit the correct execution_id.
    internal static readonly AsyncLocal<string?> CurrentExecutionId = new();

    public async Task<MailSchema> ExecuteAsync(MailSchema input, CancellationToken ct)
    {
        var requestId   = Guid.NewGuid().ToString("N");
        var executionId = CurrentExecutionId.Value
            ?? throw new InvalidOperationException(
                "RemoteToolImplementation called outside an integration execution context.");

        var inputJson    = SchemaConverter.ToJson(input);
        var inputElement = JsonDocument.Parse(inputJson).RootElement.Clone();

        // Register the TCS BEFORE sending the message to avoid the race where the
        // client responds before the TCS is in the tracker.
        var responseTask = tracker.RegisterCall(requestId);
        writer.WriteMessage("tool_request",
            new ToolRequestMsg(executionId, requestId, toolName, inputElement));

        string outputJson;
        try
        {
            outputJson = await responseTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            tracker.FailCall(requestId, "Cancelled");
            throw;
        }
        // RemoteToolException (from tool_error) propagates directly — never retried.

        return ParseOutput(outputJson);
    }

    // Parses flat JSON into a MailSchema. AgentRunner validates the result against
    // the declared output contract immediately after this returns.
    private static MailSchema ParseOutput(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var builder   = ImmutableDictionary.CreateBuilder<string, MailValue>(StringComparer.Ordinal);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            builder[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => new MailString(prop.Value.GetString()!),
                JsonValueKind.True   => new MailBool(true),
                JsonValueKind.False  => new MailBool(false),
                JsonValueKind.Number => new MailInt(prop.Value.GetInt64()),
                // Nested objects/arrays: serialize back to string (rare in current type system)
                _                   => new MailString(prop.Value.GetRawText()),
            };
        }

        return new MailSchema("RemoteOutput", builder.ToImmutable());
    }
}
