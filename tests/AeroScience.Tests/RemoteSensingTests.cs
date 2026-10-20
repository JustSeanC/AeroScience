using System.Net;
using System.Text;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
using Xunit.Abstractions;

namespace AeroScience.Tests;

// A bounded acquisition deadline must not measure unrelated parallel SQLite/raster
// fixture contention. Explicit worker/recorder concurrency remains covered separately.
[Collection(ScientificIoCollection.Name)]
public sealed class RemoteSensingTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-remote-"+Guid.NewGuid().ToString("N"));
    private readonly MissionStore missions;
    private readonly ScienceStore science;
    private readonly ResearchOperations research;
    private readonly MissionCoordinator coordinator;
    private readonly ITestOutputHelper output;
    public RemoteSensingTests(ITestOutputHelper output)
    {
        this.output = output;
        var settings=new StorageManager(new PreferenceStore(root)).Initialize();missions=new(Path.Combine(settings.MissionDirectory,"missions.db"));missions.Initialize();science=new(settings.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;research=new(missions,science);coordinator=new(missions,1000,research:research);
    }
    private static TelemetryFrame Frame(DateTimeOffset? time=null)=>new(time??DateTimeOffset.UtcNow,TelemetrySource.Simulator,"Fixture aircraft",40,-75,1000,328.0839895,140,150,160,0,0,0,0,0,MachNumber:.25);
    private void Apply(TelemetryFrame frame)=>coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulator,"Isolated fixture; no MSFS connection",frame.TimestampUtc),frame,new(frame.TimestampUtc,15,29,5,90,.002)));
    private async Task<RemoteProduct> Acquire(RemoteKind kind,bool cloudy=false)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var manifest=RemoteFixtureFiles.Create(Path.Combine(root,"input-"+Guid.NewGuid()),kind,cloudy);await science.ImportSourceAsync(manifest);
        output.WriteLine($"{kind}: fixture import completed at {clock.Elapsed}.");
        var id=RemoteAcquisition.Instruments.Single(p=>p.Value==kind).Key;var mission=coordinator.Start(new("Remote fixture",ScienceMode.Realistic,Instruments:[new(id,true)]));Apply(Frame());
        output.WriteLine($"{kind}: Apply returned at {clock.Elapsed}; waiting for actual publication.");
        await Wait(()=>science.Products(mission.Id).Length>0||coordinator.Snapshot().Research!.Instruments.Single(i=>i.InstrumentId==id).Quality==ObservationQuality.Unavailable);
        output.WriteLine($"{kind}: publication completed at {clock.Elapsed}.");
        return science.Products(mission.Id).Single();
    }
    private async Task Wait(Func<bool> predicate)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { while(!predicate())await Task.Delay(10,timeout.Token); }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The 10-second synthetic acquisition deadline elapsed. Worker state: "
                + JsonSerializer.Serialize(coordinator.Snapshot().Research?.Instruments, WireJson.Options));
        }
    }
    [Theory][InlineData(RemoteKind.Thermal)][InlineData(RemoteKind.Multispectral)][InlineData(RemoteKind.Hyperspectral)][InlineData(RemoteKind.Terrain)]
    public async Task ActualFileReadersProduceReproducibleProductsWithSeparateDatesAndPortableEvidence(RemoteKind kind)
    {
        var product=await Acquire(kind);var data=science.ReadProduct(product.Id);
        Assert.Equal(576,data.SampleLocations.Length);Assert.Equal(ScienceMode.Realistic,data.Evidence!.Mode);Assert.Equal("Fixture aircraft",data.Evidence.Aircraft.Aircraft);Assert.Equal(.25,data.Evidence.Aircraft.MachNumber);
        Assert.Equal(2020,product.SourceObservationUtc!.Value.Year);Assert.True(product.FlightPacketUtc>product.SourceObservationUtc);Assert.True(product.ProcessedUtc>=product.FlightPacketUtc);Assert.Equal(64,product.Sha256.Length);Assert.True(product.Bytes>0);
        if(kind==RemoteKind.Thermal)Assert.All(data.Derived["temperature"],v=>Assert.Equal(285.7208,v!.Value,4));
        if(kind==RemoteKind.Multispectral){Assert.All(data.Derived["ndvi"],v=>Assert.Equal(.5,v!.Value,6));Assert.NotEqual(data.Bands.Single(b=>b.Id=="B04").OriginalGrid.PixelWidth,data.Bands.Single(b=>b.Id=="SCL").OriginalGrid.PixelWidth);}
        if(kind==RemoteKind.Hyperspectral){Assert.Equal(400,data.Bands[0].WavelengthNm);Assert.Equal(.1,data.Bands[0].Values[0]!.Value,6);Assert.All(data.Bands[5].Values,Assert.Null);Assert.Contains("reflectance",data.Bands[0].Unit);}
        if(kind==RemoteKind.Terrain){Assert.All(data.Derived["elevation"],v=>Assert.Equal(123.25,v));Assert.Contains("not measured LiDAR",product.Provenance);Assert.Contains("NAVD88",data.Source.Manifest.VerticalDatum);}
        foreach(var format in new[]{"json","evidence.json","csv","geojson","gpx","kml"})Assert.NotEmpty(RemoteExports.Content(data,format));
        Assert.Contains("not tamper-proof",RemoteExports.Content(data,"evidence.json"));
        coordinator.End(product.MissionId);Assert.Single(science.Products(product.MissionId));Assert.NotEmpty(ResearchExports.Report(missions,product.MissionId));
    }
    [Fact]public async Task InvalidNdviDoesNotDiscardValidOriginalReflectance()
    {
        var path=RemoteFixtureFiles.Create(Path.Combine(root,"negative-reflectance"),RemoteKind.Multispectral);var source=ScienceStore.ReadSourceManifest(path);
        File.WriteAllText(path,JsonSerializer.Serialize(source with{Bands=source.Bands.Select(b=>b.Id=="B04"?b with{Offset=-.3}:b).ToArray()},WireJson.Options));
        await science.ImportSourceAsync(path);var mission=coordinator.Start(new("Real numeric reflectance, invalid index",Instruments:[new("multispectral-imager",true)]));Apply(Frame());
        await Wait(()=>science.Products(mission.Id).Length==1);var data=science.ReadProduct(science.Products(mission.Id).Single().Id);
        Assert.All(data.Derived["ndvi"],Assert.Null);Assert.All(data.Bands.Single(b=>b.Id=="B04").Values,v=>Assert.Equal(-.1,v!.Value,6));
    }
    [Fact]public async Task CloudMasksYieldUnavailableRatherThanFabricatedThermalProducts()
    {
        await science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,"cloud"),RemoteKind.Thermal,true));var mission=coordinator.Start(new("Cloud",Instruments:[new("thermal-imager",true)]));Apply(Frame());
        await Wait(()=>coordinator.Snapshot().Research!.Instruments.Single(i=>i.InstrumentId=="thermal-imager").Quality==ObservationQuality.Unavailable);
        Assert.Empty(science.Products(mission.Id));Assert.Single(missions.StreamSamples(mission.Id));Assert.Contains("All sampled pixels",coordinator.Snapshot().Research!.Instruments.Single(i=>i.InstrumentId=="thermal-imager").Message);
    }
    [Fact]public async Task SciencePauseAndDuplicateFramesDoNotCreateFakeProductsOrUnexpectedGaps()
    {
        var product=await Acquire(RemoteKind.Terrain);var id=product.MissionId;var frame=Frame(product.FlightPacketUtc);Apply(frame);Assert.Single(science.Products(id));
        coordinator.SetSciencePaused(id,true);Apply(Frame(product.FlightPacketUtc.AddSeconds(1)));await Task.Delay(100);Assert.Single(science.Products(id));
        coordinator.SetSciencePaused(id,false);Apply(Frame(DateTimeOffset.UtcNow.AddSeconds(1)));await Wait(()=>science.Products(id).Length==2);
        Assert.Single(science.Products(id).Select(p=>p.DatasetId).Distinct());Assert.Equal(0,coordinator.Snapshot().Research!.Instruments.Single(i=>i.InstrumentId=="terrain-scanner").MissingIntervals);
    }
    [Fact]public void FootprintsRotateAndRemainGeodesicAtDatelineAndPoles()
    {
        var frame=Frame();var level=SensorGeometry.Footprint(frame);Assert.InRange(Geodesy.Distance(level.Center,new(40,-75)),0,.01);
        Assert.True(SensorGeometry.Footprint(frame with{PitchDegrees=10}).Center.Latitude>40);Assert.True(SensorGeometry.Footprint(frame with{PitchDegrees=-10}).Center.Latitude<40);
        Assert.True(SensorGeometry.Footprint(frame with{BankDegrees=10}).Center.Longitude< -75);Assert.True(SensorGeometry.Footprint(frame with{PitchDegrees=10,HeadingDegrees=90}).Center.Longitude> -75);
        Assert.Throws<InvalidOperationException>(()=>SensorGeometry.Footprint(frame with{AltitudeAglFeet=null}));Assert.Throws<InvalidOperationException>(()=>SensorGeometry.Footprint(frame with{BankDegrees=89}));
        foreach(var point in new[]{new Coordinates(0,179.9999),new Coordinates(89.9,0)})Assert.All(SensorGeometry.Footprint(frame with{Latitude=point.Latitude,Longitude=point.Longitude}).Boundary,p=>Assert.True(MissionPlanning.Position(p.Latitude,p.Longitude)));
        var utm=ScientificRaster.Project(new(0,3),"EPSG:32631");Assert.InRange(Math.Abs(utm.X-500000),0,.001);Assert.InRange(Math.Abs(utm.Y),0,.001);Assert.Throws<InvalidDataException>(()=>ScientificRaster.Project(new(40,-75),"EPSG:26918"));
    }
    [Fact]public async Task UnsafeFilesUnsupportedCrsMalformedMetadataAndChangedProductsAreRejected()
    {
        var qualityPath=RemoteFixtureFiles.Create(Path.Combine(root,"bad-quality"),RemoteKind.Thermal);var quality=ScienceStore.ReadSourceManifest(qualityPath);File.WriteAllText(qualityPath,JsonSerializer.Serialize(quality with{Bands=quality.Bands.Select(b=>b.Id=="QA_PIXEL"?b with{Scale=.0001}:b).ToArray()},WireJson.Options));Assert.Throws<InvalidDataException>(()=>ScienceStore.ReadSourceManifest(qualityPath));
        Assert.Throws<InvalidDataException>(()=>ScientificRaster.SafeFile(root,"../missions.db"));var path=RemoteFixtureFiles.Create(Path.Combine(root,"bad"),RemoteKind.Terrain);var source=ScienceStore.ReadSourceManifest(path);
        File.WriteAllText(path,JsonSerializer.Serialize(source with{Bands=[source.Bands[0] with{File="../missions.db"}]},WireJson.Options));Assert.Throws<InvalidDataException>(()=>ScienceStore.InspectImport(path));
        var product=await Acquire(RemoteKind.Terrain);File.AppendAllText(Path.Combine(science.DirectoryPath,product.RelativePath),"tamper");Assert.Throws<IOException>(()=>science.ReadProduct(product.Id));
    }
    [Fact]public async Task ProtectedCacheAndOriginalSourcesSurviveCleanupAndExplicitClearRequiresConfirmation()
    {
        var product=await Acquire(RemoteKind.Terrain);coordinator.End(product.MissionId);var management=new StorageManagement(root,missions,science);science.Management=management;
        await using var cache=new ScientificCache(science,missions,management);var png=cache.PreviewPng(product.Id);Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},png[..8]);
        missions.Protect(product.MissionId,true);var protectedPreview=cache.Preview();Assert.Equal(0,protectedPreview.EligibleFiles);Assert.Equal("Protected",protectedPreview.Items.Single().Status);
        Assert.Throws<InvalidOperationException>(()=>cache.Clean(protectedPreview.Id,false));missions.Protect(product.MissionId,false);
        var preview=cache.Preview();Assert.Equal(1,preview.EligibleFiles);science.ProtectDataset(product.DatasetId,true);var skipped=cache.Clean(preview.Id,true);Assert.Equal(0,skipped.Removed);Assert.Equal(1,skipped.Skipped);
        science.ProtectDataset(product.DatasetId,false);var eligible=cache.Preview();var result=cache.Clean(eligible.Id,true);Assert.Equal(1,result.Removed);Assert.Equal(png.Length,result.ActualBytesRemoved);Assert.Single(science.Products(product.MissionId));Assert.Single(science.Sources());Assert.NotEmpty(cache.History());
        var source=science.Sources().Single();var original=Path.Combine(science.DirectoryPath,source.RelativeDirectory,source.Files[0].RelativePath);Assert.True(File.Exists(original));
        Assert.Throws<InvalidOperationException>(()=>science.Clear("wrong",false));science.Clear("DELETE SCIENCE DATA",false);Assert.True(File.Exists(original));Assert.Empty(science.Products());Assert.Single(science.Sources());
    }
    [Fact]public async Task QuotasPreserveHistoryAndStopFlightWithAnExplicitReasonWhileScienceIsIndependent()
    {
        var management=new StorageManagement(root,missions,science);Assert.Equal(25000000000,management.Preferences.MissionLimitBytes);Assert.Equal(50000000000,management.Preferences.ScienceLimitBytes);
        management.Configure(new(2000000000,null));Assert.Equal(2000000000,new StorageManagement(root,missions,science).Preferences.MissionLimitBytes);
        var c=new MissionCoordinator(missions,1000,ensureRecordingCapacity:()=>management.RequireFlightCapacity());var mission=c.Start("Quota");var f=Frame();c.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulator,"fixture",f.TimestampUtc),f));
        management.Configure(new(StorageManager.DatabaseBytes(missions.DatabasePath)+1,1000000000));c.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulator,"fixture",f.TimestampUtc),f with{TimestampUtc=f.TimestampUtc.AddSeconds(1)}));
        Assert.Null(c.Snapshot().ActiveMission);Assert.Equal("storageLimitReached",c.Snapshot().RecordingStop!.Code);Assert.Single(missions.StreamSamples(mission.Id));Assert.Equal(MissionStatus.Interrupted,missions.Detail(mission.Id)!.Mission.Status);
        Assert.Equal("storageLimitReached",new MissionCoordinator(missions,1000).Snapshot().RecordingStop!.Code);
        management.Configure(new(null,null));Assert.Null(new StorageManagement(root,missions,science).Preferences.ScienceLimitBytes);management.RequireFlightCapacity();await Task.CompletedTask;
        management.Configure(new(null,1000000));using(var budget=management.ReserveScience(700000)){Assert.Throws<StorageLimitException>(()=>management.ReserveScience(700000));Assert.Throws<InvalidOperationException>(()=>management.Configure(new(null,500000)));}management.RequireScienceCapacity(700000);
    }
    [Fact]public async Task SourceChecksumAndLibraryMigrationKeepStableProductAndProtectionReferences()
    {
        var product=await Acquire(RemoteKind.Terrain);coordinator.End(product.MissionId);science.ProtectDataset(product.DatasetId,true);missions.Protect(product.MissionId,true);research.Dispose();science.Dispose();
        var preferences=new PreferenceStore(root);var destination=Path.Combine(root,"moved-library");new StorageManager(preferences).Migrate(StorageKind.ScienceLibrary,destination,true,false);
        using var moved=new ScienceStore(destination);Assert.Equal(product.Sha256,moved.ReadProduct(product.Id).Product.Sha256);Assert.True(moved.DatasetProtected(product.DatasetId));Assert.True(missions.IsProtected(product.MissionId));Assert.Equal(product.LibraryId,moved.LibraryId);
    }
    [Fact]public async Task FreshOnlineDefaultWaitsForPrivacyAndUpgradesRetainOfflineChoices()
    {
        var fresh=new MapSettings(Path.Combine(root,"new"));Assert.True(fresh.Get().Preferences.OnlineEnabled);Assert.Equal("streets",fresh.Get().Preferences.Layer);Assert.True(fresh.Get().RequiresPrivacyChoice);Assert.Empty((await fresh.Client()).Tiles);
        fresh.ChoosePrivacy(false);Assert.False(new MapSettings(Path.Combine(root,"new")).Get().Preferences.OnlineEnabled);
        fresh.InitializeDefaults(false);Assert.False(fresh.Get().Preferences.OnlineEnabled);
        var legacy=new MapSettings(Path.Combine(root,"legacy"));legacy.InitializeDefaults(true);Assert.Equal("offline",legacy.Get().Preferences.Layer);Assert.False(legacy.Get().RequiresPrivacyChoice);
        var online=new MapSettings(Path.Combine(root,"online"));online.ChoosePrivacy(true);Assert.Single((await online.Client()).Tiles);Assert.Equal("streets",online.Get().Preferences.Layer);
    }
    [Fact]public async Task InterruptedWorkIsCountedAndCorruptLimitsDoNotHideOfflineHistory()
    {
        var product=await Acquire(RemoteKind.Terrain);coordinator.End(product.MissionId);
        var id=Guid.NewGuid();var relative=$"Sources/{id:N}.partial/source.bin";var file=Path.Combine(science.DirectoryPath,relative);Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllBytes(file,new byte[12345]);
        var ledger=Path.Combine(science.DirectoryPath,"Metadata","Work",id.ToString("N")+".json");Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);File.WriteAllText(ledger,JsonSerializer.Serialize(new ManagedScientificWork(id,DateTimeOffset.UtcNow,"Interrupted test import",[relative]),WireJson.Options));
        File.WriteAllText(Path.Combine(root,"storage-limits.json"),"broken json");var management=new StorageManagement(root,missions,science);var status=management.Snapshot(true);
        Assert.Equal(12345,status.Science.PendingBytes);Assert.Equal(1,status.Science.PendingOperations);Assert.NotNull(status.Flight.Error);Assert.NotNull(missions.Detail(product.MissionId));Assert.NotNull(science.ReadProduct(product.Id));
        Assert.Throws<IOException>(()=>management.RequireScienceCapacity());Assert.Throws<IOException>(()=>management.RequireFlightCapacity());management.Configure(new());management.RequireFlightCapacity();Assert.True(File.Exists(file));
    }
    [Fact]public async Task CleanupRetainsChangedFilesReportsFailuresAndNeverDeletesNewUnconfirmedCaches()
    {
        var product=await Acquire(RemoteKind.Terrain);var management=new StorageManagement(root,missions,science);science.Management=management;
        await using var cache=new ScientificCache(science,missions,management);cache.PreviewPng(product.Id);var preview=cache.Preview();Assert.Single(preview.Items);Assert.True(preview.EstimatedBytes>0);Assert.InRange(preview.Items[0].AgeDays,0,1);Assert.NotNull(preview.OldestUtc);
        var path=Path.Combine(science.DirectoryPath,preview.Items[0].Cache.RelativePath);File.AppendAllText(path,"changed");var skipped=cache.Clean(preview.Id,true);Assert.Equal(1,skipped.Skipped);Assert.True(File.Exists(path));
        cache.PreviewPng(product.Id);await using var denied=new ScientificCache(science,missions,management,_=>throw new UnauthorizedAccessException("Injected permission failure"));var failed=denied.Clean(denied.Preview().Id,true);Assert.Equal(1,failed.Failed);Assert.Equal(0,failed.ActualBytesRemoved);
        var original=cache.Preview();Apply(Frame(DateTimeOffset.UtcNow.AddSeconds(2)));await Wait(()=>science.Products().Length==2);var second=science.Products().Single(p=>p.Id!=product.Id);cache.PreviewPng(second.Id);var newCache=science.CacheEntries().Single(c=>c.ProductId==second.Id);
        var clean=cache.Clean(original.Id,true);Assert.Equal(1,clean.Removed);Assert.True(File.Exists(Path.Combine(science.DirectoryPath,newCache.RelativePath)));Assert.True(File.Exists(path));
        var page=JsonSerializer.Serialize(cache.Page(cache.Preview().Id,0,"mission",second.MissionId.ToString()),WireJson.Options);Assert.Contains(second.DatasetId.ToString(),page);
    }
    [Fact]public async Task MateriallyChangedCleanupPreviewRequiresRefreshAndCompactionRequiresStoppedConfirmation()
    {
        var product=await Acquire(RemoteKind.Terrain);coordinator.End(product.MissionId);var management=new StorageManagement(root,missions,science);science.Management=management;
        await using var cache=new ScientificCache(science,missions,management);cache.PreviewPng(product.Id);var preview=cache.Preview();management.AddedScienceFile(2097152);
        Assert.Throws<InvalidOperationException>(()=>cache.Clean(preview.Id,true));Assert.Single(science.CacheEntries());
        var storage=new StorageManager(new PreferenceStore(root));Assert.Throws<InvalidOperationException>(()=>storage.Compact(StorageKind.Missions,false,false));Assert.Throws<InvalidOperationException>(()=>storage.Compact(StorageKind.Missions,true,true));
        research.Dispose();science.Dispose();var result=storage.Compact(StorageKind.Missions,true,false);Assert.Contains("Compaction verified",result);Assert.NotNull(missions.Detail(product.MissionId));
    }
    [Fact]public async Task ScientificLimitSuspendsOnlyScienceAndPreservesCompletedProductsAndFlightRecording()
    {
        var product=await Acquire(RemoteKind.Terrain);var management=new StorageManagement(root,missions,science);science.Management=management;var used=management.Snapshot(true).Science.UsedBytes;management.Configure(new(null,used+1,AutomaticCacheCleanup:false));
        Apply(Frame(DateTimeOffset.UtcNow.AddSeconds(2)));await Wait(()=>coordinator.Snapshot().Research!.Instruments.Single(i=>i.InstrumentId=="terrain-scanner").Quality==ObservationQuality.Unavailable);
        Assert.Single(science.Products());Assert.NotNull(science.ReadProduct(product.Id));Assert.NotNull(coordinator.Snapshot().ActiveMission);Assert.Equal(2,missions.StreamSamples(product.MissionId).Count());Assert.Contains("storage limit",coordinator.Snapshot().Research!.Instruments.Single(i=>i.InstrumentId=="terrain-scanner").Message);
    }
    public void Dispose(){research.Dispose();science.Dispose();if(Directory.Exists(root))Directory.Delete(root,true);}
}
