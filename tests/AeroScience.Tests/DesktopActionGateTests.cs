using Xunit;
using AeroScience.Engine;
namespace AeroScience.Tests;
public class DesktopActionGateTests
{
    [Fact]
    public void ConcurrentRequestsHoldOneClaimBeforeDispatchAndReleaseAfterFailure()
    {
        var gate = new DesktopActionGate();
        var leases = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
        Parallel.For(0, 100, _ => { if(gate.TryEnter() is {} claim) leases.Add(claim); });
        var only = Assert.Single(leases);
        only.Dispose(); only.Dispose();
        Assert.Throws<InvalidOperationException>((Action)(() => { using var claim = gate.TryEnter(); Assert.NotNull(claim); throw new InvalidOperationException(); }));
        using var next = gate.TryEnter(); Assert.NotNull(next); Assert.Null(gate.TryEnter());
    }
}
