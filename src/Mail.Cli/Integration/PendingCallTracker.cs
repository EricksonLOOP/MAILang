using System.Collections.Concurrent;

namespace Mail.Cli.Integration;

internal sealed class PendingCallTracker
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    // Must be called BEFORE sending tool_request to avoid a race where the client
    // responds before the TCS is registered.
    public Task<string> RegisterCall(string requestId)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        return tcs.Task;
    }

    public bool CompleteCall(string requestId, string outputJson)
    {
        return _pending.TryRemove(requestId, out var tcs) && tcs.TrySetResult(outputJson);
    }

    public bool FailCall(string requestId, string error)
    {
        return _pending.TryRemove(requestId, out var tcs)
            && tcs.TrySetException(new RemoteToolException(error));
    }

    // Unblocks all pending awaits; called when stdin closes.
    public void CancelAll()
    {
        foreach (var (key, tcs) in _pending.ToArray())
        {
            _pending.TryRemove(key, out _);
            tcs.TrySetCanceled();
        }
    }
}

internal sealed class RemoteToolException(string message) : Exception(message);
