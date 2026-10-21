using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
public sealed class NasaCatalogTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"nasa-catalog-"+Guid.NewGuid().ToString("N"));
    readonly ScienceStore science;readonly ScientificAccessSettings settings;
    public NasaCatalogTests(){var storage=new StorageManager(new PreferenceStore(root)).Initialize();science=new(storage.ScienceLibraryDirectory);science.Initialize();settings=new(Path.Combine(root,"config"),()=>true);settings.Save(new(true,true));}
    internal static JsonNode Metadata(string family,string type)=>JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata",$"nasa-{family}-{type}.json")))!;
    sealed class Handler(Func<HttpRequestMessage,JsonNode> run):HttpMessageHandler
    {public int Count;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){Count++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(run(r).ToJsonString())});}}
    [Theory][InlineData("classic","AVIRIS-Classic","1")][InlineData("ng","AVIRIS-NG","1")][InlineData("emit","EMIT","001")]
    public async Task RecordedOfficialMetadataPreservesIdentityTimesFootprintsAndUnknownSpectra(string family,string name,string version)
    {
        var handler=new Handler(r=>Metadata(family,r.RequestUri!.AbsolutePath.Contains("collections")?"collections":"granules"));using var auto=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(auto,science,settings);
        var q=new ScientificCatalogQuery("nasa-"+family,new(-119,33,-117,35),PageSize:2);var page=await catalog.Search(q);Assert.Equal("Available",page.Status);Assert.NotNull(page.NextCursor);
        var item=page.Items[0];Assert.Equal(name,item.Nasa!.Family);Assert.Equal(version,item.Nasa.CollectionVersion);Assert.NotNull(item.ObservationUtc);Assert.NotNull(item.Geometry);Assert.Null(item.Nasa.WavelengthsNm);Assert.Null(item.Nasa.BandCount);Assert.Empty(item.Bands);Assert.True(item.Nasa.OriginalBytes>0);
        Assert.All(item.Nasa.Assets,a=>Assert.True(ScientificCatalog.OfficialNasaLink(a.Url)));Assert.Contains(family=="emit"?"not yet supported":"Guided import",item.Nasa.Capability);
        settings.Save(new());var cached=await catalog.Search(q with{CachedOnly=true});Assert.True(cached.Cached);Assert.Equal(page.AsOfUtc,cached.AsOfUtc);Assert.Equal(2,handler.Count);
        Assert.Empty(science.Sources());
    }
    [Fact]public async Task PagesAreBoundedAndQueryBoundAndNoMatchRemainsEmpty()
    {
        var h=new Handler(r=>r.RequestUri!.AbsolutePath.Contains("collections")?Metadata("classic","collections"):r.RequestUri.Query.Contains("page_num=2")?JsonNode.Parse("{\"feed\":{\"entry\":[]}}")!:Metadata("classic","granules"));
        using var auto=new AutomaticScientificData(science,settings,new(h));var catalog=new ScientificCatalog(auto,science,settings);var q=new ScientificCatalogQuery("nasa-classic",new(-119,33,-117,35),PageSize:2,SceneFilter:"f080611");
        var first=await catalog.Search(q);Assert.NotNull(first.NextCursor);var second=await catalog.Search(q with{Cursor=first.NextCursor});Assert.Equal("No match",second.Status);Assert.Null(second.NextCursor);
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Search(q with{Cursor=first.NextCursor,SceneFilter="different"}));
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Search(q with{SceneFilter="*?secret="}));
    }
    [Fact]public void MalformedAndAntimeridianGeometryCannotInventCoverage()
    {
        var c=Metadata("classic","collections")["feed"]!["entry"]![0]!;var e=Metadata("classic","granules")["feed"]!["entry"]![0]!;
        e["polygons"]=JsonSerializer.SerializeToNode(new[]{new[]{"1 179 1 -179 2 -179 2 179 1 179"}});
        Assert.Throws<InvalidDataException>(()=>ScientificCatalog.DescribeNasa("nasa-classic",JsonSerializer.SerializeToElement(c),JsonSerializer.SerializeToElement(e)));
        e["polygons"]=new JsonArray();Assert.Throws<InvalidDataException>(()=>ScientificCatalog.DescribeNasa("nasa-classic",JsonSerializer.SerializeToElement(c),JsonSerializer.SerializeToElement(e)));
    }
    [Theory][InlineData("https://data.ornldaac.earthdata.nasa.gov/protected/data.bin?token=secret")][InlineData("https://data.ornldaac.earthdata.nasa.gov.evil.example/data.bin")][InlineData("http://data.ornldaac.earthdata.nasa.gov/data.bin")][InlineData("https://user@data.ornldaac.earthdata.nasa.gov/data.bin")]
    public void UntrustedOrSecretBearingLinksAreRejected(string url)=>Assert.False(ScientificCatalog.OfficialNasaLink(url));
    [Fact]public async Task ExactHandoffRevalidatesRevisionAndRefusesRemovedGranule()
    {
        var collection=Metadata("classic","collections");var granules=Metadata("classic","granules");
        var handler=new Handler(r=>r.RequestUri!.AbsolutePath.Contains("collections")?collection:granules);using var auto=new AutomaticScientificData(science,settings,new(handler,cacheEnabled:false));var catalog=new ScientificCatalog(auto,science,settings);
        var item=(await catalog.Search(new("nasa-classic",new(-119,33,-117,35),PageSize:2))).Items[0];
        // Exact granule lookup returns only that authoritative entry.
        granules["feed"]!["entry"]=new JsonArray(granules["feed"]!["entry"]![0]!.DeepClone());
        var request=new NasaSelectionRequest(item.Provider,item.Scene,item.Revision);Assert.Equal(item.Scene,(await catalog.ResolveNasa(request)).Scene);
        granules["feed"]!["entry"]![0]!["updated"]="2026-10-21T10:00:00Z";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>catalog.ResolveNasa(request));
        granules["feed"]!["entry"]=new JsonArray();await Assert.ThrowsAsync<IOException>(()=>catalog.ResolveNasa(request));
        settings.Save(new());await Assert.ThrowsAsync<InvalidOperationException>(()=>catalog.ResolveNasa(request));
    }
    [Fact]public void GuidedContextNeverAllowsEmitOrBusyReplacementAndCancellationIsNonDestructive()
    {
        var c=Metadata("classic","collections")["feed"]!["entry"]![0]!;var e=Metadata("classic","granules")["feed"]!["entry"]![0]!;
        var item=ScientificCatalog.DescribeNasa("nasa-classic",JsonSerializer.SerializeToElement(c),JsonSerializer.SerializeToElement(e));
        using var session=new ScientificImportSession(science);session.BeginNasa(item);Assert.Equal(item.Scene,session.Snapshot().NasaSelection!.Scene);Assert.Equal("Choose",session.Snapshot().Stage);
        session.Cancel();Assert.Null(session.Snapshot().NasaSelection);Assert.Empty(science.Sources());
        Assert.Throws<ArgumentException>(()=>session.BeginNasa(item with{Provider="nasa-emit"}));
    }
    public void Dispose(){science.Dispose();Directory.Delete(root,true);}
}
