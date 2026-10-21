using AeroScience.Missions;
using Xunit;
namespace AeroScience.Tests;
public sealed class ScientificTransferProgressTests
{
    [Fact]public void KnownMultipleFileEstimateUsesUniquePreparedBytesAndRecentMeasuredRate()
    {
        var now=DateTimeOffset.UtcNow;var meter=new ScientificTransferProgress(()=>now);meter.Begin(30*1048576,3);meter.Stage("Downloading");meter.File(0,10*1048576,10*1048576); // verified resumed original prefix, zero network
        Assert.Null(meter.Snapshot("Downloading").RemainingSecondsHigh);
        for(int i=1;i<=4;i++){now=now.AddSeconds(1);meter.Network(i*1048576);meter.File(1,i*1048576,10*1048576);}
        var p=meter.Snapshot("Downloading");Assert.Equal(4*1048576,p.MeasuredNetworkBytes);Assert.Equal(14*1048576,p.PreparedOriginalBytes);Assert.Equal(1,p.CompletedFiles);Assert.Equal(3,p.TotalFiles);Assert.Equal(1048576d,p.BytesPerSecond);Assert.InRange(p.RemainingSecondsLow!.Value,11.9,12.1);Assert.InRange(p.RemainingSecondsHigh!.Value,23.9,24.1);
        now=now.AddSeconds(9);p=meter.Snapshot("Downloading");Assert.Null(p.BytesPerSecond);Assert.Null(p.RemainingSecondsHigh);Assert.Equal("Waiting for Network",p.Stage);
        meter.Stage("Verifying");Assert.Null(meter.Snapshot("Verifying").RemainingSecondsHigh);meter.Stage("Importing");Assert.Equal("Importing",meter.Snapshot("Verifying").Stage);Assert.Equal("Ready",meter.Snapshot("Completed").Stage);
    }
    [Fact]public void UnknownTotalRetriesPausesRestartsAndQueuedWorkNeverGetFalseCompletionOrCountdown()
    {
        var now=DateTimeOffset.UtcNow;var meter=new ScientificTransferProgress(()=>now);meter.Begin(null);meter.Stage("Downloading");for(int i=1;i<5;i++){now=now.AddSeconds(1);meter.Network(i*1048576);}Assert.NotNull(meter.Snapshot("Downloading").BytesPerSecond);Assert.Null(meter.Snapshot("Downloading").RemainingSecondsLow);Assert.Null(meter.Snapshot("Downloading").PreparedOriginalBytes);
        Assert.Null(meter.Snapshot("Paused").BytesPerSecond);Assert.Null(meter.Snapshot("Queued").RemainingSecondsHigh);meter.Begin(20*1048576,2);meter.Stage("Downloading");meter.File(0,1048576,10*1048576);now=now.AddSeconds(1);meter.Network(10*1048576);meter.File(0,1048576,10*1048576);Assert.Equal(1048576,meter.Snapshot("Downloading").PreparedOriginalBytes);Assert.Equal(0,meter.Snapshot("Downloading").CompletedFiles);
        var restarted=new ScientificTransferProgress(()=>now);restarted.Begin(20*1048576);Assert.Null(restarted.Snapshot("Downloading").BytesPerSecond);
    }
    [Fact]public void VariableSpeedIsSmoothedAndIndependentJobsHaveIndependentRateEvidence()
    {
        var now=DateTimeOffset.UtcNow;var a=new ScientificTransferProgress(()=>now);var b=new ScientificTransferProgress(()=>now);a.Begin(100*1048576);b.Begin(100*1048576);a.Stage("Downloading");b.Stage("Downloading");for(int i=1;i<=4;i++){now=now.AddSeconds(1);a.Network(i*1048576);b.Network(i*2097152);}
        now=now.AddSeconds(1);a.Network(14*1048576);Assert.InRange(a.Snapshot("Downloading").BytesPerSecond!.Value,1048577,10*1048576-1);Assert.Equal(2097152d,b.Snapshot("Downloading").BytesPerSecond);Assert.Null(a.Snapshot("Failed").BytesPerSecond);
    }
}
