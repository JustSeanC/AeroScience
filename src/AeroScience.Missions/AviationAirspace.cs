using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AeroScience.Shared;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;
using Coordinates = AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

// ESRI's documented Polygon/PolygonZ and dBASE III contracts, bounded and streamed.
// Originals stay immutable. Generalization applies only to map display, never stored source geometry.
public static partial class AviationAirspace
{
    public const string Nad83 = "EPSG:4269 (NAD83; realization/epoch unspecified by FAA PRJ)";
    public const int MaximumRecords = 100000, MaximumRecordVertices = 1000000, MaximumDisplayVertices = 12000;
    public static readonly string[] Kinds = ["class-b", "class-c", "class-d", "class-e", "sua"];
    public static string DatasetId(AviationDownloadJob job) => AviationDownloads.Hash(new
        { airspaceParser = job.Preview.Item.OriginalFileName == "aixm5.0.zip" ? 2 : 1, job.Preview.Item.Id, job.OriginalSha256 });
    public static bool IsOriginal(AviationCatalogItem item) => item.Product == AviationProduct.Nasr && item.OriginalFileName is "class_airspace_shape_files.zip" or "aixm5.0.zip";
    public sealed record SourceRecord(int Number, string Kind, string Name, Dictionary<string, string> Attributes,
        Geometry? Original, Geometry? Display, double Tolerance, string? UnsupportedReason);
    public static string Member(string folder, AviationDownloadJob job, string name)
    {
        var file = job.Files?.SingleOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("Required FAA original companion is missing: " + name);
        var path = Path.Combine(folder, file.Name); StorageManager.ValidateLocalPath(path);
        if (Path.GetFileName(file.Name) != file.Name || !File.Exists(path) || new FileInfo(path).Length != file.Bytes || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("FAA original companion is changed or linked. Preserve the originals.");
        return path;
    }
    public static string ClassCrs(string folder, AviationDownloadJob job)
    {
        var path = Member(folder, job, "Class_Airspace.prj");
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Oversized airspace CRS.");
        var wkt = new string(File.ReadAllText(path).Where(c => !char.IsWhiteSpace(c)).ToArray());
        const string horizontal = "GEOGCS[\"GCS_North_American_1983\",DATUM[\"D_North_American_1983\",SPHEROID[\"GRS_1980\",6378137.0,298.257222101]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
        if (wkt != horizontal && !wkt.StartsWith(horizontal + ",VERTCS[", StringComparison.Ordinal))
            throw new NotSupportedException("Unverified or projected FAA CRS. No coordinate transformation was guessed.");
        return Nad83; // Vertical attributes are interpreted separately; the PRJ's VERTCS is not an airspace altitude datum.
    }
    public static AviationAltitudeLimit Altitude(Dictionary<string, string> attributes, string prefix)
    {
        string Get(string suffix) => attributes.GetValueOrDefault(prefix + suffix, "");
        var raw = Get("_VAL"); var unit = Get("_UOM"); var reference = Get("_CODE");
        double? value = raw == "GND" && reference == "SFC" ? 0 : double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n : null;
        // Negative source sentinels remain Unknown; neither -9998 nor a missing upper limit means unlimited.
        var understoodUnit = unit is "FT" or "M" or "FL" ? unit : null;
        var understoodReference = reference switch { "MSL" => "MSL", "SFC" => "Above surface", "STD" => "Standard pressure", _ => "Unknown" };
        return new(value, understoodUnit, understoodReference, raw, unit, reference, Get("_DESC"),
            raw == "UNL" ? "Published unlimited" : value is not null && understoodUnit is not null && understoodReference != "Unknown" ? "Known" : "Unknown");
    }
    public static AviationAirspaceSchedule Schedule(Dictionary<string, string> attributes) => new(
        attributes.GetValueOrDefault("WKHR_CODE", ""), attributes.GetValueOrDefault("WKHR_RMK", ""),
        attributes.GetValueOrDefault("DST", ""), attributes.GetValueOrDefault("GMTOFFSET", ""),
        attributes.GetValueOrDefault("WKHR_CODE") == "H24" ? "Published H24 hours; live activation is unverified. No UTC schedule was inferred." :
        "Published hours/DST/offset retained verbatim. Activation and UTC intervals are Unknown without an authoritative interpretable schedule.");
    public static IEnumerable<SourceRecord> ReadClass(string folder, AviationDownloadJob job, CancellationToken token, Func<bool>? canContinue = null)
    {
        if (job.State != "Completed" || job.Preview.Item.OriginalFileName != "class_airspace_shape_files.zip") throw new ArgumentException("Use the verified exact Class airspace original.");
        _ = ClassCrs(folder, job);
        using var dbf = new Dbf(Member(folder, job, "Class_Airspace.dbf"));
        using var shp = new BinaryReader(File.OpenRead(Member(folder, job, "Class_Airspace.shp")));
        using var shx = new BinaryReader(File.OpenRead(Member(folder, job, "Class_Airspace.shx")));
        var type = Header(shp); if (Header(shx) != type || shx.BaseStream.Length != 100L + 8L * dbf.Count) throw new InvalidDataException("SHP/SHX/DBF inventory differs.");
        var factory = new GeometryFactory(); var record = 0;
        while (shp.BaseStream.Position < shp.BaseStream.Length)
        {
            Check(token, canContinue); var position = shp.BaseStream.Position;
            var number = Big(shp); var length = checked(2L * Big(shp));
            if (number != ++record || record > dbf.Count || 2L * Big(shx) != position || 2L * Big(shx) != length || length is < 4 or > 32000064 || length > shp.BaseStream.Length - shp.BaseStream.Position)
                throw new InvalidDataException("Invalid bounded FAA shape record/index.");
            var end = shp.BaseStream.Position + length; var row = dbf.Row(); var shapeType = shp.ReadInt32();
            if (shapeType == 0) { if (length != 4) throw new InvalidDataException("Invalid null shape."); yield return new(record, "unknown", "Null source geometry", row ?? [], null, null, 0, "Source null/deleted geometry"); continue; }
            if (shapeType != type) throw new InvalidDataException("Mixed or unsupported FAA shape type.");
            for (var i = 0; i < 4; i++) if (!double.IsFinite(shp.ReadDouble())) throw new InvalidDataException("Invalid shape bounds.");
            var parts = shp.ReadInt32(); var points = shp.ReadInt32();
            if (parts is < 1 or > 10000 || points < 4 || points > MaximumRecordVertices || parts > points / 4 || 44L + 4L * parts + 16L * points > length)
                throw new InvalidDataException("Airspace part/vertex bound exceeded.");
            var offsets = new int[parts + 1];
            for (var i = 0; i < parts; i++) { offsets[i] = shp.ReadInt32(); if (i == 0 && offsets[i] != 0 || i > 0 && offsets[i] - offsets[i - 1] < 4 || offsets[i] > points - 4) throw new InvalidDataException("Invalid polygon ring offset."); }
            offsets[parts] = points;
            var coordinates = new Coordinate[points]; var unsupported = false;
            for (var i = 0; i < points; i++)
            {
                if ((i & 4095) == 0) Check(token, canContinue);
                var x = shp.ReadDouble(); var y = shp.ReadDouble();
                if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidDataException("Nonfinite original airspace coordinate.");
                if (x is < -180 or > 180 || y is < -85 or > 85) unsupported = true;
                coordinates[i] = new(x, y);
            }
            var remaining = end - shp.BaseStream.Position;
            if (type == 5 ? remaining != 0 : remaining != 16L + 8L * points && remaining != 32L + 16L * points) throw new InvalidDataException("Invalid PolygonZ companion lengths.");
            shp.BaseStream.Position = end; // Z/M remain in the verified original; altitude limits are DBF attributes, never shape Z.
            var attributes = row ?? [];
            var kind = "class-" + attributes.GetValueOrDefault("CLASS", "").ToLowerInvariant();
            var name = attributes.GetValueOrDefault("NAME", "Unnamed FAA airspace");
            if (row is null || !Kinds.Contains(kind) || unsupported)
            { yield return new(record, kind, name, attributes, null, null, 0, row is null ? "Deleted source record" : unsupported ? "Unsupported dateline/polar source coordinates" : "Unsupported published class"); continue; }
            var rings = new List<LinearRing>();
            for (var p = 0; p < parts; p++)
            {
                var values = coordinates[offsets[p]..offsets[p + 1]];
                if (!values[0].Equals2D(values[^1])) throw new InvalidDataException("Original ring is not closed.");
                if (values.Zip(values.Skip(1)).Any(pair => Math.Abs(pair.First.X - pair.Second.X) > 180)) unsupported = true;
                rings.Add(factory.CreateLinearRing(values));
            }
            if (unsupported) { yield return new(record, kind, name, attributes, null, null, 0, "Dateline-crossing rings require a supported split; not drawn as a global polygon"); continue; }
            var geometry = Assemble(rings, factory);
            Check(token, canContinue);
            if (geometry is null || !geometry.IsValid)
            { yield return new(record, kind, name, attributes, null, null, 0, "Invalid/ambiguous source topology; original preserved, no silent repair"); continue; }
            var display = geometry; var tolerance = 0d;
            foreach (var candidate in new[] { .0001, .0005, .001, .005, .01 })
            {
                Check(token, canContinue); if (display.NumPoints <= MaximumDisplayVertices) break;
                display = TopologyPreservingSimplifier.Simplify(geometry, candidate); tolerance = candidate;
            }
            if (display.NumPoints > MaximumDisplayVertices) { yield return new(record, kind, name, attributes, null, null, 0, "Source exceeds bounded display vertices; originals preserved"); continue; }
            yield return new(record, kind, name, attributes, geometry, display, tolerance, null);
        }
        if (record != dbf.Count) throw new InvalidDataException("Truncated airspace shape inventory.");
    }
    private static Geometry? Assemble(List<LinearRing> rings, GeometryFactory factory)
    {
        // ESRI outer rings clockwise, holes counter-clockwise; independent ring ordering.
        var shells = rings.Where(r => !Orientation.IsCCW(r.Coordinates)).Select(r => (Ring: r, Holes: new List<LinearRing>())).ToArray();
        if (shells.Length == 0) return null;
        foreach (var hole in rings.Where(r => Orientation.IsCCW(r.Coordinates)))
        {
            var container = shells.Where(s => s.Ring.EnvelopeInternal.Contains(hole.EnvelopeInternal) && PointLocation.IsInRing(hole.Coordinate, s.Ring.Coordinates))
                .OrderBy(s => s.Ring.EnvelopeInternal.Area).FirstOrDefault();
            if (container.Ring is null) return null; container.Holes.Add(hole);
        }
        var polygons = shells.Select(s => factory.CreatePolygon(s.Ring, s.Holes.ToArray())).ToArray();
        return polygons.Length == 1 ? polygons[0] : factory.CreateMultiPolygon(polygons);
    }
    private static int Big(BinaryReader reader)
    { var bytes = reader.ReadBytes(4); if (bytes.Length != 4) throw new InvalidDataException("Truncated FAA binary integer."); return BinaryPrimitives.ReadInt32BigEndian(bytes); }
    private static int Header(BinaryReader reader)
    {
        if (reader.BaseStream.Length is < 100 or > 1073741824 || Big(reader) != 9994) throw new InvalidDataException("Unsupported shape signature/size.");
        reader.BaseStream.Position = 24;
        if (2L * Big(reader) != reader.BaseStream.Length || reader.ReadInt32() != 1000) throw new InvalidDataException("Incomplete or unsupported shape header.");
        var type = reader.ReadInt32(); if (type is not (5 or 15)) throw new NotSupportedException("Only original Polygon/PolygonZ airspace is supported.");
        reader.BaseStream.Position = 100; return type;
    }
    private static void Check(CancellationToken token, Func<bool>? canContinue)
    { token.ThrowIfCancellationRequested(); if (canContinue?.Invoke() == false) throw new InvalidOperationException("Airspace indexing yielded to flight recording. Retry; verified originals are unchanged."); }
    private sealed class Dbf : IDisposable
    {
        private readonly BinaryReader reader; private readonly List<(string Name, int Length)> fields = [];
        private readonly int recordLength; private readonly Encoding encoding; public int Count { get; }
        public Dbf(string path)
        {
            reader = new(File.OpenRead(path)); var bytes = reader.ReadBytes(32);
            if (bytes.Length != 32 || bytes[0] is not (3 or 131)) throw new NotSupportedException("Unsupported dBASE version.");
            Count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)); var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)); recordLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
            if (Count is < 1 or > MaximumRecords || headerLength is < 33 or > 8192 || recordLength is < 2 or > 32768 || headerLength + (long)Count * recordLength > reader.BaseStream.Length || reader.BaseStream.Length > 1073741824)
                throw new InvalidDataException("Invalid DBF inventory/length.");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = bytes[29] switch { 0 => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), 3 or 87 => Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), _ => throw new NotSupportedException("Unverified FAA DBF encoding.") };
            var length = 1;
            while (reader.BaseStream.Position < headerLength - 1)
            {
                var field = reader.ReadBytes(32); var name = Encoding.ASCII.GetString(field, 0, 11).TrimEnd('\0');
                if (field.Length != 32 || name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || fields.Any(f => f.Name == name) || field[16] == 0 || field[11] is not (67 or 78 or 70 or 76 or 68)) throw new InvalidDataException("Unsupported DBF field contract.");
                fields.Add((name, field[16])); length += field[16];
            }
            if (reader.ReadByte() != 13 || reader.BaseStream.Position != headerLength || fields.Count > 64 || length != recordLength) throw new InvalidDataException("Invalid DBF descriptors.");
        }
        public Dictionary<string, string>? Row()
        {
            var bytes = reader.ReadBytes(recordLength); if (bytes.Length != recordLength || bytes[0] is not (32 or 42)) throw new InvalidDataException("Invalid/deleted DBF marker or truncated record.");
            var row = new Dictionary<string, string>(StringComparer.Ordinal); var offset = 1;
            foreach (var field in fields)
            {
                var value = encoding.GetString(bytes, offset, field.Length).TrimEnd(' ', '\0').TrimStart(' ');
                if (value.Any(char.IsControl)) throw new InvalidDataException("Control character in FAA attribute.");
                row.Add(field.Name, value); offset += field.Length;
            }
            return bytes[0] == 42 ? null : row;
        }
        public void Dispose() => reader.Dispose();
    }
}
