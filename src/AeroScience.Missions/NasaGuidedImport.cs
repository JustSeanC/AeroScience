using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Shared;

namespace AeroScience.Missions;

// Product-bound preparation over the existing ENVI reader/importer, not a new science
// engine or downloader. Browser authorization/transfer stays with the official provider.
public static class NasaGuidedImport
{
    public static HyperspectralInspection Inspect(string selected,ScientificCatalogItem item,HyperspectralUserMetadata? additions=null,string? associated=null)
    {
        var n=item.Nasa??throw new InvalidDataException("Missing selected NASA granule.");
        if(item.Provider is not("nasa-classic" or "nasa-ng"))throw new InvalidDataException("EMIT native import is not supported.");
        var header=GuidedHyperspectralImport.FindHeader(selected);var stem=Path.GetFileNameWithoutExtension(header);
        var prefix=Regex.Match(n.Flightline,item.Provider=="nasa-classic"?@"f\d{6}t\d{2}p\d{2}r\d{2}":@"ang\d{8}t\d{6}").Value;
        if(prefix.Length==0||!stem.StartsWith(prefix+"_",StringComparison.Ordinal)||!Regex.IsMatch(stem,@"_(corr|rfl|refl)(_|$)"))throw new InvalidDataException("The selected header is not the selected NASA reflectance flightline. Do not select h2o, radiance, RGB, another scene or an incomplete download.");
        var expected=n.Assets.Where(a=>a.Role is "ENVI header" or "Original numeric binary").Select(a=>Path.GetFileNameWithoutExtension(a.Name)).Distinct().ToArray();
        if(!expected.Contains(stem,StringComparer.Ordinal))throw new InvalidDataException("Header identity does not match the exact selected NASA product revision. Select its matching original files or search for the correct granule.");
        var inspection=GuidedHyperspectralImport.Inspect(header,new(Name:additions?.Name??n.Flightline,Provider:"NASA "+n.Family+" / ORNL DAAC",Source:item.Reference,ObservationUtc:item.ObservationUtc,License:item.License,Notes:additions?.Notes,Unit:"reflectance"),associated);
        if(inspection.Manifest is null)return inspection;
        if(Path.GetFileNameWithoutExtension(inspection.Binary)!=stem)throw new InvalidDataException("NASA header and numeric binary must share the same original product stem.");
        var notes=JsonSerializer.Serialize(new{workflow="NASA official-browser acquisition followed by local ENVI verification",catalog=item,headerInspection=JsonSerializer.Deserialize<JsonElement>(inspection.Manifest.Notes!),identityLimitation="Matching metadata and filenames are not proof of NASA origin without an authoritative provider checksum. Retained file hashes establish local integrity.",userNotes=additions?.Notes},WireJson.Options);
        return inspection with{Manifest=inspection.Manifest with{SceneId=n.GranuleId,PublicationUtc=item.PublishedUtc,Notes=notes},Warnings=[..inspection.Warnings,"Original NASA observations; browser download progress/authentication is external. No new aircraft observations are created."]};
    }
}
