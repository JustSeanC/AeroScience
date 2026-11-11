using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

// Uses the real append-only session/configuration/ingress commit contracts, then
// closes and reopens the archive. All evidence is explicitly synthetic fixture data.
internal sealed class TerrainSurveyFixture : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(),"terrain-survey-"+Guid.NewGuid().ToString("N"));
    public readonly MissionStore Missions;
    public readonly ScienceStore Science;
    public readonly Guid Id;
    public readonly ScienceCollectionController Controller;
    public InstrumentConfiguration[] Settings = [new("terrain-scanner",true,1000,new(InstrumentProfiles.Generic("terrain-scanner")))];
    public readonly DateTimeOffset Utc = new(2026,1,1,12,0,0,TimeSpan.Zero);
    int slot;
    public TerrainSurveyFixture(bool legacy = false,IStorageCapacity? capacity = null)
    {
        var paths=new StorageManager(new PreferenceStore(Root)).Initialize();
        Missions=new(Path.Combine(paths.MissionDirectory,"missions.db"));Missions.Initialize();
        Science=new(paths.ScienceLibraryDirectory,capacity);Science.Initialize();Missions.ScienceLibrary=Science;
        Id=Missions.Create("SYNTHETIC archived corridor fixture",null,Utc, instruments:Settings);
        if(!legacy)Missions.InitializeCollectionEvidence(new(1,Id,0,CollectionPhase.Armed,CollectionAuthority.Manual,ControlRevision:0));
        Controller=new(Missions,Id,()=>Settings);
    }
    public CollectionCommitResult Control(CollectionAction action,int second)
    {
        var s=Controller.State;return Controller.Control(new(1,Id,Guid.NewGuid(),s.Revision,action,s.CurrentSessionId),Utc.AddSeconds(second));
    }
    public string Pose(int second,double north=0,bool intent=false,ConnectionState connection=ConnectionState.Connected,double age=0,TelemetryFrame? frame=null)
    {
        var f=frame??TerrainSurveyGeometryTests.Frame(second,north);var s=Controller.State;
        var packet=new CollectionInputReceipt("synthetic-pose-"+(++slot),new(new(connection,TelemetrySource.Simulated,"SYNTHETIC archived packet",f.TimestampUtc),f));
        var link=MissionStore.DescribeCollectionInput(packet);var next=s with{Revision=s.Revision+1};
        var e=new CollectionEvent(1,Id,next.Revision,f.TimestampUtc.AddSeconds(age),f.TimestampUtc,s.CurrentSessionId,CollectionAction.Unknown,CollectionEventOrigin.Engine,CollectionEventOutcome.Applied,s.Phase,s.Phase,s.Authority,MissionStore.IngressReason,[link],ExpectedSessionId:s.CurrentSessionId);
        ScienceAcquisitionIntent[] intents=[];
        if(intent){var config=Controller.Session!.Configurations.Where(c=>c.Settings.InstrumentId=="terrain-scanner").MaxBy(c=>c.Revision)!;intents=[new(Guid.NewGuid(),Id,s.CurrentSessionId!.Value,"terrain-scanner",slot,config.Id,1,f.TimestampUtc,packet.Id)];}
        Missions.CommitCollectionEvidence(new(next,e,Inputs:[packet],Intents:intents,FlightSample:f,EngineDecisionId:Guid.NewGuid()));return packet.Id;
    }
    public void Finish(int second=100)
    {if(Controller.State.CurrentSessionId is not null)Control(CollectionAction.EndData,second);Missions.Finish(Id,MissionStatus.Completed,Utc.AddSeconds(second),"Synthetic fixture complete");}
    public ScientificArtifact Build(TerrainSurveyOptions? options=null)=>ContinuousTerrainSurveys.Build(Science,Missions,Id,options);
    public TerrainSurveyRecord[] Read(ScientificArtifact artifact)=>JsonSerializer.Deserialize<TerrainSurveyRecord[]>(File.ReadAllBytes(Science.VerifiedArtifactPath(artifact)),WireJson.Options)!;
    public void Sql(string sql,params (string Key,object Value)[] args)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Missions.DatabasePath,Pooling=false}.ToString());db.Open();using var cmd=db.CreateCommand();cmd.CommandText=sql;
        foreach(var (key,value) in args)cmd.Parameters.AddWithValue(key,value);cmd.ExecuteNonQuery();
    }
    // Simulates older archived JSON with an absent field and its exact original
    // hash, without changing production readers or borrowing live simulator values.
    public void LegacyReceipt(string id,Action<JsonObject> edit)
    {
        var receipt=Missions.CollectionInput(Id,id)!;
        var json=JsonSerializer.SerializeToNode(receipt.Packet,WireJson.Options)!.AsObject();edit(json);
        var text=json.ToJsonString(WireJson.Options);var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Sql("UPDATE collection_inputs SET payload_json=$json,sha256=$sha WHERE mission_id=$mission AND receipt_id=$id",("$json",text),("$sha",hash),("$mission",Id.ToString()),("$id",id));
        foreach(var e in Missions.CollectionEvents(Id,limit:500).Where(e=>e.Evidence.Any(l=>l.Id==id)))
        {var changed=e with{Evidence=e.Evidence.Select(l=>l.Id==id?l with{Sha256=hash}:l).ToArray()};Sql("UPDATE collection_events SET payload_json=$json WHERE mission_id=$mission AND sequence=$sequence",("$json",JsonSerializer.Serialize(changed,WireJson.Options)),("$mission",Id.ToString()),("$sequence",e.Sequence));}
    }
    public void Dispose(){Science.Dispose();SqliteConnection.ClearAllPools();if(Directory.Exists(Root))Directory.Delete(Root,true);}
}

public sealed class TerrainSurveyArchiveTests
{
    static TerrainSurveyChunk[] Chunks(TerrainSurveyRecord[] rows)=>rows.Where(r=>r.Chunk is not null).Select(r=>r.Chunk!).ToArray();
    [Fact] public async Task PublishedOriginalTerrainProductSourceAndGradeRemainByteIdentical()
    {
        using var f=new TerrainSurveyFixture();
        var source=await f.Science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(f.Root,"synthetic-dem"),RemoteKind.Terrain));
        f.Control(CollectionAction.StartData,0);f.Pose(0,intent:true);
        var intent=Assert.Single(f.Missions.ScienceIntents(f.Id)).Intent;var frame=TerrainSurveyGeometryTests.Frame(0);
        var dataset=f.Science.EnsureDataset(f.Missions.Detail(f.Id)!.Mission,f.Settings[0],f.Missions.Research(f.Id));
        var footprint=SensorGeometry.ForInstrument(frame,f.Settings[0]);
        var grid=new RasterGrid(64,64,"EPSG:4326",-75.032,40.032,.001,-.001);
        var product=new RemoteProduct(intent.Id,dataset.Id,f.Science.LibraryId,f.Id,"terrain-scanner",source.Id,f.Utc,f.Utc,source.Manifest.ObservationUtc,footprint,$"Products/{f.Id:N}/{intent.Id:N}.json",0,"",1,1,["elevation"],"SYNTHETIC retained historical DEM fixture","valid fixture",[],SessionId:intent.SessionId,IntentId:intent.Id);
        var original=f.Science.Publish(new(1,product,source,[new(40,-75)],[new("elevation","m",null,[123.25],"nearest original synthetic source pixel",grid)],new(){["elevation"]=[123.25]},["Synthetic source only"],new(1,frame,null,f.Settings[0],null,ScienceMode.Universal,"Fixture archived host UTC")),acknowledge:p=>f.Missions.AcknowledgeScienceIntent(intent.Id,intent.SessionId,p.Id,p.Sha256));
        f.Pose(1,100);f.Finish(2);
        var input=MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id);var report=f.Missions.SavePerformanceReport(input,"Preserved original fixture grade");
        var dataHash=PreflightReadiness.Hash(f.Science.ReadProduct(original.Id));var sourceHash=PreflightReadiness.Hash(f.Science.Source(source.Id));
        var a=f.Build();var rows=f.Read(a);var reference=Assert.Single(rows,r=>r.Acquisition is not null).Acquisition!;
        Assert.Equal("Published",reference.State);Assert.Equal(original.Id,reference.OriginalProductId);Assert.Equal(original.Sha256,reference.OriginalProductSha256);
        Assert.Single(f.Science.AllProducts(f.Id));Assert.Equal(dataHash,PreflightReadiness.Hash(f.Science.ReadProduct(original.Id)));Assert.Equal(sourceHash,PreflightReadiness.Hash(f.Science.Source(source.Id)));
        Assert.Empty(rows[0].Header!.Sources);Assert.Equal(PreflightReadiness.Hash(input),PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id)));
        Assert.Equal(report.Sha256,Assert.Single(f.Missions.PerformanceReports(f.Id)).Sha256);
    }
    [Fact] public void ActualArchivedSessionsIgnoreRecorderArmedAndPausedPosesAndKeepAcquisitionReferencesSeparate()
    {
        using var f=new TerrainSurveyFixture();f.Pose(0);f.Pose(1,100);
        var a=f.Control(CollectionAction.StartData,2).Session!;f.Pose(2,200,intent:true);f.Pose(3,300,intent:true);
        f.Control(CollectionAction.PauseData,4);f.Pose(4,400);f.Pose(5,500);
        f.Control(CollectionAction.ResumeData,6);f.Pose(6,600);f.Pose(7,700);
        f.Control(CollectionAction.EndData,8);var b=f.Control(CollectionAction.StartData,9).Session!;f.Pose(9,900);f.Pose(10,1000);f.Finish(11);
        var original=PreflightReadiness.Hash(f.Missions.StreamSamples(f.Id).ToArray());
        var originalEvents=PreflightReadiness.Hash(f.Missions.CollectionEvents(f.Id,limit:500));
        var originalSessions=PreflightReadiness.Hash(f.Missions.CollectionSessions(f.Id));
        var beforeYield=PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id));
        var artifact=f.Build();var rows=f.Read(artifact);var header=Assert.Single(rows,r=>r.Header is not null).Header!;
        Assert.Equal(f.Id,header.FlightId);Assert.Equal(f.Id,header.MissionId);Assert.False(header.GrantsOriginalScienceCredit);Assert.Equal(EvidenceOrigin.ModeledGeometry,header.Origin);Assert.Empty(header.Sources);
        var chunks=Chunks(rows);Assert.Equal(3,chunks.Length);Assert.Equal(new[]{a.Id,a.Id,b.Id},chunks.Select(c=>c.SessionId));Assert.Equal(new[]{2,6,9},chunks.Select(c=>(int)(c.FromUtc-f.Utc).TotalSeconds));
        Assert.Equal(2,rows.Count(r=>r.Acquisition is not null));Assert.All(rows.Where(r=>r.Acquisition is not null),r=>{Assert.Equal("Cancelled",r.Acquisition!.State);Assert.Null(r.Acquisition.OriginalProductId);});
        Assert.Empty(f.Science.StreamObservations(f.Id));Assert.Empty(f.Science.Datasets(f.Id));Assert.Empty(f.Missions.PerformanceReports(f.Id));
        Assert.Equal(original,PreflightReadiness.Hash(f.Missions.StreamSamples(f.Id).ToArray()));Assert.Equal(originalEvents,PreflightReadiness.Hash(f.Missions.CollectionEvents(f.Id,limit:500)));Assert.Equal(originalSessions,PreflightReadiness.Hash(f.Missions.CollectionSessions(f.Id)));
        Assert.Equal(beforeYield,PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id)));
        Assert.Equal("Modeled",rows[^1].Summary!.State);Assert.Equal(3,rows[^1].Summary!.Legs);
    }
    [Fact] public void ReopenedArchivesProduceIdenticalDerivedBytesAndBoundedSourceClippingPages()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);for(var i=0;i<8;i++)f.Pose(i,i*100);f.Finish(8);
        var options=new TerrainSurveyOptions(MaximumLegsPerChunk:2);var a=f.Build(options);
        var reopened=new MissionStore(f.Missions.DatabasePath); // No Initialize/migration and no simulator required.
        var b=ContinuousTerrainSurveys.Build(f.Science,reopened,f.Id,options);
        Assert.NotEqual(a.Id,b.Id);Assert.Equal(a.InputIdentity,b.InputIdentity);Assert.Equal(a.Sha256,b.Sha256);
        var page=ContinuousTerrainSurveys.Chunks(f.Science,reopened,a,limit:2);Assert.Equal(2,page.Chunks.Length);Assert.NotNull(page.NextAfter);
        var next=ContinuousTerrainSurveys.Chunks(f.Science,reopened,a,page.NextAfter!.Value,2);Assert.Equal(2,next.Chunks.Length);Assert.Null(next.NextAfter);
        Assert.Equal(4,page.Chunks.Concat(next.Chunks).Select(c=>c.Index).Distinct().Count());Assert.All(page.Chunks,c=>{Assert.NotEmpty(c.SourceSearchWindows);Assert.NotEmpty(c.Geometry.Coordinates);Assert.All(c.Legs,l=>Assert.Equal(EvidenceOrigin.SyntheticFixture,l.From.Origin));});
        Assert.Throws<ArgumentException>(()=>ContinuousTerrainSurveys.Chunks(f.Science,reopened,a,limit:9));
    }
    [Theory][InlineData("latitude")][InlineData("longitude")][InlineData("altitudeAglFeet")][InlineData("headingDegrees")][InlineData("groundTrackDegrees")][InlineData("pitchDegrees")][InlineData("bankDegrees")]
    public void MissingHistoricalPoseFieldsAreGapsRatherThanDeserializedZeroes(string field)
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);var bad=f.Pose(1,100);f.Pose(2,200);f.Pose(3,300);f.Finish(4);
        f.LegacyReceipt(bad,json=>json["frame"]!.AsObject().Remove(field));
        var rows=f.Read(f.Build());Assert.Contains(rows,r=>r.Gap?.Reason=="Missing/invalid archived "+field);
        Assert.Equal(f.Utc.AddSeconds(2),Assert.Single(Chunks(rows)).FromUtc);
    }
    [Theory][InlineData("age")][InlineData("disconnect-packet")][InlineData("connection")][InlineData("simulator-paused")][InlineData("collection-storage-hold")][InlineData("telemetry-gap")]
    public void RetainedAgePacketsAndUnsampledConnectionEventsPreventFalseContinuity(string kind)
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);
        if(kind=="age")f.Pose(2,200,age:20);
        else if(kind=="disconnect-packet")f.Pose(2,200,connection:ConnectionState.Disconnected);
        else f.Missions.AddEvent(f.Id,f.Utc.AddSeconds(2),kind,"SYNTHETIC unsampled continuity event");
        f.Pose(3,300);f.Pose(4,400);f.Finish(5);var rows=f.Read(f.Build());
        Assert.Equal(2,Chunks(rows).Length);Assert.Equal(2,Chunks(rows).Sum(c=>c.Legs.Length));
        Assert.DoesNotContain(Chunks(rows).SelectMany(c=>c.Legs),l=>l.From.ObservedUtc==f.Utc.AddSeconds(1)&&l.To.ObservedUtc==f.Utc.AddSeconds(3));
    }
    [Fact] public void FrozenConfigurationRevisionsAndInstrumentDisabledPeriodsStayDistinct()
    {
        using var f=new TerrainSurveyFixture();var session=f.Control(CollectionAction.StartData,0).Session!;f.Pose(0);f.Pose(1,100);
        f.Settings=[f.Settings[0] with{Enabled=false}];f.Controller.ConfigurationChanged(f.Utc.AddSeconds(2));f.Pose(2,200);f.Pose(3,300);
        f.Settings=[f.Settings[0] with{Enabled=true,Payload=new(InstrumentProfiles.Catalog.Single(p=>p.Id=="riegl.vq-1560-ii-s.nominal"))}];f.Controller.ConfigurationChanged(f.Utc.AddSeconds(4));f.Pose(4,400);f.Pose(5,500);f.Finish(6);
        var rows=f.Read(f.Build());var chunks=Chunks(rows);Assert.Equal(2,chunks.Length);Assert.All(chunks,c=>Assert.Equal(session.Id,c.SessionId));Assert.Equal(new[]{1,3},chunks.Select(c=>c.Configuration.Revision));
        Assert.Equal(new[]{"generic.terrain","riegl.vq-1560-ii-s.nominal"},chunks.Select(c=>c.GeometryProfile.Profile.Id));Assert.Contains(rows,r=>r.Gap?.Reason.Contains("inactive")==true);
    }
    [Fact] public void LegacyFlightWithoutCollectionEvidenceRemainsUnavailableAndPayloadNullIsExplicit()
    {
        using(var legacy=new TerrainSurveyFixture(legacy:true))
        {
            legacy.Missions.Append(legacy.Id,TerrainSurveyGeometryTests.Frame(0));legacy.Missions.Append(legacy.Id,TerrainSurveyGeometryTests.Frame(1,100));legacy.Missions.Finish(legacy.Id,MissionStatus.Completed,legacy.Utc.AddSeconds(2),"Legacy fixture");
            var rows=legacy.Read(legacy.Build());Assert.Empty(Chunks(rows));Assert.Equal("Unavailable",rows[^1].Summary!.State);Assert.Empty(legacy.Missions.CollectionSessions(legacy.Id));
        }
        using var f=new TerrainSurveyFixture();f.Settings=[new("terrain-scanner",true)];f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Finish(2);
        // Today's controller freezes the generic profile. Retain a payload-null
        // original only in this explicit older-format fixture.
        var archived=f.Missions.CollectionSessions(f.Id).Single();
        var old=archived with{Configurations=archived.Configurations.Select(c=>c with{Settings=c.Settings with{Payload=null}}).ToArray()};
        f.Sql("UPDATE science_sessions SET payload_json=$json WHERE id=$id",("$json",JsonSerializer.Serialize(old,WireJson.Options)),("$id",old.Id.ToString()));
        var c=Assert.Single(Chunks(f.Read(f.Build())));Assert.True(c.UsesLegacyDefaultProfile);Assert.Null(c.Configuration.Settings.Payload);Assert.Equal("generic.terrain",c.GeometryProfile.Profile.Id);
    }
    [Fact] public void ChangedOriginalInputsAndProductBytesInvalidateWithoutSilentlyRepairingEvidence()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);var id=f.Pose(1,100);f.Finish(2);
        var a=f.Build();Assert.Equal("Current",ContinuousTerrainSurveys.Status(f.Science,f.Missions,a).State);
        f.LegacyReceipt(id,j=>j["frame"]!["bankDegrees"]=5);
        Assert.Equal("Invalidated",ContinuousTerrainSurveys.Status(f.Science,f.Missions,a).State);Assert.Throws<InvalidOperationException>(()=>ContinuousTerrainSurveys.Chunks(f.Science,f.Missions,a));
        var b=f.Build();Assert.NotEqual(a.InputIdentity,b.InputIdentity);
        File.AppendAllText(f.Science.VerifiedArtifactPath(b)," ");Assert.Equal("Invalidated",ContinuousTerrainSurveys.Status(f.Science,f.Missions,b).State);
    }
    [Fact] public void HashMismatchCancellationAndBudgetsPublishNoPartialOrHiddenProduct()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);var receipt=f.Pose(1,100);f.Finish(2);
        using(var cancel=new CancellationTokenSource()){cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>ContinuousTerrainSurveys.Build(f.Science,f.Missions,f.Id,token:cancel.Token));}
        Assert.Throws<TerrainSurveyBudgetException>(()=>f.Build(new(MaximumOutputBytes:1024)));
        Assert.Throws<InvalidOperationException>(()=>f.Build(new(MaximumArchiveRows:1)));
        Assert.Throws<InvalidOperationException>(()=>f.Build(new(MaximumArchiveBytes:1024)));
        f.Sql("UPDATE collection_inputs SET sha256=$sha WHERE receipt_id=$id",("$sha",new string('0',64)),("$id",receipt));Assert.Throws<InvalidDataException>(()=>f.Build());
        Assert.Empty(f.Science.Artifacts(f.Id));Assert.Empty(f.Science.PendingArtifacts().Artifacts);
    }
    sealed class Capacity : IStorageCapacity
    {public long Remaining=4L*1024*1024*1024;public long FreeBytes(string directory)=>Remaining;}
    [Fact] public void StorageReserveAndSourceMaintenanceLeasesFailClosedWithoutChangingOriginals()
    {
        var capacity=new Capacity();using var f=new TerrainSurveyFixture(capacity:capacity);f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Finish(2);
        using(f.Science.EnterSourceMaintenance())Assert.Throws<IOException>(()=>f.Build());
        capacity.Remaining=0;Assert.Throws<IOException>(()=>f.Build());Assert.Empty(f.Science.Artifacts());Assert.Equal(2,f.Missions.StreamSamples(f.Id).Count());
    }
    [Fact] public async Task QueuedAndRunningCancellationDoNotPublishOrCreateHiddenRecoveryCopies()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);for(var i=0;i<70;i++)f.Pose(i,i*50);f.Finish(70);
        using(var cancel=new CancellationTokenSource())
        {
            Assert.Throws<OperationCanceledException>(()=>ContinuousTerrainSurveys.Build(f.Science,f.Missions,f.Id,new(MaximumLegsPerChunk:1),cancel.Token,_=>cancel.Cancel()));
            Assert.Empty(f.Science.Artifacts());Assert.Empty(f.Science.PendingArtifacts().Artifacts);
        }
        using var jobs=new ScientificAnalysisJobs(f.Missions,f.Science);var queued=jobs.Enqueue(f.Id,terrain:new());jobs.Cancel(queued.Id);await jobs.StartAsync(default);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(jobs.Get(queued.Id).State is "Queued" or "Running")await Task.Delay(10,timeout.Token);
        Assert.Equal("Cancelled",jobs.Get(queued.Id).State);await jobs.StopAsync(default);Assert.Empty(f.Science.Artifacts());
    }
    [Fact] public void PermanentScienceDeletionRemovesDerivedArtifactsAndForbidsRecreationFromRetainedTrack()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Finish(2);var a=f.Build();var path=f.Science.VerifiedArtifactPath(a);
        var review=f.Science.PreviewCollectedDeletion(new([f.Id]),f.Missions);Assert.Empty(review.Blockers);Assert.Equal(1,review.Products);
        var coordinator=new MissionCoordinator(f.Missions,1000);
        using(f.Science.BeginAnalysis())Assert.Throws<InvalidOperationException>(()=>f.Science.DeleteCollectedScience(new(review.Id,review.SelectionHash),f.Missions,coordinator.EnterMaintenance));
        Assert.True(File.Exists(path));Assert.Equal("Completed",f.Science.DeleteCollectedScience(new(review.Id,review.SelectionHash),f.Missions,coordinator.EnterMaintenance).State);
        Assert.False(File.Exists(path));Assert.Empty(f.Science.Artifacts(f.Id));Assert.NotEmpty(f.Missions.StreamSamples(f.Id));Assert.True(f.Missions.ScienceDeleted(f.Id));
        Assert.Throws<InvalidOperationException>(()=>f.Build());
        using var jobs=new ScientificAnalysisJobs(f.Missions,f.Science);Assert.Throws<InvalidOperationException>(()=>jobs.Enqueue(f.Id,terrain:new()));
    }
    [Fact] public void ClearingFlightHistoryInvalidatesSurvivingIndependentLibraryArtifact()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Finish(2);var a=f.Build();
        f.Missions.ClearFlightData("DELETE FLIGHT DATA");Assert.Equal("Invalidated",ContinuousTerrainSurveys.Status(f.Science,f.Missions,a).State);Assert.Single(f.Science.Artifacts());
    }
    [Fact] public async Task QueueRechecksPermanentDeletionAndRejectsMissingCollectionRevisions()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Finish(2);
        using var jobs=new ScientificAnalysisJobs(f.Missions,f.Science);var queued=jobs.Enqueue(f.Id,terrain:new());
        var review=f.Science.PreviewCollectedDeletion(new([f.Id]),f.Missions);f.Science.DeleteCollectedScience(new(review.Id,review.SelectionHash),f.Missions,new MissionCoordinator(f.Missions,1000).EnterMaintenance);
        await jobs.StartAsync(default);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(jobs.Get(queued.Id).State is "Queued" or "Running")await Task.Delay(10,timeout.Token);
        Assert.Equal("Failed",jobs.Get(queued.Id).State);await jobs.StopAsync(default);Assert.Empty(f.Science.Artifacts());
        using var broken=new TerrainSurveyFixture();broken.Control(CollectionAction.StartData,0);broken.Pose(0);broken.Pose(1,100);broken.Finish(2);
        broken.Sql("DELETE FROM collection_events WHERE mission_id=$id AND sequence=2",("$id",broken.Id.ToString()));
        Assert.Throws<InvalidDataException>(()=>broken.Build());Assert.Empty(broken.Science.Artifacts());
    }
}
