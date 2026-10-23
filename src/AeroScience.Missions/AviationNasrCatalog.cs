using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AeroScience.Shared;

namespace AeroScience.Missions;

public static partial class AviationCatalog
{
    private static (AviationProduct Product, string Edition, string File) NasrIdentity(Uri uri)
    {
        var spatial = Regex.Match(uri.AbsolutePath, @"^/webContent/28DaySub/(\d{4}-\d{2}-\d{2})/(class_airspace_shape_files|aixm5\.0)\.zip$", RegexOptions.CultureInvariant, RegexDeadline);
        if (spatial.Success && DateOnly.TryParseExact(spatial.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return (AviationProduct.Nasr, spatial.Groups[1].Value, Path.GetFileName(uri.AbsolutePath));
        var match = Regex.Match(uri.AbsolutePath, @"^/webContent/28DaySub/extra/(\d{2}_[A-Za-z]{3}_\d{4})_(APT|NAV|FIX)_CSV\.zip$", RegexOptions.CultureInvariant, RegexDeadline);
        if (!match.Success || !DateOnly.TryParseExact(match.Groups[1].Value, "dd_MMM_yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException("Only supported exact NASR APT/NAV/FIX CSV originals are accepted.");
        return (AviationProduct.Nasr, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Path.GetFileName(uri.AbsolutePath));
    }
    public static string[] NasrCycles(byte[] html, DateOnly today)
    {
        if (html.Length is < 1 or > MaximumMetadataBytes) throw new InvalidDataException("Bound NASR metadata to 4 MiB.");
        var dates = new HashSet<DateOnly>();
        foreach (Match match in Regex.Matches(Encoding.UTF8.GetString(html), @"NASR_Subscription/(\d{4}-\d{2}-\d{2})(?:[/""'<\s])", RegexOptions.CultureInvariant, RegexDeadline))
            if (DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) dates.Add(date);
        // Only dates actually advertised by FAA. No guessed 28-day cycle or original URL.
        var current = dates.Where(d => d <= today).OrderDescending().Take(1);
        var next = dates.Where(d => d > today).Order().Take(1);
        var selected = current.Concat(next).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("No supported advertised NASR cycles. Preserve the previous catalog.");
        return selected.Select(date => NasrUrl + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/").ToArray();
    }
    public static AviationCatalogSnapshot NasrCharts(IEnumerable<byte[]> pages, DateTimeOffset utc)
    {
        var originals = new Dictionary<string, AviationCatalogItem>(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        foreach (var page in pages)
        {
            if (++count > 2) throw new InvalidDataException("At most two advertised NASR cycles per refresh.");
            hash.AppendData(BitConverter.GetBytes(page.Length)); hash.AppendData(page);
            foreach (var item in Charts(page, utc).Items.Where(i => i.Product == AviationProduct.Nasr)) originals[item.Id] = item;
        }
        if (originals.Count is < 1 or > 10) throw new InvalidDataException("No supported NASR originals, or unexpected product count.");
        return new(1, utc, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), originals.Values.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray(),
            ["Official APT/NAV/FIX CSV, Class airspace shapefile and Special Activity Airspace AIXM 5.0 originals. Download/import support is product-specific; discovery alone is not interpreted geometry.",
             "Edition is the published effective date label. Exact UTC validity/expiry remains Unknown; no activation schedule is inferred.",
             "US coordinates reference NAD83 according to the retained FAA CSV_README. Display is approximate geographic context, not certified WGS84 navigation."]);
    }
}
