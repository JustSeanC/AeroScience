using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

// Recorded authoritative STAC metadata validates adapters; handlers are not live-provider acceptance.
public sealed class ScientificCatalogTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"catalog-"+Guid.NewGuid().ToString("N"));
    private readonly ScienceStore science;private readonly ScientificAccessSettings settings;
    public ScientificCatalogTests(){var storage=new StorageManager(new PreferenceStore(root)).Initialize();science=new(storage.ScienceLibraryDirectory);science.Initialize();settings=new(root);settings.Save(new(true,true));}
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {public int Count;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){Count++;return Task.FromResult(send(r));}}
    private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private static JsonNode Metadata(string name)=>JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata",name+".json")))!;
    [Theory][InlineData("landsat","landsat",RemoteKind.Thermal)][InlineData("sentinel-2","sentinel",RemoteKind.Multispectral)][InlineData("copernicus","copdem",RemoteKind.Terrain)]
    public async Task CatalogPreservesRealMetadataAndNumericRecipe(string provider,string file,RemoteKind kind)
    {
        var feature=Metadata(file);var handler=new Handler(_=>Json(new{features=new[]{feature},links=Array.Empty<object>()}));using var auto=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(auto,science,settings);
        var result=await catalog.Search(new(provider,new(-75,40,-74.98,40.02),DateTimeOffset.Parse("2024-01-01Z"),DateTimeOffset.Parse("2024-12-31Z")));var item=Assert.Single(result.Items);
        Assert.Equal("Available",result.Status);Assert.Equal(kind,item.Kind);Assert.Equal(feature["id"]!.GetValue<string>(),item.Scene);Assert.Equal(64,item.Revision.Length);
        Assert.Equal(ScientificCatalog.Provider(provider).RequiredBands,item.Bands.Select(b=>b.Id));Assert.NotNull(item.Geometry);Assert.All(item.Bands,b=>Assert.Equal("geotiff",b.Format));
        if(kind==RemoteKind.Thermal){Assert.Equal("K",item.Bands[0].Units);Assert.Equal(149,item.Bands[0].Offset);Assert.Contains("100 m",item.Resolution);}
        if(kind==RemoteKind.Multispectral){Assert.Equal(-.1,item.Bands[0].Offset);Assert.Equal(20,item.Bands.Last().ResolutionMeters);}
        if(kind==RemoteKind.Terrain){Assert.Null(item.ObservationUtc);Assert.Contains("DSM",item.VerticalDatum);}
        var cached=await catalog.Search(new(provider,new(-75,40,-74.98,40.02),DateTimeOffset.Parse("2024-01-01Z"),DateTimeOffset.Parse("2024-12-31Z"),CachedOnly:true));Assert.True(cached.Cached);Assert.Equal(result.AsOfUtc,cached.AsOfUtc);Assert.Equal(1,handler.Count);
    }
    [Fact]public async Task PaginationUsesActualOpaqueNextLinkAndBindsFilters()
    {
        var feature=Metadata("sentinel");var handler=new Handler(r=>Json(new{features=new[]{feature},links=r.RequestUri!.Query.Contains("token=")?Array.Empty<object>():new object[]{new{rel="next",href="https://earth-search.aws.element84.com/v1/search?token=real-provider-token",method="GET"}}}));
        using var auto=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(auto,science,settings);var q=new ScientificCatalogQuery("sentinel-2",new(-75,40,-74.98,40.02),DateTimeOffset.Parse("2024-01-01Z"),DateTimeOffset.Parse("2024-12-31Z"));
        var first=await catalog.Search(q);Assert.NotNull(first.NextCursor);var next=await catalog.Search(q with{Cursor=first.NextCursor});Assert.Single(next.Items);Assert.Null(next.NextCursor);
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Search(q with{Cursor=first.NextCursor,MaximumCloudPercent=10}));
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Search(q with{Cursor="../other"}));
    }
    [Fact]public async Task UnsupportedNumericAssetsEmptyOfflineAndUnsafeNextAreHonest()
    {
        var feature=Metadata("sentinel");feature["assets"]!["blue"]!["type"]="image/png";
        var handler=new Handler(_=>Json(new{features=new[]{feature},links=Array.Empty<object>()}));using var auto=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(auto,science,settings);var q=new ScientificCatalogQuery("sentinel-2",new(-75,40,-74.98,40.02),DateTimeOffset.Parse("2024-01-01Z"),DateTimeOffset.Parse("2024-12-31Z"));
        Assert.Equal("No match",(await catalog.Search(q)).Status);Assert.Equal("Not searched",(await catalog.Search(q with{Area=new(8,46,8.02,46.02),CachedOnly=true})).Status);
        settings.Save(new());await Assert.ThrowsAsync<InvalidOperationException>(()=>catalog.Search(q with{Area=new(8,46,8.02,46.02)}));Assert.Empty(science.Sources());
        settings.Save(new(true,true));using var unsafeAuto=new AutomaticScientificData(science,settings,new(new Handler(_=>Json(new{features=Array.Empty<object>(),links=new[]{new{rel="next",href="https://127.0.0.1/private"}}}))));
        Assert.Equal("Unavailable",(await new ScientificCatalog(unsafeAuto,science,settings).Search(q with{Area=new(8,46,8.02,46.02)})).Status);
    }
    [Fact]public async Task ExactSceneResolutionRejectsChangedRevisionBandsAndCoverage()
    {
        var feature=Metadata("landsat");var handler=new Handler(r=>r.RequestUri!.AbsolutePath.Contains("/items/")?Json(feature):Json(new{features=new[]{feature},links=Array.Empty<object>()}));
        using var auto=new AutomaticScientificData(science,settings,new(handler,cacheEnabled:false));var catalog=new ScientificCatalog(auto,science,settings);var area=new GeographicBounds(-75.01,39.99,-74.99,40.01);
        var item=Assert.Single((await catalog.Search(new("landsat",area,DateTimeOffset.Parse("2024-01-01Z"),DateTimeOffset.Parse("2024-12-31Z")))).Items);var selected=new ScientificSceneSelection(item.Provider,item.Scene,item.Revision,item.Bands.Select(b=>b.Id).ToArray());
        var resolved=await catalog.Resolve(selected,area,new(1048576),default);Assert.Equal(item.Scene,resolved.Discovery.Manifest.SceneId);
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Resolve(selected with{Bands=["ST_B10"]},area,new(1048576),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Resolve(selected,new(8,46,8.02,46.02),new(1048576),default));
        feature["properties"]!["updated"]="2026-10-21T12:00:00Z";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>catalog.Resolve(selected,area,new(1048576),default));
    }
    [Fact]public async Task AuthoritativeRegionUsesBoundedTrustedEndpointAndRetainsOriginalGeometryOffline()
    {
        var metadata=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata","census-county.json"));var handler=new Handler(r=>{Assert.Equal("tigerweb.geo.census.gov",r.RequestUri!.Host);return new(HttpStatusCode.OK){Content=new StringContent(metadata)};});
        using var auto=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(auto,science,settings);var region=await catalog.Region(new("35","013"));Assert.Equal("Doña Ana County",region.Name);Assert.Contains("1:20,000,000",region.Resolution);
        settings.Save(new());var retained=await catalog.Region(new("35","013",true));Assert.True(retained.Cached);Assert.True(JsonElement.DeepEquals(region.Geometry,retained.Geometry));Assert.Equal(region.AsOfUtc,retained.AsOfUtc);Assert.Equal(1,handler.Count);
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Region(new("35 OR 1=1","013")));
    }
    [Fact]public async Task CatalogEvictionPreservesUnrelatedUserFiles()
    {
        var folder=Path.Combine(science.DirectoryPath,"Metadata","Catalog");Directory.CreateDirectory(folder);
        for(var i=0;i<129;i++){var file=Path.Combine(folder,"pilot-note-"+i+".json");File.WriteAllText(file,"{\"userOwned\":true}");File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddDays(-1));}
        using var automatic=new AutomaticScientificData(science,settings,new(new Handler(_=>Json(new{features=Array.Empty<object>()}))));
        var page=await new ScientificCatalog(automatic,science,settings).Search(new("sentinel-2",new(-75,40,-74.98,40.02)));Assert.Equal("No match",page.Status);
        Assert.Equal(129,Directory.GetFiles(folder,"pilot-note-*.json").Length);Assert.All(Directory.GetFiles(folder,"pilot-note-*.json"),file=>Assert.Equal("{\"userOwned\":true}",File.ReadAllText(file)));
    }
    public void Dispose(){science.Dispose();if(Directory.Exists(root))Directory.Delete(root,true);}
}
