using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AeroScience.Shared;

namespace AeroScience.Missions;

// FAA's published XNOTAM 0.1 + AIXM 5.0 download packets. No legal-text parsing,
// external entity resolution, clock guessing, arc generation or cancellation inference.
public static class AviationTfrOperations
{
    public const int MaximumBytes = 512 * 1024;
    public const string LifecycleUnknown = "Amendment/cancellation status Unknown; published packets are not a complete live NOTAM lifecycle feed";
    private static readonly XNamespace A = "http://www.aixm.aero/schema/5.0", G = "http://www.opengis.net/gml/3.2", T = "http://www.faa.gov/AIM/SAA/TFR", X = "http://www.w3.org/1999/xlink";
    public static readonly string[] Limitations = [
        "Only the exact selected official notice is retrieved, at most two 512 KiB packets. XML/AIXM download interfaces have no verified external API stability or completeness guarantee.",
        "UTC windows require explicit UTC AIXM timesheets agreeing with dated XNOTAM schedules. Recurring/ambiguous schedules remain Unknown. Issuance text without a timezone is not a UTC timestamp.",
        "ALT/HEI/STD source references remain distinct (MSL/AGL/standard pressure). Terrain or pressure conversion is never guessed. WFS joins require matching published merged geometry, not area order or a suffix in a NOTAM key.",
        "Missing, revised, stale, offline or failed detail evidence cannot establish that a route is clear. Published time windows do not verify cancellation, legal applicability or actual activation. Simulation reference only."
    ];
    public static string Id(string id)
    {
        if (id is null || !Regex.IsMatch(id, @"\A\d/\d{4,5}\z", RegexOptions.CultureInvariant)) throw new ArgumentException("Select an exact retained FAA NOTAM identity.");
        return id;
    }
    public static string Kind(string id) => "tfr-detail-" + Id(id);
    public static string Url(string id, bool aixm = false) => "https://tfr.faa.gov/download/detail_" + Id(id).Replace('/', '_') + (aixm ? ".aixm50" : ".xml");
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Revision(AviationTfrGeometrySnapshot snapshot, string id)
    {
        var notice = snapshot.NoticeInventory.SingleOrDefault(n => n.NotamId == Id(id)) ?? throw new InvalidOperationException("Refresh the official notice inventory before selecting this notice.");
        return AviationDownloads.Hash(new { notice, revisions = snapshot.Boundaries.Where(b => b.NotamId == id).Select(b => new { b.Id, b.ModificationText }).OrderBy(b => b.Id, StringComparer.Ordinal).ToArray() });
    }
    private static XDocument Document(byte[] data)
    {
        if (data is null || data.Length is < 1 or > MaximumBytes) throw new InvalidDataException("Bound each structured TFR original to 512 KiB.");
        using var input = new MemoryStream(data);
        using var reader = XmlReader.Create(input, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes, MaxCharactersFromEntities = 0 });
        try
        {
            var doc = XDocument.Load(reader);
            if (doc.Descendants().Take(20001).Count() > 20000 || doc.Descendants().Any(e => e.Ancestors().Take(33).Count() > 32)) throw new InvalidDataException("TFR XML complexity exceeds bounds.");
            return doc;
        }
        catch (XmlException e) { throw new InvalidDataException("Malformed or unsafe structured TFR packet.", e); }
    }
    private static string Text(XElement? element)
    {
        var text = element?.Value ?? "";
        if (text.Length > 2000 || text.Any(char.IsControl)) throw new InvalidDataException("Invalid bounded TFR field.");
        return text;
    }
    public static AviationTfrInterpretation Parse(AviationTfrOperationalSnapshot source)
    {
        if (source.Version != 1) throw new NotSupportedException("Unsupported TFR interpretation version.");
        Id(source.NotamId);
        if(source.XmlOriginal is null||source.AixmOriginal is null||source.XmlOriginal.Length>MaximumBytes||source.AixmOriginal.Length>MaximumBytes)throw new InvalidDataException("Missing or oversized TFR originals.");
        if (source.RetrievedUtc.Offset != TimeSpan.Zero || source.NoticeRevision is null || !Regex.IsMatch(source.NoticeRevision, "^[a-f0-9]{64}$") || Hash(source.XmlOriginal) != source.XmlSha256 || Hash(source.AixmOriginal) != source.AixmSha256) throw new InvalidDataException("TFR original integrity or revision mismatch.");
        var xml = Document(source.XmlOriginal); var aixm = Document(source.AixmOriginal);
        if (xml.Root?.Name != "XNOTAM-Update" || (string?)xml.Root.Attribute("version") != "0.1" || aixm.Root?.Name != T + "TFRMessage") throw new NotSupportedException("Unsupported official TFR packet format.");
        var notices = xml.Root.Elements("Group").Elements("Add").Elements("Not").ToArray();
        if (notices.Length != 1 || xml.Root.Elements("Group").Elements().Any(e => e.Name != "Add")) throw new NotSupportedException("Unsupported TFR lifecycle operation; do not infer cancellation or replacement.");
        var n = notices[0]; var uid = n.Element("NotUid");
        var year = Text(uid?.Element("dateIndexYear")); var seq = Text(uid?.Element("noSeqNo"));
        var tfr = aixm.Descendants(T + "TFRTimeSlice").ToArray();
        if (year.Length != 4 || !int.TryParse(year, out var numericYear) || numericYear is < 2000 or > 2199 || $"{numericYear % 10}/{seq}" != source.NotamId || Text(uid?.Element("txtNameAcctFac")) != "FDC" || tfr.Length != 1 || Text(tfr[0].Element(T + "number")) != source.NotamId) throw new InvalidDataException("TFR companion NOTAM identities disagree.");
        if(Text(tfr[0].Element(A+"interpretation"))!="BASELINE")throw new NotSupportedException("Unsupported AIXM lifecycle interpretation.");
        var work = Text(uid?.Element("noUSNSWorkNo"));
        var companionWork=Text(tfr[0].Element(T+"usnsWorkNumber"));
        // Some official packets omit USNS work numbers and append Z only in AIXM.
        // Compare the exact calendar components; this does not assign UTC to the XML issuance.
        var issued=Text(uid?.Element("dateIssued"));var companionIssued=Text(tfr[0].Element(T+"issued"));
        if(companionIssued.EndsWith('Z'))companionIssued=companionIssued[..^1];
        if (companionWork != work || !DateTime.TryParseExact(issued,"yyyy-MM-dd'T'HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out _) || companionIssued != issued) throw new InvalidDataException("TFR companion revision/issuance identities disagree.");
        var groups = n.Elements("TfrNot").Elements("TFRAreaGroup").ToArray();
        if (groups.Length is < 1 or > 32) throw new InvalidDataException("Unsupported TFR area count.");
        var results = new List<AviationTfrOperationalArea>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var area = group.Element("aseTFRArea") ?? throw new InvalidDataException("Missing TFR area.");
            var id = Text(area.Element("AseUid")?.Element("codeId")); var name = Text(area.Element("txtName"));
            if (!Regex.IsMatch(id, "^[0-9]{1,12}$") || name.Length == 0 || !ids.Add(id)) throw new InvalidDataException("Conflicting TFR area identity.");
            var airspaces = aixm.Descendants(A + "Airspace").Where(e => Text(e.Element(A + "timeSlice")?.Element(A + "AirspaceTimeSlice")?.Element(A + "name")) == name.ToUpperInvariant()).ToArray();
            var layers = Array.Empty<XElement>();
            if (airspaces.Length == 1)
            {
                var airspaceId = (string?)airspaces[0].Attribute(G + "id");
                if (!string.IsNullOrEmpty(airspaceId)) layers = aixm.Descendants(A + "AirspaceUsageTimeSlice").Where(e => (string?)e.Element(A + "restrictedAirspace")?.Attribute(X + "href") == "#" + airspaceId).SelectMany(e => e.Descendants(A + "LayerAndTime")).ToArray();
            }
            var windows = Windows(area, layers);
            var lower=Level(area,"Lower");var upper=Level(area,"Upper");
            if(lower.Reference==upper.Reference&&lower.Unit==upper.Unit&&lower.Value>upper.Value)throw new InvalidDataException("Inverted published TFR vertical limits.");
            if(layers.Length==1)
                foreach(var (level,key) in new[]{(lower,"lowerLimit"),(upper,"upperLimit")})
                {
                    var node=layers[0].Element(A+key);
                    if(node is not null&&level.Value is {} number&&(!double.TryParse(Text(node),NumberStyles.Float,CultureInfo.InvariantCulture,out var other)||other!=number||(string?)node.Attribute("uom")!=level.Unit))throw new InvalidDataException("TFR companion vertical limits disagree.");
                }
            results.Add(new(id, name, Polygon(group.Element("abdMergedArea"), id), windows,
                windows.Length == 0 ? "Unknown: recurring, absent or inconsistent UTC schedule" : "Explicit UTC window cross-checked in both official packets; cancellation unverified",
                lower,upper));
        }
        var facility=Text(n.Element("codeFacility"));
        if(facility.Length==0||Text(tfr[0].Element(T+"artccDesignator"))!=facility)throw new InvalidDataException("TFR companion facilities disagree.");
        return new(1, source.NotamId, work.Length==0?"Unknown (not supplied)":work, Text(uid?.Element("dateIssued")), LifecycleUnknown, results.ToArray(),facility);
    }
    private static AviationTfrLevel Level(XElement area, string suffix)
    {
        var code = Text(area.Element("codeDistVer" + suffix)); var unit = Text(area.Element("uomDistVer" + suffix)); var valueText = Text(area.Element("valDistVer" + suffix));
        double? value = double.TryParse(valueText, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v is >= -2000 and <= 100000 ? v : null;
        return new(value, unit.Length == 0 ? "Unknown" : unit, code switch { "ALT" => "MSL", "HEI" => "AGL", "STD" => "Standard pressure", _ => "Unknown (" + code + ")" }, Text(area.Element("codeExclVer" + suffix)));
    }
    private static AviationTfrWindow[] Windows(XElement area, XElement[] layers)
    {
        // Do not expand daily schedules or infer UTC from an unzoned date / local display code.
        if (Text(area.Element("isScheduledTfrArea")) != "FALSE" || layers.Length != 1) return [];
        var schedules = area.Elements("ScheduleGroup").ToArray(); var sheets = layers[0].Descendants(A + "Timesheet").ToArray();
        if (schedules.Length != 1 || sheets.Length != 1 || Text(schedules[0].Element("isTimeSeparate")) != "FALSE") return [];
        var sheet = sheets[0];
        if (Text(sheet.Element(A + "timeReference")) != "UTC" || sheet.Elements().Any(e => e.Name != A + "timeReference" && e.Name != A + "startDate" && e.Name != A + "endDate" && e.Name != A + "startTime" && e.Name != A + "endTime")) return [];
        DateTimeOffset? Read(string dateName, string prefix)
        {
            if (!DateTime.TryParseExact(Text(schedules[0].Element(dateName)), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.ToString("dd-MM", CultureInfo.InvariantCulture) != Text(sheet.Element(A + (prefix + "Date"))) || date.ToString("HH:mm", CultureInfo.InvariantCulture) != Text(sheet.Element(A + (prefix + "Time")))) return null;
            return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc));
        }
        var start = Read("dateEffective", "start"); var end = Read("dateExpire", "end");
        return start is {} s && end is {} e && s < e ? [new(s, e)] : [];
    }
    private static Coordinates[][][]? Polygon(XElement? merged, string id)
    {
        if (merged is null || Text(merged.Element("AbdUid")?.Element("AseUid")?.Element("codeId")) != id) return null;
        var vertices = merged.Elements("Avx").ToArray();
        if (vertices.Length is < 4 or > 12000 || vertices.Any(v => Text(v.Element("codeDatum")) != "WGE" || Text(v.Element("codeType")) != "GRC")) return null;
        double? Coordinate(string text, bool latitude)
        {
            if (!Regex.IsMatch(text, latitude ? @"^\d{1,2}(\.\d{1,10})?[NS]$" : @"^\d{1,3}(\.\d{1,10})?[EW]$") || !double.TryParse(text[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || v > (latitude ? 85 : 180)) return null;
            return text[^1] is 'S' or 'W' ? -v : v;
        }
        var points = new List<Coordinates>();
        foreach (var v in vertices)
        {
            var lat = Coordinate(Text(v.Element("geoLat")), true); var lon = Coordinate(Text(v.Element("geoLong")), false);
            if (lat is null || lon is null) return null; points.Add(new(lat.Value, lon.Value));
        }
        if (points[0] != points[^1] || points.Zip(points.Skip(1)).Any(p => Math.Abs(p.First.Longitude - p.Second.Longitude) > 180)) return null;
        Coordinates[][][] polygons = [[points.ToArray()]];
        return AviationTfr.Geometry(polygons).IsValid ? polygons : null;
    }
    public static AviationTfrOperationalArea? Match(AviationTfrBoundary boundary, AviationTfrInterpretation interpretation)
    {
        if (boundary.NotamId != interpretation.NotamId) return null;
        var geometry = AviationTfr.Geometry(boundary.Polygons).Normalized();
        // XNOTAM decimal degrees are rounded to 8 places in these supported packets.
        // 1e-8 degree is a representation join tolerance, never a buffer/coverage assertion.
        var matches = interpretation.Areas.Where(a => a.Polygons is not null && geometry.EqualsExact(AviationTfr.Geometry(a.Polygons).Normalized(), 1e-8)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
