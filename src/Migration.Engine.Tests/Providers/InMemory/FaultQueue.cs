using Migration.Engine.Providers;

namespace Migration.Engine.Tests.Providers.InMemory;

/// <summary>
/// Deterministic fault injection for the in-memory adaptors. A test enqueues faults against a
/// named operation (e.g. "Write", "ReadContent"); each matching call dequeues and throws the next
/// one, so you can say "throttle the first two writes, then let it succeed" and drive the retry
/// logic exactly. When the queue for an operation is empty the call runs normally.
/// </summary>
public sealed class FaultQueue
{
    private readonly Dictionary<string, Queue<Func<Exception>>> _faults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Enqueue an arbitrary exception factory for <paramref name="times"/> consecutive calls.</summary>
    public FaultQueue Enqueue(string operation, Func<Exception> factory, int times = 1)
    {
        if (!_faults.TryGetValue(operation, out var queue))
        {
            queue = new Queue<Func<Exception>>();
            _faults[operation] = queue;
        }
        for (var i = 0; i < times; i++)
        {
            queue.Enqueue(factory);
        }
        return this;
    }

    /// <summary>Simulate a throttle (transient, optional Retry-After) on the next <paramref name="times"/> calls.</summary>
    public FaultQueue Throttle(string operation, int? retryAfterSeconds = null, int times = 1)
        => Enqueue(operation, () => TransferProviderException.Throttled($"Simulated throttle on {operation}", retryAfterSeconds, "InMemory"), times);

    /// <summary>Simulate a non-throttle transient blip (timeout / transient 5xx / I/O error).</summary>
    public FaultQueue Transient(string operation, string message = "Simulated transient error", int times = 1)
        => Enqueue(operation, () => TransferProviderException.Transient($"{message} on {operation}", "InMemory"), times);

    /// <summary>Simulate a permanent failure that should fail the item terminally.</summary>
    public FaultQueue Permanent(string operation, string message = "Simulated permanent error", int times = 1)
        => Enqueue(operation, () => TransferProviderException.Permanent($"{message} on {operation}", "InMemory"), times);

    /// <summary>Called by the adaptor at the start of each operation; throws the next queued fault if any.</summary>
    public void MaybeThrow(string operation)
    {
        if (_faults.TryGetValue(operation, out var queue) && queue.Count > 0)
        {
            throw queue.Dequeue().Invoke();
        }
    }

    /// <summary>Total faults still queued (for asserting a test consumed exactly what it set up).</summary>
    public int Remaining => _faults.Values.Sum(q => q.Count);
}
