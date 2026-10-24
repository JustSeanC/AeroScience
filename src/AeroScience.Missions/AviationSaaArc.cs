using System.Globalization;
using System.Xml.Linq;
using NetTopologySuite.Geometries;
using Coordinates = AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

public static partial class AviationAirspace
{
    public const string ArcInterpolation = "WGS84 geodesic partial arcs; inherited CRS84 angles from longitude axis, increasing explicit minor span only. 0.5 m nominal chord target and maximum source endpoint join; derived arc endpoints use adjacent original literal vertices. Original literal vertices/center/radius/angles retained; not certified boundary accuracy.";

    // Do not normalize angle wraps or select a short/long path from endpoints. Those
    // interpretations require additional source proof. This subset is deliberately explicit.
    private static LinearRing ReadSaaArcRing(XElement boundary, CancellationToken token, Func<bool>? canContinue)
    {
        var allowed = new HashSet<string>(["Ring", "curveMember", "Curve", "segments", "LineStringSegment", "ArcByCenterPoint", "pointProperty", "Point", "pos", "posList", "radius", "startAngle", "endAngle"], StringComparer.Ordinal);
        if (boundary.Descendants().Any(e => e.Name.Namespace != Gml || !allowed.Contains(e.Name.LocalName) || e.Attribute("srsName") is not null || e.Attribute("srsDimension") is not null ||
            e.Attributes().Any(a => a.Name.LocalName is "href" or "orientation"))) throw new NotSupportedException("Curved SAA has linked, overridden or unsupported geometry.");
        var ring = Single(boundary, "Ring"); var members = ring.Elements().ToArray();
        if (members.Length is < 1 or > 256 || members.Any(e => e.Name != Gml + "curveMember")) throw new NotSupportedException("Curved SAA ring requires bounded ordered curve members.");
        var segments = members.SelectMany(e => Single(Single(e, "Curve"), "segments").Elements()).ToArray();
        if (segments.Length is < 2 or > 256 || segments.Count(e => e.Name == Gml + "ArcByCenterPoint") > 64) throw new NotSupportedException("Unsupported curved SAA segment inventory.");
        var decoded = segments.Select(segment => segment.Name == Gml + "ArcByCenterPoint" ? Arc(segment) : segment.Name == Gml + "LineStringSegment" ? Line(segment) : throw new NotSupportedException("Unsupported curved SAA segment.")).ToArray();
        for (var i = 0; i < segments.Length; i++)
        {
            Check(token, canContinue);
            var previous = (i + segments.Length - 1) % segments.Length; var next = (i + 1) % segments.Length;
            if (Separation(decoded[i][^1], decoded[next][0]) > .5) throw new NotSupportedException("Curved SAA source endpoints differ by more than 0.5 m; no gap was repaired.");
            if (segments[i].Name != Gml + "ArcByCenterPoint") continue;
            if (segments[previous].Name != Gml + "LineStringSegment" || segments[next].Name != Gml + "LineStringSegment") throw new NotSupportedException("Adjacent SAA arcs require independently supported join semantics.");
            // Only derived arc endpoints are aligned to the adjacent authoritative
            // literal vertices within the explicit source-precision tolerance. Joining
            // both rounded and computed endpoints can create tiny self-intersections.
            decoded[i][0] = decoded[previous][^1].Copy(); decoded[i][^1] = decoded[next][0].Copy();
        }
        var points = new List<Coordinate>();
        foreach (var part in decoded)
        {
            foreach (var p in part) if (points.Count == 0 || !points[^1].Equals2D(p)) points.Add(p);
            if (points.Count > MaximumDisplayVertices) throw new NotSupportedException("Curved SAA exceeds its bounded vertex count.");
        }
        if (points.Count < 4 || Separation(points[^1], points[0]) > .5) throw new NotSupportedException("Curved SAA source ring is not closed within the supported endpoint tolerance.");
        if (!points[0].Equals2D(points[^1])) points.Add(points[0].Copy());
        if (points.Any(p => p.X is < -180 or > 180 || p.Y is < -85 or > 85) || points.Zip(points.Skip(1)).Any(p => Math.Abs(p.First.X - p.Second.X) > 180))
            throw new NotSupportedException("Curved SAA crosses unsupported dateline/polar bounds.");
        return new GeometryFactory().CreateLinearRing(points.ToArray());

        XElement Single(XElement parent, string name)
        {
            var children = parent.Elements().ToArray();
            return children.Length == 1 && children[0].Name == Gml + name ? children[0] : throw new NotSupportedException("Ambiguous curved SAA nesting.");
        }
        double Number(XElement element)
        {
            if (element.HasElements || !double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw new NotSupportedException("Invalid curved SAA numeric source value.");
            return value;
        }
        List<Coordinate> Line(XElement element)
        {
            var coordinates = new List<Coordinate>();
            foreach (var child in element.Elements())
            {
                if (child.Name != Gml + "pos" && child.Name != Gml + "posList" || child.HasElements) throw new NotSupportedException("Unsupported curved SAA literal coordinates.");
                var values = child.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length % 2 != 0 || values.Length > MaximumDisplayVertices * 2 || child.Name == Gml + "pos" && values.Length != 2) throw new NotSupportedException("Invalid curved SAA source dimensions.");
                for (var i = 0; i < values.Length; i += 2) coordinates.Add(CoordinatePair(values[i], values[i + 1]));
                if (coordinates.Count > MaximumDisplayVertices) throw new NotSupportedException("Oversized curved SAA literal segment.");
            }
            if (coordinates.Count < 2) throw new NotSupportedException("Incomplete curved SAA literal segment.");
            return coordinates;
        }
        List<Coordinate> Arc(XElement element)
        {
            var children = element.Elements().ToArray();
            string[] fields = ["pointProperty", "radius", "startAngle", "endAngle"];
            if (element.Attribute("numArc")?.Value != "1" || children.Length != 4 || fields.Any(f => children.Count(e => e.Name == Gml + f) != 1)) throw new NotSupportedException("Curved SAA requires one explicit center, radius and angle pair.");
            var position = Single(Single(element.Element(Gml + "pointProperty")!, "Point"), "pos");
            var values = position.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (position.HasElements || values.Length != 2) throw new NotSupportedException("Invalid curved SAA center dimensions.");
            var center = CoordinatePair(values[0], values[1]);
            var radiusElement = element.Element(Gml + "radius")!;
            var metres = Number(radiusElement) * (radiusElement.Attribute("uom")?.Value switch { "NM" => 1852d, "MI" => 1609.344d, "FT" => .3048d, "M" => 1d, _ => throw new NotSupportedException("Unsupported curved SAA radius units.") });
            var startElement = element.Element(Gml + "startAngle")!; var endElement = element.Element(Gml + "endAngle")!;
            if (startElement.Attribute("uom")?.Value != "deg" || endElement.Attribute("uom")?.Value != "deg") throw new NotSupportedException("Unsupported curved SAA angular units.");
            var start = Number(startElement); var end = Number(endElement); var span = end - start;
            if (metres is < 1 or > 500000 || start is < -360 or > 360 || end is < -360 or > 360 || span is <= 0 or > 180) throw new NotSupportedException("Curved SAA radius, wrap or major/reversed angular span is not independently supported.");
            var count = Math.Max(2, (int)Math.Ceiling(span * Math.PI / 180 / (2 * Math.Acos(1 - .5 / metres))));
            if (count > 4096) throw new NotSupportedException("Curved SAA arc exceeds its vertex bound.");
            var result = new List<Coordinate>(count + 1);
            for (var i = 0; i <= count; i++)
            {
                Check(token, canContinue);
                // CRS84 axes are east/north. GML angles use the first axis;
                // GeographicLib azimuths are clockwise from north.
                var point = Geodesy.Direct(new Coordinates(center.Y, center.X), 90 - (start + span * i / count), metres);
                result.Add(new(point.Longitude, point.Latitude));
            }
            return result;
        }
        static Coordinate CoordinatePair(string x, string y)
        {
            if (!double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) || !double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.IsFinite(lon) || !double.IsFinite(lat) || lon is < -180 or > 180 || lat is < -85 or > 85) throw new NotSupportedException("Unsupported curved SAA source coordinate.");
            return new(lon, lat);
        }
        static double Separation(Coordinate a, Coordinate b) => Geodesy.Distance(new Coordinates(a.Y, a.X), new Coordinates(b.Y, b.X));
    }
}
