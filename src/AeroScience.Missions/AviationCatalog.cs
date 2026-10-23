using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Shared;

namespace AeroScience.Missions;

// Parses bounded official metadata. Discovery is not download approval or geographic verification.
public static partial class AviationCatalog
{
    public const int MaximumMetadataBytes = 4 * 1024 * 1024;
    public const string VfrUrl = "https://www.faa.gov/air_traffic/flight_info/aeronav/digital_products/vfr/";
    public const string IfrUrl = "https://www.faa.gov/air_traffic/flight_info/aeronav/digital_products/ifr/";
    public const string NasrUrl = "https://www.faa.gov/air_traffic/flight_info/aeronav/Aero_Data/NASR_Subscription/";
    public const string TfrUrl = "https://tfr.faa.gov/tfrapi/exportTfrList";
    public const string TfrReference = "https://tfr.faa.gov/tfr3/?page=map";
    public const string Attribution = "Federal Aviation Administration (FAA). Flight-simulation reference only; not a real-world preflight safety product.";
    private static readonly TimeSpan RegexDeadline = TimeSpan.FromSeconds(1);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Match(string? value, string pattern) => value is not null && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, RegexDeadline);
    private static void Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid or oversized aviation metadata field.");
    }
    public static void Validate(AviationCatalogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Version != 1) throw new NotSupportedException("Unsupported aviation catalog version. Preserve original records.");
        if (!Enum.IsDefined(item.Product) || item.Product is AviationProduct.Unknown or AviationProduct.TfrNotices)
            throw new InvalidDataException("Unsupported aviation dataset product.");
        Text(item.Id, 64); Text(item.Edition, 80); Text(item.Attribution, 2000); Text(item.GeographicVerification, 300);
        if (!Match(item.Id, "^[a-f0-9]{64}$") || item.OriginalBytes is < 1 ||
            item.OriginalSha256 is {} sha && !Match(sha, "^[a-f0-9]{64}$"))
            throw new InvalidDataException("Invalid aviation identity, byte count or original hash.");
        var (product, edition, file) = OriginalIdentity(item.SourceUrl);
        if (product != item.Product || edition != item.Edition || file != item.OriginalFileName)
            throw new InvalidDataException("Aviation source identity does not match its official endpoint.");
        if (item.EffectiveUtc is { Offset: var e } && e != TimeSpan.Zero ||
            item.ExpiresUtc is { Offset: var x } && x != TimeSpan.Zero || item.ExpiresUtc < item.EffectiveUtc)
            throw new InvalidDataException("Use authoritative UTC intervals, or Unknown.");
        if (item.Bounds is {} b && (new[] { b.West, b.South, b.East, b.North }.Any(v => !double.IsFinite(v)) ||
            b.West < -180 || b.East > 180 || b.South < -90 || b.North > 90 || b.East <= b.West || b.North <= b.South || b.East - b.West > 180))
            throw new InvalidDataException("Unsupported geographic footprint; split antimeridian geometry.");
        if (item.Crs is {} crs) Text(crs, 32768);
        // Geographic fields must be populated only by a verified original-reader phase.
        if ((item.Bounds is null) != (item.Crs is null)) throw new InvalidDataException("CRS and verified geographic bounds must be supplied together.");
    }
    public static (AviationProduct Product, string Edition, string File) OriginalIdentity(string url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 2048 || url.Contains('%') || url.Contains('\\') || !Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            u.Scheme != "https" || u.Port != 443 || u.UserInfo.Length != 0 || u.Query.Length != 0 || u.Fragment.Length != 0 || u.Host is not ("aeronav.faa.gov" or "nfdc.faa.gov") || u.AbsoluteUri != url)
            throw new InvalidDataException("Only exact trusted FAA original links are supported.");
        if (u.Host == "nfdc.faa.gov") return NasrIdentity(u);
        var m = Regex.Match(u.AbsolutePath, @"^/(visual|enroute)/(\d{2}-\d{2}-\d{4})/(?:(sectional-files|tac-files|heli_files)/)?([A-Za-z0-9_-]+\.zip)$", RegexOptions.CultureInvariant, RegexDeadline);
        if (!m.Success || !DateOnly.TryParseExact(m.Groups[2].Value, "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("Unsupported FAA chart path or edition.");
        var file = m.Groups[4].Value;
        var product = m.Groups[1].Value == "visual" ? m.Groups[3].Value switch
        { "sectional-files" => AviationProduct.Sectional, "tac-files" => AviationProduct.TerminalArea, "heli_files" => AviationProduct.Helicopter, _ => AviationProduct.Unknown }
        : m.Groups[3].Length == 0 && Match(file, @"^enr_[lh]\d{2}\.zip$")
            ? file[4] == 'l' ? AviationProduct.IfrLow : AviationProduct.IfrHigh : AviationProduct.Unknown;
        if (product == AviationProduct.Unknown) throw new InvalidDataException("Unsupported FAA chart family; PDF/delta files are not original georeferenced charts.");
        return (product, m.Groups[2].Value, file);
    }
    public static AviationCatalogSnapshot Charts(byte[] html, DateTimeOffset retrievedUtc)
    {
        Metadata(html, retrievedUtc); var items = new Dictionary<string, AviationCatalogItem>(StringComparer.Ordinal);
        foreach (Match link in Regex.Matches(Encoding.UTF8.GetString(html), """href\s*=\s*(?:"([^"<>]{1,2048})"|'([^'<>]{1,2048})'|([^\s"'<>`=]{1,2048}))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexDeadline))
        {
            var url = WebUtility.HtmlDecode(link.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value);
            if (!url.StartsWith("https://aeronav.faa.gov/", StringComparison.Ordinal) && !url.StartsWith("https://nfdc.faa.gov/", StringComparison.Ordinal)) continue;
            try
            {
                var (product, edition, file) = OriginalIdentity(url); var id = Hash(Encoding.UTF8.GetBytes(url));
                items[id] = new(1, id, product, edition, file, url, Attribution);
            }
            catch (InvalidDataException) { continue; } // Unsupported PDF/delta/other products are not relabeled.
            if (items.Count > 2048) throw new InvalidDataException("FAA catalog exceeds the bounded item limit.");
        }
        if (items.Count == 0) throw new InvalidDataException("No supported original chart links. Retain the previous snapshot; do not interpret a changed page as no FAA coverage.");
        return new(1, retrievedUtc, Hash(html), items.Values.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray(),
            ["Catalog edition is the publisher's URL edition label; exact UTC validity, sizes, CRS, coverage and neatlines remain Unknown until verified from authoritative originals/metadata.",
             "No charts were downloaded. Discovery does not establish geographic accuracy or approve a transfer."]);
    }
    private static void Metadata(byte[] bytes, DateTimeOffset utc)
    {
        if (bytes.Length is < 1 or > MaximumMetadataBytes || utc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Bound metadata to 4 MiB and record a UTC retrieval timestamp.");
    }
    public static AviationTfrSnapshot Notices(byte[] json, DateTimeOffset retrievedUtc)
    {
        Metadata(json, retrievedUtc); using var document = JsonDocument.Parse(json, new() { MaxDepth = 12 });
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 2048)
            throw new InvalidDataException("Unsupported or oversized FAA notice list.");
        var notices = new Dictionary<string, AviationTfrNotice>(StringComparer.Ordinal);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            string Field(string name, int maximum)
            {
                if (!row.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("FAA notice field is unavailable. Preserve the prior snapshot.");
                var text = value.GetString()!; Text(text, maximum); return text;
            }
            var id = Field("notam_id", 20);
            if (!Match(id, @"^\d/\d{4,5}$")) throw new InvalidDataException("Unsupported FAA NOTAM identity.");
            var notice = new AviationTfrNotice(1, id, Field("type", 100), Field("facility", 20), Field("state", 20),
                Field("description", 2000), Field("creation_date", 100), TfrReference);
            if (notices.TryGetValue(id, out var previous) && previous != notice)
                throw new InvalidDataException("Conflicting FAA notice identities; no silent replacement.");
            notices[id] = notice;
        }
        return new(1, retrievedUtc, Hash(json), notices.Values.OrderBy(n => n.NotamId, StringComparer.Ordinal).ToArray(),
            ["Official summary list only. No verified polygon, altitude or exact UTC activation is supplied.",
             "Description dates/Local/UTC wording are retained verbatim, not converted to inferred activation times.",
             "An empty list or unavailable geometry cannot clear a route of restrictions. Review official notices; flight-simulation reference only."]);
    }
    public static void Validate(AviationCatalogSnapshot snapshot)
    {
        if (snapshot.Version != 1) throw new NotSupportedException("Unsupported aviation snapshot version.");
        if (snapshot.Items is null || snapshot.Items.Length > 2048 || snapshot.Items.Any(i => i is null) ||
            snapshot.Items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Items.Length ||
            snapshot.RetrievedUtc.Offset != TimeSpan.Zero || !Match(snapshot.MetadataSha256, "^[a-f0-9]{64}$") ||
            snapshot.Limitations is null || snapshot.Limitations.Length > 20)
            throw new InvalidDataException("Invalid aviation snapshot.");
        foreach (var item in snapshot.Items) Validate(item);
        foreach (var limitation in snapshot.Limitations) Text(limitation, 2000);
    }
    public static void Validate(AviationTfrSnapshot snapshot)
    {
        if (snapshot.Version != 1) throw new NotSupportedException("Unsupported aviation notice snapshot version.");
        if (snapshot.Notices is null || snapshot.Notices.Length > 2048 || snapshot.Notices.Any(n => n is null) ||
            snapshot.Notices.Select(n => n.NotamId).Distinct(StringComparer.Ordinal).Count() != snapshot.Notices.Length ||
            snapshot.RetrievedUtc.Offset != TimeSpan.Zero || !Match(snapshot.MetadataSha256, "^[a-f0-9]{64}$") ||
            snapshot.Limitations is null || snapshot.Limitations.Length > 20)
            throw new InvalidDataException("Invalid aviation notice snapshot.");
        foreach (var n in snapshot.Notices)
        {
            if (n.Version != 1) throw new NotSupportedException("Unsupported FAA notice version.");
            if (!Match(n.NotamId, @"^\d/\d{4,5}$") || n.OfficialUrl != TfrReference || n.GeometryStatus != "Not provided by the notice list")
                throw new InvalidDataException("Unsupported notice identity or geometry claim.");
            Text(n.Type, 100); Text(n.Facility, 20); Text(n.State, 20); Text(n.Description, 2000); Text(n.CreationDateText, 100);
        }
        foreach (var limitation in snapshot.Limitations) Text(limitation, 2000);
    }

}
