using System.Net;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
[Collection(ScientificIoCollection.Name)]
public sealed class LidarCatalogTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"lidar-catalog-"+Guid.NewGuid().ToString("N"));readonly ScienceStore science;readonly ScientificAccessSettings settings;
    public LidarCatalogTests(){var p=new StorageManager(new PreferenceStore(root)).Initialize();science=new(p.ScienceLibraryDirectory);science.Initialize();settings=new(root,()=>true);}
    sealed class Handler:HttpMessageHandler
    {
        public readonly List<Uri> Requests=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var u=request.RequestUri!;Requests.Add(u);string body;
            if(u.Host=="index.nationalmap.gov")body=JsonSerializer.Serialize(new{type="FeatureCollection",features=new[]{new{type="Feature",geometry=new{type="Polygon",coordinates=new[]{new[]{new[]{-94d,41d},new[]{-93d,41d},new[]{-93d,42d},new[]{-94d,41d}}}},properties=new{project="IA_STATEWIDE_2008_Legacy_Data",collect_start=1175904000000L,collect_end=1274918400000L,lpc_pub_date=1403481600000L}}}});
            else if(u.AbsolutePath=="/")body="<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><CommonPrefixes><Prefix>IA_FullState/</Prefix></CommonPrefixes><CommonPrefixes><Prefix>IA_Unsupported/</Prefix></CommonPrefixes><NextContinuationToken>abc+def=</NextContinuationToken></ListBucketResult>";
            else body=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"LidarOriginals","ia-fullstate-ept.json"));
            if(u.AbsolutePath.Contains("Unsupported"))body=body.Replace("laszip","unsupported");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)});
        }
    }
    [Fact]public async Task GeographicDiscoveryKeepsOfficialDatesAndVerifiedEptSeparateAndNeverFetchesPoints()
    {
        settings.Save(new(true,true));var handler=new Handler();using var automatic=new AutomaticScientificData(science,settings,new(handler));var catalog=new LidarCatalog(automatic,science,settings);var request=new LidarDiscoveryRequest(new(-93.61,41.59,-93.59,41.61));var result=await catalog.Discover(request);
        var source=Assert.Single(result.Projects);Assert.Equal("IA_FullState",source.Project);Assert.Equal("EPSG:3857",source.HorizontalCrs);Assert.Contains("Unknown",source.VerticalReference);Assert.Equal(1175904000000L,Assert.Single(result.OfficialWorkUnits).GetProperty("properties").GetProperty("collect_start").GetInt64());Assert.Single(result.UnavailableProjects!);Assert.Equal("abc+def=",result.NextCursor);
        Assert.All(handler.Requests,u=>Assert.DoesNotContain("ept-data",u.AbsolutePath));Assert.Contains(handler.Requests,u=>u.Query.Contains("resultRecordCount=5"));Assert.Contains(handler.Requests,u=>u.Query.Contains("prefix=IA_"));var n=handler.Requests.Count;settings.Save(new(false,false));Assert.True((await catalog.Discover(request with{CachedOnly=true})).Cached);Assert.Equal(n,handler.Requests.Count);
    }
    [Fact]public async Task ConsentBoundsAndUnavailableCachePreventAnyRequest()
    {
        var handler=new Handler();using var automatic=new AutomaticScientificData(science,settings,new(handler));var catalog=new LidarCatalog(automatic,science,settings);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>catalog.Discover(new(new(-75,40,-74.99,40.01))));
        await Assert.ThrowsAsync<IOException>(()=>catalog.Discover(new(new(-75,40,-74.99,40.01),CachedOnly:true)));
        await Assert.ThrowsAsync<ArgumentException>(()=>catalog.Discover(new(new(-180,-80,180,80))));
        Assert.Empty(handler.Requests);
        Assert.Throws<IOException>(()=>LidarCatalog.ValidateUrl(new(LidarCatalog.Bucket+"?list-type=2&prefix=&delimiter=%2F&max-keys=100000")));
    }
    public void Dispose(){science.Dispose();if(Directory.Exists(root))Directory.Delete(root,true);}
}
