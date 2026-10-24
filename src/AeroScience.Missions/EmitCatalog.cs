using System.Text.RegularExpressions;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;
public sealed partial class ScientificCatalog
{
    public async Task<Dictionary<string,(long Bytes,string Sha512)>> EmitChecksums(ScientificCatalogItem item,CancellationToken token)
    {
        using var document=await automatic.Http.Json(item.Nasa!.MetadataUrl,new(8388608),token,refresh:true);
        var root=document.RootElement;if(root.TryGetProperty("umm",out var umm))root=umm;
        if(root.GetProperty("GranuleUR").GetString()!=item.Nasa.Flightline||root.GetProperty("CollectionReference").GetProperty("ShortName").GetString()!=item.Nasa.CollectionShortName||root.GetProperty("CollectionReference").GetProperty("Version").GetString()!=item.Nasa.CollectionVersion)throw new IOException("NASA original integrity metadata changed identity; preview again.");
        var result=new Dictionary<string,(long,string)>(StringComparer.Ordinal);
        foreach(var entry in root.GetProperty("DataGranule").GetProperty("ArchiveAndDistributionInformation").EnumerateArray())
        {
            if(entry.GetProperty("Name").GetString() is not {} name||!name.EndsWith(".nc",StringComparison.Ordinal))continue;
            if(!entry.TryGetProperty("SizeInBytes",out var size)||!size.TryGetInt64(out var bytes)||bytes<=0||!entry.TryGetProperty("Checksum",out var checksum))continue;
            if(checksum.GetProperty("Algorithm").GetString()!="SHA-512")continue;
            var value=checksum.GetProperty("Value").GetString();if(value?.Length!=128||value.Any(c=>!char.IsAsciiHexDigit(c))||!result.TryAdd(name,(bytes,value.ToLowerInvariant())))throw new IOException("Invalid or ambiguous NASA checksum metadata; no original was approved.");
        }
        return result;
    }
    // Same official CMR parser, consent, bounds, cache and pagination. No guessed assets.
    public async Task<ScientificCatalogItem> EmitMask(ScientificCatalogItem reflectance,CancellationToken token)
    {
        var title=reflectance.Nasa?.Flightline??throw new IOException("Missing exact EMIT scene identity.");
        var match=Regex.Match(title,@"^EMIT_L2A_RFL_002_(\d{8}T\d{6}(?:_\d{7}_\d{3})?)$");
        if(!match.Success)throw new IOException("Unsupported EMIT source identity; no mask was guessed.");
        var query=new ScientificCatalogQuery("nasa-emit-mask",reflectance.Bounds,PageSize:2,Refresh:true,SceneFilter:match.Groups[1].Value);
        var page=await SearchNasa(query,NasaSearchUrl(query,1),new(8388608),token);
        var matches=page.Items.Where(i=>i.Nasa?.Flightline=="EMIT_L2A_MASK_003_"+match.Groups[1].Value&&i.ObservationUtc==reflectance.ObservationUtc&&i.Nasa.ObservationEndUtc==reflectance.Nasa!.ObservationEndUtc).ToArray();
        if(page.Next is not null||matches.Length!=1)throw new IOException("Exactly one compatible MASK003 scene could not be confirmed. No alternative mask was substituted; use matching local originals.");
        return matches[0];
    }
}
public sealed partial class ScientificLibraryJobs
{
    private async Task<NasaDownloadBundle> DiscoverEmitBundle(ScientificCatalogItem selected,CancellationToken token)
    {
        var mask=await catalog.EmitMask(selected,token);
        var stem=selected.Nasa!.Flightline;var tail=stem["EMIT_L2A_RFL_002_".Length..];
        string[] required=[stem+".nc","EMIT_L2A_RFLUNCERT_002_"+tail+".nc","EMIT_L2A_MASK_003_"+tail+".nc"];
        var candidates=selected.Nasa.Assets.Concat(mask.Nasa!.Assets).Where(a=>required.Contains(a.Name)).DistinctBy(a=>a.Url).ToArray();
        if(candidates.Length!=3||required.Any(name=>candidates.Count(a=>a.Name==name)!=1))throw new IOException("CMR did not supply all three exact original NetCDF assets. Use official browser acquisition; no companion URL was inferred.");
        var checksums=await catalog.EmitChecksums(selected,token);foreach(var pair in await catalog.EmitChecksums(mask,token))if(!checksums.TryAdd(pair.Key,pair.Value))throw new IOException("Duplicate NASA original checksum identity.");
        var files=new List<NasaDownloadFile>();foreach(var asset in candidates.OrderBy(a=>a.Name,StringComparer.Ordinal))
        {
            var file=await automatic.Http.DescribeNasaFile(asset,earthdata,token);
            if(checksums.TryGetValue(asset.Name,out var identity)){if(identity.Bytes!=file.Bytes)throw new IOException("NASA original HEAD length disagrees with CMR integrity metadata.");file=file with{Sha512=identity.Sha512};}
            files.Add(file);
        }
        return new(selected,[mask],files.ToArray(),ScientificCatalog.Hash(new{selected,mask,files}));
    }
}
