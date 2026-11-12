using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed class LidarCatalog(AutomaticScientificData automatic,ScienceStore science,ScientificAccessSettings settings)
{
    public const string Bucket="https://usgs-lidar-public.s3-us-west-2.amazonaws.com/";
    public const string Official="https://index.nationalmap.gov/arcgis/rest/services/3DEPElevationIndex/MapServer/24/query";
    private static readonly Regex ProjectName=new("^[A-Za-z0-9][A-Za-z0-9_-]{1,159}$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
    public static void Project(string key){if(key is null||!ProjectName.IsMatch(key))throw new ArgumentException("Use the exact public EPT project identifier, without a URL, path or Requester Pays authorization.");}
    public static void ValidateUrl(Uri u)
    {
        if(u.Scheme!="https"||u.Port!=443||u.UserInfo.Length!=0||u.Fragment.Length!=0||u.AbsoluteUri.Length>8192)throw new IOException("Unsupported LiDAR endpoint; no request sent.");
        if(u.Host=="index.nationalmap.gov"&&u.AbsolutePath==new Uri(Official).AbsolutePath)return;
        if(u.Host!="usgs-lidar-public.s3-us-west-2.amazonaws.com")throw new IOException("Only the public EPT bucket is supported. Raw usgs-lidar and Requester Pays endpoints are never accessed.");
        var parts=u.AbsolutePath.Trim('/').Split('/');
        if(u.AbsolutePath=="/"&&Regex.IsMatch(u.Query,"^\\?list-type=2&prefix=[A-Za-z0-9_-]{3,40}&delimiter=%2F&max-keys=5(&continuation-token=[A-Za-z0-9%_.~-]{1,6144})?$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))return;
        if(u.Query.Length!=0||parts.Length is <2 or >3||!ProjectName.IsMatch(parts[0]))throw new IOException("Unsupported bounded public EPT path.");
        if(parts.Length==2&&parts[1]=="ept.json")return;
        if(parts.Length==3&&parts[1] is "ept-hierarchy" or "ept-data"&&Regex.IsMatch(parts[2],parts[1]=="ept-data"?"^[0-9]{1,2}-[0-9]{1,8}-[0-9]{1,8}-[0-9]{1,8}\\.laz$":"^[0-9]{1,2}-[0-9]{1,8}-[0-9]{1,8}-[0-9]{1,8}\\.json$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))return;
        throw new IOException("Unsupported EPT asset. No provider-supplied URL or source substitution is followed.");
    }
    private void Consent()
    {var p=settings.Get().Preferences;if(!p.OnlineEnabled||!p.PrivacyAccepted)throw new InvalidOperationException("Scientific online discovery requires the existing online-access and privacy consent. Retained LiDAR remains usable offline.");}
    public async Task<LidarEptProject> Verify(string project,ScientificDownloadBudget budget,CancellationToken token=default)
    {
        Project(project);Consent();var bytes=await automatic.Http.LidarBytes(new(Bucket+project+"/ept.json"),65536,budget,token);
        using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=24});var j=doc.RootElement;
        if(j.GetProperty("dataType").GetString()!="laszip"||j.GetProperty("hierarchyType").GetString()!="json"||j.GetProperty("version").GetString() is not("1.0.0" or "1.1.0"))throw new IOException("EPT requires a supported LASzip/JSON hierarchy; no decoder or source is substituted.");
        var crs=j.GetProperty("srs");if(crs.GetProperty("authority").GetString()!="EPSG")throw new IOException("EPT horizontal authority is unsupported.");var code="EPSG:"+crs.GetProperty("horizontal").GetString();
        if(!LidarCoordinates.Supported(code))throw new IOException("EPT horizontal transformation is unsupported; the original source is not relabeled WGS84.");
        var b=j.GetProperty("boundsConforming").EnumerateArray().Select(x=>x.GetDouble()).ToArray();var cube=j.GetProperty("bounds").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
        if(b.Length!=6||cube.Length!=6||b.Concat(cube).Any(x=>!double.IsFinite(x))||Enumerable.Range(0,3).Any(i=>b[i]>b[i+3]||cube[i]>=cube[i+3]||b[i]<cube[i]||b[i+3]>cube[i+3]))throw new IOException("Invalid EPT source bounds.");
        var h=new LidarLasHeader(4,6,30,1,true,[1,1,1],[0,0,0],b,code,"Unknown","unknown",null,[],false,[],[]);var domain=LidarCoordinates.Domain(h);
        var points=j.GetProperty("points").GetInt64();var dimensions=j.GetProperty("schema");if(points<1||points>10000000000000||dimensions.GetArrayLength() is <3 or >128)throw new IOException("EPT point-count/schema bounds are unsupported.");
        var names=dimensions.EnumerateArray().Select(d=>d.GetProperty("name").GetString()!).ToArray();if(names.Any(n=>n.Length is <1 or >80)||names.Distinct().Count()!=names.Length||new[]{"X","Y","Z"}.Except(names).Any())throw new IOException("EPT lacks a unique original XYZ schema.");
        foreach(var d in dimensions.EnumerateArray()){if(d.GetProperty("size").GetInt32() is <1 or >8||d.GetProperty("type").GetString() is not("signed" or "unsigned" or "float"))throw new IOException("Unsupported EPT dimension encoding.");}
        var vertical=crs.TryGetProperty("vertical",out var v)&&v.ValueKind==JsonValueKind.String?"Original EPT vertical declaration: "+v.GetString():"Unknown (EPT metadata does not establish vertical datum or height units)";
        return new(project,Convert.ToHexStringLower(SHA256.HashData(bytes)),System.Text.Encoding.UTF8.GetString(bytes),domain,code,vertical,points,names,"Available",[
            "Verified public EPT metadata, not proof of point density or valid-data coverage in every part of its bounds.","Original acquisition/publication dates are unknown unless linked original metadata establishes them; project names and retrieval time are not survey dates.","EPT reprojection describes delivered coordinates. Native pre-Entwine contractor coordinates are not reconstructed. Vertical values are never transformed.","Public EPT coverage differs from raw 3DEP coverage. No Requester Pays bucket or paid request is supported."]);
    }
    public async Task<LidarDiscovery> Discover(LidarDiscoveryRequest request,CancellationToken token=default)
    {
        ScientificCatalog.ValidateBounds(request.Area);if(request.Area.East-request.Area.West>1||request.Area.North-request.Area.South>1)throw new ArgumentException("Search a region no larger than one degree per side; split the dateline.");
        if(request.Cursor?.Length>2048||request.Prefix is {} prefix&&!Regex.IsMatch(prefix,"^[A-Za-z0-9_-]{3,40}$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))throw new ArgumentException("Use a bounded project prefix (at least three characters), and the returned page cursor.");
        if(request.Project is {} project)Project(project);
        var key=ScientificCatalog.Hash(new{lidarDiscovery=1,request.Area,request.Prefix,request.Cursor,request.Project});var path=Path.Combine(science.DirectoryPath,"Metadata","Catalog",key+".json");
        if(File.Exists(path)&&request.CachedOnly){if(new FileInfo(path).Length>2097152)throw new IOException("Cached LiDAR metadata exceeds its bound.");return JsonSerializer.Deserialize<LidarDiscovery>(File.ReadAllText(path),WireJson.Options)! with{Cached=true};}
        if(request.CachedOnly)throw new IOException("This bounded LiDAR query is not cached. No network access was made.");Consent();
        var budget=new ScientificDownloadBudget(Math.Min(4194304,settings.Get().Preferences.MaximumDownloadBytes));
        var a=request.Area;string N(double n)=>n.ToString("R",CultureInfo.InvariantCulture);
        var url=Official+"?f=geojson&where=1%3D1&geometry="+Uri.EscapeDataString(string.Join(',',N(a.West),N(a.South),N(a.East),N(a.North)))+"&geometryType=esriGeometryEnvelope&inSR=4326&outSR=4326&spatialRel=esriSpatialRelIntersects&outFields=OBJECTID,workunit,workunit_id,project,project_id,collect_start,collect_end,horiz_crs,vert_crs,geoid,lpc_pub_date,ql,lpc_category,lpc_reason,metadata_link&returnGeometry=true&resultRecordCount=5";
        using var official=JsonDocument.Parse(await automatic.Http.LidarBytes(new(url),1048576,budget,token),new JsonDocumentOptions{MaxDepth=32});
        if(!official.RootElement.TryGetProperty("features",out var fs)||fs.GetArrayLength()>5)throw new IOException("Official USGS discovery response is unsupported; no availability was inferred.");var work=fs.EnumerateArray().Select(f=>f.Clone()).ToArray();
        var chosen=request.Prefix??work.Select(f=>f.GetProperty("properties").GetProperty("project").GetString()).Where(p=>p is not null&&Regex.IsMatch(p,"^[A-Z]{2}_",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1))).Select(p=>p![..3]).FirstOrDefault();
        var projects=new List<LidarEptProject>();var unavailable=new List<string>();string? next=null;
        if(request.Project is {} exact)projects.Add(await Verify(exact,budget,token));
        else if(chosen is not null)
        {
            var query=Bucket+"?list-type=2&prefix="+Uri.EscapeDataString(chosen)+"&delimiter=%2F&max-keys=5"+(request.Cursor is null?"":"&continuation-token="+Uri.EscapeDataString(request.Cursor));
            var bytes=await automatic.Http.LidarBytes(new(query),65536,budget,token);using var reader=System.Xml.XmlReader.Create(new MemoryStream(bytes),new(){DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536});var xml=XDocument.Load(reader);XNamespace ns="http://s3.amazonaws.com/doc/2006-03-01/";
            var keys=xml.Root!.Elements(ns+"CommonPrefixes").Select(e=>e.Element(ns+"Prefix")!.Value.TrimEnd('/')).ToArray();if(keys.Length>5)throw new IOException("Public EPT listing exceeded its five-project bound.");
            foreach(var p in keys){try{var item=await Verify(p,budget,token);if(Intersects(item.Bounds,a))projects.Add(item);}catch(Exception e)when(e is IOException or JsonException or KeyNotFoundException){unavailable.Add(p+": "+(e is JsonException or KeyNotFoundException?"Unsupported original EPT metadata":e.Message));}}
            next=xml.Root.Element(ns+"NextContinuationToken")?.Value;
        }
        var result=new LidarDiscovery(projects.OrderBy(p=>p.Project,StringComparer.Ordinal).ToArray(),work,next,chosen,DateTimeOffset.UtcNow,false,["At most five official work units and five public EPT prefixes per page. This is not a complete national inventory or a guarantee of local point density.","Official work-unit polygons and acquisition/publication dates remain separate from EPT project metadata unless an exact original association is established; overlapping surveys are not blended.","Only metadata was requested. Numeric point retrieval requires a separate reviewed Library job and explicit approval."],unavailable.ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);PreferenceStore.WriteAtomic(path,result);foreach(var old in new DirectoryInfo(Path.GetDirectoryName(path)!).GetFiles("*.json").Where(f=>ScientificCatalog.IsCacheFileName(f.Name)).OrderByDescending(f=>f.LastWriteTimeUtc).Skip(128))old.Delete();return result;
    }
    internal static bool Intersects(GeographicBounds a,GeographicBounds b)=>a.West<=b.East&&a.East>=b.West&&a.South<=b.North&&a.North>=b.South;
}

public sealed partial class ScientificHttp
{
    public async Task<byte[]> LidarBytes(Uri uri,int maximum,ScientificDownloadBudget budget,CancellationToken token)
    {
        if(maximum is <1 or >8388608)throw new ArgumentException("LiDAR responses must be bounded to at most 8 MiB.");
        budget.EnsureCapacity(1);using var response=await SendCore(uri,System.Net.Http.HttpMethod.Get,null,null,null,token,validate:LidarCatalog.ValidateUrl);
        if(!response.IsSuccessStatusCode||response.Content.Headers.ContentEncoding.Any(c=>c!="identity"))throw new IOException("Public LiDAR source is unavailable or returned an unsupported representation. No redirect/alternate survey was followed.");
        var length=response.Content.Headers.ContentLength;if(length>maximum)throw new IOException("EPT response exceeds this reviewed regional limit.");
        using var input=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();var buffer=new byte[65536];
        while(true){var remaining=Math.Min(maximum-output.Length,budget.Maximum-budget.Bytes);var count=(int)Math.Min(buffer.Length,remaining);if(length is {} n){if(output.Length==n)break;count=(int)Math.Min(count,n-output.Length);}if(count<=0){budget.EnsureCapacity(1);count=1;}budget.EnsureCapacity(count);var read=await input.ReadAsync(buffer.AsMemory(0,count),token);if(read==0)break;budget.Charge(read);Received(uri,read,uri.AbsolutePath.EndsWith(".laz",StringComparison.Ordinal));if(output.Length+read>maximum)throw new IOException("EPT response exceeded its bounded size.");output.Write(buffer,0,read);}
        if(length is {} expected&&output.Length!=expected)throw new EndOfStreamException("EPT response ended early; transferred bytes remain charged.");return output.ToArray();
    }
}
