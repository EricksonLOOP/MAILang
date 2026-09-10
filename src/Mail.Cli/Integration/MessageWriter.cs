using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Mail.Cli.Integration;

internal sealed class MessageWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Task _consumer;

    public MessageWriter()
    {
        _consumer = Task.Run(ConsumeAsync);
    }

    public void WriteMessage(string type, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, _opts)!.AsObject();
        node.Insert(0, "type", JsonValue.Create(type));
        _channel.Writer.TryWrite(node.ToJsonString(_opts));
    }

    // Called during shutdown after all executions have ended.
    public async Task CompleteAsync()
    {
        _channel.Writer.Complete();
        await _consumer.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var line in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }
    }
}
