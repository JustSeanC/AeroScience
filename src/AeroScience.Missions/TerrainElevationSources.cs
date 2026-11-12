using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public static class TerrainElevationSources
{
    public static void Validate(TerrainElevationOptions o)
    {
        if(o.TileSize is <1 or >32||o.MaximumCandidateCells is <1 or >20000000||o.MaximumTiles is <1 or >20000
            ||o.MaximumOutputBytes is <4096 or >1073741824||o.MaximumScratchBytes is <1048576 or >1073741824
            ||o.MaximumVerifiedSourceBytes is <1024 or >21474836480||o.MaximumShapesPerTile is <1 or >2048
            ||o.MaximumProjectedVertices is <16 or >100000||o.FirstGeometryChunk is <1 or >20000||o.LastGeometryChunk is <1 or >20000||o.LastGeometryChunk<o.FirstGeometryChunk)throw new ArgumentException("Choose bounded terrain processing, regional scope, tile, verification and storage limits.");
    }
    public static TerrainElevationSource Bind(ScienceStore science,TerrainElevationSourceChoice choice,
        TerrainElevationOptions options,ScientificAccessPreferences? preferences=null,CancellationToken token=default)
    {
        if(choice.SourceId==Guid.Empty||choice.ManifestSha256?.Length!=64||choice.BandId?.Length is <1 or >150
            ||choice.Surface is not("dem" or "dsm" or "unknown")||choice.AcceptedQualityValues?.Length>64
            ||choice.AcceptedQualityValues?.Any(v=>!double.IsFinite(v))==true)throw new ArgumentException("Review an exact retained source, elevation band and bounded quality policy.");
        var source=science.Source(choice.SourceId)??throw new InvalidOperationException("Selected terrain source is no longer retained. Review coverage again.");
        if(source.RelativeDirectory!=$"Sources/{source.Id:N}")throw new IOException("Retained source ownership path is unsupported.");
        if(source.ManifestSha256!=choice.ManifestSha256||ScientificComposites.Identity(new{manifest=source.Manifest,files=source.Files})!=source.ManifestSha256)
            throw new IOException("Source revision/metadata changed. No source substitution was performed.");
        var ownership=ScientificRaster.SafeFile(Path.Combine(science.DirectoryPath,source.RelativeDirectory),"source.json");
        if(new FileInfo(ownership).Length>1048576||ScientificCatalog.Hash(JsonSerializer.Deserialize<ScientificSource>(File.ReadAllText(ownership),WireJson.Options))!=ScientificCatalog.Hash(source))
            throw new IOException("Original source ownership metadata differs from its retained catalog.");
        var m=source.Manifest;
        if(m.Kind!=RemoteKind.Terrain||m.Swath is not null)throw new InvalidOperationException("Select an explicitly declared affine terrain source; an elevation-like band name is insufficient.");
        if(ScientificSourceSelection.Select([new(source,true,true,null)],RemoteKind.Terrain,preferences) is null)
            throw new InvalidOperationException("The selected source is excluded by the current scientific source policy.");
        var band=m.Bands.SingleOrDefault(b=>b.Id==choice.BandId)??throw new ArgumentException("The selected native elevation band is not retained.");
        if(band.BadBand||band.Format is not("geotiff" or "envi")||band.Unit is not("m" or "ft"))
            throw new InvalidOperationException("Use a supported numeric terrain band with declared metres or feet. No unit or format is guessed.");
        var providerComposite=m.Acquisition?.Workflow=="usgs-3dep-numeric";
        var providerDsm=m.Acquisition?.Workflow=="stac-cog-native-subset"&&m.Provider.Contains("Copernicus",StringComparison.OrdinalIgnoreCase);
        var composite=providerComposite||m.Provider.Contains("USGS",StringComparison.OrdinalIgnoreCase)&&m.Notes?.Contains("composite",StringComparison.OrdinalIgnoreCase)==true;
        var copernicus=providerDsm||m.Provider.Contains("Copernicus",StringComparison.OrdinalIgnoreCase)&&m.Notes?.Contains("surface model",StringComparison.OrdinalIgnoreCase)==true;
        var surface=composite?"composite-dem":copernicus?"dsm":choice.Surface;
        var localDefinition=!providerComposite&&!providerDsm;
        if(localDefinition&&(!choice.LocalDefinitionAcknowledged||surface=="unknown"))
            throw new InvalidOperationException("Review the local source's DEM/DSM definition explicitly; the band name does not establish its scientific meaning.");
        var datum=m.VerticalDatum??"";
        var known=!composite&&!string.IsNullOrWhiteSpace(datum)&&!new[]{"unknown","unspecified","unverified","not applicable","not supplied","not established"}.Any(t=>datum.Contains(t,StringComparison.OrdinalIgnoreCase));
        if(!known&&!choice.UnverifiedDatumAcknowledged)throw new InvalidOperationException("The vertical reference is unverified. Explicitly review this limitation before producing a separate unverified-reference result.");
        if(source.Files.Length is <1 or >128||JsonSerializer.SerializeToUtf8Bytes(source,WireJson.Options).Length>1048576)
            throw new InvalidOperationException("This source exceeds bounded affine-terrain metadata/file limits.");
        long verified=0;
        foreach(var file in source.Files)
        {
            token.ThrowIfCancellationRequested();verified=checked(verified+file.Bytes);
            if(verified>options.MaximumVerifiedSourceBytes)throw new InvalidOperationException("Source verification byte budget exceeded; no partial verification is accepted.");
            var path=ScientificRaster.SafeFile(Path.Combine(science.DirectoryPath,source.RelativeDirectory),file.RelativePath);
            StorageManager.ValidateLocalPath(path);using var stream=File.OpenRead(path);
            if(stream.Length!=file.Bytes)throw new IOException("Retained original length changed.");
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];int read;
            while((read=stream.Read(buffer))!=0){token.ThrowIfCancellationRequested();hash.AppendData(buffer,0,read);}
            if(Convert.ToHexStringLower(hash.GetHashAndReset())!=file.Sha256)throw new IOException("Retained original hash changed. Restore/reimport it; no substitute was used.");
        }
        using var raster=new ScientificRaster(Path.Combine(science.DirectoryPath,source.RelativeDirectory),band);
        if(raster.RequiresTerrainMaskReview)throw new InvalidOperationException("This TIFF has additional directories, sub-IFDs or extra-sample masks. Their quality semantics require review; no potentially masked terrain is exported.");
        if(!source.Grids.TryGetValue(band.Id,out var grid)||grid!=raster.Grid)throw new IOException("Original grid differs from its retained metadata.");
        _=TerrainNativeGeometry.Project(grid,ScientificRaster.InspectionGridPoint(grid,.5,.5));
        var qa=choice.QualityBandId is null?null:m.Bands.SingleOrDefault(b=>b.Id==choice.QualityBandId)??throw new ArgumentException("The declared quality band is absent.");
        if(qa is not null&&(qa.BadBand||qa.Format is not("geotiff" or "envi")||qa.Unit!="1"||qa.Scale!=1||qa.Offset!=0||choice.AcceptedQualityValues is not{Length:>0}||!source.Grids.TryGetValue(qa.Id,out var qg)||qg!=grid))
            throw new InvalidOperationException("Quality filtering requires an explicit accepted-value policy on the identical native grid; no mask resampling is implicit.");
        if(qa is null&&m.Bands.Any(b=>b.Id!=band.Id&&(b.Id.Contains("mask",StringComparison.OrdinalIgnoreCase)||b.Id.StartsWith("QA",StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("A possible quality mask is retained. Review its meaning and select an explicit quality policy before processing.");
        return new(source,band,grid,surface,known,localDefinition,qa,choice.AcceptedQualityValues??[],[
            "Original decoded and scale/offset values, native grid, dates, license and nodata are retained. No interpolation or vertical/unit transformation.",
            composite?"USGS composite export: source survey date, native per-pixel resolution and vertical reference are unspecified; delivered grid is not native survey resolution.":"Native delivered source lattice retained; grid spacing is not positional accuracy.",
            known?"Vertical reference is the retained source declaration, not a new datum/accuracy certification.":"Unverified vertical reference explicitly acknowledged; this result is isolated and cannot establish comparable elevations.",
            "Sources/DEM/DSM and different datums are separate outputs; overlapping sources are never averaged or blended. Source deletion removes these managed derived values."]);
    }
    internal static TerrainElevationSourceChoice Choice(TerrainElevationSource s)=>new(s.Original.Id,s.Original.ManifestSha256,s.Band.Id,
        s.Surface is "composite-dem"?"dem":s.Surface,s.UserDeclaredDefinition,!s.DatumVerified,s.QualityBand?.Id,s.AcceptedQualityValues);
}
