using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using AeroScience.Shared;
using NetTopologySuite.Geometries;

namespace AeroScience.Missions;

public static partial class AviationAirspace
{
    public const string SaaComposition = "Derived CRS84 horizontal geometry: BASE then explicit operationSequence UNION/SUBTR with identical verified original vertical limits. NTS polygon Boolean operations, no topology repair or altitude conversion. Original component curves/coordinates and relationships remain authoritative in the retained XML; no live activation inferred.";

    private static Geometry ReadSaaComposite(XElement[] components, Dictionary<string, string> attributes,
        CancellationToken token, Func<bool>? canContinue,out Geometry? footprint,out AviationAirspaceVolume[]? volumes)
    {
        footprint=null;volumes=null;
        if (components.Length is < 2 or > 32) throw new NotSupportedException("SUBTR/UNION requires 2–32 explicit components and exactly one BASE.");
        var decoded = new List<(int Sequence, string Operation, XElement Volume, XElement Surface, int SourceIndex)>();
        for (var i = 0; i < components.Length; i++)
        {
            Check(token, canContinue); var c = components[i];
            if (c.Elements().Any(e => e.Name != Aixm + "operation" && e.Name != Aixm + "operationSequence" && e.Name != Aixm + "theAirspaceVolume" && e.Name != Aixm + "annotation") ||
                c.Elements(Aixm + "operation").Count() != 1 || c.Elements(Aixm + "operationSequence").Count() != 1 ||
                !int.TryParse(c.Element(Aixm + "operationSequence")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 0 ||
                c.DescendantsAndSelf().Any(e => e.Attributes().Any(a => a.Name.LocalName == "href")))
                throw new NotSupportedException("Missing/linked contributor, unsupported component or ambiguous operation sequence.");
            var operation = c.Element(Aixm + "operation")!.Value;
            if (new[] { c.Element(Aixm + "operation")!, c.Element(Aixm + "operationSequence")! }.Any(e => e.HasElements || e.Attributes().Any(a => a.Name.LocalName is "nil" or "nilReason")))
                throw new NotSupportedException("Unknown component operation or sequence.");
            if (operation is not ("BASE" or "UNION" or "SUBTR")) throw new NotSupportedException("Unsupported SAA component operation.");
            var properties = c.Elements(Aixm + "theAirspaceVolume").ToArray();
            if (properties.Length != 1 || properties[0].Elements().Count() != 1 || properties[0].Element(Aixm + "AirspaceVolume") is not {} volume)
                throw new NotSupportedException("Missing or unresolved contributor volume; no external references resolved.");
            if (volume.Elements().Any(e => e.Name.Namespace != Aixm || e.Name.LocalName is not ("upperLimit" or "upperLimitReference" or "lowerLimit" or "lowerLimitReference" or "horizontalProjection" or "annotation")))
                throw new NotSupportedException("Additional vertical or contributor semantics require separate volume interpretation.");
            decoded.Add((sequence, operation, volume, SaaSurface(volume), i + 1));
        }
        var ordered = decoded.OrderBy(c => c.Sequence).ToArray();
        if (ordered.Select(c => c.Sequence).Distinct().Count() != ordered.Length || ordered[0].Operation != "BASE" || ordered.Count(c => c.Operation == "BASE") != 1)
            throw new NotSupportedException("SUBTR/UNION requires unique explicit ordering with exactly one initial BASE.");
        foreach(var c in ordered)foreach(var field in new[]{"lowerLimit","lowerLimitReference","upperLimit","upperLimitReference"})
        {
            var values=c.Volume.Elements(Aixm+field).ToArray();
            if(values.Length>1||values.Any(v=>v.HasElements||v.Attributes().Any(a=>a.Name.LocalName is "nil" or "nilReason")&&!string.IsNullOrWhiteSpace(v.Value)))
                throw new NotSupportedException("Ambiguous or contradictory native altitude field.");
        }
        var limits=ordered.Select(c=>{var raw=new Dictionary<string,string>();RetainSaaLimits(c.Volume,raw);return raw;}).ToArray();
        if(limits.Any(r=>Altitude(r,"LOWER").Status!="Known"||Altitude(r,"UPPER").Status!="Known")||limits.Select(r=>JsonSerializer.Serialize(r,WireJson.Options)).Distinct().Count()!=1)
        {
            var native=new List<Geometry>();var definitions=new List<AviationAirspaceVolume>();var total=0;Geometry? extent=null;
            foreach(var c in ordered)
            {
                Check(token,canContinue);var raw=new Dictionary<string,string>();RetainSaaLimits(c.Volume,raw);
                var lo=Altitude(raw,"LOWER");var hi=Altitude(raw,"UPPER");
                if(lo.Status=="Known"&&hi.Status=="Known"&&lo.Reference==hi.Reference&&lo.Unit==hi.Unit&&lo.Value>hi.Value)throw new NotSupportedException("Contradictory comparable native vertical bounds.");
                var geometry=ReadSaaSurface(c.Surface,raw,token,canContinue);total+=geometry.NumPoints;
                if(total>MaximumDisplayVertices)throw new NotSupportedException("Native volume input exceeds its bounded work budget.");
                native.Add(geometry);definitions.Add(new(1,c.SourceIndex,c.Sequence,c.Operation,c.Volume.Attribute(Gml+"id")?.Value,c.Surface.Attribute(Gml+"id")?.Value,Altitude(raw,"LOWER"),Altitude(raw,"UPPER")));
                if(c.Operation is "BASE" or "UNION")
                {
                    if(extent is not null&&(long)extent.NumPoints*geometry.NumPoints>4000000)throw new NotSupportedException("Native volume extent exceeds its bounded work budget.");
                    extent=extent is null?geometry:extent.Union(geometry);
                    if(!extent.IsValid||extent.NumPoints>MaximumDisplayVertices)throw new NotSupportedException("Invalid/oversized potential horizontal extent.");
                }
                Check(token,canContinue);
            }
            // GeometryCollection preserves every native component independently. The
            // separate display/index extent is only a conservative candidate region.
            foreach(var key in new[]{"LOWER_VAL","LOWER_UOM","LOWER_CODE","UPPER_VAL","UPPER_UOM","UPPER_CODE"})attributes[key]="";
            attributes["GEOMETRY_COMPOSITION"]="Ordered native vertical-volume model; map shows potential horizontal extent, not a flattened restriction. Inspect each component and comparable altitude dimensions. Activation remains Unknown.";
            footprint=extent!;volumes=definitions.ToArray();
            return new GeometryFactory().CreateGeometryCollection(native.ToArray());
        }
        string? vertical = null; var manifest = new List<object>(); var vertices = 0; Geometry? result = null;
        var interpolation = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in ordered)
        {
            Check(token, canContinue); var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in new[] { "lowerLimit", "lowerLimitReference", "upperLimit", "upperLimitReference" })
                if (c.Volume.Elements(Aixm + field).Count() != 1 || c.Volume.Element(Aixm + field)!.HasElements || c.Volume.Element(Aixm + field)!.Attributes().Any(a => a.Name.LocalName is "nil" or "nilReason"))
                    throw new NotSupportedException("Missing, duplicate or unknown composite vertical limits.");
            RetainSaaLimits(c.Volume, raw);
            if (Altitude(raw, "LOWER").Status != "Known" || Altitude(raw, "UPPER").Status != "Known")
                throw new NotSupportedException("Composite vertical extents must be verified, not inferred from unknown limits.");
            var lower=Altitude(raw,"LOWER");var upper=Altitude(raw,"UPPER");
            if (lower.Reference==upper.Reference && lower.Unit==upper.Unit && lower.Value>upper.Value)
                throw new NotSupportedException("Contradictory comparable vertical bounds.");
            var key = JsonSerializer.Serialize(raw, WireJson.Options);
            if (vertical is not null && key != vertical)
                throw new NotSupportedException("Different original vertical limits; separate volume relationships are required (not flattened).");
            if (vertical is null) { vertical = key; RetainSaaLimits(c.Volume, attributes); }
            var geometry = ReadSaaSurface(c.Surface, raw, token, canContinue);
            vertices = checked(vertices + geometry.NumPoints);
            if (vertices > MaximumDisplayVertices) throw new NotSupportedException("Composite input exceeds its bounded vertex work budget.");
            if (raw.TryGetValue("GEOMETRY_INTERPOLATION", out var method)) interpolation.Add(method);
            manifest.Add(new { sourceComponent = c.SourceIndex, sequence = c.Sequence, operation = c.Operation,
                volumeId = c.Volume.Attribute(Gml + "id")?.Value, surfaceId = c.Surface.Attribute(Gml + "id")?.Value,
                sourceCrs = c.Surface.Attribute("srsName")!.Value, lower = Altitude(raw, "LOWER"), upper = Altitude(raw, "UPPER") });
            // Bound overlay work before entering synchronous NTS code, as well as
            // checking the resulting inventory. No unbounded intersection explosion.
            if(result is not null && (long)result.NumPoints*geometry.NumPoints>4000000)
                throw new NotSupportedException("Composite Boolean pair exceeds the bounded operation work budget.");
            Check(token, canContinue);
            result = result is null ? geometry : c.Operation == "UNION" ? result.Union(geometry) : result.Difference(geometry);
            Check(token, canContinue);
            if (result.IsEmpty || result is not (Polygon or MultiPolygon) || !result.IsValid || result.NumPoints > MaximumDisplayVertices)
                throw new NotSupportedException("Empty, invalid, nonpolygonal or oversized Boolean result; no geometry repaired.");
        }
        var json = JsonSerializer.Serialize(new { version = 1, components = manifest }, WireJson.Options);
        if (json.Length > 8192) throw new NotSupportedException("Composite provenance exceeds its bounded metadata budget.");
        attributes["GEOMETRY_COMPOSITION"] = SaaComposition;
        attributes["SOURCE_COMPONENTS"] = json;
        if (interpolation.Count != 0) attributes["GEOMETRY_INTERPOLATION"] = interpolation.Contains(ArcInterpolation) ? ArcInterpolation : CircleInterpolation;
        return result!;
    }
}
