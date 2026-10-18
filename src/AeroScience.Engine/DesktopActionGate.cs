namespace AeroScience.Engine;

/// <summary>Claims a native action before it is queued to the desktop thread.</summary>
public sealed class DesktopActionGate
{
    private int entered;
    public IDisposable? TryEnter() => Interlocked.CompareExchange(ref entered, 1, 0) == 0 ? new Lease(this) : null;
    private sealed class Lease(DesktopActionGate owner) : IDisposable
    {
        private DesktopActionGate? current = owner;
        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref current, null);
            if (previous is not null) Interlocked.Exchange(ref previous.entered, 0);
        }
    }
}
