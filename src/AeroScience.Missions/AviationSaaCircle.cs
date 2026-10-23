using System.Globalization;
using System.Xml.Linq;
using Coordinates = AeroScience.Shared.Coordinates;
using NetTopologySuite.Geometries;

namespace AeroScience.Missions;

public static partial class AviationAirspace
{
    // Only full circles have unambiguous angular semantics in this adapter. Partial arcs
    // and mixed curved rings still remain explicit omissions, never guessed or straightened.
    private static LinearRing ReadSaaCircle(XElement boundary, CancellationToken token, Func<bool>? canContinue)
    {
        var circles = boundary.Descendants(Gml + "CircleByCenterPoint").ToArray();
        var segments = boundary.Descendants(Gml + "segments").ToArray();
        if (circles.Length != 1 || segments.Length != 1 || segments[0].Elements().Count() != 1 ||
            circles[0].Attribute("numArc")?.Value != "1" || boundary.Descendants().Any(n =>
                n.Name.Namespace != Gml || n.Name.LocalName is not ("Ring" or "curveMember" or "Curve" or "segments" or "CircleByCenterPoint" or "pointProperty" or "Point" or "pos" or "radius") ||
                n.Attribute("srsName") is not null || n.Attribute("srsDimension") is not null || n.Attributes().Any(a => a.Name.LocalName == "href")))
            throw new NotSupportedException("Unsupported mixed/linked/overridden SAA circle; no geometry was guessed.");
        XElement Only(XElement parent, string name)
        {
            var children = parent.Elements().ToArray();
            if (children.Length != 1 || children[0].Name != Gml + name) throw new NotSupportedException("Ambiguous SAA circle nesting; no extra geometry may be discarded.");
            return children[0];
        }
        var nested = Only(Only(Only(Only(Only(boundary, "Ring"), "curveMember"), "Curve"), "segments"), "CircleByCenterPoint");
        var circle = circles[0];
        if (!ReferenceEquals(nested, circle)) throw new InvalidDataException("Circle path identity differs.");
        if (circle.Elements().Count() != 2 || circle.Elements(Gml + "pointProperty").Count() != 1 || circle.Elements(Gml + "radius").Count() != 1 ||
            circle.Descendants(Gml + "Point").Count() != 1 || circle.Descendants(Gml + "pos").Count() != 1)
            throw new InvalidDataException("SAA circle requires one original center and radius.");
        var centerPosition = Only(Only(circle.Element(Gml + "pointProperty")!, "Point"), "pos");
        if (centerPosition.HasElements || circle.Element(Gml + "radius")!.HasElements) throw new InvalidDataException("Unexpected nested source values.");
        var values = circle.Descendants(Gml + "pos").Single().Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 2 || !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
            !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
            !double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -85 or > 85 || longitude is < -180 or > 180)
            throw new InvalidDataException("Invalid source circle center in inherited CRS84 order.");
        var radiusElement = circle.Element(Gml + "radius")!;
        var factor = radiusElement.Attribute("uom")?.Value switch { "NM" => 1852d, "MI" => 1609.344d, "FT" => .3048d, "M" => 1d, _ => throw new NotSupportedException("Unsupported original SAA radius unit.") };
        if (!double.TryParse(radiusElement.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var nativeRadius) || !double.IsFinite(nativeRadius))
            throw new InvalidDataException("Invalid original SAA radius.");
        var radius = nativeRadius * factor;
        if (!double.IsFinite(radius) || radius is < 1 or > 500000) throw new NotSupportedException("SAA circle radius exceeds the supported bounded rendering range.");
        var steps = Math.Max(36, (int)Math.Ceiling(Math.PI / Math.Acos(1 - .5 / radius)));
        if (steps > 4096) throw new NotSupportedException("SAA circle exceeds the vertex bound.");
        var center = new Coordinates(latitude, longitude); var points = new Coordinate[steps + 1];
        for (var i = 0; i < steps; i++)
        {
            Check(token, canContinue);
            var point = Geodesy.Direct(center, 360d * i / steps, radius);
            points[i] = new(point.Longitude, point.Latitude);
            if (point.Latitude is < -85 or > 85 || i > 0 && Math.Abs(points[i].X - points[i - 1].X) > 180)
                throw new NotSupportedException("SAA circle crosses unsupported polar/dateline bounds; original retained.");
        }
        points[^1] = points[0].Copy();
        if (Math.Abs(points[^2].X - points[0].X) > 180) throw new NotSupportedException("Unsupported dateline circle closure.");
        return new GeometryFactory().CreateLinearRing(points);
    }
}
