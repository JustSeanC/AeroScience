using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public static class LidarImports
{
    public static ScientificSourceManifest Inspect(string path,LidarImportMetadata metadata,LidarEptProject? ept=null,string? node=null)
    {
        var h=LidarLas.Inspect(path);var name=Path.GetFileName(path);var domain=LidarCoordinates.Domain(h);
        if(metadata.AcquiredToUtc<metadata.AcquiredFromUtc)throw new ArgumentException("Original acquisition range is reversed.");
        if(metadata.AcquiredToUtc is {} end&&(end.Offset!=TimeSpan.Zero||end>DateTimeOffset.UtcNow.AddDays(1)))throw new ArgumentException("Original acquisition end must be UTC and cannot be invented in the future.");
        if(ept is not null&&(h.HorizontalCrs!=ept.HorizontalCrs||node is null))throw new InvalidDataException("Delivered EPT tile CRS/node differs from the selected original project.");
        var definition=new LidarSourceDefinition(1,h.Compressed?"laz":"las",domain,[new(name,h,node)],ept?.Project,ept?.MetadataSha256,ept?.MetadataJson);
        return ScienceStore.ValidateSourceManifest(new(1,metadata.Name,RemoteKind.Terrain,ept is null?"Local historical LAS/LAZ":"USGS public EPT",metadata.License,metadata.Citation,metadata.Project,metadata.AcquiredFromUtc,h.VerticalReference,[new("historical-lidar",name,"las-laz",Unit:"native XYZ")],metadata.PublishedUtc,
            Notes:JsonSerializer.Serialize(new{sourceKind="Historical LiDAR points",metadata.AcquiredFromUtc,metadata.AcquiredToUtc,sourceDateBasis="Explicit original acquisition metadata only; file creation date is not substituted",noOriginalScienceCredit=true,verticalUnit=h.VerticalUnit,limitations=h.Limitations},WireJson.Options),PointCloud:definition));
    }
    public static async Task<ScientificSource> Import(ScienceStore science,string path,LidarImportMetadata metadata,CancellationToken token=default,IProgress<string>? progress=null)
    {
        var manifest=Inspect(path,metadata);return await science.ImportSourceAsync(manifest,Path.GetDirectoryName(Path.GetFullPath(path))!,token,progress,reuseExisting:true,verifyPointCloud:(root,ct)=>Verify(root,manifest,ct,progress));
    }
    internal static async Task Verify(string root,ScientificSourceManifest manifest,CancellationToken token,IProgress<string>? progress=null)
    {
        var definition=manifest.PointCloud??throw new InvalidDataException("A verified point-cloud definition is required.");
        if(definition.Originals.Length!=1)throw new InvalidDataException("Import one bounded original per source.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(10));
        foreach(var original in definition.Originals)
        {
            var path=ScientificRaster.SafeFile(root,original.File);var h=LidarLas.Inspect(path);
            if(ScientificCatalog.Hash(h)!=ScientificCatalog.Hash(original.Header)||LidarCoordinates.Domain(h)!=definition.Domain)throw new IOException("Copied point-cloud structure/CRS differs from its reviewed original.");
            if(checked(h.PointCount*(long)h.RecordLength)>1073741824)throw new IOException("Decoded original exceeds the 1 GiB streaming work bound. Import smaller geographic tiles.");
            for(var first=0L;first<h.PointCount;first+=4096)
            {
                var batch=await LidarDecoder.Read(path,h,first,(int)Math.Min(4096,h.PointCount-first),deadline.Token);
                foreach(var p in batch){if(!double.IsFinite(p.Z))throw new InvalidDataException("Nonfinite original point coordinate.");_=LidarCoordinates.Geographic(h.HorizontalCrs!,p.X,p.Y);var xy=new[]{p.X,p.Y,p.Z};for(var d=0;d<3;d++){var e=Math.Max(h.Scale[d],Math.Abs(xy[d])*1e-14);if(xy[d]<h.NativeBounds[d]-e||xy[d]>h.NativeBounds[d+3]+e)throw new InvalidDataException("Decoded point lies outside its original declared bounds.");}}
                progress?.Report($"Verified {first+batch.Length:N0} / {h.PointCount:N0} historical point records; no flight measurement credit.");
            }
        }
    }
    public static ScientificSource Bind(ScienceStore science,Guid id,string fingerprint,bool unknownDatumAcknowledged,ScientificAccessPreferences? preferences=null,CancellationToken token=default)
    {
        var s=science.Source(id)??throw new IOException("Historical point original was deleted; review sources again.");
        if(s.Manifest.PointCloud is not {} cloud||s.RelativeDirectory!=$"Sources/{s.Id:N}"||s.ManifestSha256!=fingerprint||ScientificComposites.Identity(new{manifest=s.Manifest,files=s.Files})!=fingerprint)throw new IOException("Historical source definition/ownership/revision differs; no replacement is selected.");
        if(preferences?.ExcludedSourceIds?.Contains(id)==true||preferences?.SourceModes?.GetValueOrDefault(RemoteKind.Terrain)=="local"&&cloud.EptProject is not null)throw new InvalidOperationException("The existing source exclusion/local-only policy does not permit this historical point source.");
        if((s.Manifest.VerticalDatum.Contains("Unknown",StringComparison.OrdinalIgnoreCase)||cloud.Originals[0].Header.VerticalUnit=="unknown")&&!unknownDatumAcknowledged)throw new InvalidOperationException("Review and acknowledge the unknown vertical reference/height unit before isolated historical-point analysis. No datum conversion is inferred.");
        var root=Path.Combine(science.DirectoryPath,s.RelativeDirectory);var ownership=ScientificRaster.SafeFile(root,"source.json");
        if(new FileInfo(ownership).Length>2097152||ScientificCatalog.Hash(JsonSerializer.Deserialize<ScientificSource>(File.ReadAllText(ownership),WireJson.Options))!=ScientificCatalog.Hash(s))throw new IOException("Original point-cloud ownership metadata differs.");
        long bytes=0;foreach(var file in s.Files){token.ThrowIfCancellationRequested();bytes=checked(bytes+file.Bytes);if(bytes>LidarLas.MaximumOriginalBytes+2097152)throw new IOException("Point source verification bound exceeded.");var path=ScientificRaster.SafeFile(root,file.RelativePath);using var input=File.OpenRead(path);if(input.Length!=file.Bytes||Convert.ToHexStringLower(SHA256.HashData(input))!=file.Sha256)throw new IOException("Historical original hash/length changed; no checkpoint or point is trusted.");}
        foreach(var o in cloud.Originals)if(ScientificCatalog.Hash(LidarLas.Inspect(ScientificRaster.SafeFile(root,o.File)))!=ScientificCatalog.Hash(o.Header))throw new IOException("Retained LAS metadata differs from its frozen structure.");return s;
    }
}
