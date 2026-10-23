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
            { yield return new(++index, "sua", entry.FullName, new() { ["ORIGINAL_MEMBER"] = entry.FullName }, null, null, 0, "Multiple contributor airspaces require full relationship interpretation; no arbitrary first feature selected"); continue; }
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
            var volumes = components.SelectMany(c => c.Elements(Aixm + "theAirspaceVolume").Elements(Aixm + "AirspaceVolume")).ToArray();
            var number = ++index;
            if (components.Length != 1 || volumes.Length != 1 || components[0].Element(Aixm + "operation")?.Value != "BASE")
            { yield return new(number, "sua", name, attributes, null, null, 0, "Composite SUBTR/UNION or multiple SAA volumes require full vertical operation interpretation; not silently flattened"); continue; }
            var volume = volumes[0];
            foreach (var (prefix, field) in new[] { ("LOWER", "lowerLimit"), ("UPPER", "upperLimit") })
            { Retain(prefix + "_VAL", volume.Element(Aixm + field)?.Value); Retain(prefix + "_UOM", volume.Element(Aixm + field)?.Attribute("uom")?.Value); Retain(prefix + "_CODE", volume.Element(Aixm + (field + "Reference"))?.Value); }
            var surface = volume.Element(Aixm + "horizontalProjection")?.Elements().SingleOrDefault();
            var crs = surface?.Attribute("srsName")?.Value;
            if (surface?.Name != Aixm + "Surface" || !string.Equals(crs, "urn:ogc:def:crs:OGC:1.3:CRS84", StringComparison.OrdinalIgnoreCase) || surface.Attribute("srsDimension")?.Value != "2")
                throw new NotSupportedException("Unverified SAA coordinate order/CRS/dimensions.");
            if (surface.Descendants(Gml + "ArcByCenterPoint").Any() || surface.Descendants(Gml + "CircleByCenterPoint").Any())
            { yield return new(number, "sua", name, attributes, null, null, 0, "Curved SAA geometry requires independently validated curve semantics; source retained, no guessed arcs/circles"); continue; }
            var factory = new GeometryFactory(); var polygons = new List<Polygon>(); var unsupported = false;
            foreach (var patch in surface.Descendants(Gml + "PolygonPatch"))
            {
                LinearRing ReadRing(XElement boundary)
                {
                    if (boundary.Descendants().Any(n => n.Name.LocalName is not ("LinearRing" or "Ring" or "curveMember" or "Curve" or "segments" or "LineStringSegment" or "pos" or "posList"))) throw new NotSupportedException("Unsupported SAA ring/curve element.");
                    var points = new List<Coordinate>();
                    foreach (var p in boundary.Descendants().Where(n => n.Name == Gml + "pos" || n.Name == Gml + "posList"))
                    {
                        var values = p.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (values.Length % 2 != 0 || values.Length > 2 * MaximumRecordVertices) throw new InvalidDataException("Invalid original SAA coordinate dimensions.");
                        for (var i = 0; i < values.Length; i += 2)
                        {
                            var x = double.Parse(values[i], CultureInfo.InvariantCulture); var y = double.Parse(values[i + 1], CultureInfo.InvariantCulture);
                            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidDataException("Invalid SAA source coordinate.");
                            if (x is < -180 or > 180 || y is < -85 or > 85) unsupported = true;
                            points.Add(new(x, y));
                        }
                    }
                    if (points.Count is < 4 or > MaximumRecordVertices || !points[0].Equals2D(points[^1])) throw new InvalidDataException("SAA ring is not explicitly closed.");
                    if (points.Zip(points.Skip(1)).Any(pair => Math.Abs(pair.First.X - pair.Second.X) > 180)) unsupported = true;
                    return factory.CreateLinearRing(points.ToArray());
                }
                var exterior = patch.Element(Gml + "exterior") ?? throw new InvalidDataException("SAA exterior missing.");
                polygons.Add(factory.CreatePolygon(ReadRing(exterior), patch.Elements(Gml + "interior").Select(ReadRing).ToArray()));
            }
            Geometry geometry = polygons.Count == 1 ? polygons[0] : factory.CreateMultiPolygon(polygons.ToArray());
            if (unsupported || polygons.Count == 0 || !geometry.IsValid || geometry.NumPoints > MaximumDisplayVertices)
            { yield return new(number, "sua", name, attributes, null, null, 0, "Unsupported SAA dateline/polar, invalid topology or oversized display; source preserved"); continue; }
            Check(token, canContinue); yield return new(number, "sua", name, attributes, geometry, geometry, 0, null);
        }
    }
}
