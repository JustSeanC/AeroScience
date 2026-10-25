using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public static partial class ScientificSourceSelection
{
    public static bool CoversArea(ScientificSource source,GeographicBounds area)=>source.Manifest.Swath is null && area.Corners.All(p=>source.Grids.Values.All(g=>ScientificRaster.GridCovers(g,new(p.Latitude+(area.Center.Latitude-p.Latitude)*.000001,p.Longitude+(area.Center.Longitude-p.Longitude)*.000001))));
    public static ScientificSource? Select(IEnumerable<SourceAvailability> available,RemoteKind kind,ScientificAccessPreferences? p=null,PayloadConfiguration? payload=null)
    {
        var candidates=available.Where(a=>a.FilesAvailable&&a.CoversPoint==true&&a.Source.Manifest.Kind==kind&&a.Source.Manifest.Swath is null).Select(a=>a.Source);
        if(payload is not null)candidates=candidates.Where(s=>ScientificCapabilities.Supports(payload,s));
        candidates=candidates.Where(s=>p?.ExcludedSourceIds?.Contains(s.Id)!=true);
        var mode=p?.SourceModes?.GetValueOrDefault(kind)??"automatic";
        if(kind==RemoteKind.Hyperspectral||mode=="local")candidates=candidates.Where(s=>s.Manifest.Acquisition is null);
        if(p is null)return candidates.OrderByDescending(s=>s.Manifest.ObservationUtc).FirstOrDefault();
        var latest=p.PreferredDateUtc??DateTimeOffset.UtcNow;
        candidates=candidates.Where(s=>s.Manifest.Acquisition is null||s.Manifest.ObservationUtc is null||s.Manifest.ObservationUtc<=latest&&s.Manifest.ObservationUtc>=latest.AddDays(-p.LookbackDays)).Where(s=>s.Manifest.Acquisition is null||Cloud(s)<=p.MaximumCloudPercent);
        return mode=="prefer-local"?candidates.OrderBy(s=>s.Manifest.Acquisition is null?0:1).ThenBy(Cloud).ThenByDescending(s=>s.Manifest.ObservationUtc).FirstOrDefault():p.SelectionPolicy=="newest"?candidates.OrderBy(s=>s.Manifest.Acquisition is null?1:0).ThenByDescending(s=>s.Manifest.ObservationUtc).FirstOrDefault():candidates.OrderBy(s=>s.Manifest.Acquisition is null?1:0).ThenBy(Cloud).ThenByDescending(s=>s.Manifest.ObservationUtc).FirstOrDefault();
    }
    private static double Cloud(ScientificSource s)
    {
        if(s.Manifest.Acquisition?.ProviderMetadataJson is not {} metadata)return 0;
        try{using var json=JsonDocument.Parse(metadata);return json.RootElement.TryGetProperty("properties",out var props)?StacScientificProvider.Number(props,"eo:cloud_cover",0):0;}
        catch(JsonException){return 100;}
    }
}
