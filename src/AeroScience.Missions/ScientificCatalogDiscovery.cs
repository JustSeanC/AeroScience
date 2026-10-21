using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed partial class ScientificCatalog
{
    private static bool CatalogOrderVerified(List<ScientificCatalogItem> items,string sort)
    {
        if(items.Count==0)return false;
        if(sort=="cloud")return items.All(i=>i.CloudPercent is not null)&&items.Zip(items.Skip(1)).All(p=>p.First.CloudPercent<=p.Second.CloudPercent);
        return items.All(i=>i.ObservationUtc is not null)&&items.Zip(items.Skip(1)).All(p=>sort=="oldest"?p.First.ObservationUtc<=p.Second.ObservationUtc:p.First.ObservationUtc>=p.Second.ObservationUtc);
    }

    // Names and codes come from the same authoritative service as the boundary,
    // never an inferred polygon or arbitrary user-controlled lookup URL.
    public async Task<ScientificRegionNames> RegionNames(ScientificRegionNamesRequest request,CancellationToken token=default)
    {
        if(request.StateFips is {} code&&(code.Length!=2||!code.All(char.IsAsciiDigit)))throw new ArgumentException("Select a supported state.");
        if(science.AvailabilityError() is {} unavailable)throw new IOException(unavailable);
        var path=Path.Combine(CacheFolder,request.StateFips is null?"names-states.json":"names-counties-"+request.StateFips+".json");
        if(File.Exists(path))
        {
            var saved=JsonSerializer.Deserialize<ScientificRegionNames>(await File.ReadAllTextAsync(path,token),WireJson.Options)!;
            if(request.CachedOnly||saved.AsOfUtc>DateTimeOffset.UtcNow.AddDays(-7))return saved with{Cached=true};
        }
        if(request.CachedOnly)throw new IOException("These official region names are not cached. Use offline place search or a drawn area.");
        Consent();
        var counties=request.StateFips is not null;
        var url="https://tigerweb.geo.census.gov/arcgis/rest/services/Generalized_ACS2024/State_County/MapServer/"+(counties?"13":"9")+
            "/query?where="+Uri.EscapeDataString(counties?"STATE='"+request.StateFips+"'":"1=1")+
            "&outFields="+(counties?"NAME,STATE,COUNTY":"NAME,STATE")+"&returnGeometry=false&orderByFields=NAME&resultRecordCount="+(counties?"400":"100")+"&f=json";
        using var json=await automatic.Http.Json(url,new(Math.Min(8388608,settings.Get().Preferences.MaximumDownloadBytes)),token);
        if(!json.RootElement.TryGetProperty("features",out var features)||features.ValueKind!=JsonValueKind.Array||features.GetArrayLength()>(counties?400:100)||json.RootElement.TryGetProperty("exceededTransferLimit",out var limit)&&limit.ValueKind==JsonValueKind.True)
            throw new IOException("Official name lookup is incomplete or unavailable. Use a drawn area.");
        var names=new List<ScientificRegionName>();
        foreach(var feature in features.EnumerateArray())
        {
            var attributes=feature.GetProperty("attributes");var state=attributes.GetProperty("STATE").GetString();
            var name=attributes.GetProperty("NAME").GetString();var id=counties?attributes.GetProperty("COUNTY").GetString():state;
            if(state is null||state.Length!=2||!state.All(char.IsAsciiDigit)||counties&&state!=request.StateFips||id is null||id.Length!=(counties?3:2)||!id.All(char.IsAsciiDigit)||string.IsNullOrWhiteSpace(name)||name.Length>150||name.Any(char.IsControl))
                throw new IOException("Invalid official region names; no boundary was inferred.");
            names.Add(new(id,name));
        }
        if(names.Select(n=>n.Code).Distinct().Count()!=names.Count)throw new IOException("Conflicting official region identifiers.");
        var result=new ScientificRegionNames(names.OrderBy(n=>n.Name,StringComparer.Ordinal).ToArray(),url,DateTimeOffset.UtcNow,false);
        Directory.CreateDirectory(CacheFolder);PreferenceStore.WriteAtomic(path,result);return result;
    }
}
