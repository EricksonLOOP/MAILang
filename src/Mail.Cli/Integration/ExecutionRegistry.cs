using System.Collections.Concurrent;

namespace Mail.Cli.Integration;

internal sealed class ExecutionRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed record Entry(CancellationTokenSource Cts, Task? ExecutionTask);

    // Returns false if the execution_id is already active (duplicate run message).
    public bool TryRegister(string executionId, out CancellationTokenSource cts)
    {
        cts = new CancellationTokenSource();
        if (_entries.TryAdd(executionId, new Entry(cts, null)))
            return true;

        cts.Dispose();
        cts = null!;
        return false;
    }

    // Associates the background Task with the entry so CancelAllAndWaitAsync can await it.
    public void SetTask(string executionId, Task task)
    {
        if (_entries.TryGetValue(executionId, out var entry))
            _entries[executionId] = entry with { ExecutionTask = task };
    }

    public bool Cancel(string executionId)
    {
        if (_entries.TryGetValue(executionId, out var entry))
        {
            entry.Cts.Cancel();
            return true;
        }
        return false;
    }

    public void Remove(string executionId)
    {
        if (_entries.TryRemove(executionId, out var entry))
            entry.Cts.Dispose();
    }

    // Cancels all live executions and waits for all background tasks to finish.
    public async Task CancelAllAndWaitAsync()
    {
        var entries = _entries.Values.ToArray();
        foreach (var entry in entries)
            entry.Cts.Cancel();

        var tasks = entries
            .Select(e => e.ExecutionTask)
            .Where(t => t is not null)
            .Cast<Task>()
            .ToArray();

        if (tasks.Length > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
