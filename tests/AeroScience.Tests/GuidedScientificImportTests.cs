using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed class GuidedScientificImportTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-guided-"+Guid.NewGuid().ToString("N"));
    private string Cube(string layout="bsq",int channels=224)
    {
        var path=Path.Combine(root,layout+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,"cube.hdr"),"ENVI\nsamples = 4\nlines = 3\nbands = "+channels+"\ndata type = 4\ninterleave = "+layout+"\nbyte order = 0\nmap info = {UTM, 1, 1, 499680, 4428000, 10, 10, 18, North, WGS-84}\nwavelength units = Nanometers\nwavelength = {"+string.Join(",",Enumerable.Range(0,channels).Select(i=>400+5*i))+"}\nbbl = {"+string.Join(",",Enumerable.Range(0,channels).Select(i=>i==5?0:1))+"}\nreflectance scale factor = 10000\ndata ignore value = -9999\n");
        using var f=File.Create(Path.Combine(path,"cube.img"));using var w=new BinaryWriter(f);
        if(layout=="bsq")for(var b=0;b<channels;b++)for(var y=0;y<3;y++)for(var x=0;x<4;x++)w.Write((float)(1000+b));
        else if(layout=="bil")for(var y=0;y<3;y++)for(var b=0;b<channels;b++)for(var x=0;x<4;x++)w.Write((float)(1000+b));
        else for(var y=0;y<3;y++)for(var x=0;x<4;x++)for(var b=0;b<channels;b++)w.Write((float)(1000+b));
        return path;
    }
    [Theory][InlineData("bsq")][InlineData("bil")][InlineData("bip")]
    public async Task HundredsOfOriginalBandsAreAutomaticallyManifestedSampledAndReopenedOffline(string layout)
    {
        var path=Cube(layout);var original=File.ReadAllBytes(Path.Combine(path,"cube.img"));var inspection=GuidedHyperspectralImport.Inspect(path);
        Assert.Equal(224,inspection.BandCount);Assert.Equal(400,inspection.MinimumWavelengthNm);Assert.Equal(1515,inspection.MaximumWavelengthNm);Assert.Equal(1,inspection.BadBands);Assert.Null(inspection.ObservationUtc);Assert.NotNull(inspection.Manifest);Assert.Equal(.0001,inspection.Manifest.Bands[0].Scale);
        var storage=new StorageManager(new PreferenceStore(Path.Combine(root,"settings"))).Initialize();using var science=new ScienceStore(storage.ScienceLibraryDirectory);science.Initialize();
        var imported=await science.ImportSourceAsync(inspection.Manifest,inspection.Root,reuseExisting:true);var again=await science.ImportSourceAsync(inspection.Manifest,inspection.Root,reuseExisting:true);Assert.Equal(imported.Id,again.Id);Assert.Single(science.Sources());
        var g=inspection.Grid!;var point=ScientificRaster.Unproject(g.OriginX+g.PixelWidth*1.5,g.OriginY+g.PixelHeight*1.5,g.Crs);
        var available=science.Availability(point).Single();Assert.True(available.CoversPoint);Assert.True(available.FilesAvailable);
        using var raster=new ScientificRaster(Path.Combine(science.DirectoryPath,imported.RelativeDirectory),imported.Manifest.Bands[100]);Assert.Equal(.11,raster.Read(point)!.Value,8);
        using var bad=new ScientificRaster(Path.Combine(science.DirectoryPath,imported.RelativeDirectory),imported.Manifest.Bands[5]);Assert.Null(bad.Read(point));
        Assert.Equal(original,File.ReadAllBytes(Path.Combine(path,"cube.img")));Assert.False(File.Exists(Path.Combine(path,"source.json")));Assert.Empty(science.PendingArtifacts().Artifacts);
        Assert.False(science.Availability(new(0,0)).Single().CoversPoint);Assert.NotNull(ScientificSourceSelection.Select(science.Availability(point),RemoteKind.Hyperspectral,new()));
    }
    [Fact]public void RotatedEnviUsesExplicitCornerConventionAndKeepsOriginalDetectorChannels()
    {
        var path=Cube();var header=Path.Combine(path,"cube.hdr");var text=File.ReadAllText(header).Replace("18, North, WGS-84}","18, North, WGS-84, rotation=10}").Replace("400,405,410","410,405,400");File.WriteAllText(header,text);
        var inspection=GuidedHyperspectralImport.Inspect(path);Assert.NotNull(inspection.Manifest);Assert.Equal(2,inspection.Manifest.Bands[0].Channel);Assert.Equal(0,inspection.Manifest.Bands[2].Channel);Assert.Equal(400,inspection.Manifest.Bands[0].WavelengthNm);
        Assert.InRange(inspection.Grid!.RowX,1.7364,1.7365);Assert.Equal(499680,inspection.Grid.OriginX);Assert.Equal(4428000,inspection.Grid.OriginY);Assert.Contains(inspection.Warnings,w=>w.Contains("Detector wavelength overlaps"));
        var center=ScientificRaster.GridPoint(inspection.Grid,1.5,1.5);using var raster=new ScientificRaster(path,inspection.Manifest.Bands[0]);Assert.Equal(.1002,raster.Read(center)!.Value,8);
        // Legacy archives explicitly retain their old reader convention; no historical product is rewritten.
        Assert.Throws<InvalidDataException>(()=>new ScientificRaster(path,inspection.Manifest.Bands[0] with{EnviMapReference="legacy-pixel-center"}));
    }
    [Fact]public async Task TinyLocalHyperspectralCoverageUsesPredictedPointOfflineAndDoesNotClaimAnEntireCell()
    {
        var folder=Cube();var inspected=GuidedHyperspectralImport.Inspect(folder);var preferences=new PreferenceStore(Path.Combine(root,"offline-settings"));var storage=new StorageManager(preferences).Initialize();using var science=new ScienceStore(storage.ScienceLibraryDirectory);science.Initialize();
        var source=await science.ImportSourceAsync(inspected.Manifest!,inspected.Root);var point=ScientificRaster.GridPoint(inspected.Grid!,1.5,1.5);var area=GeographicBounds.Around(point);
        using var automatic=new AutomaticScientificData(science,new(preferences.DirectoryPath));automatic.Prepare(area,[RemoteKind.Hyperspectral],samplePoint:point);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(automatic.Snapshot().Single().Status=="Queued")await Task.Delay(10,timeout.Token);
        var state=automatic.Snapshot().Single();Assert.Equal("Partially usable",state.Status);Assert.Equal(source.Id,state.SourceId);Assert.Equal(4,state.SourceCoverage!.Length);Assert.Equal(0,automatic.QueueSummary().HourlyBytes);
        var outside=area.Corners[0];automatic.Prepare(area,[RemoteKind.Hyperspectral],samplePoint:outside);while(automatic.Snapshot().Single().Status=="Queued")await Task.Delay(10,timeout.Token);Assert.Equal("Dataset Outside Coverage",automatic.Snapshot().Single().Status);
    }
    [Fact]public void HeaderUnitsCannotBeRelabeledAndAnUnzonedDateIsNotInventedAsUtc()
    {
        var folder=Cube();Assert.Throws<InvalidDataException>(()=>GuidedHyperspectralImport.Inspect(folder,new(Unit:"radiance W/(m2 sr um)")));
        var header=Path.Combine(folder,"cube.hdr");File.AppendAllText(header,"acquisition time = 2020-01-02T03:04:05\n");Assert.Null(GuidedHyperspectralImport.Inspect(folder).ObservationUtc);
        File.WriteAllText(header,File.ReadAllText(header).Replace("2020-01-02T03:04:05","2020-01-02T03:04:05Z"));Assert.Equal(DateTimeOffset.Parse("2020-01-02T03:04:05Z"),GuidedHyperspectralImport.Inspect(folder).ObservationUtc);
    }
    [Fact]public void AmbiguousAndMissingBinariesNeverGuessAnAssociation()
    {
        var path=Cube();File.Copy(Path.Combine(path,"cube.img"),Path.Combine(path,"cube.dat"));var inspection=GuidedHyperspectralImport.Inspect(path);Assert.Null(inspection.Binary);Assert.Equal(2,inspection.Candidates.Length);Assert.Null(inspection.Manifest);
        Assert.NotNull(GuidedHyperspectralImport.Inspect(path,associated:"cube.img").Manifest);Assert.Throws<InvalidDataException>(()=>GuidedHyperspectralImport.Inspect(path,associated:"../other.img"));
        File.Delete(Path.Combine(path,"cube.img"));File.Delete(Path.Combine(path,"cube.dat"));Assert.Null(GuidedHyperspectralImport.Inspect(path).Binary);
    }
    [Theory][InlineData("wavelength units = Nanometers","wavelength units = Unknown")][InlineData("bbl = {1,1,1,1,1,0,1","bbl = {1,1,1,1,1,2,1")][InlineData("UTM, 1, 1","UnknownCRS, 1, 1")][InlineData("wavelength = {","missing wavelengths = {")]
    public void CorruptScientificMetadataIsRejectedWithoutInventingValues(string from,string to)
    {var path=Cube();var header=Path.Combine(path,"cube.hdr");File.WriteAllText(header,File.ReadAllText(header).Replace(from,to));Assert.Throws<InvalidDataException>(()=>GuidedHyperspectralImport.Inspect(path));}
    [Fact]public async Task CancelledImportsAndStorageFailuresKeepExternalOriginalsAndNeverPublishPartialSources()
    {
        var path=Cube();var inspection=GuidedHyperspectralImport.Inspect(path);var storage=new StorageManager(new PreferenceStore(Path.Combine(root,"settings"))).Initialize();using var science=new ScienceStore(storage.ScienceLibraryDirectory);science.Initialize();
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>science.ImportSourceAsync(inspection.Manifest!,inspection.Root,cancelled.Token));Assert.Empty(science.Sources());Assert.True(File.Exists(Path.Combine(path,"cube.img")));
        var missions=new MissionStore(Path.Combine(storage.MissionDirectory,"missions.db"));missions.Initialize();var management=new StorageManagement(root,missions,science,new FullDisk());science.Management=management;management.Configure(new(null,null));await Assert.ThrowsAsync<StorageLimitException>(()=>science.ImportSourceAsync(inspection.Manifest!,inspection.Root));Assert.Empty(science.Sources());
    }
    [Fact]public async Task LocalTerrainThermalAndMultispectralCollectionsGenerateManifestsWithoutJsonEditing()
    {
        var terrain=RemoteFixtureFiles.Create(Path.Combine(root,"terrain"),RemoteKind.Terrain);var first=GuidedScientificImport.Inspect(Path.Combine(Path.GetDirectoryName(terrain)!,"elevation.tif"),RemoteKind.Terrain);Assert.Null(first.Manifest);
        var elevation=GuidedScientificImport.Inspect(Path.Combine(Path.GetDirectoryName(terrain)!,"elevation.tif"),RemoteKind.Terrain,new(Unit:"m",VerticalDatum:"Unknown"));Assert.NotNull(elevation.Manifest);Assert.Null(elevation.ObservationUtc);
        var thermal=RemoteFixtureFiles.Create(Path.Combine(root,"thermal"),RemoteKind.Thermal);var temperature=GuidedScientificImport.Inspect(Path.GetDirectoryName(thermal)!,RemoteKind.Thermal,new(SceneId:"L2SP-test-scene"));Assert.NotNull(temperature.Manifest);Assert.Equal(.00341802,temperature.Manifest.Bands[0].Scale);Assert.Contains(temperature.Manifest.Bands,b=>b.Id=="QA_PIXEL");
        var multi=RemoteFixtureFiles.Create(Path.Combine(root,"multi"),RemoteKind.Multispectral);Assert.Null(GuidedScientificImport.Inspect(Path.GetDirectoryName(multi)!,RemoteKind.Multispectral).Manifest);
        var reflectance=GuidedScientificImport.Inspect(Path.GetDirectoryName(multi)!,RemoteKind.Multispectral,new(SceneId:"S2-test-scene",Scale:.0001,Offset:-.1));Assert.NotNull(reflectance.Manifest);Assert.Equal(-.1,reflectance.Manifest.Bands[0].Offset);Assert.Equal(0,reflectance.Manifest.Bands.Last().Offset);
        var storage=new StorageManager(new PreferenceStore(Path.Combine(root,"settings"))).Initialize();using var science=new ScienceStore(storage.ScienceLibraryDirectory);science.Initialize();foreach(var i in new[]{elevation,temperature,reflectance})await science.ImportSourceAsync(i.Manifest!,i.Root);Assert.Equal(3,science.Sources().Length);
    }
    [Fact]public void IrrelevantPatternDimensionsDoNotBlockValidGeometry()
    {
        var orbit=new PatternDefinition(Guid.NewGuid(),FlightPatternKind.Orbit,new(40,-75),LengthMetres:-1,WidthMetres:-1,SpacingMetres:-1,RadiusMetres:1000,Legs:0);
        Assert.NotEmpty(FlightPatterns.Generate(orbit).Waypoints);
        var lawn=new PatternDefinition(Guid.NewGuid(),FlightPatternKind.Lawnmower,new(40,-75),RadiusMetres:-1,Orbits:0);Assert.NotEmpty(FlightPatterns.Generate(lawn).Waypoints);
    }
    private sealed class FullDisk:IStorageCapacity{public long FreeBytes(string directory)=>0;}
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
