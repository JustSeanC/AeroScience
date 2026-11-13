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
        if(request.Project is {} exact){var item=await Verify(exact,budget,token);if(Intersects(item.Bounds,a))projects.Add(item);else unavailable.Add(exact+": verified source bounds do not intersect this study region.");}
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
    public async Task<LidarDownloadBundle> Plan(LidarDownloadRequest request,CancellationToken token=default)
    {
        ScientificCatalog.ValidateBounds(request.Area);Project(request.Project);
        if(request.MetadataSha256?.Length!=64||request.MaximumTransferBytes is <1048576 or >536870912||request.MaximumNodes is <1 or >64||request.MaximumCandidatePoints is <1 or >2000000||request.Area.East-request.Area.West>.1||request.Area.North-request.Area.South>.1)throw new ArgumentException("Review an exact public EPT revision, region ≤0.1 degree per side, 1–64 nodes, at most 2 million candidate points and an explicit transfer cap.");
        var budget=new ScientificDownloadBudget(Math.Min(8388608,settings.Get().Preferences.MaximumDownloadBytes));var source=await Verify(request.Project,budget,token);
        if(source.MetadataSha256!=request.MetadataSha256)throw new InvalidOperationException("Selected EPT metadata changed. Discover/review it again; no alternative survey is selected.");
        if(!Intersects(source.Bounds,request.Area))throw new InvalidOperationException("The selected EPT source does not intersect this bounded study region.");
        using var doc=JsonDocument.Parse(source.MetadataJson);var bounds=doc.RootElement.GetProperty("bounds").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
        var nodes=new SortedDictionary<string,long>(StringComparer.Ordinal);var hierarchy=new List<string>();var pending=new Queue<string>();var visited=new HashSet<string>(StringComparer.Ordinal);pending.Enqueue("0-0-0-0");
        bool Relevant(string key)
        {
            var v=key.Split('-');if(v.Length!=4||v.Any(s=>s.Length==0||s.Any(c=>!char.IsAsciiDigit(c))))throw new IOException("Unsupported EPT node identity.");
            var d=int.Parse(v[0],CultureInfo.InvariantCulture);if(d is <0 or >24)throw new IOException("EPT hierarchy depth exceeds the bounded reader.");var size=1L<<d;var xyz=v.Skip(1).Select(s=>long.Parse(s,CultureInfo.InvariantCulture)).ToArray();if(xyz.Any(n=>n<0||n>=size))throw new IOException("EPT node coordinate is outside its depth.");
            // Supported XY transforms are monotonic for Mercator/geographic. UTM
            // uses a conservative domain so candidates are never excluded by corners.
            if(source.HorizontalCrs is not("EPSG:4326" or "EPSG:3857"))return true;
            var x0=bounds[0]+(bounds[3]-bounds[0])*xyz[0]/size;var x1=bounds[0]+(bounds[3]-bounds[0])*(xyz[0]+1)/size;var y0=bounds[1]+(bounds[4]-bounds[1])*xyz[1]/size;var y1=bounds[1]+(bounds[4]-bounds[1])*(xyz[1]+1)/size;
            // Octree cube may extend beyond the valid projection domain. Clip the
            // prefilter only; actual decoded points must pass the strict transform.
            if(source.HorizontalCrs=="EPSG:3857"){x0=Math.Max(x0,-20037508.34278925);x1=Math.Min(x1,20037508.34278925);y0=Math.Max(y0,-20037508.34278925);y1=Math.Min(y1,20037508.34278925);}else{x0=Math.Max(x0,-180);x1=Math.Min(x1,180);y0=Math.Max(y0,-90);y1=Math.Min(y1,90);}
            if(x0>x1||y0>y1)return false;var lo=LidarCoordinates.Geographic(source.HorizontalCrs,x0,y0);var hi=LidarCoordinates.Geographic(source.HorizontalCrs,x1,y1);return Intersects(new(lo.Longitude,lo.Latitude,hi.Longitude,hi.Latitude),request.Area);
        }
        while(pending.TryDequeue(out var file))
        {
            token.ThrowIfCancellationRequested();if(!visited.Add(file))continue;if(visited.Count>8)throw new IOException("EPT hierarchy exceeds eight bounded pages. Select a smaller region.");
            var data=await automatic.Http.LidarBytes(new(Bucket+request.Project+"/ept-hierarchy/"+file+".json"),2097152,budget,token);hierarchy.Add(System.Text.Encoding.UTF8.GetString(data));
            using var json=JsonDocument.Parse(data,new JsonDocumentOptions{MaxDepth=4});if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new IOException("EPT hierarchy is not a supported object.");var entries=0;
            foreach(var property in json.RootElement.EnumerateObject())
            {
                if(++entries>100000)throw new IOException("EPT hierarchy entry bound exceeded.");if(!Relevant(property.Name))continue;var count=property.Value.GetInt64();
                if(count==-1){if(pending.Count+visited.Count>=16)throw new IOException("EPT hierarchy requires too many child pages; narrow the region.");pending.Enqueue(property.Name);continue;}
                if(count<0||count>LidarLas.MaximumPoints)throw new IOException("Unsupported EPT node point count.");if(count==0)continue;
                if(nodes.TryGetValue(property.Name,out var previous)&&previous!=count)throw new IOException("EPT hierarchy changes the same node count.");nodes[property.Name]=count;
                if(nodes.Count>request.MaximumNodes||nodes.Values.Sum()>request.MaximumCandidatePoints)throw new IOException("The full-density candidates exceed the reviewed node/point limits. Use a smaller study region; no lower-resolution level is silently substituted.");
            }
        }
        if(nodes.Count==0)throw new IOException("No supported EPT point nodes intersect this bounded region.");
        var selected=nodes.Select(p=>new LidarDownloadNode(p.Key,p.Value)).ToArray();var revision=ScientificCatalog.Hash(new{source.MetadataSha256,request.Area,nodes=selected,hierarchy});
        return new(source,request.Area,selected,hierarchy.ToArray(),revision,nodes.Values.Sum(),true,request.MaximumTransferBytes,["All hierarchy nodes intersecting the selected XY rectangle are included within the explicit caps. Rectangle/node candidates are overfetch; exact modeled-corridor clipping is a separate offline operation.","Compressed node byte sizes are unknown before transfer; the explicit job cap includes metadata/retries and may stop the job before all nodes complete. No invented ETA or density.","Delivery hashes bind retained bytes; EPT metadata and hierarchy snapshots do not invent contractor dates or vertical reference."]);
    }
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
