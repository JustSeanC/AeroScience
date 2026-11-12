using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using Xunit;

namespace AeroScience.Tests;

// Deterministic SYNTHETIC rasters and archived flight evidence. Genuine-provider
// numerical acceptance has a separate explicit receipt; these are not MSFS tests.
[Collection(ScientificIoCollection.Name)]
public sealed class TerrainElevationTests
{
    static readonly TerrainElevationOptions Options=new(TileSize:4,MaximumScratchBytes:4194304,MaximumOutputBytes:16777216);
    static ScientificArtifact Flight(TerrainSurveyFixture f)
    {f.Control(CollectionAction.StartData,0);for(var i=0;i<4;i++)f.Pose(i,i*100);f.Finish(4);return f.Build();}
    static async Task<ScientificSource> Source(TerrainSurveyFixture f,RasterGrid? grid=null,string datum="NAVD88 (synthetic declaration)",string provider="SYNTHETIC local DEM",string? notes=null,bool qa=false,Func<int,int,double?>? values=null,double scale=1,double offset=0,bool extraDirectory=false)
    {
        var g=grid??new(64,64,"EPSG:4326",-75.016,40.016,.0005,-.0005);var path=Path.Combine(f.Root,"source-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        var data=Enumerable.Range(0,g.Width*g.Height).Select(i=>values is null?(double?)(i+100):values(i%g.Width,i/g.Width)).ToArray();
        ScientificRaster.WriteGeoTiff(Path.Combine(path,"elevation.tif"),g,[data],["m"],new{synthetic=true});
        if(extraDirectory)
        {
            using var tif=BitMiracle.LibTiff.Classic.Tiff.Open(Path.Combine(path,"elevation.tif"),"a");
            tif.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH,1);tif.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH,1);tif.SetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLESPERPIXEL,1);tif.SetField(BitMiracle.LibTiff.Classic.TiffTag.BITSPERSAMPLE,8);tif.SetField(BitMiracle.LibTiff.Classic.TiffTag.ROWSPERSTRIP,1);tif.SetField(BitMiracle.LibTiff.Classic.TiffTag.PHOTOMETRIC,BitMiracle.LibTiff.Classic.Photometric.MINISBLACK);tif.SetField(BitMiracle.LibTiff.Classic.TiffTag.PLANARCONFIG,BitMiracle.LibTiff.Classic.PlanarConfig.CONTIG);tif.WriteScanline([1],0);tif.WriteDirectory();
        }
        var bands=new List<SourceBand>{new("elevation","elevation.tif","geotiff",Unit:"m",Scale:scale,Offset:offset)};
        if(qa){ScientificRaster.WriteGeoTiff(Path.Combine(path,"QA.tif"),g,[Enumerable.Range(0,g.Width*g.Height).Select(i=>(double?)(i%2)).ToArray()],["1"],new{synthetic=true});bands.Add(new("QA","QA.tif","geotiff"));}
        var manifest=new ScientificSourceManifest(1,provider,RemoteKind.Terrain,provider,"Synthetic fixture only","No genuine source acceptance","synthetic-"+Guid.NewGuid(),null,datum,bands.ToArray(),Notes:notes);
        if(!qa)return await f.Science.ImportSourceAsync(manifest,path);
        // Preserve and assert the existing single-band import contract. A separately
        // constructed synthetic retained catalog exercises the explicit QA adapter;
        // this does not claim multi-band terrain import/provider acceptance.
        await Assert.ThrowsAsync<InvalidDataException>(()=>f.Science.ImportSourceAsync(manifest,path));
        var s=await f.Science.ImportSourceAsync(manifest with{Bands=[bands[0]]},path);var qaPath=Path.Combine(f.Science.DirectoryPath,s.RelativeDirectory,"QA.tif");File.Copy(Path.Combine(path,"QA.tif"),qaPath);
        ManagedSourceFile[] files=[..s.Files,new("QA.tif",new FileInfo(qaPath).Length,Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(qaPath))))];
        s=s with{Manifest=manifest,Files=files,Grids=new(s.Grids){["QA"]=g},ManifestSha256=ScientificComposites.Identity(new{manifest,files})};PreferenceStore.WriteAtomic(Path.Combine(f.Science.DirectoryPath,s.RelativeDirectory,"source.json"),s);
        using var db=new SqliteConnection("Data Source="+f.Science.DatabasePath+";Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE remote_sources SET payload_json=$j,manifest_sha=$sha WHERE id=$id";cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(s,WireJson.Options));cmd.Parameters.AddWithValue("$sha",s.ManifestSha256);cmd.Parameters.AddWithValue("$id",s.Id.ToString());cmd.ExecuteNonQuery();return s;
    }
    static TerrainElevationSourceChoice Choice(ScientificSource s,bool unknown=false,bool qa=false)=>new(s.Id,s.ManifestSha256,Surface:"dem",LocalDefinitionAcknowledged:true,UnverifiedDatumAcknowledged:unknown,QualityBandId:qa?"QA":null,AcceptedQualityValues:qa?[0d]:null);
    static TerrainElevationJob Create(TerrainSurveyFixture f,ScientificArtifact geometry,params TerrainElevationSourceChoice[] sources)=>TerrainElevations.Create(f.Science,f.Missions,f.Id,new(geometry.Id,sources,Options));
    static ScientificArtifact Run(TerrainSurveyFixture f,TerrainElevationJob job)
    {f.Science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));return TerrainElevations.Process(f.Science,f.Missions,job.Id);}
    static TerrainElevationTile[] Tiles(TerrainSurveyFixture f,Guid job)=>f.Science.TerrainCheckpoints(job).Select(t=>JsonSerializer.Deserialize<TerrainElevationTile>(File.ReadAllBytes(f.Science.VerifiedArtifactPath(f.Science.Artifact(t.ArtifactId))),WireJson.Options)!).ToArray();
    static Geometry Native(TerrainSurveyGeometry g,RasterGrid grid)
    {
        var factory=new GeometryFactory();Coordinate P(double[] p)=>new((p[0]-grid.OriginX)/grid.PixelWidth,(p[1]-grid.OriginY)/grid.PixelHeight);
        return factory.CreateMultiPolygon(g.Coordinates.Select(p=>factory.CreatePolygon(factory.CreateLinearRing(p[0].Select(P).ToArray()),p.Skip(1).Select(r=>factory.CreateLinearRing(r.Select(P).ToArray())).ToArray())).ToArray());
    }
    [Fact] public async Task OriginalNativeCellsArePreciselyClippedScaledAndIndependentOfOriginalScience()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var source=await Source(f,scale:.5,offset:10);var grid=source.Grids["elevation"];
        var scienceBefore=PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id));var eventsBefore=PreflightReadiness.Hash(f.Missions.CollectionEvents(f.Id,limit:500));
        var job=Create(f,geometry,Choice(source));Assert.True(job.TotalTiles>0);var result=Run(f,job);
        var cells=Tiles(f,job.Id).SelectMany(t=>t.Cells).ToArray();Assert.NotEmpty(cells);Assert.Equal(cells.Length,cells.Select(c=>(c.Column,c.Row)).Distinct().Count());
        var chunk=Assert.Single(ContinuousTerrainSurveys.StreamCurrentChunks(f.Science,f.Missions,geometry));var mask=Native(chunk.Geometry,grid);var factory=new GeometryFactory();var expected=0;
        for(var y=0;y<grid.Height;y++)for(var x=0;x<grid.Width;x++)
        {
            var clip=mask.Intersection(factory.ToGeometry(new Envelope(x,x+1,y,y+1)));if(clip.Area<=0)continue;expected++;
            var cell=Assert.Single(cells,c=>c.Column==x&&c.Row==y);Assert.Equal(y*grid.Width+x+100,cell.DecodedSourceValue);Assert.Equal((y*grid.Width+x+100)*.5+10,cell.Value);
            Assert.InRange(Math.Abs(cell.NativeCellSupportFraction-clip.Area),0,1e-10);var support=Assert.Single(cell.Support);Assert.Equal(chunk.SessionId,support.SessionId);Assert.Equal(chunk.Configuration.Id,support.ConfigurationId);Assert.Equal(chunk.GeometryProfile.Profile.Id,support.ProfileId);Assert.Equal([chunk.Index],support.GeometryChunks);
        }
        Assert.Equal(expected,cells.Length);Assert.Contains(cells,c=>c.NativeCellSupportFraction is >0 and <.99);
        Assert.Equal(scienceBefore,PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Missions,f.Science,f.Id)));Assert.Equal(eventsBefore,PreflightReadiness.Hash(f.Missions.CollectionEvents(f.Id,limit:500)));
        Assert.Empty(f.Science.StreamObservations(f.Id));Assert.Empty(f.Science.Products());Assert.Empty(f.Missions.PerformanceReports(f.Id));
        var output=JsonSerializer.Deserialize<TerrainElevationIndex>(File.ReadAllBytes(f.Science.VerifiedArtifactPath(result)),WireJson.Options)!;
        Assert.False(output.GrantsOriginalScienceCredit);Assert.Equal(geometry.Sha256,output.GeometrySha256);Assert.Equal(geometry.InputIdentity,output.GeometryInputFingerprint);Assert.Equal("PartiallyComplete",output.State);Assert.Null(Assert.Single(output.Sources).Original.Manifest.ObservationUtc);
    }
    [Fact] public async Task CurvedRepeatedPassesAndSessionsDoNotDuplicateNativeCellsOrFillPauses()
    {
        using var f=new TerrainSurveyFixture();var first=f.Control(CollectionAction.StartData,0).Session!;f.Pose(0);f.Pose(1,100);f.Pose(2,200,frame:TerrainSurveyGeometryTests.Frame(2,200,heading:45));
        f.Control(CollectionAction.PauseData,3);f.Pose(3,300);f.Pose(4,400,frame:TerrainSurveyGeometryTests.Frame(4,400,agl:0));
        f.Control(CollectionAction.ResumeData,5);f.Pose(5);f.Pose(6,100);f.Control(CollectionAction.EndData,7);
        var second=f.Control(CollectionAction.StartData,8).Session!;f.Pose(8);f.Pose(9,100);f.Finish(10);var geometry=f.Build();var source=await Source(f);var job=Create(f,geometry,Choice(source));Run(f,job);
        var cells=Tiles(f,job.Id).SelectMany(t=>t.Cells).ToArray();Assert.Equal(cells.Length,cells.DistinctBy(c=>(c.Column,c.Row)).Count());
        Assert.Contains(cells,c=>c.Support.Select(s=>s.SessionId).Distinct().Count()==2);Assert.All(cells.SelectMany(c=>c.Support),s=>Assert.Contains(s.SessionId,new[]{first.Id,second.Id}));
        Assert.All(cells,c=>Assert.InRange(c.NativeCellSupportFraction,0,1));
        var chunks=ContinuousTerrainSurveys.StreamCurrentChunks(f.Science,f.Missions,geometry).ToArray();Assert.DoesNotContain(chunks.SelectMany(c=>c.Legs),l=>l.From.ObservedUtc==f.Utc.AddSeconds(3));
    }
    [Fact] public async Task ClosedTurningCorridorKeepsItsInteriorHoleEmpty()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);
        (double North,double East,double Heading)[] positions=[(0,0,90),(0,500,0),(500,500,270),(500,0,180),(0,0,90)];
        for(var i=0;i<positions.Length;i++){var p=positions[i];f.Pose(i,frame:TerrainSurveyGeometryTests.Frame(i,p.North,p.East,heading:p.Heading));}f.Finish(5);var geometry=f.Build();
        var chunks=ContinuousTerrainSurveys.StreamCurrentChunks(f.Science,f.Missions,geometry).ToArray();Assert.True(chunks.Sum(c=>c.Geometry.Coordinates.Sum(p=>p.Length-1))>0);
        var source=await Source(f);var job=Create(f,geometry,Choice(source));Run(f,job);var grid=source.Grids["elevation"];var center=Geodesy.Direct(Geodesy.Direct(new(40,-75),0,250),90,250);var col=(int)Math.Floor((center.Longitude-grid.OriginX)/grid.PixelWidth);var row=(int)Math.Floor((center.Latitude-grid.OriginY)/grid.PixelHeight);
        Assert.DoesNotContain(Tiles(f,job.Id).SelectMany(t=>t.Cells),c=>c.Column==col&&c.Row==row);
        var hole=await Source(f,new(1,1,"EPSG:4326",center.Longitude-.00005,center.Latitude+.00005,.0001,-.0001));
        Assert.Equal("NoGridOverlap",Assert.Single(TerrainElevations.Review(f.Science,f.Missions,geometry.Id).Sources,s=>s.SourceId==hole.Id).State);
    }
    [Fact] public async Task SplitAntimeridianPolygonsReadOnlyTheirOriginalNativeGridSides()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);for(var i=0;i<3;i++)f.Pose(i,frame:TerrainSurveyGeometryTests.Frame(i,east:i*100,heading:90,origin:new(0,179.9995)));f.Finish(3);var geometry=f.Build();
        var west=await Source(f,new(20,20,"EPSG:4326",179.998,.001,.0001,-.0001),values:(x,y)=>10);var east=await Source(f,new(20,20,"EPSG:4326",-180,.001,.0001,-.0001),values:(x,y)=>20);
        var job=Create(f,geometry,Choice(west),Choice(east));Run(f,job);var tiles=Tiles(f,job.Id);Assert.Equal(2,tiles.Select(t=>t.Source.Original.Id).Distinct().Count());Assert.All(tiles,t=>Assert.All(t.Cells,c=>{Assert.InRange(c.Column,0,19);Assert.InRange(c.Row,0,19);Assert.Equal(t.Source.Original.Id==west.Id?10:20,c.Value);}));
    }
    [Fact] public async Task NodataAndQualityMasksRemainExplicitGapsWithNoExportedExcludedElevations()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var source=await Source(f,qa:true,values:(x,y)=>y%2==0?null:100+x);
        Assert.Throws<InvalidOperationException>(()=>Create(f,geometry,Choice(source)));
        var job=Create(f,geometry,Choice(source,qa:true));Run(f,job);var cells=Tiles(f,job.Id).SelectMany(t=>t.Cells).ToArray();
        Assert.Contains(cells,c=>c.Quality=="valid");Assert.Contains(cells,c=>c.Quality=="nodata");Assert.Contains(cells,c=>c.Quality=="quality-excluded");Assert.All(cells.Where(c=>c.Quality!="valid"),c=>{Assert.Null(c.Value);Assert.Null(c.DecodedSourceValue);});
    }
    [Fact] public async Task AdditionalTiffDirectoriesAreUnavailableRatherThanSilentlyIgnoringPossibleMasks()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var source=await Source(f,extraDirectory:true);
        var error=Assert.Throws<InvalidOperationException>(()=>Create(f,geometry,Choice(source)));Assert.Contains("quality semantics",error.Message);Assert.DoesNotContain(f.Science.Artifacts(),a=>a.Kind==TerrainElevations.TileKind);Assert.NotNull(f.Science.Source(source.Id));
    }
    [Theory][InlineData("Copernicus DEM","Digital surface model includes vegetation and buildings","EGM2008",false,"dsm")][InlineData("USGS","Composite export; native date/reference unspecified","Unknown",true,"composite-dem")][InlineData("SYNTHETIC local","Local DEM documentation","Unknown",true,"dem")]
    public async Task SourceClassesAndUnverifiedReferencesStaySeparate(string provider,string notes,string datum,bool acknowledge,string expected)
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var source=await Source(f,provider:provider,notes:notes,datum:datum);
        if(acknowledge)Assert.Throws<InvalidOperationException>(()=>Create(f,geometry,Choice(source)));
        var job=Create(f,geometry,Choice(source,acknowledge));Run(f,job);Assert.All(Tiles(f,job.Id),t=>{Assert.Equal(expected,t.Source.Surface);Assert.Equal(datum,t.Source.Original.Manifest.VerticalDatum);Assert.Equal(!acknowledge,t.Source.DatumVerified);});
    }
    [Fact] public async Task LocalDefinitionPolicyHashAndUnsupportedUnitsAreNotGuessedFromBandNames()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var s=await Source(f);
        Assert.Throws<InvalidOperationException>(()=>Create(f,geometry,new TerrainElevationSourceChoice(s.Id,s.ManifestSha256)));
        Assert.Throws<IOException>(()=>Create(f,geometry,Choice(s) with{ManifestSha256=new('a',64)}));
        Assert.Throws<InvalidOperationException>(()=>TerrainElevations.Create(f.Science,f.Missions,f.Id,new(geometry.Id,[Choice(s)],Options),new(ExcludedSourceIds:[s.Id])));
        Assert.Throws<ArgumentException>(()=>Create(f,geometry,Choice(s) with{BandId="imagined elevation"}));
        Assert.Throws<ArgumentException>(()=>TerrainElevations.Create(f.Science,f.Missions,f.Id,new(geometry.Id,[Choice(s),Choice(s)],Options)));
    }
    [Fact] public async Task DifferentResolutionsAndDatumsPublishIndependentUnblendedTiles()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var a=await Source(f,values:(x,y)=>100);var b=await Source(f,new(32,32,"EPSG:4326",-75.016,40.016,.001,-.001),datum:"EGM2008",values:(x,y)=>900);
        var job=Create(f,geometry,Choice(a),Choice(b));Run(f,job);var tiles=Tiles(f,job.Id);Assert.Equal(2,tiles.Select(t=>t.Source.Original.Id).Distinct().Count());
        Assert.All(tiles,t=>Assert.All(t.Cells.Where(c=>c.Quality=="valid"),c=>Assert.Equal(t.Source.Original.Id==a.Id?100:900,c.Value)));
        Assert.True(tiles.Where(t=>t.Source.Original.Id==a.Id).Sum(t=>t.Cells.Length)>tiles.Where(t=>t.Source.Original.Id==b.Id).Sum(t=>t.Cells.Length));
    }
    [Fact] public async Task ExplicitRegionalScopeDoesNotPopulateUnprocessedCorridorChunks()
    {
        using var f=new TerrainSurveyFixture();f.Control(CollectionAction.StartData,0);for(var i=0;i<4;i++)f.Pose(i,i*300);f.Finish(4);var geometry=f.Build(new(MaximumLegsPerChunk:1));var source=await Source(f);
        var options=Options with{FirstGeometryChunk=2,LastGeometryChunk=2};var job=TerrainElevations.Create(f.Science,f.Missions,f.Id,new(geometry.Id,[Choice(source)],options));var result=Run(f,job);
        Assert.All(Tiles(f,job.Id).SelectMany(t=>t.Cells).SelectMany(c=>c.Support),s=>Assert.Equal([2],s.GeometryChunks));
        var index=JsonSerializer.Deserialize<TerrainElevationIndex>(File.ReadAllBytes(f.Science.VerifiedArtifactPath(result)),WireJson.Options)!;Assert.Equal(options,index.Options);Assert.Equal(job.Recipe,index.Recipe);Assert.NotEmpty(index.Tiles);
        Assert.Throws<ArgumentException>(()=>TerrainElevations.Create(f.Science,f.Missions,f.Id,new(geometry.Id,[Choice(source)],options with{LastGeometryChunk=1})));
    }
    [Fact] public async Task AccurateWgs84UtmNativeGridWorksWithoutChangingLegacyPointSampling()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var p=GeographicLib.Projections.TransverseMercator.UTM.Forward(-75,40,-75);
        var source=await Source(f,new(100,100,"EPSG:32618",p.Item1+499500,p.Item2+700,10,-10));var job=Create(f,geometry,Choice(source));Run(f,job);var cells=Tiles(f,job.Id).SelectMany(t=>t.Cells).ToArray();
        Assert.NotEmpty(cells);Assert.All(cells,c=>{Assert.InRange(c.Column,40,60);Assert.InRange(c.Row,30,72);Assert.Equal(c.Row*100+c.Column+100,c.Value);});
    }
    [Fact] public async Task ReviewUsesPreciseGridMasksAndMissingSourcesNeverStartOnlineWork()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var far=await Source(f,new(4,4,"EPSG:4326",10,10,.01,-.01));var near=await Source(f);
        var review=TerrainElevations.Review(f.Science,f.Missions,geometry.Id);Assert.NotEmpty(review.SearchWindows);Assert.DoesNotContain(review.Sources,s=>s.SourceId==far.Id);var candidate=Assert.Single(review.Sources,s=>s.SourceId==near.Id);Assert.Equal("ReviewRequired",candidate.State);Assert.NotEmpty(candidate.PotentialGridSupport);
        Assert.Equal("Unavailable",Assert.Single(TerrainElevations.Review(f.Science,f.Missions,geometry.Id,preferences:new(ExcludedSourceIds:[near.Id])).Sources).State);
        var none=Create(f,geometry);Assert.Throws<InvalidOperationException>(()=>f.Science.ControlTerrainJob(none.Id,new(Guid.NewGuid(),none.Revision,"start")));Assert.Equal(2,f.Science.CountSources());Assert.DoesNotContain(f.Science.Artifacts(),a=>a.Kind==TerrainElevations.TileKind);
    }
    [Fact] public async Task ExplicitPauseAndRestartResumeVerifiedTilesAndDeterministicReproduction()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var source=await Source(f);var job=Create(f,geometry,Choice(source));f.Science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));
        Assert.Throws<OperationCanceledException>(()=>TerrainElevations.Process(f.Science,f.Missions,job.Id,progress:(n,total)=>{if(n==1){var current=f.Science.TerrainJob(job.Id);f.Science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),current.Revision,"pause"));}}));
        Assert.Equal("Paused",f.Science.TerrainJob(job.Id).State);var first=Assert.Single(f.Science.TerrainCheckpoints(job.Id));
        f.Science.Dispose();using(var reopened=new ScienceStore(f.Science.DirectoryPath)){reopened.Initialize();reopened.RecoverTerrainJobs();var paused=reopened.TerrainJob(job.Id);reopened.ControlTerrainJob(job.Id,new(Guid.NewGuid(),paused.Revision,"resume"));TerrainElevations.Process(reopened,f.Missions,job.Id);}f.Science.Initialize();
        Assert.Contains(f.Science.TerrainCheckpoints(job.Id),t=>t.ArtifactId==first.ArtifactId&&t.Sha256==first.Sha256);
        var original=f.Science.Artifact(f.Science.TerrainJob(job.Id).ResultArtifactId!.Value);var count=f.Science.Artifacts().Count(a=>a.Kind==TerrainElevations.TileKind);var reproduced=Run(f,Create(f,geometry,Choice(source)));
        Assert.Equal(original.Sha256,reproduced.Sha256);Assert.Equal(count,f.Science.Artifacts().Count(a=>a.Kind==TerrainElevations.TileKind));
    }
    [Fact] public async Task StaleCommandsDuplicateReceiptsAndInterruptedJobRecoveryRetainIdentity()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var s=await Source(f);var job=Create(f,geometry,Choice(s));var request=new TerrainElevationControl(Guid.NewGuid(),job.Revision,"start");
        var accepted=f.Science.ControlTerrainJob(job.Id,request);Assert.Equal(ScientificComposites.Identity(accepted),ScientificComposites.Identity(f.Science.ControlTerrainJob(job.Id,request)));
        Assert.Throws<InvalidOperationException>(()=>f.Science.ControlTerrainJob(job.Id,request with{Action="cancel"}));Assert.Throws<InvalidOperationException>(()=>f.Science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),job.Revision,"cancel")));
        f.Science.ChangeTerrainJob(job.Id,j=>j with{State="Running"});f.Science.RecoverTerrainJobs();Assert.Equal("Interrupted",f.Science.TerrainJob(job.Id).State);Assert.Empty(f.Science.TerrainCheckpoints(job.Id));
    }
    [Theory][InlineData("geometry")][InlineData("source")][InlineData("checkpoint")]
    public async Task ChangedInputsAndCorruptedCheckpointsCannotBeResumed(string changed)
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var s=await Source(f);var job=Create(f,geometry,Choice(s));f.Science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));
        using var cancellation=new CancellationTokenSource();Assert.Throws<OperationCanceledException>(()=>TerrainElevations.Process(f.Science,f.Missions,job.Id,token:cancellation.Token,progress:(n,total)=>cancellation.Cancel()));
        if(changed=="geometry")f.Missions.AddEvent(f.Id,f.Utc,"connection","SYNTHETIC archive continuity changed");
        if(changed=="source")File.AppendAllText(Path.Combine(f.Science.DirectoryPath,s.RelativeDirectory,s.Files[0].RelativePath),"synthetic corruption");
        if(changed=="checkpoint")File.AppendAllText(f.Science.VerifiedArtifactPath(f.Science.Artifact(f.Science.TerrainCheckpoints(job.Id).First().ArtifactId)),"synthetic corruption");
        var interrupted=f.Science.TerrainJob(job.Id);f.Science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),interrupted.Revision,"resume"));
        if(changed=="geometry")Assert.Throws<InvalidOperationException>(()=>TerrainElevations.Process(f.Science,f.Missions,job.Id));else Assert.Throws<IOException>(()=>TerrainElevations.Process(f.Science,f.Missions,job.Id));Assert.Null(f.Science.TerrainJob(job.Id).ResultArtifactId);
    }
    [Fact] public async Task CandidateAndOutputBudgetsFailWithoutUnboundedRasterOrFalseCompletion()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var s=await Source(f);
        Assert.Throws<InvalidOperationException>(()=>TerrainElevations.Create(f.Science,f.Missions,f.Id,new(geometry.Id,[Choice(s)],Options with{MaximumCandidateCells=1})));
        var job=TerrainElevations.Create(f.Science,f.Missions,f.Id,new(geometry.Id,[Choice(s)],Options with{MaximumOutputBytes=4096}));Assert.Throws<StorageLimitException>(()=>Run(f,job));Assert.Equal("Failed",f.Science.TerrainJob(job.Id).State);Assert.Null(f.Science.TerrainJob(job.Id).ResultArtifactId);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Science.DirectoryPath,"Metadata","AnalysisWork")));Assert.Empty(Directory.EnumerateFiles(f.Science.DirectoryPath,"*.partial",SearchOption.AllDirectories));
    }
    sealed class MutableDisk:IStorageCapacity{public long Available=4294967296;public long FreeBytes(string directory)=>Available;}
    [Fact] public async Task DiskExhaustionAfterCheckpointKeepsOnlyAtomicVisibleTilesAndCleansScratch()
    {
        var disk=new MutableDisk();using var f=new TerrainSurveyFixture(capacity:disk);var geometry=Flight(f);var source=await Source(f);var job=Create(f,geometry,Choice(source));f.Science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));
        Assert.Throws<IOException>(()=>TerrainElevations.Process(f.Science,f.Missions,job.Id,progress:(n,total)=>disk.Available=0));Assert.Equal("Failed",f.Science.TerrainJob(job.Id).State);Assert.Single(f.Science.TerrainCheckpoints(job.Id));Assert.Null(f.Science.TerrainJob(job.Id).ResultArtifactId);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Science.DirectoryPath,"Metadata","AnalysisWork")));Assert.Empty(Directory.EnumerateFiles(f.Science.DirectoryPath,"*.partial",SearchOption.AllDirectories));Assert.NotNull(f.Science.Source(source.Id));
    }
    [Fact] public async Task SourceDeletionRemovesManagedDerivedValuesInvalidatesIndexAndPreservesOtherOriginals()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var source=await Source(f);var other=await Source(f,values:(x,y)=>999);var job=Create(f,geometry,Choice(source),Choice(other));var result=Run(f,job);
        var tiles=f.Science.TerrainCheckpoints(job.Id).Where(t=>t.SourceId==source.Id).Select(t=>f.Science.Artifact(t.ArtifactId)).ToArray();
        using(var lease=f.Science.BeginAnalysis())Assert.Equal(0,f.Science.PreviewSourceDeletion(new([source.Id]),f.Missions).EligibleSources);
        var review=f.Science.PreviewSourceDeletion(new([source.Id]),f.Missions);Assert.Equal(1,review.EligibleSources);var coordinator=new MissionCoordinator(f.Missions,1000);
        var deleted=f.Science.DeleteExternalSources(new(review.Id,review.SelectionHash),f.Missions,coordinator.EnterMaintenance);Assert.True(deleted.State=="Completed",deleted.Warning);
        Assert.All(tiles,t=>{Assert.False(File.Exists(Path.Combine(f.Science.DirectoryPath,t.RelativePath)));Assert.Throws<ArgumentException>(()=>f.Science.Artifact(t.Id));});
        Assert.Equal(other.Id,Assert.Single(f.Science.Sources()).Id);Assert.Equal("Invalidated",f.Science.TerrainJob(job.Id).State);Assert.Throws<InvalidOperationException>(()=>TerrainElevations.RequireCurrent(f.Science,f.Missions,result));Assert.Equal("Current",ContinuousTerrainSurveys.Status(f.Science,f.Missions,geometry).State);
        Assert.Contains(f.Science.Artifacts(),a=>a.Kind==TerrainElevations.TileKind);Assert.Equal(4,f.Missions.StreamSamples(f.Id).Count());
    }
    [Fact] public async Task PermanentCollectedDeletionAlsoPurgesTerrainJobsReceiptsAndTiles()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var s=await Source(f);var job=Create(f,geometry,Choice(s));Run(f,job);var review=f.Science.PreviewCollectedDeletion(new([f.Id]),f.Missions);var coordinator=new MissionCoordinator(f.Missions,1000);
        var result=f.Science.DeleteCollectedScience(new(review.Id,review.SelectionHash),f.Missions,coordinator.EnterMaintenance);Assert.Equal("Completed",result.State);Assert.Empty(f.Science.TerrainJobs(f.Id));Assert.Empty(f.Science.Artifacts(f.Id));Assert.NotNull(f.Science.Source(s.Id));
    }
    [Fact] public async Task SourceDeletionPurgesOwnedUnindexedTilePartialAndRegisteredRecoveryValues()
    {
        using var f=new TerrainSurveyFixture();var geometry=Flight(f);var source=await Source(f);var other=await Source(f,values:(x,y)=>999);var job=Create(f,geometry,Choice(source),Choice(other));Run(f,job);
        var tile=f.Science.Artifact(f.Science.TerrainCheckpoints(job.Id).First(t=>t.SourceId==source.Id).ArtifactId);var otherTile=f.Science.Artifact(f.Science.TerrainCheckpoints(job.Id).First(t=>t.SourceId==other.Id).ArtifactId);
        var recovery=f.Science.CreateRecoveryBackup(Path.Combine(f.Root,"registered-recovery"));var relative=$"Products/{f.Id:N}/Derived/{Guid.NewGuid():N}.json";var orphan=Path.Combine(f.Science.DirectoryPath,relative);var bytes=File.ReadAllBytes(f.Science.VerifiedArtifactPath(tile));File.WriteAllBytes(orphan,bytes);File.WriteAllBytes(orphan+".partial",bytes[..^10]);
        var ledger=f.Science.BeginManagedWrite(TerrainElevations.SourceWriteOperation(source.Id),[relative,relative+".partial"]);
        var preview=f.Science.PreviewSourceDeletion(new([source.Id]),f.Missions);Assert.Equal(1,preview.EligibleSources);var coordinator=new MissionCoordinator(f.Missions,1000);var result=f.Science.DeleteExternalSources(new(preview.Id,preview.SelectionHash),f.Missions,coordinator.EnterMaintenance);Assert.True(result.State=="Completed",result.Warning);
        Assert.False(File.Exists(orphan));Assert.False(File.Exists(orphan+".partial"));Assert.False(File.Exists(ledger));Assert.False(File.Exists(Path.Combine(recovery,tile.RelativePath)));Assert.True(File.Exists(Path.Combine(recovery,otherTile.RelativePath)));Assert.True(File.Exists(f.Science.VerifiedArtifactPath(otherTile)));Assert.NotNull(f.Science.Source(other.Id));
    }
}
