using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Shared;
using NetTopologySuite.Geometries;
using Coordinates = AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

public static class AviationTfr
{
    public const string Kind="tfr-geometry";
    public const string NoticesUrl="https://tfr.faa.gov/tfrapi/getTfrList";
    public const string NoShapeUrl="https://tfr.faa.gov/tfrapi/noShapeTfrList";
    // Used by the official FAA graphical client; not a promised external API/SLA.
    public const string GeometryUrl="https://tfr.faa.gov/geoserver/TFR/ows?service=WFS&version=1.1.0&request=GetFeature&typeName=TFR:V_TFR_LOC&maxFeatures=2000&outputFormat=application/json&srsname=EPSG:3857";
    public const int MaximumVertices=50000;
    public static readonly string[] Limitations=[
        "Official FAA graphical service, joined to exact notice identity/title/facility/state. Its external API stability and full operational completeness are unverified.",
        "Not every notice has geometry. A geometry ID is not a verified NOTAM segment. Selected XML/AIXM packets can support matched-area altitude/UTC findings; absent or ambiguous fields and cancellation remain Unknown.",
        "Graphical boundaries do not establish confirmed-active airspace. Dates in descriptions are never parsed into guessed UTC intervals. At most 32 distinct notice interpretations are projected per bounded query; remaining areas stay unassessed.",
        "No map or screening result establishes clearance or absence of restrictions. Flight-simulation reference only; mission/recording/science controls remain unrestricted."
    ];
    private static string Text(JsonElement row,string name,int maximum)
    {
        if(!row.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.String)throw new InvalidDataException("FAA TFR field missing.");
        var text=value.GetString()!;if(text.Length>maximum||text.Any(char.IsControl))throw new InvalidDataException("Oversized/control TFR text.");return text;
    }
    private static string Notam(string value)
    {if(!Regex.IsMatch(value,@"^\d/\d{4,5}$",RegexOptions.CultureInvariant))throw new InvalidDataException("Unsupported NOTAM identity.");return value;}
    private static string NotamKey(string value)
    {if(!Regex.IsMatch(value,@"^\d/\d{4,5}-\d+-FDC-F$",RegexOptions.CultureInvariant))throw new InvalidDataException("Unsupported official NOTAM key.");return Notam(value.Split('-')[0]);}
    public static AviationTfrGeometrySnapshot Parse(byte[] notices,byte[] noShape,byte[] geometry,DateTimeOffset utc)
    {
        foreach(var bytes in new[]{notices,noShape,geometry})if(bytes.Length is <1 or >AviationCatalog.MaximumMetadataBytes)throw new InvalidDataException("Bound TFR metadata to 4 MiB per source.");
        if(utc.Offset!=TimeSpan.Zero)throw new ArgumentException("Use UTC retrieval time.");
        using var n=JsonDocument.Parse(notices,new(){MaxDepth=12});using var missing=JsonDocument.Parse(noShape,new(){MaxDepth=12});using var g=JsonDocument.Parse(geometry,new(){MaxDepth=16});
        if(n.RootElement.ValueKind!=JsonValueKind.Array||missing.RootElement.ValueKind!=JsonValueKind.Array||n.RootElement.GetArrayLength()>2048||missing.RootElement.GetArrayLength()>2048)throw new InvalidDataException("Unsupported TFR notice inventory.");
        var inventory=new Dictionary<string,JsonElement>(StringComparer.Ordinal);
        foreach(var row in n.RootElement.EnumerateArray())if(!inventory.TryAdd(Notam(Text(row,"notam_id",20)),row))throw new InvalidDataException("Duplicate notice identity.");
        var noticeInventory=inventory.Select(pair=>new AviationTfrNoticeMetadata(pair.Key,Text(pair.Value,"description",2000),Text(pair.Value,"facility",20),Text(pair.Value,"state",20),Text(pair.Value,"type",100),AviationCatalog.TfrReference)).OrderBy(p=>p.NotamId,StringComparer.Ordinal).ToArray();
        var noShapeIds=missing.RootElement.EnumerateArray().Select(row=>Notam(Text(row,"notam_id",20))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var root=g.RootElement;
        if(root.GetProperty("type").GetString()!="FeatureCollection"||root.GetProperty("crs").GetProperty("properties").GetProperty("name").GetString()!="urn:ogc:def:crs:EPSG::3857")throw new NotSupportedException("Unsupported official TFR geometry CRS.");
        var features=root.GetProperty("features");
        if(features.ValueKind!=JsonValueKind.Array||features.GetArrayLength()>2000||root.GetProperty("numberReturned").GetInt32()!=features.GetArrayLength()||root.GetProperty("totalFeatures").GetInt32()!=features.GetArrayLength())throw new InvalidDataException("Truncated/unsupported FAA geometry snapshot; previous successful snapshot retained.");
        var result=new List<AviationTfrBoundary>();var ids=new HashSet<long>();var omitted=0;var vertices=0;
        foreach(var f in features.EnumerateArray())
        {
            var p=f.GetProperty("properties");var key=Text(p,"NOTAM_KEY",40);var id=NotamKey(key);var gid=p.GetProperty("GID").GetInt64();
            if(gid<=0||!ids.Add(gid))throw new InvalidDataException("Conflicting official geometry ID.");
            var title=Text(p,"TITLE",2000);var facility=Text(p,"CNS_LOCATION_ID",20);var state=Text(p,"STATE",20);var type=Text(p,"LEGAL",100);
            if(!inventory.TryGetValue(id,out var notice)||noShapeIds.Contains(id,StringComparer.Ordinal)||
                title!=Text(notice,"description",2000)||facility!=Text(notice,"facility",20)||state!=Text(notice,"state",20)||type!=Text(notice,"type",100))
            {omitted++;continue;} // No speculative join or stale notice/geometry pairing.
            var shape=f.GetProperty("geometry");var kind=shape.GetProperty("type").GetString();
            if(kind is not("Polygon" or "MultiPolygon")){omitted++;continue;}
            var polys=kind=="Polygon"?new[]{shape.GetProperty("coordinates")}:shape.GetProperty("coordinates").EnumerateArray().ToArray();
            if(polys.Length is <1 or >100)throw new InvalidDataException("Oversized TFR multipart geometry.");
            var converted=new List<Coordinates[][]>();
            foreach(var polygon in polys)
            {
                if(polygon.GetArrayLength() is <1 or >100)throw new InvalidDataException("Invalid TFR ring count.");
                var rings=new List<Coordinates[]>();
                foreach(var ring in polygon.EnumerateArray())
                {
                    if(ring.GetArrayLength() is <4 or >12000)throw new InvalidDataException("TFR ring size outside bounds.");
                    var points=new List<Coordinates>();
                    foreach(var point in ring.EnumerateArray())
                    {
                        if(++vertices>MaximumVertices||point.GetArrayLength()!=2)throw new InvalidDataException("TFR vertex/dimension bound exceeded.");
                        var x=point[0].GetDouble();var y=point[1].GetDouble();
                        if(!double.IsFinite(x)||!double.IsFinite(y)||Math.Abs(x)>20037508.343||Math.Abs(y)>20037508.343)throw new InvalidDataException("Invalid published Web Mercator coordinate.");
                        points.Add(new((2*Math.Atan(Math.Exp(y/6378137))-Math.PI/2)*180/Math.PI,x/6378137*180/Math.PI));
                    }
                    if(points[0]!=points[^1]||points.Zip(points.Skip(1)).Any(z=>Math.Abs(z.First.Longitude-z.Second.Longitude)>180))throw new NotSupportedException("Unclosed/dateline TFR ring requires supported interpretation.");
                    rings.Add(points.ToArray());
                }
                converted.Add(rings.ToArray());
            }
            var coordinates=converted.ToArray();if(!Geometry(coordinates).IsValid)throw new InvalidDataException("Invalid official TFR topology; no silent repair.");
            result.Add(new(1,AviationDownloads.Hash(new{key,gid}),Text(f,"id",100),gid,id,key,title,facility,state,type,Text(p,"LAST_MODIFICATION_DATETIME",100),coordinates,AviationCatalog.TfrReference));
        }
        static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(1,utc,Hash(notices),Hash(geometry),Hash(noShape),inventory.Keys.Order(StringComparer.Ordinal).ToArray(),noShapeIds,result.ToArray(),omitted,Limitations,noticeInventory);
    }
    public static Geometry Geometry(Coordinates[][][] polygons)
    {
        var factory=new GeometryFactory();
        LinearRing Ring(Coordinates[] p)=>factory.CreateLinearRing(p.Select(v=>new Coordinate(v.Longitude,v.Latitude)).ToArray());
        return factory.CreateMultiPolygon(polygons.Select(p=>factory.CreatePolygon(Ring(p[0]),p.Skip(1).Select(Ring).ToArray())).ToArray());
    }
    public static void Validate(AviationTfrGeometrySnapshot value)
    {
        if(value.Version!=1)throw new NotSupportedException("Unsupported TFR boundary version.");
        if(value.Boundaries is null||value.NoticeIds is null||value.NoShapeNoticeIds is null||value.NoticeInventory is null||value.Limitations is null||value.Limitations.Length>20||value.Limitations.Any(t=>t is null||t.Length>2000)||value.NoticeInventory.Any(n=>n is null)||value.Boundaries.Any(b=>b is null)||value.NoticeIds.Any(n=>n is null)||value.NoShapeNoticeIds.Any(n=>n is null))throw new InvalidDataException("Incomplete retained TFR snapshot.");
        if(value.RetrievedUtc.Offset!=TimeSpan.Zero||new[]{value.NoticeSha256,value.GeometrySha256,value.NoShapeSha256}.Any(h=>h is null||!Regex.IsMatch(h,"^[a-f0-9]{64}$"))||value.Boundaries.Length>2000||value.NoticeIds.Length>2048||value.NoShapeNoticeIds.Length>2048||value.OmittedFeatures<0||value.NoticeInventory.Length!=value.NoticeIds.Length||!value.NoticeInventory.Select(n=>n.NotamId).SequenceEqual(value.NoticeIds))throw new InvalidDataException("Invalid retained TFR snapshot.");
        foreach(var n in value.NoticeInventory)if(Notam(n.NotamId)!=n.NotamId||n.OfficialUrl!=AviationCatalog.TfrReference||n.Title.Length>2000||n.Facility.Length>20||n.State.Length>20||n.Type.Length>100||new[]{n.Title,n.Facility,n.State,n.Type}.Any(t=>t.Any(char.IsControl)))throw new InvalidDataException("Invalid retained notice metadata.");
        var vertices=0;
        foreach(var b in value.Boundaries)
        {
            if(b.Version!=1||b.OfficialUrl!=AviationCatalog.TfrReference||b.Id is null||!Regex.IsMatch(b.Id,"^[a-f0-9]{64}$")||b.NotamKey is null||NotamKey(b.NotamKey)!=b.NotamId||!value.NoticeIds.Contains(b.NotamId,StringComparer.Ordinal)||b.SourceGeometryId<=0||b.Polygons is null||b.Polygons.Length is <1 or >100||b.Polygons.Any(p=>p is null||p.Any(r=>r is null||r.Any(c=>c is null))))throw new InvalidDataException("Invalid retained TFR identity.");
            foreach(var p in b.Polygons){if(p.Length is <1 or >100)throw new InvalidDataException("Invalid retained rings.");foreach(var r in p){if(r.Length is <4 or >12000)throw new InvalidDataException("Invalid retained ring length.");foreach(var c in r)if(++vertices>MaximumVertices||!double.IsFinite(c.Latitude)||!double.IsFinite(c.Longitude)||Math.Abs(c.Latitude)>85.1||Math.Abs(c.Longitude)>180)throw new InvalidDataException("Invalid retained coordinates.");if(r[0]!=r[^1])throw new InvalidDataException("Unclosed retained ring.");}}
            if(!Geometry(b.Polygons).IsValid)throw new InvalidDataException("Invalid retained TFR geometry.");
        }
    }
}
