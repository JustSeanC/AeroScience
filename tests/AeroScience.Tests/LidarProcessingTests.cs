using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class LidarProcessingTests
{
    static readonly LidarProcessingOptions Options=new(RecordsPerTile:2,MaximumScratchBytes:4194304,MaximumOutputBytes:16777216);
    static LidarSourceChoice Choice(ScientificSource s)=>new(s.Id,s.ManifestSha256,true);
    static ScientificArtifact Flight(TerrainSurveyFixture f,TerrainSurveyOptions? options=null)
    {f.Control(CollectionAction.StartData,0);for(var i=0;i<4;i++)f.Pose(i,i*100);f.Finish(4);return f.Build(options);}
    static Task<ScientificSource> Source(TerrainSurveyFixture f,(int,int,int)[]? p=null,string? project=null,string? license=null)=>LidarImports.Import(f.Science,LidarFixture.Write(Path.Combine(f.Root,"synthetic-las"),points:p),new("SYNTHETIC LAS test",project??"synthetic-points","Synthetic test only",license??"Synthetic numeric format and archived flight geometry; not genuine-source acceptance"));
    static LidarProcessingJob Create(TerrainSurveyFixture f,ScientificArtifact geometry,params ScientificSource[] sources)=>LidarProcessing.Create(f.Science,f.Missions,f.Id,new(geometry.Id,sources.Select(Choice).ToArray(),Options,Guid.NewGuid()));
    static async Task<ScientificArtifact> Run(TerrainSurveyFixture f,LidarProcessingJob j)
    {f.Science.ControlLidarJob(j.Id,new(Guid.NewGuid(),j.Revision,"start"));return await LidarProcessing.Process(f.Science,f.Missions,j.Id);}
    static LidarPointTile[] Tiles(ScienceStore science,Guid job)=>science.LidarCheckpoints(job).Select(c=>LidarProcessing.ReadTile(science,science.Artifact(c.ArtifactId))).ToArray();
    [Fact]public async Task StraightCorridorRetainsOnlyTrueSourceRecordsAndNeverGrantsOriginalScience()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var s=await Source(f,[(0,0,12345),(0,1000,43210),(100000,100000,99999)]);var before=PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id));var originalHash=geometry.Sha256;
        var job=Create(f,geometry,s);var result=await Run(f,job);var points=Tiles(f.Science,job.Id).SelectMany(t=>t.Points).ToArray();Assert.Equal(2,points.Length);Assert.Equal(new long[]{0,1},points.Select(p=>p.Original.Record).Order().ToArray());Assert.DoesNotContain(points,p=>p.Original.RawZ==99999);Assert.All(points,p=>Assert.Single(p.Support));Assert.All(Tiles(f.Science,job.Id),t=>Assert.False(t.GrantsOriginalScienceCredit));
        Assert.Equal(3,f.Science.LidarJob(job.Id).ScannedPoints);Assert.Equal(2,f.Science.LidarJob(job.Id).RetainedPoints);Assert.Equal("PartiallyComplete",f.Science.LidarJob(job.Id).State);Assert.Equal(originalHash,f.Science.Artifact(geometry.Id).Sha256);Assert.Equal(before,PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id)));Assert.Empty(f.Science.Products());Assert.Empty(f.Science.Datasets());Assert.True(File.Exists(LidarProcessing.RequireCurrent(f.Science,f.Missions,result)));
    }
    [Fact]public async Task RepeatedPassesAndMultipleSessionsKeepOneRecordWithAllActualBindings()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Pose(2);f.Control(CollectionAction.EndData,3);f.Control(CollectionAction.StartData,4);f.Pose(4);f.Pose(5,100);f.Finish(6);var geometry=f.Build(new(MaximumLegsPerChunk:1));var s=await Source(f,[(0,450,12345)]);var j=Create(f,geometry,s);await Run(f,j);var p=Assert.Single(Tiles(f.Science,j.Id).SelectMany(t=>t.Points));Assert.Equal(2,p.Support.Select(b=>b.SessionId).Distinct().Count());Assert.True(p.Support.Sum(b=>b.GeometryChunks.Length)>=3);Assert.All(p.Support,b=>Assert.NotEqual(Guid.Empty,b.ConfigurationId));Assert.Equal(1,f.Science.LidarJob(j.Id).RetainedPoints);
    }
    [Fact]public async Task PausesKeepUnsupportedMiddleEmpty()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Control(CollectionAction.PauseData,2);f.Control(CollectionAction.ResumeData,4);f.Pose(4,400);f.Pose(5,500);f.Finish(6);var geometry=f.Build();var s=await Source(f,[(0,450,1),(0,2250,2),(0,4050,3)]);var job=Create(f,geometry,s);await Run(f,job);var p=Tiles(f.Science,job.Id).SelectMany(t=>t.Points).ToArray();Assert.Equal(new[]{1,3},p.Select(v=>v.Original.RawZ).Order().ToArray());
    }
    [Fact]public async Task AntimeridianCorridorRetainsBothSeamRepresentationsWithoutIncludingTheWorldRectangle()
    {
        using var f=new TerrainSurveyFixture();var origin=new Coordinates(40,179.9998);f.Control(CollectionAction.StartData,0);
        f.Pose(0,frame:TerrainSurveyGeometryTests.Frame(0,heading:90,origin:origin));f.Pose(1,frame:TerrainSurveyGeometryTests.Frame(1,east:100,heading:90,origin:origin));f.Finish(2);var geometry=f.Build();
        Assert.Contains(ContinuousTerrainSurveys.StreamCurrentChunks(f.Science,f.Missions,geometry),c=>c.Geometry.Coordinates.Length>=2);
        var file=LidarFixture.Write(Path.Combine(f.Root,"synthetic-seam-las"),points:[(179999950,40000000,10),(-179999950,40000000,20),(0,40000000,30),(180000000,40000000,40),(-180000000,40000000,50)],coordinateOffset:(0,0,0));
        var source=await LidarImports.Import(f.Science,file,new("SYNTHETIC seam source","synthetic-seam","Synthetic fixture only","Controlled antimeridian record membership, not genuine-provider acceptance"));var job=Create(f,geometry,source);await Run(f,job);
        var retained=Tiles(f.Science,job.Id).SelectMany(t=>t.Points).OrderBy(p=>p.Original.Record).ToArray();Assert.Equal(new long[]{0,1,3,4},retained.Select(p=>p.Original.Record));Assert.Equal(4,retained.Select(p=>p.Original.Record).Distinct().Count());Assert.All(retained,p=>Assert.NotEmpty(p.Support));Assert.Equal(5,f.Science.LidarJob(job.Id).ScannedPoints);Assert.Empty(f.Science.Products());
    }
    [Fact]public async Task ActualTurningCorridorHoleDoesNotBecomeABoundingRectangle()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);(double N,double E,double H)[] poses=[(0,0,90),(0,500,0),(500,500,270),(500,0,180),(0,0,90)];for(var i=0;i<poses.Length;i++){var p=poses[i];f.Pose(i,frame:TerrainSurveyGeometryTests.Frame(i,p.N,p.E,heading:p.H));}f.Finish(5);var g=f.Build();Assert.True(ContinuousTerrainSurveys.StreamCurrentChunks(f.Science,f.Missions,g).Sum(c=>c.Geometry.Coordinates.Sum(p=>p.Length-1))>0);
        var center=Geodesy.Direct(Geodesy.Direct(new(40,-75),0,250),90,250);var hole=((int)Math.Round((center.Longitude+75)*1e6),(int)Math.Round((center.Latitude-40)*1e6),99999);var s=await Source(f,[(0,0,12345),hole]);var j=Create(f,g,s);await Run(f,j);Assert.DoesNotContain(Tiles(f.Science,j.Id).SelectMany(t=>t.Points),p=>p.Original.RawZ==99999);Assert.Contains(Tiles(f.Science,j.Id).SelectMany(t=>t.Points),p=>p.Original.RawZ==12345);
    }
    [Fact]public async Task PauseAndDurableRestartResumeVerifiedTilesWithoutDuplication()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var s=await Source(f);var j=Create(f,g,s);f.Science.ControlLidarJob(j.Id,new(Guid.NewGuid(),j.Revision,"start"));var paused=false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>LidarProcessing.Process(f.Science,f.Missions,j.Id,progress:(n,total)=>{if(!paused){paused=true;var current=f.Science.LidarJob(j.Id);f.Science.ControlLidarJob(j.Id,new(Guid.NewGuid(),current.Revision,"pause"));}}));
        Assert.Equal("Paused",f.Science.LidarJob(j.Id).State);var first=Assert.Single(f.Science.LidarCheckpoints(j.Id));f.Science.Dispose();using var reopened=new ScienceStore(f.Science.DirectoryPath);reopened.Initialize();reopened.RecoverLidarJobs();var current=reopened.LidarJob(j.Id);Assert.Equal("Paused",current.State);reopened.ControlLidarJob(j.Id,new(Guid.NewGuid(),current.Revision,"resume"));await LidarProcessing.Process(reopened,f.Missions,j.Id);Assert.Equal(first.ArtifactId,reopened.LidarCheckpoints(j.Id).First().ArtifactId);Assert.Equal(3,reopened.LidarJob(j.Id).RetainedPoints);Assert.Equal(2,reopened.LidarJob(j.Id).CompletedTiles);
    }
    [Fact]public async Task DeterministicReprocessingReusesTilesAndCommandsRequireExactIdentityRevision()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var source=await Source(f);var j=Create(f,g,source);var control=new TerrainElevationControl(Guid.NewGuid(),j.Revision,"start");var receipt=f.Science.ControlLidarJob(j.Id,control);Assert.Equal(JsonSerializer.Serialize(receipt,WireJson.Options),JsonSerializer.Serialize(f.Science.ControlLidarJob(j.Id,control),WireJson.Options));Assert.Throws<InvalidOperationException>(()=>f.Science.ControlLidarJob(j.Id,control with{Action="cancel"}));Assert.Throws<InvalidOperationException>(()=>f.Science.ControlLidarJob(j.Id,new(Guid.NewGuid(),1,"cancel")));await LidarProcessing.Process(f.Science,f.Missions,j.Id);var first=f.Science.LidarCheckpoints(j.Id).ToArray();var again=Create(f,g,source);Assert.Equal(j.Recipe,again.Recipe);await Run(f,again);Assert.Equal(first.Select(t=>t.ArtifactId),f.Science.LidarCheckpoints(again.Id).Select(t=>t.ArtifactId));Assert.Equal(first.Select(t=>t.Sha256),f.Science.LidarCheckpoints(again.Id).Select(t=>t.Sha256));
    }
    [Fact]public async Task ChangedArchiveSourceBytesAndCheckpointEachRefuseResumeOrDownload()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var s=await Source(f);var j=Create(f,g,s);await Run(f,j);var tile=f.Science.Artifact(f.Science.LidarCheckpoints(j.Id).First().ArtifactId);File.AppendAllText(f.Science.VerifiedArtifactPath(tile),"changed");Assert.Throws<IOException>(()=>LidarProcessing.ReadTile(f.Science,tile));
        var sourcePath=Path.Combine(f.Science.DirectoryPath,s.RelativeDirectory,s.Files[0].RelativePath);File.AppendAllText(sourcePath,"changed");Assert.Throws<IOException>(()=>Create(f,g,s));Assert.Throws<IOException>(()=>LidarProcessing.RequireCurrent(f.Science,f.Missions,f.Science.Artifact(f.Science.LidarJob(j.Id).ResultArtifactId!.Value)));
    }
    [Fact]public async Task ReviewCreationIsIdempotentAndChangedRequestIdentityCannotCreateAnotherJob()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var s=await Source(f);var request=new LidarProcessingRequest(g.Id,[Choice(s)],Options,Guid.NewGuid());var first=LidarProcessing.Create(f.Science,f.Missions,f.Id,request);Assert.Equal(first.Id,LidarProcessing.Create(f.Science,f.Missions,f.Id,request).Id);Assert.Single(f.Science.LidarJobs(f.Id));Assert.Throws<InvalidOperationException>(()=>LidarProcessing.Create(f.Science,f.Missions,f.Id,request with{Options=Options with{RecordsPerTile=1}}));
    }
    [Fact]public async Task OutputLimitIsAtomicAndDoesNotChangeOriginalArchives()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var s=await Source(f,license:new string('x',2000));var j=LidarProcessing.Create(f.Science,f.Missions,f.Id,new(g.Id,[Choice(s)],Options with{MaximumOutputBytes=4096},Guid.NewGuid()));f.Science.ControlLidarJob(j.Id,new(Guid.NewGuid(),j.Revision,"start"));await Assert.ThrowsAsync<StorageLimitException>(()=>LidarProcessing.Process(f.Science,f.Missions,j.Id));Assert.Equal("Failed",f.Science.LidarJob(j.Id).State);Assert.Empty(f.Science.LidarCheckpoints(j.Id));Assert.DoesNotContain(f.Science.Artifacts(f.Id),a=>a.Kind==LidarProcessing.TileKind);Assert.Equal("Current",ContinuousTerrainSurveys.Status(f.Science,f.Missions,g).State);Assert.Empty(f.Science.Products());
    }
    [Fact]public async Task SourceDeletionPurgesPointValuesAndInvalidatesDerivedProductsOnly()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var s=await Source(f);var j=Create(f,g,s);var result=await Run(f,j);var paths=f.Science.LidarCheckpoints(j.Id).Select(t=>f.Science.VerifiedArtifactPath(f.Science.Artifact(t.ArtifactId))).ToArray();var other=await f.Science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(f.Root,"unrelated-dem"),RemoteKind.Terrain));var coordinator=new MissionCoordinator(f.Missions,1000);
        var preview=f.Science.PreviewSourceDeletion(new([s.Id]),f.Missions);Assert.True(preview.EligibleSources==1,string.Join(';',preview.Sources.SelectMany(v=>v.Blockers)));Assert.Equal("Completed",f.Science.DeleteExternalSources(new(preview.Id,preview.SelectionHash),f.Missions,coordinator.EnterMaintenance).State);Assert.All(paths,p=>Assert.False(File.Exists(p)));Assert.Equal("Invalidated",f.Science.LidarJob(j.Id).State);Assert.Throws<IOException>(()=>LidarProcessing.RequireCurrent(f.Science,f.Missions,result));Assert.Equal(other.Id,Assert.Single(f.Science.Sources()).Id);Assert.Equal("Current",ContinuousTerrainSurveys.Status(f.Science,f.Missions,g).State);
    }
}
