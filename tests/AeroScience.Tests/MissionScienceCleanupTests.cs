using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class MissionScienceCleanupTests
{
    [Theory][InlineData(true)][InlineData(false)]
    public async Task PhysicalCleanupWaitsForTransientReaderButKeepsPersistentReaderPending(bool releaseDuringCleanup)
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Finish(2);_=f.Build();
        var coordinator=new MissionCoordinator(f.Missions,1000);var preview=f.Science.PreviewCollectedDeletion(new([f.Id]),f.Missions);var execute=new SourceDeletionExecution(preview.Id,preview.SelectionHash);
        using var pin=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=f.Missions.DatabasePath,Pooling=false}.ToString());pin.Open();using var tx=pin.BeginTransaction(deferred:true);using var read=pin.CreateCommand();read.Transaction=tx;read.CommandText="SELECT COUNT(*) FROM telemetry";_=read.ExecuteScalar();
        // Force an older WAL snapshot, as a concurrent background archive reader can
        // hold during physical deletion. Recording/source authority is not mocked.
        f.Missions.AddEvent(f.Id,f.Utc,"diagnostic","SYNTHETIC WAL reader concurrency fixture");
        var deleting=Task.Run(()=>f.Science.DeleteCollectedScience(execute,f.Missions,coordinator.EnterMaintenance));
        if(releaseDuringCleanup){await Task.Delay(150);tx.Rollback();}
        var result=await deleting.WaitAsync(TimeSpan.FromSeconds(15));
        if(releaseDuringCleanup)Assert.True(result.State=="Completed",result.Warning);
        else
        {
            Assert.Equal("Cleanup pending",result.State);Assert.True(result.DatabaseCleanupPending);Assert.Contains("busy",result.Warning);tx.Rollback();
            var retry=f.Science.DeleteCollectedScience(execute,f.Missions,coordinator.EnterMaintenance);Assert.Equal("Completed",retry.State);
        }
        Assert.Equal(2,f.Missions.StreamSamples(f.Id).Count());Assert.True(f.Missions.ScienceDeleted(f.Id));Assert.Empty(f.Science.Artifacts(f.Id));Assert.Empty(f.Science.StreamObservations(f.Id));
    }
}
