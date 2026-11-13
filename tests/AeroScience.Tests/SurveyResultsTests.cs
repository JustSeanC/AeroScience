using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class SurveyResultsTests
{
    static ScientificArtifact Flight(TerrainSurveyFixture f)
    {f.Control(CollectionAction.StartData,0);for(var i=0;i<4;i++)f.Pose(i,i*100);f.Finish(4);return f.Build();}
    static async Task<(ScientificArtifact Index,ScientificSource Source)> Terrain(TerrainSurveyFixture f,ScientificArtifact geometry,string surface="dem",ScientificSource? original=null)
    {
        var s=original??await f.Science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(f.Root,"synthetic-dem"),RemoteKind.Terrain));
        var job=TerrainElevations.Create(f.Science,f.Missions,f.Id,new(geometry.Id,[new(s.Id,s.ManifestSha256,Surface:surface,LocalDefinitionAcknowledged:true)],new(TileSize:4,MaximumScratchBytes:4194304)));
        f.Science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));return(TerrainElevations.Process(f.Science,f.Missions,job.Id),s);
    }
    [Fact] public async Task PagedNativeCellsRetainExactValuesMasksSourcesAndNoScienceCredit()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var before=ScientificCatalog.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id));var (a,s)=await Terrain(f,g);
        using var lease=f.Science.BeginAnalysis();var m=SurveyResults.Manifest(f.Science,f.Missions,a.Id);Assert.Equal("dem",Assert.Single(m.Sources).Surface);Assert.False(m.OriginalScienceCredit);Assert.Equal(g.Sha256,m.GeometrySha256);
        var actual=new List<SurveyViewValue>();long? cursor=0;do{var page=SurveyResults.Page(f.Science,f.Missions,a.Id,new(a.Sha256,cursor.Value,3,s.Id));Assert.InRange(page.Values.Length,0,3);Assert.InRange(page.ScannedTiles,0,32);actual.AddRange(page.Values);cursor=page.NextCursor;}while(cursor is not null);
        Assert.NotEmpty(actual);Assert.Equal(actual.Count,actual.Select(v=>v.Id).Distinct().Count());Assert.Equal(m.Values,actual.Count(v=>v.Quality=="valid"));
        var originals=m.Tiles.Select(t=>JsonSerializer.Deserialize<TerrainElevationTile>(File.ReadAllBytes(f.Science.VerifiedArtifactPath(f.Science.Artifact(t.Id))),WireJson.Options)!).SelectMany(t=>t.Cells).ToArray();
        foreach(var v in actual){var c=Assert.Single(originals,c=>c.Column==v.Column&&c.Row==v.Row);Assert.Equal(c.Value,v.Value);Assert.Equal(c.DecodedSourceValue,v.DecodedSourceValue);Assert.Equal(c.Quality,v.Quality);Assert.NotNull(v.Geometry);Assert.Equal(c.Support.Select(s=>s.SessionId).Distinct().Order(),v.Sessions.Order());Assert.NotNull(v.ArtifactSha256);Assert.Null(v.Point);}
        Assert.Equal(before,ScientificCatalog.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id)));
    }
    [Fact] public async Task IdentityWindowSessionBoundsDeletionAndPolicyFailuresDoNotLeaveAReadableNumericalCopy()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var (a,s)=await Terrain(f,g,"dsm");Assert.Equal("dsm",Assert.Single(SurveyResults.Manifest(f.Science,f.Missions,a.Id).Sources).Surface);
        Assert.Throws<IOException>(()=>SurveyResults.Page(f.Science,f.Missions,a.Id,new(new('0',64))));
        Assert.Throws<ArgumentException>(()=>SurveyResults.Page(f.Science,f.Missions,a.Id,new(a.Sha256,Limit:1025)));
        Assert.Throws<ArgumentException>(()=>SurveyResults.Page(f.Science,f.Missions,a.Id,new(a.Sha256,West:0)));
        Assert.Empty(SurveyResults.Page(f.Science,f.Missions,a.Id,new(a.Sha256,SessionId:Guid.NewGuid())).Values);
        Assert.Empty(SurveyResults.Page(f.Science,f.Missions,a.Id,new(a.Sha256,West:0,South:0,East:1,North:1)).Values);
        Assert.Throws<InvalidOperationException>(()=>SurveyResults.Manifest(f.Science,f.Missions,a.Id,new(ExcludedSourceIds:[s.Id])));
        File.Delete(Path.Combine(f.Science.DirectoryPath,s.RelativeDirectory,s.Files[0].RelativePath));
        Assert.ThrowsAny<IOException>(()=>SurveyResults.Manifest(f.Science,f.Missions,a.Id));
    }
    [Fact] public void ModeledGeometryIsNeverReturnedAsMeasuredTerrainAndArchivedTrackIsBounded()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var c=Assert.Single(SurveyResults.Catalog(f.Science,f.Missions,f.Id));Assert.Equal(g.Id,c.Id);
        var header=SurveyResults.Geometry(f.Science,f.Missions,g.Id,g.Sha256,limit:1);Assert.NotNull(Assert.Single(header.Records).Header);Assert.Equal(1,header.NextCursor);
        var m=SurveyResults.Manifest(f.Science,f.Missions,g.Id);Assert.Empty(m.Sources);Assert.Equal(0,m.Values);Assert.False(m.OriginalScienceCredit);
        Assert.Throws<ArgumentException>(()=>SurveyResults.Page(f.Science,f.Missions,g.Id,new(g.Sha256)));
        var track=SurveyResults.Track(f.Missions,f.Id,limit:2);Assert.Equal(2,track.Points.Length);Assert.Equal(2,track.NextAfter);Assert.True(track.Points[0].BreakBefore);Assert.Equal(0,track.Points[0].Ordinal);
        Assert.Equal(2,SurveyResults.Track(f.Missions,f.Id,after:2,limit:2).Points[0].Ordinal);
    }
    [Fact] public async Task HistoricalViewerPagesAreExactOriginalRecordsAndPreserveEverySessionBinding()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Control(CollectionAction.EndData,2);f.Control(CollectionAction.StartData,3);f.Pose(3);f.Pose(4,100);f.Finish(5);var g=f.Build();
        var s=await LidarImports.Import(f.Science,LidarFixture.Write(Path.Combine(f.Root,"synthetic-las"),points:[(0,450,12345),(0,550,23456),(100000,100000,99999)]),new("SYNTHETIC viewer fixture","synthetic-points","Synthetic tests only","Not genuine source acceptance"));
        var j=LidarProcessing.Create(f.Science,f.Missions,f.Id,new(g.Id,[new(s.Id,s.ManifestSha256,true)],new(RecordsPerTile:2,MaximumScratchBytes:4194304),Guid.NewGuid()));f.Science.ControlLidarJob(j.Id,new(Guid.NewGuid(),j.Revision,"start"));var a=await LidarProcessing.Process(f.Science,f.Missions,j.Id);
        var originals=f.Science.LidarCheckpoints(j.Id).SelectMany(t=>LidarProcessing.ReadTile(f.Science,f.Science.Artifact(t.ArtifactId)).Points).ToArray();Assert.Equal(2,originals.Length);
        var values=new List<SurveyViewValue>();long? cursor=0;do{var page=SurveyResults.Page(f.Science,f.Missions,a.Id,new(a.Sha256,cursor.Value,1,s.Id));Assert.InRange(page.Values.Length,0,1);values.AddRange(page.Values);cursor=page.NextCursor;}while(cursor is not null);
        Assert.Equal(originals.Length,values.Count);
        foreach(var v in values){var p=Assert.Single(originals,p=>p.Original.Record==v.Point!.Original.Record);Assert.Equal(ScientificCatalog.Hash(p),ScientificCatalog.Hash(v.Point!));Assert.Equal(p.Original.RawZ,v.DecodedSourceValue);Assert.Equal(p.Original.Z,v.Value);Assert.Equal(p.Geographic,v.Position);Assert.Equal(2,v.Sessions.Length);Assert.Null(v.Geometry);Assert.Contains(p.Original.Record.ToString(),v.Id);}
        Assert.False(SurveyResults.Manifest(f.Science,f.Missions,a.Id).OriginalScienceCredit);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>SurveyResults.Page(f.Science,f.Missions,a.Id,new(a.Sha256),token:cancelled.Token));
    }
    [Fact] public async Task ProfileUsesWgs84GroundDistanceExactNativeValuesAndSeparatelyRecordedAircraftAltitude()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var (a,s)=await Terrain(f,g);var before=ScientificCatalog.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id));
        var result=SurveyProfiles.Create(f.Science,f.Missions,a.Id,new(a.Sha256,s.Id,MaximumSamples:101));Assert.InRange(result.DistanceMetres,299.999,300.001);Assert.Equal(101,result.Samples.Length);Assert.Contains(result.Samples,p=>p.Value is not null);Assert.Equal(4,result.Aircraft.Length);Assert.Equal(5000,result.Aircraft[0].AltitudeMslFeet);Assert.Null(result.NextAfter);
        var cells=SurveyResults.Page(f.Science,f.Missions,a.Id,new(a.Sha256,Limit:1024)).Values;
        foreach(var p in result.Samples.Where(p=>p.Value is not null)){var cell=Assert.Single(cells,c=>c.Column==p.Column&&c.Row==p.Row);Assert.Equal(cell.Value,p.Value);Assert.Equal(cell.DecodedSourceValue,p.DecodedSourceValue);Assert.Equal(cell.ArtifactId,p.TileId);Assert.Equal(cell.ArtifactSha256,p.TileSha256);Assert.NotNull(p.FromPose);Assert.NotNull(p.ToUtc);}
        Assert.Equal(before,ScientificCatalog.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id)));
        var empty=SurveyProfiles.Create(f.Science,f.Missions,a.Id,new(a.Sha256,s.Id,SessionId:Guid.NewGuid(),MaximumSamples:11));Assert.All(empty.Samples,p=>{Assert.Null(p.Value);Assert.False(p.Connect);});
        Assert.Throws<ArgumentException>(()=>SurveyProfiles.Create(f.Science,f.Missions,a.Id,new(a.Sha256,s.Id,MaximumSamples:1026)));
        Assert.Throws<IOException>(()=>SurveyProfiles.Create(f.Science,f.Missions,a.Id,new(new('0',64),s.Id)));
    }
    [Fact] public async Task ProfilesKeepNodataAndSessionBoundariesDisconnectedAndNeverTurnTransectsIntoTelemetry()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);f.Pose(0);f.Pose(1,100);f.Control(CollectionAction.EndData,2);f.Control(CollectionAction.StartData,4);f.Pose(4,200);f.Pose(5,300);f.Finish(6);var g=f.Build();
        var path=Path.Combine(f.Root,"nodata-source");Directory.CreateDirectory(path);var grid=new RasterGrid(64,64,"EPSG:4326",-75.016,40.016,.0005,-.0005);ScientificRaster.WriteGeoTiff(Path.Combine(path,"native.tif"),grid,[Enumerable.Range(0,4096).Select(i=>i/64==30?(double?)null:100+i).ToArray()],["m"],new{synthetic=true});
        var source=await f.Science.ImportSourceAsync(new(1,"SYNTHETIC nodata profile",RemoteKind.Terrain,"SYNTHETIC local DEM","Synthetic fixture only","Not a genuine source","profile-nodata",null,"NAVD88 (synthetic declaration)",[new("elevation","native.tif","geotiff",Unit:"m")]),path);var (a,s)=await Terrain(f,g,original:source);
        var p=SurveyProfiles.Create(f.Science,f.Missions,a.Id,new(a.Sha256,s.Id,MaximumSamples:301));Assert.InRange(p.DistanceMetres,199.99,200.01);Assert.True(p.Samples.Select(v=>v.Segment).Distinct().Count()>1);Assert.Contains(p.Samples,v=>v.Value is null);Assert.All(p.Samples.Zip(p.Samples.Skip(1)).Where(v=>v.First.Segment!=v.Second.Segment),v=>Assert.False(v.Second.Connect));
        var line=SurveyProfiles.Create(f.Science,f.Missions,a.Id,new(a.Sha256,s.Id,MaximumSamples:11,Transect:[new(41,-75),new(41.001,-75)]));Assert.Empty(line.Aircraft);Assert.All(line.Samples,v=>{Assert.Null(v.Value);Assert.Null(v.FromUtc);Assert.False(v.Connect);});
    }
    [Fact] public async Task ExistingResearchPackageKeepsExactDerivedArtifactsAndOriginalSourceReferences()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var (a,s)=await Terrain(f,g);using var data=new MemoryStream();await ResearchPackage.WriteAsync(f.Missions,f.Science,f.Id,data);data.Position=0;using var zip=new System.IO.Compression.ZipArchive(data);
        var entry=Assert.Single(zip.Entries,e=>e.FullName==$"analysis/{a.Id:N}.json");using var stream=entry.Open();using var bytes=new MemoryStream();await stream.CopyToAsync(bytes);Assert.Equal(a.Sha256,Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())));
        using var sources=new StreamReader(zip.GetEntry("sources.json")!.Open());var json=await sources.ReadToEndAsync();Assert.Contains(s.ManifestSha256,json);Assert.Contains(s.Id.ToString(),json);Assert.DoesNotContain(zip.Entries,e=>e.FullName.EndsWith(".tif",StringComparison.OrdinalIgnoreCase));
        using var methods=new StreamReader(zip.GetEntry("methods.txt")!.Open());Assert.Contains("post-flight derived reconstruction",await methods.ReadToEndAsync());
    }
    [Fact] public async Task IncompatibleSourcesRequireExplicitSelectionAndNeverConvertUnknownDatumToConfirmed()
    {
        using var f=new TerrainSurveyFixture();var g=Flight(f);var (_,first)=await Terrain(f,g);var path=Path.Combine(f.Root,"different-source");Directory.CreateDirectory(path);var grid=first.Grids["elevation"];ScientificRaster.WriteGeoTiff(Path.Combine(path,"dsm.tif"),grid,[Enumerable.Repeat((double?)1700,grid.Width*grid.Height).ToArray()],["m"],new{synthetic=true});
        var second=await f.Science.ImportSourceAsync(new(1,"SYNTHETIC DSM unknown height reference",RemoteKind.Terrain,"SYNTHETIC source","Synthetic only","Not genuine acceptance","different-reference",null,"Unknown vertical datum",[new("elevation","dsm.tif","geotiff",Unit:"m")]),path);
        var j=TerrainElevations.Create(f.Science,f.Missions,f.Id,new(g.Id,[new(first.Id,first.ManifestSha256,Surface:"dem",LocalDefinitionAcknowledged:true),new(second.Id,second.ManifestSha256,Surface:"dsm",LocalDefinitionAcknowledged:true,UnverifiedDatumAcknowledged:true)],new(TileSize:4,MaximumScratchBytes:4194304)));f.Science.ControlTerrainJob(j.Id,new(Guid.NewGuid(),j.Revision,"start"));var a=TerrainElevations.Process(f.Science,f.Missions,j.Id);
        var m=SurveyResults.Manifest(f.Science,f.Missions,a.Id);Assert.Equal(2,m.Sources.Length);Assert.False(Assert.Single(m.Sources,s=>s.Id==second.Id).ReferenceVerified);Assert.Throws<ArgumentException>(()=>SurveyResults.Page(f.Science,f.Missions,a.Id,new(a.Sha256)));
        var page=SurveyResults.Page(f.Science,f.Missions,a.Id,new(a.Sha256,SourceId:second.Id));Assert.NotEmpty(page.Values);Assert.All(page.Values,v=>{Assert.Equal(second.Id,v.SourceId);Assert.Equal("m",v.Unit);Assert.Equal(1700,v.Value);});
    }
}
