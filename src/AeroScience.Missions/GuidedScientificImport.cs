using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using AeroScience.Shared;

namespace AeroScience.Missions;

// Compatible numeric local files only. This is metadata preparation, not a general GIS converter.
public static class GuidedScientificImport
{
    public static HyperspectralInspection Inspect(string selected,RemoteKind kind,HyperspectralUserMetadata? supplied=null)
    {
        if(kind==RemoteKind.Hyperspectral)return GuidedHyperspectralImport.Inspect(selected,supplied);
        var m=supplied??new();StorageManager.ValidateLocalPath(selected);
        var root=Directory.Exists(selected)?Path.GetFullPath(selected):Path.GetDirectoryName(Path.GetFullPath(selected))!;
        var files=Directory.EnumerateFiles(root).Take(1025).ToArray();if(files.Length>1024)throw new InvalidDataException("Choose a collection folder containing at most 1024 files.");
        string Find(string id)
        {
            var matches=files.Where(f=>Path.GetExtension(f).Equals(".tif",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(f).Equals(".tiff",StringComparison.OrdinalIgnoreCase)).Where(f=>
            {var name=Path.GetFileNameWithoutExtension(f);return name.Equals(id,StringComparison.OrdinalIgnoreCase)||name.EndsWith("_"+id,StringComparison.OrdinalIgnoreCase)||name.EndsWith("_"+id+"_10m",StringComparison.OrdinalIgnoreCase)||name.EndsWith("_"+id+"_20m",StringComparison.OrdinalIgnoreCase);}).ToArray();
            if(matches.Length!=1)throw new InvalidDataException($"Choose one compatible {id} GeoTIFF from the same collection. JP2, rendered RGB, ambiguous scenes and missing QA are unsupported; originals were not changed.");return Path.GetFileName(matches[0]);
        }
        DateTimeOffset? observation=m.ObservationUtc;string? scene=m.SceneId;string provider=m.Provider??"User supplied; provider not specified";string? metadataFile=null;
        double? scale=m.Scale,offset=m.Offset;string? datum=m.VerticalDatum;var warnings=new List<string>();var bands=new List<SourceBand>();
        if(kind==RemoteKind.Terrain)
        {
            var candidates=File.Exists(selected)?new[]{selected}:files.Where(f=>Path.GetExtension(f).Equals(".tif",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(f).Equals(".tiff",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(candidates.Length!=1)throw new InvalidDataException("Choose one single-band numeric elevation GeoTIFF. An RGB terrain screenshot is not an elevation dataset.");
            var file=Path.GetFileName(candidates[0]);
            bands.Add(new("elevation",file,"geotiff",Unit:"m",Scale:scale??1,Offset:offset??0));
            if(m.Unit is null)warnings.Add("Confirm the original elevation unit is metres. Feet are not silently treated as metres.");
            if(string.IsNullOrWhiteSpace(datum))warnings.Add("Enter the documented vertical datum, or explicitly state Unknown. DEM and surface-model heights are not simulated laser returns.");
            if(m.Unit is not null&&m.Unit!="m")throw new InvalidDataException("Local elevation import requires documented metre units. Convert other original units in a supported scientific workflow before importing.");
            scene??=Path.GetFileNameWithoutExtension(file);
        }
        else if(kind==RemoteKind.Thermal)
        {
            var file=Find("ST_B10");var qa=Find("QA_PIXEL");var fileStem=Path.GetFileNameWithoutExtension(file);var prefix=fileStem.Length>7?fileStem[..^7]:"";
            if(prefix.Length>0&&Path.GetFileNameWithoutExtension(qa)!=prefix+"_QA_PIXEL")throw new InvalidDataException("ST_B10 and QA_PIXEL must belong to the same Landsat scene.");
            var metadata=files.SingleOrDefault(f=>Path.GetFileName(f).Equals(prefix+"_MTL.json",StringComparison.OrdinalIgnoreCase));
            if(metadata is not null)
            {
                if(new FileInfo(metadata).Length>1048576)throw new InvalidDataException("Landsat metadata is too large.");
                using var json=JsonDocument.Parse(File.ReadAllText(metadata));var l=json.RootElement.GetProperty("LANDSAT_METADATA_FILE");var product=l.GetProperty("PRODUCT_CONTENTS");
                if(product.GetProperty("PROCESSING_LEVEL").GetString()!="L2SP")throw new InvalidDataException("Only Landsat Collection 2 Level-2 surface-temperature (L2SP) products are supported.");
                var identity=l.GetProperty("IMAGE_ATTRIBUTES");var spacecraft=identity.GetProperty("SPACECRAFT_ID").GetString();if(spacecraft is not("LANDSAT_8" or "LANDSAT_9"))throw new InvalidDataException("Select a Landsat 8/9 surface-temperature collection.");
                scene=product.GetProperty("LANDSAT_PRODUCT_ID").GetString();provider="USGS Landsat Collection 2 Level-2 (local files)";
                observation=DateTimeOffset.Parse(identity.GetProperty("DATE_ACQUIRED").GetString()+"T"+identity.GetProperty("SCENE_CENTER_TIME").GetString(),CultureInfo.InvariantCulture).ToUniversalTime();metadataFile=Path.GetFileName(metadata);
            }
            if(scene is null)warnings.Add("Enter the original Landsat L2SP scene identifier or include its official *_MTL.json metadata. Never combine scenes.");
            bands.Add(new("ST_B10",file,"geotiff",Unit:"K",Scale:.00341802,Offset:149));bands.Add(new("QA_PIXEL",qa,"geotiff"));
            if(files.Any(f=>Path.GetFileNameWithoutExtension(f).Equals(prefix+"_QA_RADSAT",StringComparison.OrdinalIgnoreCase)))bands.Add(new("QA_RADSAT",Find("QA_RADSAT"),"geotiff"));
            warnings.Add("Landsat Collection 2 ST_B10 uses 0.00341802 × DN + 149 K. QA_PIXEL and any supplied QA_RADSAT remain original unscaled masks.");datum="not applicable";
        }
        else
        {
            foreach(var id in new[]{"B02","B03","B04","B08","SCL"})bands.Add(new(id,Find(id),"geotiff",Unit:id=="SCL"?"1":"reflectance"));
            var xml=files.SingleOrDefault(f=>Path.GetFileName(f).Equals("MTD_MSIL2A.xml",StringComparison.OrdinalIgnoreCase));
            if(xml is not null)
            {
                if(new FileInfo(xml).Length>1048576)throw new InvalidDataException("Sentinel metadata is too large.");
                using var reader=System.Xml.XmlReader.Create(xml,new(){DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576});var document=XDocument.Load(reader);
                string? Value(string key)=>document.Descendants().FirstOrDefault(e=>e.Name.LocalName==key)?.Value;
                var quantification=Value("BOA_QUANTIFICATION_VALUE")??Value("QUANTIFICATION_VALUE");if(quantification is null)throw new InvalidDataException("Sentinel metadata has no reflectance quantification value.");
                scale=1/double.Parse(quantification,CultureInfo.InvariantCulture);provider="Copernicus Sentinel-2 Level-2A (local files)";scene=Value("PRODUCT_URI")??m.SceneId;
                if(DateTimeOffset.TryParse(Value("DATATAKE_SENSING_START"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date))observation=date.ToUniversalTime();
                var offsets=document.Descendants().Where(e=>e.Name.LocalName=="BOA_ADD_OFFSET").ToDictionary(e=>int.Parse(e.Attribute("band_id")!.Value,CultureInfo.InvariantCulture),e=>double.Parse(e.Value,CultureInfo.InvariantCulture));
                var index=new Dictionary<string,int>{{"B02",1},{"B03",2},{"B04",3},{"B08",7}};
                bands=bands.Select(b=>b.Id=="SCL"?b:b with{Scale=scale.Value,Offset=offsets.GetValueOrDefault(index[b.Id])*scale.Value}).ToList();offset=0;metadataFile=Path.GetFileName(xml);
            }
            else if(scale is not null&&offset is not null)bands=bands.Select(b=>b.Id=="SCL"?b:b with{Scale=scale.Value,Offset=offset.Value}).ToList();
            else warnings.Add("Reflectance scale and additive offset are missing. Include official Sentinel-2 L2A MTD_MSIL2A.xml or enter the original documented scale and offset. Different processing baselines are never guessed.");
            if(scene is null)warnings.Add("Enter the original common Sentinel-2 scene identifier. All spectral and SCL files must describe the same observation.");datum="not applicable";
        }
        if(observation is null)warnings.Add("Original observation time is unknown and will remain unknown.");
        foreach(var b in bands){using var check=new ScientificRaster(root,b);}
        using var raster=new ScientificRaster(root,bands[0]);var g=raster.Grid;
        var coverage=ScientificRaster.GridCoverage(g);
        var compatible=kind==RemoteKind.Terrain?m.Unit=="m"&&!string.IsNullOrWhiteSpace(datum):kind==RemoteKind.Thermal?!string.IsNullOrWhiteSpace(scene):!string.IsNullOrWhiteSpace(scene)&&scale is not null&&offset is not null;
        if(kind==RemoteKind.Terrain&&datum is not null&&datum.Equals("unknown",StringComparison.OrdinalIgnoreCase))warnings.Add("Vertical datum is explicitly unknown; no aircraft/DEM vertical comparison is implied.");
        var manifest=compatible?ScienceStore.ValidateSourceManifest(new(1,m.Name??scene??Path.GetFileNameWithoutExtension(selected),kind,provider,m.License??"Usage terms not supplied; verify rights",m.Source??"Original local numeric files; original metadata retained where supplied",scene!,observation,datum!,bands.ToArray(),Notes:JsonSerializer.Serialize(new{metadataFromFile=metadataFile,userEntered=m,warnings,sourceCategory="User-supplied external data"},WireJson.Options),OriginalMetadataFiles:metadataFile is null?null:[metadataFile])):null;
        var bytes=bands.Select(b=>b.File).Distinct().Sum(f=>new FileInfo(ScientificRaster.SafeFile(root,f)).Length);
        return new(selected,kind==RemoteKind.Terrain?bands[0].File:string.Join(", ",bands.Select(b=>b.File)),[],bands.Count,null,null,kind==RemoteKind.Terrain?m.Unit:kind==RemoteKind.Thermal?"K":"reflectance",g,coverage,bytes,bytes+1048576,observation,0,warnings.ToArray(),manifest,root,kind);
    }
}
