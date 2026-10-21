using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed partial class ScientificCatalog
{
    // Official short names/versions verified against CMR, 2026-10-21. Resolve collection
    // concept/revision at query time: a renamed/replaced collection is not silently substituted.
    private static ScientificCatalogProvider[] NasaProviders=>[
        new("nasa-classic","NASA AVIRIS-Classic","Level-2 reflectance V1",RemoteKind.Hyperspectral,[],true,true,"Guided official acquisition and compatible ENVI verification. Historical NASA observations; format must be checked."),
        new("nasa-ng","NASA AVIRIS-NG","Level-2 reflectance V1",RemoteKind.Hyperspectral,[],true,true,"Guided official acquisition; NG companions can be separate granules. Compatible ENVI only, not every distribution."),
        new("nasa-emit","NASA EMIT","Level-2A reflectance V001",RemoteKind.Hyperspectral,[],true,true,"Discovery / official access only — native NetCDF-4 scientific import not yet supported.")];
    public static bool IsNasa(string id)=>id is "nasa-classic" or "nasa-ng" or "nasa-emit";
    public async Task<ScientificCatalogItem> ResolveNasa(NasaSelectionRequest request,CancellationToken token=default)
    {
        if(!IsNasa(request.Provider)||string.IsNullOrWhiteSpace(request.GranuleId)||string.IsNullOrWhiteSpace(request.Revision)||request.GranuleId.Length>100||!System.Text.RegularExpressions.Regex.IsMatch(request.GranuleId,@"^G[0-9]+-[A-Z0-9_]+$")||request.Revision.Length!=64||!request.Revision.All(char.IsAsciiHexDigit))throw new ArgumentException("Select an exact NASA catalog granule and revision.");
        Consent();
        var q=new ScientificCatalogQuery(request.Provider,new(0,0,.01,.01),PageSize:1,Refresh:true);
        var result=await SearchNasa(q,"https://cmr.earthdata.nasa.gov/search/granules.json?concept_id="+Uri.EscapeDataString(request.GranuleId)+"&page_size=1&page_num=1",new(Math.Min(settings.Get().Preferences.MaximumDownloadBytes,8388608)),token);
        var item=result.Items.SingleOrDefault(i=>i.Scene==request.GranuleId)??throw new IOException("NASA granule removed, unavailable or unsupported. Search again; no alternate scene was selected.");
        if(item.Revision!=request.Revision)throw new InvalidOperationException("NASA granule or collection changed. Search and review the revised product before continuing.");
        return item;
    }
    private static (string Name,string Version,string Family) NasaProduct(string id)=>id switch
    {
        "nasa-classic"=>("AVIRIS-Classic_L2_Reflectance_2154","1","AVIRIS-Classic"),
        "nasa-ng"=>("AVIRIS-NG_L2_Reflectance_2110","1","AVIRIS-NG"),
        "nasa-emit"=>("EMITL2ARFL","001","EMIT"),
        _=>throw new ArgumentException("Unsupported NASA product.")
    };
    private static string NasaSearchUrl(ScientificCatalogQuery q,int page)
    {
        var p=NasaProduct(q.Provider);
        var url="https://cmr.earthdata.nasa.gov/search/granules.json?short_name="+Uri.EscapeDataString(p.Name)+"&version="+p.Version+
            "&bounding_box="+StacScientificProvider.Bounds(q.Area)+"&page_size="+q.PageSize+"&page_num="+page+"&sort_key[]=start_date&sort_key[]=granule_ur";
        if(q.FromUtc is {} from&&q.ToUtc is {} to)url+="&temporal="+Uri.EscapeDataString(from.ToUniversalTime().ToString("O")+","+to.ToUniversalTime().ToString("O"));
        if(!string.IsNullOrWhiteSpace(q.SceneFilter))url+="&granule_ur="+Uri.EscapeDataString("*"+q.SceneFilter+"*")+"&options[granule_ur][pattern]=true";
        return url;
    }
    private async Task<(ScientificCatalogItem[] Items,string? Next,string[] Limitations)> SearchNasa(ScientificCatalogQuery q,string url,ScientificDownloadBudget budget,CancellationToken token)
    {
        var p=NasaProduct(q.Provider);var uri=new Uri(url);
        // Cursor URLs originate only here and are bound to the complete query by the shared cache.
        if(uri.GetLeftPart(UriPartial.Path)!="https://cmr.earthdata.nasa.gov/search/granules.json")throw new IOException("Unsafe NASA pagination endpoint.");
        using var collections=await automatic.Http.Json("https://cmr.earthdata.nasa.gov/search/collections.json?short_name="+Uri.EscapeDataString(p.Name)+"&version="+p.Version+"&page_size=2",budget,token,refresh:q.Refresh);
        var matches=collections.RootElement.GetProperty("feed").GetProperty("entry");
        if(matches.GetArrayLength()!=1)throw new IOException("NASA collection identity changed or is unavailable. No replacement product was selected.");
        var collection=matches[0];
        if(Text(collection,"short_name")!=p.Name||Text(collection,"version_id")!=p.Version)throw new IOException("NASA returned a different collection version.");
        using var granules=await automatic.Http.Json(url,budget,token,refresh:q.Refresh);
        var entries=granules.RootElement.GetProperty("feed").GetProperty("entry");
        if(entries.ValueKind!=JsonValueKind.Array||entries.GetArrayLength()>q.PageSize)throw new IOException("NASA exceeded the bounded page size.");
        var items=new List<ScientificCatalogItem>();var warnings=new List<string>{"NASA footprints are catalog geometry, not QA-valid pixel coverage. Browser transfers are not controlled or measured by AeroScience.","Granule size, when supplied by CMR, is the original distribution total, not a subset estimate. Spectral details are verified from original files before import."};
        foreach(var entry in entries.EnumerateArray())
        {
            try
            {
                var item=DescribeNasa(q.Provider,collection,entry);
                if(!uri.Query.Contains("concept_id=",StringComparison.Ordinal)&&
                    (item.Bounds.East<q.Area.West||item.Bounds.West>q.Area.East||item.Bounds.North<q.Area.South||item.Bounds.South>q.Area.North||
                     q.FromUtc is {} from&&(item.ObservationUtc is not {} observed||observed<from||observed>q.ToUtc)))
                {warnings.Add("A returned NASA record did not match the requested bounds/date interval and was excluded.");continue;}
                if(!items.Any(i=>i.Scene==item.Scene))items.Add(item);
            }
            catch(Exception e)when(e is ArgumentException or InvalidDataException or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
            {warnings.Add("A NASA record had missing, invalid or unsupported geometry/identity and was excluded. Split antimeridian searches; no footprint was invented.");}
        }
        var page=int.Parse(System.Web.HttpUtility.ParseQueryString(uri.Query)["page_num"]??"1",CultureInfo.InvariantCulture);
        // CMR documents page_num/page_size, not STAC next links. A full page permits a
        // next request (which can truthfully be empty). Bound exploration to 100 pages.
        var next=entries.GetArrayLength()==q.PageSize&&page<100?NasaSearchUrl(q,page+1):null;
        if(page==100)warnings.Add("NASA search reached 100 pages. Narrow dates, flightline or bounds.");
        return(items.ToArray(),next,warnings.ToArray());
    }
    private static string? Text(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    private static DateTimeOffset? Time(JsonElement e,string key)=>DateTimeOffset.TryParse(Text(e,key),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var t)?t.ToUniversalTime():null;
    internal static ScientificCatalogItem DescribeNasa(string provider,JsonElement collection,JsonElement entry)
    {
        var p=NasaProduct(provider);var id=Text(entry,"id")??throw new InvalidDataException("Missing granule identity.");
        var cid=Text(collection,"id")??throw new InvalidDataException("Missing collection identity.");
        var title=Text(entry,"title")??throw new InvalidDataException("Missing original flightline.");
        if(Text(entry,"collection_concept_id")!=cid||!id.StartsWith('G')||!cid.StartsWith('C'))throw new InvalidDataException("Different NASA collection.");
        var polygons=new List<double[][]>();
        if(entry.TryGetProperty("polygons",out var ps))foreach(var group in ps.EnumerateArray())foreach(var ring in group.EnumerateArray())
        {
            var numbers=ring.GetString()!.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(n=>double.Parse(n,CultureInfo.InvariantCulture)).ToArray();
            if(numbers.Length<8||numbers.Length%2!=0||numbers.Length>8192)throw new InvalidDataException("Invalid NASA footprint.");
            var pairs=Enumerable.Range(0,numbers.Length/2).Select(i=>new[]{numbers[i*2+1],numbers[i*2]}).ToArray();
            if(pairs.Any(v=>!MissionPlanning.Position(v[1],v[0]))||pairs.Zip(pairs.Skip(1)).Any(v=>Math.Abs(v.First[0]-v.Second[0])>180))throw new InvalidDataException("Unsupported antimeridian footprint.");
            if(!pairs[0].SequenceEqual(pairs[^1]))pairs=[..pairs,pairs[0]];polygons.Add(pairs);
        }
        if(polygons.Count==0)throw new InvalidDataException("No supported original polygon footprint.");
        var all=polygons.SelectMany(r=>r).ToArray();var bounds=new GeographicBounds(all.Min(v=>v[0]),all.Min(v=>v[1]),all.Max(v=>v[0]),all.Max(v=>v[1]));
        if(bounds.East<=bounds.West||bounds.North<=bounds.South||bounds.East-bounds.West>180)throw new InvalidDataException("Degenerate or unsupported NASA footprint.");
        var geometry=JsonSerializer.SerializeToElement(new{type="MultiPolygon",coordinates=polygons.Select(r=>new[]{r}).ToArray()});
        var assets=new List<NasaAsset>();
        if(entry.TryGetProperty("links",out var links))foreach(var link in links.EnumerateArray())
        {
            var href=Text(link,"href");if(!OfficialNasaLink(href))continue;
            var u=new Uri(href!);var name=Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath));
            if(Text(link,"rel")?.EndsWith("/data#",StringComparison.Ordinal)!=true||link.TryGetProperty("inherited",out var inherited)&&inherited.ValueKind==JsonValueKind.True)continue;
            var role=name.EndsWith(".hdr",StringComparison.OrdinalIgnoreCase)?"ENVI header":name.EndsWith(".bin",StringComparison.OrdinalIgnoreCase)?"Original numeric binary":name.EndsWith(".sha256",StringComparison.OrdinalIgnoreCase)?"Provider SHA-256":name.EndsWith(".nc",StringComparison.OrdinalIgnoreCase)?"Unsupported NetCDF-4":"Official access";
            assets.Add(new(name,u.AbsoluteUri,role));
        }
        long? bytes=null;if(double.TryParse(Text(entry,"granule_size"),NumberStyles.Float,CultureInfo.InvariantCulture,out var mb)&&double.IsFinite(mb)&&mb>0&&mb<long.MaxValue/1048576d)bytes=(long)Math.Ceiling(mb*1048576);
        var reference="https://search.earthdata.nasa.gov/search/granules?p="+cid+"&g="+id;
        var format=provider=="nasa-emit"?"NetCDF-4 (not imported)":assets.Any(a=>a.Role is "ENVI header" or "Original numeric binary")?"ENVI (individual companions; verify original header)":"Unknown; inspect official distribution";
        var reflectance=provider=="nasa-classic"||assets.Any(a=>a.Role is "ENVI header" or "Original numeric binary"&&System.Text.RegularExpressions.Regex.IsMatch(a.Name,@"_(corr|rfl|refl)_"));
        var capability=provider=="nasa-emit"?"Discovery / official access only — native scientific import not yet supported":reflectance?"Guided import — compatible original ENVI reflectance only; verify files before use":"Download only — auxiliary or unknown product; no supported hyperspectral reflectance import";
        var details=new NasaGranuleMetadata(p.Family,cid,p.Name,p.Version,id,title,provider=="nasa-emit"?"2A":"2",format,capability,
            "Individual NASA Earthdata authorization may be required; official browser handles login",bytes,null,null,null,assets.DistinctBy(a=>a.Url).ToArray(),Hash(collection),
            "https://cmr.earthdata.nasa.gov/search/concepts/"+id+".umm_json",Time(entry,"time_end"));
        return new(provider,Text(collection,"dataset_id")??p.Name,id,Hash(new{collection,entry}),RemoteKind.Hyperspectral,bounds,geometry,Time(entry,"time_start"),Time(entry,"updated"),[],null,"Unknown / not inferred",
            "Catalog footprint; source nodata, bad bands and per-pixel quality remain unverified.","Unknown at granule discovery; verify original grid",
            "Review NASA collection terms and citation at the official source before acquisition.",reference,"Official browser handoff",["Historical NASA reflectance, not simulated-aircraft measurements.","No missing wavelengths, units or QA values were inferred.",capability],details);
    }
    // Browser links only. This does NOT expand the backend ScientificHttp allow-list.
    public static bool OfficialNasaLink(string? value)=>Uri.TryCreate(value,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.Port==443&&u.UserInfo.Length==0&&u.Query.Length==0&&u.Fragment.Length==0&&u.AbsoluteUri.Length<=2048&&
        u.Host is "data.ornldaac.earthdata.nasa.gov" or "daac.ornl.gov" or "data.lpdaac.earthdatacloud.nasa.gov" or "earth.jpl.nasa.gov" or "aviris.jpl.nasa.gov" or "avirisng.jpl.nasa.gov";
}
