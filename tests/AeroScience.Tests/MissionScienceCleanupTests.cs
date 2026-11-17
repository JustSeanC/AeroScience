using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class MissionScienceCleanupTests
{
    [Theory][InlineData(true)][InlineData(false)]
    public async Task ScientificLibraryCheckpointWaitsForBriefReaderAndKeepsPersistentSnapshotPending(bool releaseDuringCleanup)
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Finish(2);_=f.Build();
        var coordinator=new MissionCoordinator(f.Missions,1000);var preview=f.Science.PreviewCollectedDeletion(new([f.Id]),f.Missions);var execute=new SourceDeletionExecution(preview.Id,preview.SelectionHash);
        using var pin=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=f.Science.DatabasePath,Pooling=false}.ToString());pin.Open();
        using var tx=pin.BeginTransaction(deferred:true);using var read=pin.CreateCommand();read.Transaction=tx;read.CommandText="SELECT COUNT(*) FROM scientific_artifacts";Assert.True(Convert.ToInt64(read.ExecuteScalar())>0);
        using var recordsRemoved=new ManualResetEventSlim();
        f.Science.CollectedDeletionFault=stage=>{if(stage=="records-removed")recordsRemoved.Set();};
        var deleting=Task.Run(()=>f.Science.DeleteCollectedScience(execute,f.Missions,coordinator.EnterMaintenance));
        Assert.True(recordsRemoved.Wait(TimeSpan.FromSeconds(5)),"Deletion did not reach its authorized physical cleanup.");
        if(releaseDuringCleanup){await Task.Delay(150);tx.Rollback();}
        var result=await deleting.WaitAsync(TimeSpan.FromSeconds(15));
        if(releaseDuringCleanup)Assert.True(result.State=="Completed",result.Warning);
        else
        {
            Assert.Equal("Cleanup pending",result.State);Assert.True(result.DatabaseCleanupPending);Assert.Null(result.PendingFiles);Assert.Contains("Deletion checkpoint is busy",result.Warning);
            Assert.True(Convert.ToInt64(read.ExecuteScalar())>0); // The pinned old snapshot is not represented as erased.
            tx.Rollback();
            Assert.Equal("Completed",f.Science.DeleteCollectedScience(execute,f.Missions,coordinator.EnterMaintenance).State);
        }
        Assert.Equal(2,f.Missions.StreamSamples(f.Id).Count());Assert.True(f.Missions.ScienceDeleted(f.Id));Assert.Empty(f.Science.Artifacts(f.Id));Assert.Empty(f.Science.StreamObservations(f.Id));
    }
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
    [Theory][InlineData("transient")][InlineData("persistent")][InlineData("changed")]
    public async Task CollectedDeletionAcquiresRealExclusiveLeaseAndRevalidatesAfterWaiting(string scenario)
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Finish(2);_=f.Build();
        var coordinator=new MissionCoordinator(f.Missions,1000);var preview=f.Science.PreviewCollectedDeletion(new([f.Id]),f.Missions);var execute=new SourceDeletionExecution(preview.Id,preview.SelectionHash);
        using var analysis=f.Science.BeginAnalysis();var deleting=Task.Run(()=>f.Science.DeleteCollectedScience(execute,f.Missions,coordinator.EnterMaintenance));
        if(scenario!="persistent")
        {
            await Task.Delay(150);
            if(scenario=="changed")_=f.Science.EnsureDataset(f.Missions.Detail(f.Id)!.Mission,f.Settings[0],f.Missions.Research(f.Id));
            analysis.Dispose();
        }
        if(scenario=="transient")Assert.Equal("Completed",(await deleting.WaitAsync(TimeSpan.FromSeconds(15))).State);
        else
        {
            var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>deleting.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains(scenario=="changed"?"changed":"active scientific",error.Message);Assert.False(f.Missions.ScienceDeleted(f.Id));Assert.NotEmpty(f.Science.Artifacts(f.Id));
            analysis.Dispose();
            if(scenario=="persistent")Assert.Equal("Completed",f.Science.DeleteCollectedScience(execute,f.Missions,coordinator.EnterMaintenance).State);
            else Assert.NotEmpty(f.Science.Datasets(f.Id));
        }
        Assert.Equal(2,f.Missions.StreamSamples(f.Id).Count());
    }
}
