using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Tests;
// Test-host-only HTTP fixtures. Production catalog, jobs, SQLite, ranges, readers and importer run unchanged.
internal sealed class LibraryHttpFixture(string root):HttpMessageHandler
{
    private readonly string fixtureFolder=Path.Combine(root,"library-http-fixtures");
    public bool Enabled=>File.Exists(Path.Combine(root,"library-http-fixture.enabled"));
    public Guid[] InactiveSources=[];
    public string Mode="normal";public int Revision=File.Exists(Path.Combine(root,"library-http-fixture.enabled"))&&int.TryParse(File.ReadAllText(Path.Combine(root,"library-http-fixture.enabled")),out var savedRevision)?savedRevision:0;
    private readonly HttpMessageInvoker fallback=new(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false});
    public void Enable()
    {
        Revision++;Directory.CreateDirectory(fixtureFolder);File.WriteAllText(Path.Combine(root,"library-http-fixture.enabled"),Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach(var kind in new[]{RemoteKind.Terrain,RemoteKind.Thermal,RemoteKind.Multispectral})RemoteFixtureFiles.Create(Path.Combine(fixtureFolder,kind.ToString()),kind);
        RemoteFixtureFiles.GeoTiff(Path.Combine(fixtureFolder,"Thermal","QA_RADSAT.tif"),0);
    }
    public void Disable(){Mode="normal";File.Delete(Path.Combine(root,"library-http-fixture.enabled"));}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        if(!Enabled)return await fallback.SendAsync(request,token);if(!Directory.Exists(fixtureFolder))Enable();
        var uri=request.RequestUri!;
        HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value,System.Text.Encoding.UTF8,"application/json")};
        if(uri.Host=="cmr.earthdata.nasa.gov")
        {
            var family=uri.Query.Contains("Classic")||uri.Query.Contains("G347702")?"classic":uri.Query.Contains("AVIRIS-NG")||uri.Query.Contains("G26638")?"ng":"emit";
            var query=System.Web.HttpUtility.ParseQueryString(uri.Query);
            var type=uri.AbsolutePath.Contains("collections")?"collections":family=="ng"&&(query["granule_ur"]?.Contains("_corr_",StringComparison.Ordinal)==true||query["concept_id"]=="G2663884932-ORNL_CLOUD")?"companions":"granules";
            var nasa=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata",$"nasa-{family}-{type}.json")))!;
            if(type=="collections")nasa["feed"]!["entry"]![0]!["dataset_id"]="SYNTHETIC NASA browser test · "+family;
            if(uri.Query.Contains("concept_id=")){var id=System.Web.HttpUtility.ParseQueryString(uri.Query)["concept_id"];var entries=nasa["feed"]!["entry"]!.AsArray();nasa["feed"]!["entry"]=new JsonArray(entries.Where(e=>e!["id"]!.GetValue<string>()==id).Select(e=>e!.DeepClone()).ToArray());}
            else if(type=="granules"){
                var entries=nasa["feed"]!["entry"]!.AsArray();var descending=query["sort_key[]"]?.Contains("-start_date")==true;
                var ordered=descending?entries.OrderByDescending(e=>e!["time_start"]!.GetValue<string>()).ThenBy(e=>e!["id"]!.GetValue<string>()):entries.OrderBy(e=>e!["time_start"]!.GetValue<string>()).ThenBy(e=>e!["id"]!.GetValue<string>());
                nasa["feed"]!["entry"]=new JsonArray(ordered.Select(e=>e!.DeepClone()).ToArray());
            }
            return Json(nasa.ToJsonString());
        }
        if(uri.Host=="data.ornldaac.earthdata.nasa.gov")
        {
            if(request.Headers.Authorization?.Parameter!=FixtureEarthdata.Token)return new(HttpStatusCode.Unauthorized);
            if(Mode=="outage")return new(HttpStatusCode.ServiceUnavailable);
            if(Mode=="wait"&&request.Method==HttpMethod.Get)await Task.Delay(Timeout.Infinite,token);
            var family=uri.AbsolutePath.Contains("Classic")?"classic":"ng";var item=NasaFixtureFiles.Item(family,Path.GetFileName(uri.AbsolutePath));var folder=Path.Combine(fixtureFolder,"NASA-"+family);
            var header=Path.Combine(folder,item.Nasa!.Assets.First(a=>a.Role=="Original numeric binary").Name[..^4]+".hdr");
            if(!File.Exists(header))NasaFixtureFiles.Cube(folder,item,family=="classic"?224:425);
            var name=Path.GetFileName(uri.AbsolutePath);var local=Path.Combine(folder,name);
            var originalBytes=!File.Exists(local)&&name.EndsWith(".hdr.sha256",StringComparison.Ordinal)?System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(local[..^7]))).ToLowerInvariant()+"  "+name[..^7]):File.ReadAllBytes(local);
            var originalResponse=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(request.Method==HttpMethod.Head?[]:originalBytes)};
            originalResponse.Content.Headers.ContentLength=originalBytes.Length;originalResponse.Headers.ETag=new("\"synthetic-browser-original-v1\"");originalResponse.Headers.AcceptRanges.Add("bytes");return originalResponse;
        }
        if(uri.Host=="tigerweb.geo.census.gov")return Json(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata",uri.Query.Contains("returnGeometry=false")?uri.Query.Contains("COUNTY")?"census-new-mexico-counties.json":"census-states.json":"census-county.json")));
        if(uri.AbsolutePath.Contains("/sas/"))return Json(System.Text.Json.JsonSerializer.Serialize(new{href=System.Web.HttpUtility.ParseQueryString(uri.Query)["href"]}));
        if(uri.AbsolutePath.Contains("ImageServer")&&!uri.AbsolutePath.Contains("exportImage"))return Json(System.Text.Json.JsonSerializer.Serialize(new{pixelType="F32",bandCount=1,description="SYNTHETIC browser test service revision "+Revision}));
        if(uri.AbsolutePath.EndsWith("/search")||uri.AbsolutePath.Contains("/items/"))
        {
            if(Mode=="catalog-outage")return new(HttpStatusCode.ServiceUnavailable);
            var file=uri.Host=="earth-search.aws.element84.com"?"sentinel":uri.PathAndQuery.Contains("landsat")?"landsat":"copdem";
            var f=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata",file+".json")))!;f["properties"]!["aeroscience:test_fixture"]=true;
            return uri.AbsolutePath.Contains("/items/")?Json(f.ToJsonString()):Json(new JsonObject{["features"]=Mode=="catalog-empty"?new JsonArray():new JsonArray(f),["links"]=new JsonArray()}.ToJsonString());
        }
        if(Mode=="wait")await Task.Delay(Timeout.Infinite,token);
        if(Mode=="outage")return new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("SYNTHETIC provider outage")};
        byte[] bytes;
        if(uri.AbsolutePath.Contains("exportImage"))bytes=File.ReadAllBytes(Path.Combine(fixtureFolder,"Terrain","elevation.tif"));
        else
        {
            var name=uri.AbsolutePath.Split('/').Last();var kind=uri.Host=="sentinel-cogs.s3.us-west-2.amazonaws.com"?"Multispectral":uri.Host=="landsateuwest.blob.core.windows.net"?"Thermal":"Terrain";
            var band=kind=="Terrain"?"elevation":kind=="Thermal"?name.Contains("QA_PIXEL")?"QA_PIXEL":name.Contains("QA_RADSAT")?"QA_RADSAT":"ST_B10":name.Contains("B02")?"B02":name.Contains("B03")?"B03":name.Contains("B04")?"B04":name.Contains("B08")?"B08":"SCL";
            bytes=File.ReadAllBytes(Path.Combine(fixtureFolder,kind,band+".tif"));
        }
        if(request.Headers.Range is null)return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
        var range=request.Headers.Range.Ranges.Single();var from=range.From!.Value;var to=Math.Min(range.To!.Value,bytes.Length-1);var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new ByteArrayContent(bytes[(int)from..((int)to+1)])};response.Headers.ETag=new("\"synthetic-browser-v1\"");response.Content.Headers.ContentRange=new(from,to,bytes.Length);return response;
    }
    protected override void Dispose(bool disposing){if(disposing)fallback.Dispose();base.Dispose(disposing);}
}
// Only the unpublished test host can supply this explicit synthetic credential.
internal sealed class FixtureEarthdata:IEarthdataCredentials
{
    public const string Token="synthetic_browser_earthdata_token";
    public bool Available=>true;public bool Connected;
    public string? ReadToken()=>Connected?Token:null;
    public void Clear()=>Connected=false;
}
