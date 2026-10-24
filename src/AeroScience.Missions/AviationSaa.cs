using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using AeroScience.Shared;
using NetTopologySuite.Geometries;

namespace AeroScience.Missions;

public static partial class AviationAirspace
{
    public const string Crs84 = "OGC:CRS84 (WGS84 longitude/latitude)";
    private static readonly XNamespace Aixm = "http://www.aixm.aero/schema/5.0", Gml = "http://www.opengis.net/gml/3.2";
    public static IEnumerable<SourceRecord> ReadSaa(string folder, AviationDownloadJob job, CancellationToken token, Func<bool>? canContinue = null)
    {
        if (job.State != "Completed" || job.Preview.Item.OriginalFileName != "aixm5.0.zip") throw new ArgumentException("Use the verified FAA AIXM 5.0 original.");
        using var zip = ZipFile.OpenRead(Member(folder, job, "Saa_Sub_File.zip"));
        if (zip.Entries.Count is < 1 or > 2000) throw new InvalidDataException("SAA source count exceeds its bound.");
        var index = 0;
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            Check(token, canContinue);
            if (entry.Length is < 1 or > 1048576 || !entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported SAA original XML.");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576, IgnoreComments = true };
            // Validate depth before constructing a bounded per-feature XML tree. Never resolve provider schema URLs.
            using (var input = entry.Open()) using (var reader = XmlReader.Create(input, settings))
                while (reader.Read()) { if (reader.Depth > 64 || reader.AttributeCount > 64) throw new InvalidDataException("SAA XML nesting exceeds its bound."); token.ThrowIfCancellationRequested(); }
            XDocument document; using (var input = entry.Open()) using (var reader = XmlReader.Create(input, settings)) document = XDocument.Load(reader);
            var airspaces = document.Descendants(Aixm + "Airspace").ToArray();
            if (airspaces.Length == 0) throw new InvalidDataException("No exact AIXM 5.0 airspace in the official source member.");
            if (airspaces.Length > 1)
            {
                var identities=string.Join("; ",airspaces.Select(a=>(a.Element(Gml+"identifier")?.Value??"Unknown")+" / "+(a.Descendants(Aixm+"designator").FirstOrDefault()?.Value??"Unknown")));
                if(identities.Length>8192)throw new InvalidDataException("Oversized contributor identity inventory.");
                yield return new(++index, "sua", entry.FullName, new() { ["ORIGINAL_MEMBER"] = entry.FullName, ["SOURCE_IDENTITIES"] = identities }, null, null, 0, "Multiple contributor airspaces require full relationship interpretation; no arbitrary first feature selected"); continue;
            }
            var slices = airspaces[0].Elements(Aixm + "timeSlice").Elements(Aixm + "AirspaceTimeSlice").ToArray();
            if (slices.Length != 1 || slices[0].Element(Aixm + "interpretation")?.Value != "BASELINE") throw new NotSupportedException("Unverified SAA timeslice interpretation.");
            var slice = slices[0]; var name = slice.Element(Aixm + "name")?.Value ?? "Unnamed published SAA";
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["ORIGINAL_MEMBER"] = entry.FullName, ["NAME"] = name,
                ["DESIGNATOR"] = slice.Element(Aixm + "designator")?.Value ?? "", ["SOURCE_IDENTIFIER"] = airspaces[0].Element(Gml + "identifier")?.Value ?? "" };
            void Retain(string key, string? value)
            { if (value?.Length > 8192 || value?.Any(char.IsControl) == true) throw new InvalidDataException("Oversized/control SAA attribute."); attributes[key] = value ?? ""; }
            Retain("SUA_TYPE", slice.Descendants().FirstOrDefault(e => e.Name.LocalName == "suaType")?.Value);
            Retain("ADMINISTRATIVE_AREA", slice.Descendants().FirstOrDefault(e => e.Name.LocalName == "administrativeArea")?.Value);
            // Definition validity is not activation. Preserve explicit UTC/unknown attributes without pretending this is live.
            var definition = slice.Element(Gml + "validTime");
            Retain("DEFINITION_BEGIN", definition?.Descendants(Gml + "beginPosition").SingleOrDefault()?.Value);
            Retain("DEFINITION_END", definition?.Descendants(Gml + "endPosition").SingleOrDefault()?.Value);
            var usage = document.Descendants(Aixm + "AirspaceUsageTimeSlice").FirstOrDefault();
            Retain("ACTIVATION_SOURCE_STATUS", usage?.Descendants(Aixm + "statusActivation").FirstOrDefault()?.Value);
            Retain("WKHR_CODE", usage?.Descendants(Aixm + "workingHours").FirstOrDefault()?.Value);
            Retain("WKHR_RMK", string.Join("; ", usage?.Descendants(Aixm + "Timesheet").Select(e => string.Join(" ", e.Elements().Where(n => !n.HasElements).Select(n => n.Name.LocalName + "=" + n.Value))) ?? []));
            Retain("DST", usage?.Descendants(Aixm + "daylightSavingAdjust").FirstOrDefault()?.Value);
            Retain("GMTOFFSET", usage?.Descendants().FirstOrDefault(e => e.Name.LocalName == "timeOffset")?.Value);
            var components = slice.Elements(Aixm + "geometryComponent").Elements(Aixm + "AirspaceGeometryComponent").ToArray();
            var number = ++index;
            if (components.Length != 1 || components[0].Element(Aixm + "operation")?.Value != "BASE")
            {
                Geometry? composite = null; string? omission = null;
                try { composite = ReadSaaComposite(components, attributes, token, canContinue); }
                catch (Exception e) when (e is NotSupportedException or InvalidDataException or TopologyException)
                { omission = "Composite SAA omitted: " + e.Message; }
                yield return new(number, "sua", name, attributes, composite, composite, 0, omission); continue;
            }
            var volumes = components[0].Elements(Aixm + "theAirspaceVolume").Elements(Aixm + "AirspaceVolume").ToArray();
            if (volumes.Length != 1) { yield return new(number, "sua", name, attributes, null, null, 0, "Unresolved or multiple SAA volumes; no geometry invented"); continue; }
            if (components[0].Elements(Aixm+"theAirspaceVolume").Any(p=>p.Attributes().Any(a=>a.Name.LocalName=="href")))
            { yield return new(number,"sua",name,attributes,null,null,0,"Linked SAA volume is unsupported; no reference resolved");continue; }
            var volume = volumes[0]; RetainSaaLimits(volume, attributes);
            var surface = SaaSurface(volume); // A bad declared CRS remains a hard source error.
            Geometry? geometry = null; string? reason = null;
            try { geometry = ReadSaaSurface(surface, attributes, token, canContinue); }
            catch (NotSupportedException e) { reason = "Curved SAA omitted: " + e.Message; }
            yield return new(number, "sua", name, attributes, geometry, geometry, 0, reason);
        }
    }
    private static void RetainSaaLimits(XElement volume, Dictionary<string, string> attributes)
    {
        foreach (var (prefix, field) in new[] { ("LOWER", "lowerLimit"), ("UPPER", "upperLimit") })
        {
            foreach (var (suffix,value) in new[] { ("_VAL",volume.Element(Aixm + field)?.Value), ("_UOM",volume.Element(Aixm + field)?.Attribute("uom")?.Value), ("_CODE",volume.Element(Aixm + (field + "Reference"))?.Value) })
            {
                if (value?.Length > 8192 || value?.Any(char.IsControl) == true) throw new InvalidDataException("Oversized/control SAA limit.");
                attributes[prefix + suffix] = value ?? "";
            }
        }
    }
    private static XElement SaaSurface(XElement volume)
    {
        var projections = volume.Elements(Aixm + "horizontalProjection").ToArray();
        if(projections.Any(p=>p.Attributes().Any(a=>a.Name.LocalName=="href")))throw new NotSupportedException("Linked SAA horizontal projection is unsupported.");
        var children = projections.Length == 1 ? projections[0].Elements().ToArray() : [];
        var surface = children.Length == 1 ? children[0] : null;
        if (surface?.Name != Aixm + "Surface" || !string.Equals(surface.Attribute("srsName")?.Value, "urn:ogc:def:crs:OGC:1.3:CRS84", StringComparison.OrdinalIgnoreCase) || surface.Attribute("srsDimension")?.Value != "2")
            throw new NotSupportedException("Unverified SAA coordinate order/CRS/dimensions.");
        return surface;
    }
    private static Geometry ReadSaaSurface(XElement surface, Dictionary<string, string> attributes, CancellationToken token, Func<bool>? canContinue)
    {
        if (surface.DescendantsAndSelf().Any(n => n.Attributes().Any(a => a.Name.LocalName is "href" or "orientation")) ||
            surface.Descendants().Any(n => n.Attribute("srsName") is not null || n.Attribute("srsDimension") is not null))
            throw new NotSupportedException("Linked, reversed or overridden SAA geometry requires explicit interpretation.");
        foreach (var n in surface.Descendants().Where(n => n.Attribute("interpolation") is not null))
            if (n.Attribute("interpolation")!.Value != (n.Name == Gml + "LineStringSegment" ? "linear" : n.Name == Gml + "ArcByCenterPoint" || n.Name == Gml + "CircleByCenterPoint" ? "circularArcCenterPointWithRadius" : "unsupported"))
                throw new NotSupportedException("Unsupported SAA interpolation override.");
        var hasArcs = surface.Descendants(Gml + "ArcByCenterPoint").Any();
        if (hasArcs) attributes["GEOMETRY_INTERPOLATION"] = ArcInterpolation;
        else if (surface.Descendants(Gml + "CircleByCenterPoint").Any()) attributes["GEOMETRY_INTERPOLATION"] = CircleInterpolation;
        var factory = new GeometryFactory(); var polygons = new List<Polygon>();
        LinearRing ReadRing(XElement boundary)
        {
            if (boundary.Descendants(Gml + "ArcByCenterPoint").Any()) return ReadSaaArcRing(boundary, token, canContinue);
            if (boundary.Descendants(Gml + "CircleByCenterPoint").Any()) return ReadSaaCircle(boundary, token, canContinue);
            if (boundary.Descendants().Any(n => n.Name.Namespace != Gml || n.Name.LocalName is not ("LinearRing" or "Ring" or "curveMember" or "Curve" or "segments" or "LineStringSegment" or "pos" or "posList")))
                throw new NotSupportedException("Unsupported SAA ring/curve element.");
            var points = new List<Coordinate>();
            foreach (var p in boundary.Descendants().Where(n => n.Name == Gml + "pos" || n.Name == Gml + "posList"))
            {
                var values = p.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.HasElements || values.Length % 2 != 0 || values.Length > 2 * MaximumRecordVertices || p.Name == Gml + "pos" && values.Length != 2)
                    throw new InvalidDataException("Invalid original SAA coordinate dimensions.");
                for (var i = 0; i < values.Length; i += 2)
                {
                    if (points.Count >= MaximumRecordVertices) throw new NotSupportedException("SAA literal ring exceeds its vertex bound.");
                    if (!double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.TryParse(values[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || !double.IsFinite(x) || !double.IsFinite(y))
                        throw new InvalidDataException("Invalid SAA source coordinate.");
                    if (x is < -180 or > 180 || y is < -85 or > 85) throw new NotSupportedException("Unsupported SAA dateline/polar source coordinates.");
                    points.Add(new(x, y));
                }
            }
            if (points.Count < 4 || !points[0].Equals2D(points[^1])) throw new InvalidDataException("SAA ring is not explicitly closed.");
            if (points.Zip(points.Skip(1)).Any(p => Math.Abs(p.First.X - p.Second.X) > 180)) throw new NotSupportedException("Unsupported SAA dateline ring.");
            return factory.CreateLinearRing(points.ToArray());
        }
        var patches = surface.Elements(Gml + "patches").ToArray();
        if (patches.Length != 1 || surface.Elements().Count() != 1 || patches[0].Elements().Any(p => p.Name != Gml + "PolygonPatch")) throw new NotSupportedException("Unsupported SAA surface patches.");
        foreach (var patch in patches[0].Elements())
        {
            Check(token, canContinue);
            if (polygons.Count >= 64 || patch.Elements().Any(e => e.Name != Gml + "exterior" && e.Name != Gml + "interior") || patch.Elements(Gml + "exterior").Count() != 1 || patch.Elements(Gml + "interior").Count() > 64)
                throw new NotSupportedException("Unsupported SAA polygon/ring inventory.");
            polygons.Add(factory.CreatePolygon(ReadRing(patch.Element(Gml + "exterior")!), patch.Elements(Gml + "interior").Select(ReadRing).ToArray()));
            if (polygons.Sum(p => p.NumPoints) > MaximumDisplayVertices) throw new NotSupportedException("SAA geometry exceeds its bounded vertex count.");
        }
        Geometry geometry = polygons.Count == 1 ? polygons[0] : factory.CreateMultiPolygon(polygons.ToArray());
        if (polygons.Count == 0 || !geometry.IsValid) throw new NotSupportedException("Invalid or empty source SAA topology; no silent repair.");
        return geometry;
    }
    private const string CircleInterpolation = "WGS84 geodesic circle; 0.5 m nominal chord target. Original center/radius/units retained in the immutable XML. Not certified boundary accuracy.";
}
