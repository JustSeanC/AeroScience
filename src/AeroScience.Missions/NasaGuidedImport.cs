using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AeroScience.Shared;

namespace AeroScience.Missions;

// Product-bound preparation over the existing ENVI reader/importer, not a new science
// engine or downloader. Browser authorization/transfer stays with the official provider.
public static class NasaGuidedImport
{
    public static HyperspectralInspection Inspect(string selected,ScientificCatalogItem item,HyperspectralUserMetadata? additions=null,string? associated=null,CancellationToken token=default)
    {
        var n=item.Nasa??throw new InvalidDataException("Missing selected NASA granule.");
        if(item.Provider is not("nasa-classic" or "nasa-ng"))throw new InvalidDataException("EMIT native import is not supported.");
        var header=GuidedHyperspectralImport.FindHeader(selected);_=ScientificRaster.SafeFile(Path.GetDirectoryName(header)!,Path.GetFileName(header));var stem=Path.GetFileNameWithoutExtension(header);
        var prefix=Regex.Match(n.Flightline,item.Provider=="nasa-classic"?@"f\d{6}t\d{2}p\d{2}r\d{2}":@"ang\d{8}t\d{6}").Value;
        if(prefix.Length==0||!stem.StartsWith(prefix+"_",StringComparison.Ordinal)||!Regex.IsMatch(stem,@"_(corr|rfl|refl)(_|$)"))throw new InvalidDataException("The selected header is not the selected NASA reflectance flightline. Do not select h2o, radiance, RGB, another scene or an incomplete download.");
        var expected=n.Assets.Where(a=>a.Role is "ENVI header" or "Original numeric binary").Select(a=>Path.GetFileNameWithoutExtension(a.Name)).Distinct().ToArray();
        if(!expected.Contains(stem,StringComparer.Ordinal))throw new InvalidDataException("Header identity does not match the exact selected NASA product revision. Select its matching original files or search for the correct granule.");
        var fields=ScientificRaster.ReadEnviFields(header);
        if(item.Provider=="nasa-classic")
        {
            if(!fields.TryGetValue("bands",out var count)||count.Trim()!="224")throw new InvalidDataException("This Classic adapter supports documented 224-band Level-2 reflectance only. Other distributions remain advanced/future formats.");
            if(fields.GetValueOrDefault("data type") is not("4" or "5"))throw new InvalidDataException("NASA Classic Level-2 reflectance requires documented Float32/Float64 original numeric values.");
        }
        if(fields.TryGetValue("acquisition time",out var date)&&DateTimeOffset.TryParse(date,CultureInfo.InvariantCulture,DateTimeStyles.None,out var original)&&
            (date.EndsWith('Z')||Regex.IsMatch(date,@"[+-]\d{2}:\d{2}$"))&&item.ObservationUtc is {} start&&
            (original<start||original>(n.ObservationEndUtc??start)))throw new InvalidDataException("Original header acquisition time conflicts with the selected NASA granule interval.");
        if(fields.TryGetValue("data units",out var declaredUnits)&&declaredUnits.Trim().ToLowerInvariant() is not("reflectance" or "1" or "unitless" or "dimensionless"))throw new InvalidDataException("Original header units conflict with supported NASA reflectance. Radiance or undocumented units cannot be relabeled.");
        var inspection=GuidedHyperspectralImport.Inspect(header,new(Name:additions?.Name??n.Flightline,Provider:"NASA "+n.Family+" / ORNL DAAC",Source:item.Reference,ObservationUtc:item.ObservationUtc,License:item.License,Notes:additions?.Notes,Unit:"reflectance"),associated);
        if(inspection.Manifest is null)return inspection;
        if(Path.GetFileNameWithoutExtension(inspection.Binary)!=stem)throw new InvalidDataException("NASA header and numeric binary must share the same original product stem.");
        if(!Path.GetExtension(inspection.Binary!).Equals(".bin",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("This NASA adapter requires its original .bin companion, not an RGB or renamed unrelated file.");
        if(inspection.Coverage.Max(p=>p.Longitude)<item.Bounds.West||inspection.Coverage.Min(p=>p.Longitude)>item.Bounds.East||inspection.Coverage.Max(p=>p.Latitude)<item.Bounds.South||inspection.Coverage.Min(p=>p.Latitude)>item.Bounds.North)throw new InvalidDataException("Original numeric grid does not overlap the selected NASA catalog footprint. No geographic coordinates were replaced.");
        // Original individual files only. Archives are never unpacked by this assistant.
        // Bounded streaming hashes; the complete cube is never loaded into memory.
        _=ScienceStore.InspectImport(inspection.Manifest,inspection.Root);
        var checksums=new List<object>();var companions=new List<string>();
        foreach(var file in new[]{Path.GetFileName(header),inspection.Binary!})
        {
            token.ThrowIfCancellationRequested();var sidecar=file+".sha256";
            var advertised=n.Assets.Any(a=>a.Role=="Provider SHA-256"&&a.Name==sidecar);
            if(!File.Exists(Path.Combine(inspection.Root,sidecar)))
            {if(advertised)throw new InvalidDataException("Download the advertised NASA checksum "+sidecar+" alongside the originals before verification.");continue;}
            var checksum=ScientificRaster.SafeFile(inspection.Root,sidecar);
            if(new FileInfo(checksum).Length>4096)throw new InvalidDataException("Checksum companion exceeds 4 KiB.");
            var text=File.ReadAllText(checksum).Trim();var match=Regex.Match(text,@"^([a-fA-F0-9]{64})(?:[ \t]+\*?([^\r\n]+))?$",RegexOptions.CultureInvariant);
            if(!match.Success||match.Groups[2].Success&&match.Groups[2].Value!=file)throw new InvalidDataException("Invalid or mismatched SHA-256 companion filename.");
            using var stream=File.OpenRead(ScientificRaster.SafeFile(inspection.Root,file));
            var actual=SHA256.HashDataAsync(stream,token).GetAwaiter().GetResult();
            if(!Convert.ToHexString(actual).Equals(match.Groups[1].Value,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Original file SHA-256 does not match its checksum. Download the complete unchanged original again.");
            companions.Add(sidecar);checksums.Add(new{file,sha256=Convert.ToHexString(actual).ToLowerInvariant(),catalogAdvertised=advertised,limitation="Sidecar was selected locally; compare with the official provider. Local sidecars alone do not authenticate NASA origin."});
        }
        _=ScienceStore.InspectImport(inspection.Manifest,inspection.Root);
        var notes=JsonSerializer.Serialize(new{workflow="NASA official-browser acquisition followed by local ENVI verification",catalog=item,checksumVerification=checksums,headerInspection=JsonSerializer.Deserialize<JsonElement>(inspection.Manifest.Notes!),identityLimitation="Matching metadata and filenames are not proof of NASA origin without an authoritative provider checksum. Retained file hashes establish local integrity.",userNotes=additions?.Notes},WireJson.Options);
        return inspection with{Manifest=inspection.Manifest with{SceneId=n.GranuleId,PublicationUtc=item.PublishedUtc,Notes=notes,OriginalMetadataFiles=companions.ToArray()},Warnings=[..inspection.Warnings,"Original NASA observations; browser download progress/authentication is external. No new aircraft observations are created."]};
    }
}
