using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AeroScience.Shared;
using Microsoft.VisualBasic.FileIO;

namespace AeroScience.Missions;

// Reviewed NASR CSV contract. Original coordinates and metadata remain authoritative.
public static class AviationNasr
{
    public const int MaximumFeatures = 120000;
    public static readonly string[] Kinds = ["airport", "heliport", "runway", "runway-end", "navaid", "fix"];
    public sealed record Parsed(AviationVectorDataset Dataset, AviationVectorFeature[] Features);
    public static Parsed Parse(string files, AviationDownloadJob job, DateTimeOffset utc, CancellationToken token = default, Func<bool>? canContinue = null)
    {
        if (job.Preview.Item.Product != AviationProduct.Nasr || job.State != "Completed" || job.OriginalSha256 is null)
            throw new ArgumentException("Import only a verified completed NASR original.");
        var name = job.Preview.Item.OriginalFileName;
        var family = name.EndsWith("_APT_CSV.zip", StringComparison.Ordinal) ? "APT" : name.EndsWith("_NAV_CSV.zip", StringComparison.Ordinal) ? "NAV" : name.EndsWith("_FIX_CSV.zip", StringComparison.Ordinal) ? "FIX" : throw new NotSupportedException("Unsupported NASR distribution.");
        var date = DateOnly.ParseExact(job.Preview.Item.Edition, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var features = new Dictionary<string, AviationVectorFeature>(StringComparer.Ordinal); var omitted = 0;
        var examined = 0;
        void YieldToFlight() { if ((++examined & 255) == 0 && canContinue?.Invoke() == false) throw new InvalidOperationException("FAA indexing yielded to flight recording. Retry after flight; originals are preserved."); }
        var ends = new Dictionary<string, List<Coordinates>>(StringComparer.Ordinal);
        void Add(string key, string kind, string title, Coordinates[] coordinates, Dictionary<string, string> attributes)
        {
            var feature = new AviationVectorFeature(1, AviationDownloads.Hash(new { family, key }), kind, title, coordinates, attributes);
            Validate(feature);
            if (!features.TryAdd(feature.Id, feature)) throw new InvalidDataException("Conflicting NASR feature identities. Original files were preserved.");
            if (features.Count > MaximumFeatures) throw new InvalidDataException("NASR feature bound exceeded.");
        }
        var primary = family + "_BASE.csv";
        foreach (var row in Rows(files, primary, job, token))
        {
            YieldToFlight(); CheckDate(row, date);
            if (Field(row, "COUNTRY_CODE") != "US" || Point(row) is not {} point) { omitted++; continue; }
            var id = family == "APT" ? Field(row, "SITE_NO") + "/" + Field(row, "SITE_TYPE_CODE") : family == "NAV" ? System.Text.Json.JsonSerializer.Serialize(new[] { Field(row, "NAV_ID"), Field(row, "NAV_TYPE"), Field(row, "STATE_CODE"), Field(row, "COUNTRY_CODE"), Field(row, "CITY") }) : Field(row, "FIX_ID") + "/" + Field(row, "ICAO_REGION_CODE") + "/" + Field(row, "STATE_CODE");
            var kind = family == "APT" ? Field(row, "SITE_TYPE_CODE") == "H" ? "heliport" : "airport" : family == "NAV" ? "navaid" : "fix";
            var title = family == "APT" ? Field(row, "ARPT_NAME") + " (" + Field(row, "ARPT_ID") + ")" : family == "NAV" ? Field(row, "NAV_ID") + " · " + Field(row, "NAV_TYPE") + " · " + Field(row, "NAME") : Field(row, "FIX_ID");
            Add(id, kind, title, [point], Attributes(row));
        }
        if (family == "APT")
        {
            foreach (var row in Rows(files, "APT_RWY_END.csv", job, token))
            {
                YieldToFlight(); CheckDate(row, date);
                if (Field(row, "COUNTRY_CODE") != "US" || Point(row, "RWY_END_") is not {} point) { omitted++; continue; }
                var key = Field(row, "SITE_NO") + "/" + Field(row, "SITE_TYPE_CODE") + "/" + Field(row, "RWY_ID");
                if (!ends.TryGetValue(key, out var list)) ends[key] = list = [];
                list.Add(point);
                if (list.Count > 2) throw new InvalidDataException("More than two NASR runway ends. No inferred runway line was generated.");
                Add(key + "/" + Field(row, "RWY_END_ID"), "runway-end", Field(row, "ARPT_ID") + " runway end " + Field(row, "RWY_END_ID"), [point], Attributes(row));
            }
            foreach (var row in Rows(files, "APT_RWY.csv", job, token))
            {
                YieldToFlight(); CheckDate(row, date); var key = Field(row, "SITE_NO") + "/" + Field(row, "SITE_TYPE_CODE") + "/" + Field(row, "RWY_ID");
                if (!ends.TryGetValue(key, out var list) || list.Count != 2 || list[0] == list[1] || Math.Abs(list[0].Longitude - list[1].Longitude) > 180) { omitted++; continue; }
                Add(key, "runway", Field(row, "ARPT_ID") + " runway " + Field(row, "RWY_ID"), [list[0], list[1]], Attributes(row));
            }
        }
        if (features.Count == 0) throw new InvalidDataException("No supported US NASR reference coordinates. An empty import cannot replace existing data.");
        var dataset = new AviationVectorDataset(1, AviationDownloads.Hash(new { parser = 1, job.Preview.Item.Id, job.OriginalSha256 }), job.Id,
            job.Preview.Item.Edition, job.OriginalSha256, family, "EPSG:4269 (NAD83; realization/epoch unspecified by NASR CSV)", utc,
            features.Count, omitted,
            ["Federal Aviation Administration NASR. Flight-simulation reference only; not certified navigation or a real-world safety product.",
             "Original US NAD83 coordinates are retained. Leaflet display uses these geographic coordinates approximately; no precise WGS84/epoch transformation or positional accuracy is claimed.",
             "Non-US/unknown-datum or missing-coordinate rows and unsupported runway geometry are omitted with a count; no coordinates are inferred.",
             "Airspace polygons, activation schedules and TFR screening are not supplied by APT/NAV/FIX references. Airport elevation does not establish an airspace floor.",
             "Edition is a date label, not an inferred exact UTC validity interval. Retained older cycles are not silently updated."]);
        return new(dataset, features.Values.OrderBy(f => f.Id, StringComparer.Ordinal).ToArray());
    }
    private static IEnumerable<Dictionary<string, string>> Rows(string directory, string file, AviationDownloadJob job, CancellationToken token)
    {
        var manifest = job.Files?.SingleOrDefault(f => f.Name == file) ?? throw new InvalidDataException("Required original NASR companion is missing: " + file);
        var path = Path.Combine(directory, file); StorageManager.ValidateLocalPath(path);
        if (new FileInfo(path).LinkTarget is not null || manifest.Bytes is < 1 or > 64 * 1024 * 1024 || new FileInfo(path).Length != manifest.Bytes)
            throw new InvalidDataException("Unsupported NASR file size or link.");
        using var reader = new StreamReader(path, new UTF8Encoding(false, true));
        using var parser = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var header = ReadFields(parser) ?? throw new InvalidDataException("Missing NASR header.");
        if (header.Length is < 3 or > 128 || header.Distinct(StringComparer.Ordinal).Count() != header.Length || header.Any(h => h.Length is < 1 or > 80))
            throw new InvalidDataException("Invalid NASR columns.");
        var count = 0;
        while (!parser.EndOfData)
        {
            token.ThrowIfCancellationRequested();
            if (++count > 200000) throw new InvalidDataException("NASR record bound exceeded.");
            var fields = ReadFields(parser) ?? throw new InvalidDataException("Incomplete NASR row.");
            if (fields.Length != header.Length || fields.Any(f => f.Length > 4096 || f.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))))
                throw new InvalidDataException("Invalid or oversized NASR row.");
            yield return header.Zip(fields).ToDictionary(v => v.First, v => v.Second.Trim(), StringComparer.Ordinal);
        }
    }
    private static string[]? ReadFields(TextFieldParser parser)
    {
        try { return parser.ReadFields(); }
        catch (Exception error) when (error is MalformedLineException or DecoderFallbackException)
        { throw new InvalidDataException("Invalid NASR CSV encoding/quoting. Retained originals were preserved."); }
    }
    private static string Field(Dictionary<string, string> row, string name) => row.TryGetValue(name, out var value) ? value : throw new InvalidDataException("Required NASR field is missing: " + name);
    private static void CheckDate(Dictionary<string, string> row, DateOnly date)
    { if (!DateOnly.TryParseExact(Field(row, "EFF_DATE"), "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var actual) || actual != date) throw new InvalidDataException("NASR row cycle differs from the approved original."); }
    private static Coordinates? Point(Dictionary<string, string> row, string prefix = "")
    {
        var lat = Field(row, "LAT_DECIMAL"); var lon = Field(row, "LONG_DECIMAL");
        if (lat.Length == 0 || lon.Length == 0) return null;
        double Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : throw new InvalidDataException("Invalid NASR coordinate.");
        double Dms(string axis, bool latitude)
        {
            var degrees = Number(Field(row, prefix + axis + "_DEG")); var minutes = Number(Field(row, prefix + axis + "_MIN")); var seconds = Number(Field(row, prefix + axis + "_SEC")); var hemisphere = Field(row, prefix + axis + "_HEMIS");
            if (degrees < 0 || minutes is < 0 or >= 60 || seconds is < 0 or >= 60 || !(latitude ? hemisphere is "N" or "S" : hemisphere is "E" or "W")) throw new InvalidDataException("Invalid NASR DMS components.");
            return (degrees + minutes / 60 + seconds / 3600) * (hemisphere is "S" or "W" ? -1 : 1);
        }
        var point = new Coordinates(Number(lat), Number(lon));
        if (point.Latitude is < -90 or > 90 || point.Longitude is < -180 or > 180 || Math.Abs(point.Latitude - Dms("LAT", true)) > 1e-7 || Math.Abs(point.Longitude - Dms("LONG", false)) > 1e-7)
            throw new InvalidDataException("NASR decimal and DMS coordinates disagree.");
        return point;
    }
    private static Dictionary<string, string> Attributes(Dictionary<string, string> row)
    {
        string[] names = ["EFF_DATE", "CITY", "STATE_CODE", "COUNTRY_CODE", "ARPT_ID", "SITE_TYPE_CODE", "ARPT_STATUS", "FACILITY_USE_CODE", "ELEV", "NAV_ID", "NAV_TYPE", "NAV_STATUS", "FREQ", "CHAN", "OPER_HOURS", "FIX_ID", "ICAO_REGION_CODE", "FIX_USE_CODE", "RWY_ID", "RWY_END_ID", "TRUE_ALIGNMENT", "RWY_LEN", "RWY_WIDTH", "SURFACE_TYPE_CODE", "RWY_END_ELEV", "LAT_DECIMAL", "LONG_DECIMAL"];
        return names.Where(row.ContainsKey).ToDictionary(n => n, n => row[n], StringComparer.Ordinal);
    }
    public static void Validate(AviationVectorFeature feature)
    {
        if (feature.Version != 1 || !Regex.IsMatch(feature.Id, "^[a-f0-9]{64}$") || !Kinds.Contains(feature.Kind) || string.IsNullOrWhiteSpace(feature.Name) || feature.Name.Length > 300 ||
            feature.Coordinates.Length != (feature.Kind == "runway" ? 2 : 1) || feature.Coordinates.Any(p => !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude) || p.Latitude is < -90 or > 90 || p.Longitude is < -180 or > 180) ||
            feature.Attributes.Count > 32 || feature.Attributes.Any(a => a.Key.Length > 80 || a.Value.Length > 4096)) throw new InvalidDataException("Invalid versioned FAA reference feature.");
    }
}
