using System.Collections.Concurrent;
namespace AeroScience.Engine;

public sealed class ScientificDownlinkSignals
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> active = new();
    public IDisposable Begin(Guid id, CancellationToken stop, out CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stop); active[id] = source; token = source.Token; return new Operation(() => { active.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(id, source)); source.Dispose(); });
    }
    public void Cancel(Guid id) { if (active.TryGetValue(id, out var source)) try { source.Cancel(); } catch (ObjectDisposedException) { } }
    private sealed class Operation(Action end) : IDisposable { private int done; public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) end(); } }
}
