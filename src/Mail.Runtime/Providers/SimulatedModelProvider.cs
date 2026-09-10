using Mail.Contracts;

namespace Mail.Runtime.Providers;

/// <summary>
/// Replays a fixed script of responses for offline testing.
/// </summary>
public sealed class SimulatedModelProvider(IReadOnlyList<ModelResponse> script) : IModelProvider
{
    private int _callCount;

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_callCount >= script.Count)
            throw new InvalidProviderResponseException(
                $"SimulatedModelProvider ran out of scripted responses after {_callCount} calls.");

        return Task.FromResult(script[_callCount++]);
    }
}
