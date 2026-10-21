using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record ProviderAsset(SourceBand Band,Uri Url);
public sealed record ScientificDiscovery(ScientificSourceManifest Manifest,ProviderAsset[] Assets,string Catalog,string Item,
    string Quality,string Resolution,GeographicBounds Area,string? NumericExport=null,string[]? Limitations=null,string? ProviderMetadataJson=null);
public interface IScientificDataProvider
{
    string Id {get;}
    bool Supports(RemoteKind kind);
    Task<ScientificDiscovery?> Discover(RemoteKind kind,GeographicBounds area,ScientificAccessPreferences preferences,ScientificDownloadBudget budget,CancellationToken token);
}

public sealed class StacScientificProvider(ScientificHttp http,string id):IScientificDataProvider
{
    public string Id=>id;
    private readonly Dictionary<string,(DateTimeOffset At,JsonElement Features)> scenes=[];

    public bool Supports(RemoteKind kind)=>id switch{"earth-search"=>kind==RemoteKind.Multispectral,"planetary-computer"=>kind is RemoteKind.Thermal or RemoteKind.Terrain,_=>false};
    private static string QueryDate(ScientificAccessPreferences p)
    {
        var to=(p.PreferredDateUtc??DateTimeOffset.UtcNow).ToUniversalTime();var from=to.AddDays(-p.LookbackDays);
        return from.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture)+"/"+to.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture);
    }
    public async Task<ScientificDiscovery?> Discover(RemoteKind kind,GeographicBounds area,ScientificAccessPreferences p,ScientificDownloadBudget budget,CancellationToken token)
    {
        if(!Supports(kind))return null;
        var root=id=="earth-search"?"https://earth-search.aws.element84.com/v1":"https://planetarycomputer.microsoft.com/api/stac/v1";
        var collection=kind switch{RemoteKind.Thermal=>"landsat-c2-l2",RemoteKind.Multispectral=>"sentinel-2-l2a",_=>"cop-dem-glo-30"};
        var query="?collections="+collection+"&bbox="+Bounds(area)+"&limit=60";
        if(kind!=RemoteKind.Terrain)query+="&datetime="+Uri.EscapeDataString(QueryDate(p))+"&sortby="+(p.SelectionPolicy=="newest"?(id=="earth-search"?"-properties.datetime":"-datetime"):(id=="earth-search"?"properties.eo:cloud_cover":"eo:cloud_cover"))+"&query="+Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string,object>{{"eo:cloud_cover",new{lte=p.MaximumCloudPercent}}}));
        var profile=kind+"|"+p.SelectionPolicy+"|"+p.MaximumCloudPercent+"|"+p.LookbackDays+"|"+(p.PreferredDateUtc?.ToString("O")??DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
        JsonElement features;
        lock(scenes)features=http.CacheEnabled&&scenes.TryGetValue(profile,out var cached)&&DateTimeOffset.UtcNow-cached.At<TimeSpan.FromMinutes(5)&&cached.Features.EnumerateArray().Any(f=>Matches(f,kind,area,p))?cached.Features:default;
        if(features.ValueKind==JsonValueKind.Undefined)
        {
            using var json=await http.Json(root+"/search"+query,budget,token);
            if(!json.RootElement.TryGetProperty("features",out features)||features.ValueKind!=JsonValueKind.Array)throw new IOException("Provider returned an invalid STAC search response.");
            features=features.Clone();lock(scenes){if(scenes.Count>=4)scenes.Remove(scenes.Keys.First());scenes[profile]=(DateTimeOffset.UtcNow,features);}
        }
        var candidates=features.EnumerateArray().Take(60).Where(f=>Matches(f,kind,area,p));
        candidates=p.SelectionPolicy=="newest"?candidates.OrderByDescending(f=>Date(f.GetProperty("properties"))):candidates.OrderBy(f=>Cloud(f.GetProperty("properties"))).ThenByDescending(f=>Date(f.GetProperty("properties")));
        foreach(var feature in candidates)
        {
            var result=Describe(feature,kind,area);if(result is not null)return result;
        }
        return null;
    }
    internal ScientificDiscovery? Describe(JsonElement feature,RemoteKind kind,GeographicBounds area)
    {
        var root=id=="earth-search"?"https://earth-search.aws.element84.com/v1":"https://planetarycomputer.microsoft.com/api/stac/v1";
        var collection=kind switch{RemoteKind.Thermal=>"landsat-c2-l2",RemoteKind.Multispectral=>"sentinel-2-l2a",_=>"cop-dem-glo-30"};
            var props=feature.GetProperty("properties");var assets=feature.GetProperty("assets");var scene=feature.GetProperty("id").GetString()!;
            var wanted=kind switch{RemoteKind.Thermal=>new[]{("ST_B10","lwir11"),("QA_PIXEL","qa_pixel"),("QA_RADSAT","qa_radsat")},RemoteKind.Multispectral=>new[]{("B02","blue"),("B03","green"),("B04","red"),("B08","nir"),("SCL","scl")},_=>new[]{("elevation","data")}};
            if(wanted.Any(pair=>!assets.TryGetProperty(pair.Item2,out _)))return null;
            var bands=new List<ProviderAsset>();
            foreach(var (bandId,key) in wanted)
            {
                var asset=assets.GetProperty(key);var href=asset.GetProperty("href").GetString()!;var uri=new Uri(href);ScientificHttp.ValidateUrl(uri);
                if(kind==RemoteKind.Thermal&&uri.Host!="landsateuwest.blob.core.windows.net"||kind==RemoteKind.Multispectral&&uri.Host!="sentinel-cogs.s3.us-west-2.amazonaws.com"||kind==RemoteKind.Terrain&&uri.Host!="elevationeuwest.blob.core.windows.net")throw new IOException("Scientific catalog returned a different asset host; review the adapter before downloading.");
                if(uri.Query.Length!=0)throw new IOException("Catalog assets must provide stable unsigned identifiers.");
                if(!asset.TryGetProperty("type",out var type)||!type.GetString()!.Contains("image/tiff",StringComparison.OrdinalIgnoreCase))throw new IOException("A required scientific band is not a supported numeric GeoTIFF.");
                var raster=asset.TryGetProperty("raster:bands",out var rs)&&rs.GetArrayLength()==1?rs[0]:default;
                var quality=bandId is "QA_PIXEL" or "QA_RADSAT" or "SCL";
                var scale=Number(raster,"scale",kind==RemoteKind.Terrain||quality?1:double.NaN);
                var offset=Number(raster,"offset",0);var nodata=Number(raster,"nodata",double.NaN);
                if(!double.IsFinite(scale)||!double.IsFinite(offset))throw new IOException("Provider omitted required band scaling. Scientific values were not guessed.");
                if(quality&&(scale!=1||offset!=0))throw new IOException("Provider changed quality-mask scaling; original integer flags are required.");
                if(kind==RemoteKind.Thermal&&bandId=="ST_B10"&&(Math.Abs(scale-.00341802)>1e-10||offset!=149))throw new IOException("Provider temperature scale differs from the supported Landsat C2 product.");
                var unit=quality?"1":kind==RemoteKind.Thermal?"K":kind==RemoteKind.Multispectral?"reflectance":"m";
                bands.Add(new(new(bandId,bandId+".tif","geotiff",Unit:unit,Scale:scale,Offset:offset,NoData:double.IsFinite(nodata)?nodata:null,SceneId:scene),uri));
            }
            var observation=kind==RemoteKind.Terrain?(DateTimeOffset?)null:Date(props);
            var notes=kind==RemoteKind.Terrain?"Copernicus GLO-30 digital surface model (includes vegetation/buildings), EGM2008 geoid heights. Catalog publication time is not a per-pixel observation date.":
                kind==RemoteKind.Thermal?"Landsat Collection 2 Level-2 Surface Temperature (L2SP), not brightness temperature or aircraft thermal imagery. 30 m product grid; Landsat 8/9 TIRS native thermal resolution 100 m. QA is applied at sampling.":
                "Sentinel-2 Level-2A surface reflectance. Provider raster:bands scale/offset is retained, including product-specific radiometric offsets; SCL is sampled on its own native grid.";
            var license=kind==RemoteKind.Thermal?"USGS Landsat public domain; https://www.usgs.gov/landsat-missions/landsat-data-access":kind==RemoteKind.Multispectral?$"Contains modified Copernicus Sentinel data ({observation!.Value.Year}); https://sentinels.copernicus.eu/documents/247904/690755/Sentinel_Data_Legal_Notice.pdf":"Produced using Copernicus WorldDEM-30: © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved. GLO-30 license: https://spacedata.copernicus.eu/collections/copernicus-digital-elevation-model";
            var item=root+"/collections/"+collection+"/items/"+Uri.EscapeDataString(scene);
            var manifest=new ScientificSourceManifest(1,collection+" / "+scene,kind,kind==RemoteKind.Multispectral?"Copernicus / Element 84 Earth Search":"Microsoft Planetary Computer / "+(kind==RemoteKind.Thermal?"USGS Landsat":"Copernicus DEM"),license,item,scene,observation,kind==RemoteKind.Terrain?"EGM2008 geoid (DSM, not bare-earth)":"not applicable",bands.Select(b=>b.Band).ToArray(),PublicationUtc:kind==RemoteKind.Terrain?Date(props):null,Notes:notes);
            return new(manifest,bands.ToArray(),root,item,kind==RemoteKind.Terrain?"Numeric DSM; vertical reference differs from 3DEP and aircraft MSL":$"Scene cloud cover {Cloud(props):0.##}%; local QA/nodata still determines valid samples",kind==RemoteKind.Thermal?"30 m delivered grid / 100 m native TIRS":kind==RemoteKind.Multispectral?"10 m reflectance / 20 m SCL":"~30 m geographic DSM",area,
                Limitations:["Discovery examines at most 60 ordered catalog candidates; no claim of exhaustive coverage.",notes],
                ProviderMetadataJson:JsonSerializer.Serialize(new{collection,scene,properties=props,geometry=feature.GetProperty("geometry"),assets=wanted.ToDictionary(pair=>pair.Item1,pair=>assets.GetProperty(pair.Item2))}));

    }
    public static bool Matches(JsonElement feature,RemoteKind kind,GeographicBounds area,ScientificAccessPreferences p)
    {
        try
        {
            var props=feature.GetProperty("properties");if(kind==RemoteKind.Thermal&&(props.GetProperty("landsat:correction").GetString()!="L2SP"||!feature.GetProperty("id").GetString()!.StartsWith("LC0",StringComparison.Ordinal)))return false;
            if(kind!=RemoteKind.Terrain&&(Cloud(props)>p.MaximumCloudPercent||Date(props)> (p.PreferredDateUtc??DateTimeOffset.UtcNow)||Date(props)<(p.PreferredDateUtc??DateTimeOffset.UtcNow).AddDays(-p.LookbackDays)))return false;
            var geometry=feature.GetProperty("geometry");
            return area.Corners.Append(area.Center).All(point=>Contains(geometry,point));
        }
        catch(Exception e)when(e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException){return false;}
    }
    internal static bool Contains(JsonElement geometry,Coordinates point)
    {
        var type=geometry.GetProperty("type").GetString();var coords=geometry.GetProperty("coordinates");
        return type=="Polygon"?Polygon(coords,point):type=="MultiPolygon"&&coords.EnumerateArray().Any(p=>Polygon(p,point));
    }
    private static bool Polygon(JsonElement polygon,Coordinates p)=>Ring(polygon[0],p)&&!polygon.EnumerateArray().Skip(1).Any(r=>Ring(r,p));
    private static bool Ring(JsonElement ring,Coordinates p)
    {
        var vertices=ring.EnumerateArray().ToArray();var inside=false;
        for(int i=0,j=vertices.Length-1;i<vertices.Length;j=i++)
        {
            var x=vertices[i][0].GetDouble();var y=vertices[i][1].GetDouble();var xj=vertices[j][0].GetDouble();var yj=vertices[j][1].GetDouble();
            if((y>p.Latitude)!=(yj>p.Latitude)&&p.Longitude<(xj-x)*(p.Latitude-y)/(yj-y)+x)inside=!inside;
        }
        return inside;
    }
    internal static double Number(JsonElement value,string field,double fallback)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(field,out var n)&&n.TryGetDouble(out var d)?d:fallback;
    private static double Cloud(JsonElement props)=>Number(props,"eo:cloud_cover",0);
    private static DateTimeOffset Date(JsonElement props)=>DateTimeOffset.Parse(props.GetProperty("datetime").GetString()!,CultureInfo.InvariantCulture).ToUniversalTime();
    internal static string Bounds(GeographicBounds b)=>string.Join(",",new[]{b.West,b.South,b.East,b.North}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)));
}

public sealed class UsgsElevationProvider(ScientificHttp http):IScientificDataProvider
{
    public const string Endpoint="https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer";
    public string Id=>"usgs-3dep";public bool Supports(RemoteKind kind)=>kind==RemoteKind.Terrain;
    public async Task<ScientificDiscovery?> Discover(RemoteKind kind,GeographicBounds area,ScientificAccessPreferences p,ScientificDownloadBudget budget,CancellationToken token)
    {
        if(!Supports(kind)||area.West< -170||area.East> -64||area.South<17||area.North>72)return null;
        using var metadata=await http.Json(Endpoint+"?f=json",budget,token);
        if(metadata.RootElement.GetProperty("pixelType").GetString()!="F32"||metadata.RootElement.GetProperty("bandCount").GetInt32()!=1)throw new IOException("USGS no longer exposes the supported single numeric elevation band.");
        var pixels=(int)Math.Clamp(Math.Ceiling(Math.Max(area.East-area.West,area.North-area.South)/.02*256-1e-7),256,768);
        var url=Endpoint+"/exportImage?bbox="+StacScientificProvider.Bounds(area)+"&bboxSR=4326&imageSR=4326&size="+pixels+","+pixels+"&format=tiff&pixelType=F32&interpolation=RSP_NearestNeighbor&noData=-999999&f=image";
        var scene="3DEP-"+StacScientificProvider.Bounds(area);
        var manifest=new ScientificSourceManifest(1,"USGS 3DEP bare-earth numeric elevation subset",kind,"USGS 3DEP National Map","USGS public domain; https://www.usgs.gov/information-policies-and-instructions/copyrights-and-credits",Endpoint,scene,null,
            "Source-native vertical reference is unspecified by this composite export; no datum conversion or aircraft MSL comparison",[new("elevation","elevation.tif","geotiff",Unit:"m",NoData:-999999,SceneId:scene)],Notes:"USGS multi-resolution composite bare-earth DEM, nearest-neighbor numeric export. Original survey dates, per-pixel native resolution and vertical datum are not supplied by this endpoint; they remain unknown. Not live aircraft LiDAR.");
        return new(manifest,[],Endpoint,Endpoint,"USGS numeric composite; actual nodata checked before publishing","Composite source resolution varies; "+pixels+" × "+pixels+" requested geographic grid (not a claim of native resolution)",area,url,[manifest.Notes!],JsonSerializer.Serialize(new{pixelType="F32",bandCount=1,description=metadata.RootElement.GetProperty("description").GetString(),export=url}));
    }
}
