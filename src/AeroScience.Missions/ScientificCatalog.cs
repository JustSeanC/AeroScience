using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Shared;
namespace AeroScience.Missions;

// The Library shares consent, the HTTP allow-list and the automatic numeric asset parser.
public sealed class ScientificCatalog(AutomaticScientificData automatic,ScienceStore science,ScientificAccessSettings settings)
{
    public static readonly ScientificCatalogProvider[] Providers=[
        new("usgs-3dep","USGS 3DEP","Multi-resolution bare-earth elevation",RemoteKind.Terrain,["elevation"],false,false,"Bounded composite numeric export, not a dated scene catalog. Survey dates, native resolution and composite vertical datum are unknown."),
        new("copernicus","Copernicus GLO-30","GLO-30 surface elevation",RemoteKind.Terrain,["elevation"],false,true,"DSM includes buildings/vegetation; EGM2008 heights, not a bare-earth DEM."),
        new("landsat","Landsat / Planetary Computer","Collection 2 Level-2 Surface Temperature",RemoteKind.Thermal,["ST_B10","QA_PIXEL","QA_RADSAT"],true,true,"L2SP numeric temperature and integer QA; historical Earth observations, not aircraft imagery."),
        new("sentinel-2","Copernicus / Element 84","Sentinel-2 Level-2A",RemoteKind.Multispectral,["B02","B03","B04","B08","SCL"],true,true,"Native reflectance and product-specific scaling; mandatory native SCL mask.")];
    private sealed record Cached(ScientificCatalogQuery Query,ScientificCatalogPage Page,string? NextUrl);
    private readonly SemaphoreSlim gate=new(1);
    public static ScientificCatalogProvider Provider(string id)=>Providers.SingleOrDefault(p=>p.Id==id)??throw new ArgumentException("Select a supported scientific provider.");
    internal static string Hash<T>(T value)
    {
        static JsonNode? Ordered(JsonNode? n)=>n switch{JsonObject o=>new JsonObject(o.OrderBy(k=>k.Key,StringComparer.Ordinal).Select(k=>KeyValuePair.Create(k.Key,Ordered(k.Value)))),JsonArray a=>new JsonArray(a.Select(Ordered).ToArray()),_=>n?.DeepClone()};
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Ordered(JsonSerializer.SerializeToNode(value,WireJson.Options))!.ToJsonString()))).ToLowerInvariant();
    }
    private static string Root(string id)=>id=="sentinel-2"?"https://earth-search.aws.element84.com/v1":"https://planetarycomputer.microsoft.com/api/stac/v1";
    private static string Collection(string id)=>id switch{"sentinel-2"=>"sentinel-2-l2a","landsat"=>"landsat-c2-l2",_=>"cop-dem-glo-30"};
    private string CacheFolder=>Path.Combine(science.DirectoryPath,"Metadata","Catalog");
    public object Local()=>science.Availability().Select(a=>new{a.Source.Id,a.FilesAvailable,a.Source.Manifest,
        bounds=a.Source.Grids.Values.Select(ScientificRaster.GridCoverage).ToArray(),status=a.FilesAvailable?"Retained":"Unavailable",limitation="Retained footprint; scientific QA is still applied at sampling."}).ToArray();
    private void Consent()
    {
        var p=settings.Get().Preferences;
        if(!p.OnlineEnabled||!p.PrivacyAccepted)throw new InvalidOperationException("Scientific online access requires separate privacy consent. Cached searches and retained sources remain available offline.");
        if(p.UnmeteredOnly&&settings.IsUnmetered()!=true)throw new InvalidOperationException("Waiting for a reliably detected unmetered connection.");
    }
    public static void ValidateBounds(GeographicBounds a)
    {
        if(new[]{a.West,a.South,a.East,a.North}.Any(v=>!double.IsFinite(v))||a.West< -180||a.East>180||a.South< -80||a.North>84||a.East<=a.West||a.North<=a.South||a.East-a.West>15||a.North-a.South>15)
            throw new ArgumentException("Choose a bounded region up to 15° per side within 80°S–84°N. Split dateline-crossing, polar or larger regions.");
    }
    public async Task<ScientificCatalogPage> Search(ScientificCatalogQuery query,CancellationToken token=default)
    {
        var provider=Provider(query.Provider);ValidateBounds(query.Area);
        if(query.PageSize is <1 or >30||!double.IsFinite(query.MaximumCloudPercent)||query.MaximumCloudPercent is <0 or >100||query.FromUtc>query.ToUtc||query.FromUtc.HasValue!=query.ToUtc.HasValue||query.ToUtc-query.FromUtc>TimeSpan.FromDays(3650))throw new ArgumentException("Choose 1–30 results and an ordered date range no longer than ten years.");
        if(query.Cursor is {} c&&(c.Length!=32||!c.All(char.IsAsciiHexDigit)))throw new ArgumentException("Invalid catalog cursor.");
        var normalized=query with{Cursor=null,CachedOnly=false};var key=Hash(new{normalized,query.Cursor});
        await gate.WaitAsync(token);
        try
        {
            if(science.AvailabilityError() is {} unavailable)throw new IOException(unavailable);
            var path=Path.Combine(CacheFolder,key+".json");Cached? saved=null;
            if(File.Exists(path))try{saved=JsonSerializer.Deserialize<Cached>(await File.ReadAllTextAsync(path,token),WireJson.Options);}catch(JsonException){}
            if(query.CachedOnly)return saved is null?new("Not searched",[],null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,true,["No cached search for these filters; no network request was made."]):saved.Page with{Cached=true,Status="Cached"};
            if(saved is not null&&saved.Page.ExpiresUtc>DateTimeOffset.UtcNow)return saved.Page with{Cached=true};
            Consent();var budget=new ScientificDownloadBudget(Math.Min(settings.Get().Preferences.MaximumDownloadBytes,8388608));
            string url;
            if(query.Cursor is {} cursor)
            {
                var cursorPath=Path.Combine(CacheFolder,"cursor-"+cursor+".json");
                if(!File.Exists(cursorPath))throw new ArgumentException("Catalog cursor expired; search again.");
                var previous=JsonSerializer.Deserialize<Cached>(await File.ReadAllTextAsync(cursorPath,token),WireJson.Options)!;
                if(Hash(previous.Query)!=Hash(normalized)||previous.Page.ExpiresUtc<DateTimeOffset.UtcNow||previous.NextUrl is null)throw new ArgumentException("Catalog cursor does not match this search or has expired.");url=previous.NextUrl;
            }
            else url=Root(provider.Id)+"/search?collections="+Collection(provider.Id)+"&bbox="+StacScientificProvider.Bounds(query.Area)+"&limit="+query.PageSize;
            if(query.Cursor is null&&provider.SceneDates)
            {
                var to=query.ToUtc??DateTimeOffset.UtcNow;var from=query.FromUtc??to.AddDays(-365);
                url+="&datetime="+Uri.EscapeDataString(from.ToUniversalTime().ToString("O")+"/"+to.ToUniversalTime().ToString("O"))+"&query="+Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string,object>{{"eo:cloud_cover",new{lte=query.MaximumCloudPercent}}}));
            }
            var items=new List<ScientificCatalogItem>();string? next=null;var limitations=new List<string>{"Catalog outlines do not establish QA-valid pixels. Original asset sizes, where known, are not subset transfer estimates."};
            if(provider.Id=="usgs-3dep")
            {
                var d=await automatic.Provider("usgs-3dep").Discover(RemoteKind.Terrain,query.Area,settings.Get().Preferences,budget,token);
                if(d is not null)items.Add(Describe(provider,d,null));limitations.Add(provider.Description);
            }
            else
            {
                using var json=await automatic.Http.Json(url,budget,token);var features=json.RootElement.GetProperty("features");
                if(features.ValueKind!=JsonValueKind.Array||features.GetArrayLength()>100)throw new IOException("Provider exceeded bounded catalog response limits.");
                foreach(var feature in features.EnumerateArray().Take(query.PageSize))
                {
                    if(provider.Id=="landsat"&&(!feature.GetProperty("properties").TryGetProperty("landsat:correction",out var correction)||correction.GetString()!="L2SP"))continue;
                    try{var d=Stac(provider.Id).Describe(feature,provider.Kind,query.Area);if(d is not null)items.Add(Describe(provider,d,feature));}
                    catch(Exception e)when(e is IOException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException){limitations.Add("An item lacked supported numeric assets or scientific metadata and was excluded.");}
                }
                if(json.RootElement.TryGetProperty("links",out var links))foreach(var link in links.EnumerateArray())if(link.GetProperty("rel").GetString()=="next")
                {
                    if(link.TryGetProperty("method",out var method)&&method.GetString()!="GET"){limitations.Add("Provider pagination method unsupported; narrow this search.");break;}
                    var uri=new Uri(new Uri(url),link.GetProperty("href").GetString()!);ScientificHttp.ValidateUrl(uri);
                    if(uri.GetLeftPart(UriPartial.Path)!=Root(provider.Id)+"/search"||uri.Query.Contains("sig=",StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe catalog pagination link refused.");next=uri.AbsoluteUri;break;
                }
            }
            var now=DateTimeOffset.UtcNow;var cursorId=next is null?null:Guid.NewGuid().ToString("N");
            var page=new ScientificCatalogPage(items.Count>0?"Available":"No match",items.ToArray(),cursorId,now,now.AddMinutes(15),false,limitations.Distinct().ToArray());
            Directory.CreateDirectory(CacheFolder);var cached=new Cached(normalized,page,next);PreferenceStore.WriteAtomic(path,cached);
            if(cursorId is not null)PreferenceStore.WriteAtomic(Path.Combine(CacheFolder,"cursor-"+cursorId+".json"),cached);
            foreach(var old in new DirectoryInfo(CacheFolder).GetFiles("*.json").OrderByDescending(f=>f.LastWriteTimeUtc).Skip(128))old.Delete();return page;
        }
        catch(Exception e)when(e is HttpRequestException or IOException or JsonException or KeyNotFoundException)
        {return new("Unavailable",[],null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,false,["This search did not establish current availability. Retained sources remain separate."],e is HttpRequestException?"Provider network unavailable.":e is JsonException or KeyNotFoundException?"Unsupported provider metadata.":e.Message);}
        finally{gate.Release();}
    }
    private StacScientificProvider Stac(string provider)=>(StacScientificProvider)automatic.Provider(provider=="sentinel-2"?"earth-search":"planetary-computer");
    internal async Task<(ScientificCatalogItem Item,ScientificDiscovery Discovery)> Resolve(ScientificSceneSelection selection,GeographicBounds area,ScientificDownloadBudget budget,CancellationToken token,bool requireCoverage=true)
    {
        var provider=Provider(selection.Provider);AutomaticScientificData.ValidateArea(area);Consent();
        if(!selection.Bands.Order().SequenceEqual(provider.RequiredBands.Order())||selection.Scene.Length>300||selection.Scene.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '_' or '-' or '.' or ',')))throw new ArgumentException("Choose supported numeric measurements and mandatory quality bands.");
        ScientificDiscovery d;ScientificCatalogItem item;
        if(provider.Id=="usgs-3dep")
        {d=await automatic.Provider("usgs-3dep").Discover(provider.Kind,area,settings.Get().Preferences,budget,token)??throw new IOException("USGS composite unavailable for this area.");item=Describe(provider,d,null);}
        else
        {
            using var json=await automatic.Http.Json(Root(provider.Id)+"/collections/"+Collection(provider.Id)+"/items/"+Uri.EscapeDataString(selection.Scene),budget,token);var f=json.RootElement;
            if(f.GetProperty("id").GetString()!=selection.Scene||provider.Id=="landsat"&&f.GetProperty("properties").GetProperty("landsat:correction").GetString()!="L2SP")throw new IOException("Provider returned a different or unsupported product.");
            if(requireCoverage&&!area.Corners.Append(area.Center).All(p=>StacScientificProvider.Contains(f.GetProperty("geometry"),p)))throw new ArgumentException("Selected scene does not cover this work unit. Select cells inside its footprint.");
            d=Stac(provider.Id).Describe(f,provider.Kind,area)??throw new IOException("Selected scene lacks required numeric bands.");item=Describe(provider,d,f);
        }
        if(item.Revision!=selection.Revision)throw new InvalidOperationException("Source revision changed. Search, preview and approve again; no alternate scene was selected.");return(item,d with{CatalogRevision=item.Revision});
    }
    internal static ScientificCatalogItem Describe(ScientificCatalogProvider provider,ScientificDiscovery d,JsonElement? feature)
    {
        var props=feature?.GetProperty("properties");var geometry=feature?.GetProperty("geometry").Clone();var bounds=d.Area;
        if(feature is {} f&&f.TryGetProperty("bbox",out var box)&&box.GetArrayLength()==4)bounds=new(box[0].GetDouble(),box[1].GetDouble(),box[2].GetDouble(),box[3].GetDouble());
        var bands=d.Manifest.Bands.Select(b=>new ScientificCatalogBand(b.Id,b.Unit,b.Scale,b.Offset,b.NoData,provider.Id=="sentinel-2"?(b.Id=="SCL"?20:10):provider.Id=="landsat"||provider.Id=="copernicus"?30:null,b.Format,null)).ToArray();
        string? crs=null;if(props is {} p){if(p.TryGetProperty("proj:epsg",out var epsg))crs="EPSG:"+epsg;if(p.TryGetProperty("proj:code",out var code))crs=code.GetString();}
        var revision=Hash(new{provider.Id,scene=provider.Id=="usgs-3dep"?"composite":d.Manifest.SceneId,d.Manifest.ObservationUtc,d.Manifest.PublicationUtc,d.Manifest.VerticalDatum,bands=d.Manifest.Bands.Select(b=>b with{SceneId=provider.Id=="usgs-3dep"?"composite":b.SceneId}),metadata=feature is {} ff?new{properties=ff.GetProperty("properties"),assets=ff.GetProperty("assets"),geometry=ff.GetProperty("geometry")}:null});
        DateTimeOffset? published=d.Manifest.PublicationUtc;
        if(props is {} properties&&properties.TryGetProperty("updated",out var updated)&&DateTimeOffset.TryParse(updated.GetString(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var timestamp))published=timestamp.ToUniversalTime();
        return new(provider.Id,provider.Product,provider.Id=="usgs-3dep"?"3dep-composite":d.Manifest.SceneId,revision,provider.Kind,bounds,geometry,d.Manifest.ObservationUtc,published,bands,crs,d.Manifest.VerticalDatum,d.Quality,d.Resolution,d.Manifest.License,d.Manifest.Citation,provider.Id=="usgs-3dep"?"Bounded F32 numeric export":"HTTP range COG native-grid subset",d.Limitations??[]);
    }
}
