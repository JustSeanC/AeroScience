using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

public sealed class CatalogDiscoveryTests:IDisposable
{
    readonly string folder=Path.Combine(Path.GetTempPath(),"catalog-discovery-"+Guid.NewGuid());
    readonly ScienceStore science;readonly ScientificAccessSettings settings;
    public CatalogDiscoveryTests(){var storage=new StorageManager(new PreferenceStore(folder)).Initialize();science=new(storage.ScienceLibraryDirectory);science.Initialize();settings=new(folder);settings.Save(new(true,true));}
    sealed class Handler(Func<Uri,object> respond):HttpMessageHandler
    {public readonly List<Uri> Requests=[];protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){Requests.Add(r.RequestUri!);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(respond(r.RequestUri!)))});}}
    static JsonNode Metadata(string name)=>JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata",name+".json")))!;
    [Theory][InlineData("landsat","landsat","latest","-properties.datetime,id")][InlineData("sentinel-2","sentinel","oldest","properties.datetime,id")][InlineData("sentinel-2","sentinel","cloud","properties.eo:cloud_cover,-properties.datetime,id")]
    public async Task SortIsSentToProviderAndBoundToOpaquePagination(string provider,string metadata,string sort,string expected)
    {
        var f=Metadata(metadata);var h=new Handler(uri=>new{features=new[]{f},links=uri.Query.Contains("next=")?Array.Empty<object>():new object[]{new{rel="next",href="https://"+uri.Host+uri.AbsolutePath+"?next=provider-cursor",method="GET"}}});
        using var auto=new AutomaticScientificData(science,settings,new(h));var catalog=new ScientificCatalog(auto,science,settings);
        var q=new ScientificCatalogQuery(provider,new(-75,40,-74.98,40.02),DateTimeOffset.Parse("2024-01-01Z"),DateTimeOffset.Parse("2024-12-31Z"),Sort:sort);
        var first=await catalog.Search(q);Assert.True(first.ServerOrdered);Assert.NotNull(first.Ordering);Assert.NotNull(first.NextCursor);
        var query=System.Web.HttpUtility.ParseQueryString(h.Requests[0].Query);Assert.Equal(expected,query["sortby"]);Assert.All(query["datetime"]!.Split('/'),instant=>Assert.EndsWith("Z",instant));
        if(provider=="landsat")Assert.Contains("L2SP",query["query"]);
        await catalog.Search(q with{Cursor=first.NextCursor});Assert.Contains("next=provider-cursor",h.Requests[^1].Query);
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Search(q with{Cursor=first.NextCursor,Sort=sort=="oldest"?"latest":"oldest"}));
        Assert.NotNull(first.Items[0].CloudPercent);
    }
    [Fact]public async Task IgnoredProviderOrderingCannotClaimNewestAndUnknownOrderIsRejected()
    {
        var old=Metadata("sentinel");var recent=old.DeepClone();old["properties"]!["datetime"]="2024-01-01T00:00:00Z";recent["properties"]!["datetime"]="2024-12-01T00:00:00Z";
        using var auto=new AutomaticScientificData(science,settings,new(new Handler(_=>new{features=new[]{old,recent}})));var catalog=new ScientificCatalog(auto,science,settings);
        var q=new ScientificCatalogQuery("sentinel-2",new(-75,40,-74.98,40.02),DateTimeOffset.Parse("2024-01-01Z"),DateTimeOffset.Parse("2024-12-31Z"));
        var result=await catalog.Search(q);Assert.Equal(2,result.Items.Length);Assert.False(result.ServerOrdered);Assert.Contains("no newest-match",result.Ordering);
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Search(q with{Sort="invented"}));
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Search(q with{Provider="nasa-classic",Sort="cloud"}));
    }
    [Theory][InlineData("latest","-start_date")][InlineData("oldest","start_date")]
    public async Task NasaHistoricalOrderUsesDocumentedSortKeysAndNoTemporalDefault(string sort,string expected)
    {
        var h=new Handler(uri=>Metadata(uri.AbsolutePath.Contains("collections")?"nasa-classic-collections":"nasa-classic-granules"));using var auto=new AutomaticScientificData(science,settings,new(h));
        var page=await new ScientificCatalog(auto,science,settings).Search(new("nasa-classic",new(-119,33,-117,35),Sort:sort));
        var q=System.Web.HttpUtility.ParseQueryString(h.Requests[^1].Query);Assert.Equal(expected+",granule_ur",q["sort_key[]"]);Assert.Null(q["temporal"]);
        Assert.All(page.Items,i=>Assert.Equal(2008,i.ObservationUtc!.Value.Year));
    }
    [Fact]public async Task OfficialNamesAreStateDependentAndRemainAvailableOfflineWithoutInventingBoundaries()
    {
        var h=new Handler(uri=>Metadata(uri.Query.Contains("COUNTY")?"census-new-mexico-counties":"census-states"));using var auto=new AutomaticScientificData(science,settings,new(h));var catalog=new ScientificCatalog(auto,science,settings);
        var states=await catalog.RegionNames(new());Assert.Contains(states.Items,i=>i.Code=="35"&&i.Name=="New Mexico");Assert.Equal(52,states.Items.Length);
        var counties=await catalog.RegionNames(new("35"));Assert.Contains(counties.Items,i=>i.Code=="013"&&i.Name=="Doña Ana County");Assert.Equal(33,counties.Items.Length);
        Assert.All(h.Requests,u=>{Assert.Equal("tigerweb.geo.census.gov",u.Host);Assert.Contains("returnGeometry=false",u.Query);});
        settings.Save(new());var cached=await catalog.RegionNames(new("35",true));Assert.True(cached.Cached);Assert.Equal(counties.AsOfUtc,cached.AsOfUtc);Assert.Equal(2,h.Requests.Count);
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.RegionNames(new("35' OR 1=1")));
        await Assert.ThrowsAsync<IOException>(()=>catalog.RegionNames(new("06",true)));
        Assert.Empty(science.Sources());
    }
    public void Dispose(){science.Dispose();Directory.Delete(folder,true);}
}
