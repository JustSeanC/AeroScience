using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

// Recorded real STAC metadata tests parser/scaling regressions. Synthetic TIFFs
// and HTTP handlers below test failure boundaries; neither establishes online acceptance.
public sealed class AutomaticScientificDataTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-auto-test-"+Guid.NewGuid().ToString("N"));
    private readonly MissionStore missions;private readonly ScienceStore science;private readonly StorageManagement management;
    public AutomaticScientificDataTests()
    {
        var p=new StorageManager(new PreferenceStore(root)).Initialize();missions=new(Path.Combine(root,"missions.db"));missions.Initialize();science=new(p.ScienceLibraryDirectory);science.Initialize();management=new(root,missions,science);science.Management=management;
    }
    private sealed class Handler(Func<HttpRequestMessage,int,HttpResponseMessage> send):HttpMessageHandler
    {
        public int Count;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();return Task.FromResult(send(request,Interlocked.Increment(ref Count)));}
    }
    private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private static JsonNode Metadata(string name)=>JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata",name+".json")))!;
    private static ScientificAccessPreferences Preferences=>new(true,true,PreferredDateUtc:DateTimeOffset.Parse("2024-10-01T00:00:00Z"),LookbackDays:365);
    private static GeographicBounds Area=>new(-75.01,39.99,-74.99,40.01);
    private static HttpResponseMessage Range(HttpRequestMessage request,byte[] bytes,string etag="\"fixture-v1\"")
    {
        var from=request.Headers.Range!.Ranges.Single().From!.Value;var to=Math.Min(bytes.Length-1,request.Headers.Range.Ranges.Single().To!.Value);
        var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new ByteArrayContent(bytes[(int)from..((int)to+1)])};
        response.Content.Headers.ContentRange=new ContentRangeHeaderValue(from,to,bytes.Length);response.Headers.ETag=new(etag);return response;
    }
    [Fact]public void ScientificConsentIsIndependentPersistentAndFailClosed()
    {
        var settings=new ScientificAccessSettings(root);Assert.False(settings.Get().Preferences.OnlineEnabled);Assert.False(settings.Get().Preferences.PrivacyAccepted);
        Assert.Throws<ArgumentException>(()=>settings.Save(new(true,false)));settings.Save(new(true,true,UnmeteredOnly:true));
        Assert.True(new ScientificAccessSettings(root).Get().Preferences.UnmeteredOnly);Assert.Null(settings.IsUnmetered());
        File.WriteAllText(Path.Combine(root,"scientific-access.json"),"broken");Assert.False(settings.Get().Preferences.OnlineEnabled);Assert.NotNull(settings.Get().Warning);
        Assert.False(File.Exists(Path.Combine(root,"maps.json")));
    }
    [Theory][InlineData(RemoteKind.Thermal,"landsat","planetary-computer")][InlineData(RemoteKind.Multispectral,"sentinel","earth-search")][InlineData(RemoteKind.Terrain,"copdem","planetary-computer")]
    public async Task RecordedProviderMetadataSelectsActualNumericAssetsAndRetainsScaling(RemoteKind kind,string file,string id)
    {
        var feature=Metadata(file);var handler=new Handler((request,_)=>Json(new{features=new[]{feature}}));using var http=new ScientificHttp(handler);
        var area=kind==RemoteKind.Terrain?new(8.1,46.1,8.11,46.11):kind==RemoteKind.Multispectral?new(-75,40,-74.98,40.02):Area;
        var d=await new StacScientificProvider(http,id).Discover(kind,area,Preferences,new(1048576),default);Assert.NotNull(d);
        Assert.All(d.Assets,a=>Assert.DoesNotContain("rendered",a.Url.AbsoluteUri));Assert.NotNull(d.ProviderMetadataJson);
        if(kind==RemoteKind.Thermal){Assert.Equal(new[]{"ST_B10","QA_PIXEL","QA_RADSAT"},d.Manifest.Bands.Select(b=>b.Id));Assert.Equal(.00341802,d.Manifest.Bands[0].Scale);Assert.Equal(149,d.Manifest.Bands[0].Offset);}
        if(kind==RemoteKind.Multispectral){Assert.Equal(.0001,d.Manifest.Bands[0].Scale);Assert.Equal(-.1,d.Manifest.Bands[0].Offset);Assert.Equal("SCL",d.Manifest.Bands.Last().Id);Assert.Equal(1,d.Manifest.Bands.Last().Scale);}
        if(kind==RemoteKind.Terrain){Assert.Null(d.Manifest.ObservationUtc);Assert.NotNull(d.Manifest.PublicationUtc);Assert.Contains("EGM2008",d.Manifest.VerticalDatum);Assert.Contains("surface model",d.Manifest.Notes);}
    }
    [Fact]public async Task DateQualityGeographyMissingThermalProductAndMissingScaleAreEnforced()
    {
        var feature=Metadata("landsat");var handler=new Handler((_,_)=>Json(new{features=new[]{feature}}));using var http=new ScientificHttp(handler);var provider=new StacScientificProvider(http,"planetary-computer");
        Assert.Null(await provider.Discover(RemoteKind.Thermal,new(10,45,10.01,45.01),Preferences,new(1048576),default));
        feature["properties"]!["landsat:correction"]="L2SR";Assert.Null(await provider.Discover(RemoteKind.Thermal,Area,Preferences,new(1048576),default));
        feature["properties"]!["landsat:correction"]="L2SP";feature["properties"]!["eo:cloud_cover"]=80;Assert.Null(await provider.Discover(RemoteKind.Thermal,Area,Preferences,new(1048576),default));
        feature["properties"]!["eo:cloud_cover"]=0;Assert.Null(await provider.Discover(RemoteKind.Thermal,Area,Preferences with{PreferredDateUtc=DateTimeOffset.Parse("2020-01-01Z")},new(1048576),default));
        feature["assets"]!["lwir11"]!["raster:bands"]![0]!.AsObject().Remove("scale");await Assert.ThrowsAsync<IOException>(()=>provider.Discover(RemoteKind.Thermal,Area,Preferences,new(1048576),default));
    }
    [Fact]public void RangeRequestsAreExactBoundedCachedAndRejectFullDownloadsOrChangedAssets()
    {
        var bytes=Enumerable.Range(0,150000).Select(i=>(byte)i).ToArray();var handler=new Handler((r,_)=>Range(r,bytes));using var http=new ScientificHttp(handler);var budget=new ScientificDownloadBudget(200000);
        using(var stream=new ScientificRangeStream(http,new("https://sentinel-cogs.s3.us-west-2.amazonaws.com/test.tif"),budget,default))
        {
            Assert.Equal(bytes.Length,stream.Length);stream.Position=65530;var actual=new byte[40];stream.ReadExactly(actual);Assert.Equal(bytes[65530..65570],actual);Assert.Equal(2,handler.Count);stream.Position=0;stream.ReadByte();Assert.Equal(2,handler.Count);
            Assert.Throws<IOException>(()=>stream.Seek(-1,SeekOrigin.Begin));
        }
        using var full=new ScientificHttp(new Handler((_,_)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}));Assert.Throws<IOException>(()=>new ScientificRangeStream(full,new("https://sentinel-cogs.s3.us-west-2.amazonaws.com/test.tif"),new(200000),default));
        using var changed=new ScientificHttp(new Handler((r,n)=>Range(r,bytes,n==1?"\"one\"":"\"two\"")));using var rs=new ScientificRangeStream(changed,new("https://sentinel-cogs.s3.us-west-2.amazonaws.com/test.tif"),new(200000),default);rs.Position=70000;Assert.Throws<IOException>(()=>rs.ReadByte());
        using var small=new ScientificHttp(new Handler((r,_)=>Range(r,bytes)));Assert.Throws<IOException>(()=>new ScientificRangeStream(small,new("https://sentinel-cogs.s3.us-west-2.amazonaws.com/test.tif"),new(1000),default));
        Assert.Throws<IOException>(()=>ScientificHttp.ValidateUrl(new("https://127.0.0.1/private")));
    }
    [Fact]public async Task RateLimitsRetryAndExpiredAnonymousProviderSignaturesAreRenewedInMemory()
    {
        var attempts=new Handler((_,n)=>{if(n>1)return Json(new{ok=true});var response=new HttpResponseMessage((HttpStatusCode)429);response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));return response;});
        using(var http=new ScientificHttp(attempts)){using var json=await http.Json("https://earth-search.aws.element84.com/v1/search",new(1048576),default);Assert.True(json.RootElement.GetProperty("ok").GetBoolean());Assert.Equal(2,attempts.Count);}
        var signs=0;var ranges=0;var bytes=new byte[1000];
        var handler=new Handler((r,_)=>{if(r.RequestUri!.Host=="planetarycomputer.microsoft.com")return Json(new{href="https://landsateuwest.blob.core.windows.net/test.tif?sig=private-"+(++signs)});if(++ranges==1)return new(HttpStatusCode.Forbidden);return Range(r,bytes);});
        using var signed=new ScientificHttp(handler);using var stream=new ScientificRangeStream(signed,new("https://landsateuwest.blob.core.windows.net/test.tif"),new(1048576),default);Assert.Equal(2,signs);Assert.Equal(1000,stream.Length);
    }
    [Fact]public async Task AutomaticManifestFeedsExistingLibraryAndManualImportWithoutFabricatingFlightHistory()
    {
        var input=RemoteFixtureFiles.Create(Path.Combine(root,"mock-input"),RemoteKind.Terrain);var manifest=ScienceStore.ReadSourceManifest(input);
        var bytes=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(input)!,manifest.Bands[0].File));var handler=new Handler((r,_)=>Range(r,bytes));using var http=new ScientificHttp(handler);
        var provider=new FixtureProvider(manifest with{Citation="https://sentinel-cogs.s3.us-west-2.amazonaws.com/fixture.tif"});var settings=new ScientificAccessSettings(root);settings.Save(Preferences);
        using var auto=new AutomaticScientificData(science,settings,http,[provider]);auto.Prepare(Area,[RemoteKind.Terrain],true);await Wait(()=>auto.Snapshot().Single().Status is not("Queued" or "Downloading"));
        var state=auto.Snapshot().Single();Assert.Equal("Ready",state.Status);var source=science.Sources().Single();Assert.NotNull(source.Manifest.Acquisition);Assert.Equal(81,source.Manifest.Acquisition.UsablePixels);Assert.Empty(science.Products());Assert.Empty(science.PendingArtifacts().Artifacts);
        Assert.Empty(Directory.GetFiles(root,"*.sas",SearchOption.AllDirectories));Assert.True(File.Exists(input));
        var retained=Path.Combine(science.DirectoryPath,source.RelativeDirectory,source.Files[0].RelativePath);using(var raster=new ScientificRaster(Path.GetDirectoryName(retained)!,source.Manifest.Bands[0]))Assert.Equal(123.25,raster.Read(Area.Center));
        // Ordinary manual import still goes through the same original validator.
        await science.ImportSourceAsync(input);Assert.Equal(2,science.Sources().Length);
    }
    private sealed class FixtureProvider(ScientificSourceManifest manifest):IScientificDataProvider
    {
        public string Id=>"fixture-only";public bool Supports(RemoteKind kind)=>kind==RemoteKind.Terrain;
        public Task<ScientificDiscovery?> Discover(RemoteKind kind,GeographicBounds area,ScientificAccessPreferences p,ScientificDownloadBudget budget,CancellationToken token)=>Task.FromResult<ScientificDiscovery?>(new(manifest,[new(manifest.Bands[0],new("https://sentinel-cogs.s3.us-west-2.amazonaws.com/fixture.tif"))],"Isolated fixture — not genuine acceptance","fixture","Fixture", "Fixture",area));
    }
    [Fact]public async Task OfflineUnknownMeteringCapsOutagesAndCancellationPreserveOriginals()
    {
        var handler=new Handler((_,_)=>throw new InvalidOperationException("No network expected"));var settings=new ScientificAccessSettings(root);using var auto=new AutomaticScientificData(science,settings,new ScientificHttp(handler));
        auto.Prepare(Area,[RemoteKind.Terrain]);await Wait(()=>auto.Snapshot().Single().Status!="Queued");Assert.Equal("Offline",auto.Snapshot().Single().Status);Assert.Equal(0,handler.Count);
        auto.Configure(Preferences with{UnmeteredOnly=true});auto.Prepare(Area,[RemoteKind.Terrain]);await Wait(()=>auto.Snapshot().Single().Status!="Queued");Assert.Equal("Offline",auto.Snapshot().Single().Status);Assert.Equal(0,handler.Count);
        var input=RemoteFixtureFiles.Create(Path.Combine(root,"fixture"),RemoteKind.Terrain);var original=File.ReadAllBytes(Path.Combine(root,"fixture","elevation.tif"));
        var settings2=new ScientificAccessSettings(Path.Combine(root,"two"));settings2.Save(Preferences);
        var provider=new FixtureProvider(ScienceStore.ReadSourceManifest(input));using var limited=new AutomaticScientificData(science,settings2,new ScientificHttp(handler),[provider]);management.Configure(new(null,100000));limited.Prepare(Area,[RemoteKind.Terrain]);await Wait(()=>limited.Snapshot().Single().Status=="Unavailable");Assert.Equal(0,handler.Count);Assert.Empty(science.Sources());
        Assert.Equal(original,File.ReadAllBytes(Path.Combine(root,"fixture","elevation.tif")));
        using var cts=new CancellationTokenSource();cts.Cancel();using var http=new ScientificHttp(handler);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>http.Json("https://earth-search.aws.element84.com/v1/search",new(1048576),cts.Token));Assert.Equal(0,handler.Count);
        using var outage=new ScientificHttp(new Handler((_,_)=>new(HttpStatusCode.NotFound)));await Assert.ThrowsAsync<IOException>(()=>outage.Json("https://earth-search.aws.element84.com/v1/search",new(1048576),default));
    }
    [Fact]public async Task FailedPartialPreparationIsAccountedAndRetryPreservesItWhilePublishingOnlyVerifiedSources()
    {
        var input=RemoteFixtureFiles.Create(Path.Combine(root,"partial-input"),RemoteKind.Terrain);var manifest=ScienceStore.ReadSourceManifest(input);
        RemoteFixtureFiles.GeoTiff(Path.Combine(Path.GetDirectoryName(input)!,manifest.Bands[0].File),0,size:512,pixel:1.0/3600,gradient:true);
        var bytes=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(input)!,manifest.Bands[0].File));Assert.True(bytes.Length>65536);
        var fail=true;var handler=new Handler((r,n)=>fail&&n>=3?new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}:Range(r,bytes));
        var settings=new ScientificAccessSettings(root);settings.Save(Preferences);using var auto=new AutomaticScientificData(science,settings,new ScientificHttp(handler),[new FixtureProvider(manifest)]);
        auto.Prepare(Area,[RemoteKind.Terrain],true);await Wait(()=>auto.Snapshot().Single().Status=="Unavailable");Assert.Empty(science.Sources());
        var partial=science.PendingArtifacts();Assert.NotEmpty(partial.Ledgers);Assert.True(management.Snapshot(true).Science.PendingBytes>0);
        var retained=partial.Artifacts.Where(File.Exists).ToArray();Assert.NotEmpty(retained);
        fail=false;auto.Prepare(Area,[RemoteKind.Terrain],true);await Wait(()=>auto.Snapshot().Single().Status=="Ready");Assert.Single(science.Sources());Assert.All(retained,path=>Assert.True(File.Exists(path)));
        Assert.Equal(partial.Ledgers,science.PendingArtifacts().Ledgers);Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(input)!,manifest.Bands[0].File)));
    }
    private sealed class WaitingProvider:IScientificDataProvider
    {
        public int Started;public string Id=>"waiting-fixture";public bool Supports(RemoteKind kind)=>true;
        public async Task<ScientificDiscovery?> Discover(RemoteKind kind,GeographicBounds area,ScientificAccessPreferences p,ScientificDownloadBudget budget,CancellationToken token)
        {Interlocked.Increment(ref Started);await Task.Delay(Timeout.Infinite,token);return null;}
    }
    [Fact]public async Task QueuedDownloadsCanBeCancelledWithoutFallingThroughToNetworkOrMissingObservationsBackfilled()
    {
        var settings=new ScientificAccessSettings(root);settings.Save(Preferences);var waiting=new WaitingProvider();
        using(var auto=new AutomaticScientificData(science,settings,providers:[waiting]))
        {
            auto.Prepare(Area,[RemoteKind.Thermal,RemoteKind.Multispectral]);await Wait(()=>waiting.Started==1);
            auto.Cancel();await Wait(()=>auto.Snapshot().All(s=>s.Status=="Unavailable"));Assert.Equal(1,waiting.Started);Assert.Empty(science.Sources());
        }
        using var research=new ResearchOperations(missions,science);var coordinator=new MissionCoordinator(missions,1000,research:research);var mission=coordinator.Start(new("No backfill",Instruments:[new("terrain-scanner",true)]));
        var time=DateTimeOffset.UtcNow;var frame=new TelemetryFrame(time,TelemetrySource.Simulated,"Test fixture",40,-75,1000,328,100,100,100,0,0,0,0,0);
        void Apply(TelemetryFrame f)=>coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Fixture",f.TimestampUtc),f));
        Apply(frame);await Wait(()=>coordinator.Snapshot().Research!.Instruments.Single(i=>i.InstrumentId=="terrain-scanner").Quality==ObservationQuality.Unavailable);Assert.Empty(science.Products());
        await science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,"late-source"),RemoteKind.Terrain));Apply(frame);await Task.Delay(50);Assert.Empty(science.Products());
        Apply(frame with{TimestampUtc=time.AddSeconds(1)});await Wait(()=>science.Products().Length==1);Assert.Equal(time.AddSeconds(1),science.Products().Single().FlightPacketUtc);Assert.Equal(2,missions.StreamSamples(mission.Id).Count());
    }
    [Fact]public void NativePointGridCropKeepsPixelValuesAndBoundaryTiesStable()
    {
        var folder=Path.Combine(root,"point-grid");Directory.CreateDirectory(folder);var path=Path.Combine(folder,"source.tif");
        RemoteFixtureFiles.GeoTiff(path,0,size:64,pixel:1.0/3600,gradient:true,pixelPoint:true);
        var band=new SourceBand("elevation","source.tif","geotiff",Unit:"m");using var source=new ScientificRaster(folder,band);
        var g=source.Grid;var area=new GeographicBounds(g.OriginX+7*g.PixelWidth,g.OriginY+50*g.PixelHeight,g.OriginX+40*g.PixelWidth,g.OriginY+4*g.PixelHeight);
        source.CopySubset(area,Path.Combine(folder,"crop.tif"),default);using var crop=new ScientificRaster(folder,band with{File="crop.tif"});
        for(var row=5;row<49;row++)for(var col=8;col<39;col++)
        {
            var point=new Coordinates(g.OriginY+row*g.PixelHeight,g.OriginX+col*g.PixelWidth);
            Assert.Equal(row*64+col,source.Read(point));Assert.Equal(source.Read(point),crop.Read(point));
        }
    }
    [Fact]public async Task HugePolarOrInvalidAreasAreRejectedAndLargePlansAreExplicitlyPartial()
    {
        Assert.Throws<ArgumentException>(()=>AutomaticScientificData.ValidateArea(new(-180,-80,180,84)));Assert.Throws<ArgumentException>(()=>AutomaticScientificData.ValidateArea(new(0,85,.01,85.01)));
        using var auto=new AutomaticScientificData(science,new(root));var result=ScientificPlanPreparation.Prepare(auto,new(Guid.NewGuid(),"Long",Waypoints:[new("A",40,-75),new("B",41,-74)],Instruments:[new("thermal-imager",true)]));Assert.True(result.LimitedCoverage);Assert.InRange(result.AreasQueued,1,8);await Wait(()=>auto.Snapshot().All(s=>s.Status!="Queued"));Assert.All(auto.Snapshot(),s=>Assert.Equal("Offline",s.Status));
    }
    private static async Task Wait(Func<bool> condition){using var c=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!condition())await Task.Delay(20,c.Token);}
    public void Dispose(){science.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
