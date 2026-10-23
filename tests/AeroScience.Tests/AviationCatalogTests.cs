using System.Text;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

// Deterministic synthetic metadata/error cases. Genuine FAA samples are recorded separately.
public sealed class AviationCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-aviation-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Utc = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    private const string Chart = "https://aeronav.faa.gov/visual/09-03-2026/tac-files/Boston_TAC.zip";
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static AviationCatalogSnapshot Catalog() => AviationCatalog.Charts(Bytes($"<a href='{Chart}'>Original</a><a href='{Chart}'>Duplicate</a>"), Utc);
    private static AviationTfrSnapshot Notices() => AviationCatalog.Notices(Bytes("""
        [{"notam_id":"6/1800","type":"SECURITY","facility":"ZTL","state":"GA",
          "description":"Synthetic example, Saturday Local; not a real restriction",
          "creation_date":"10/22/2026"}]
        """), Utc);
    [Fact]
    public void ChartIdentityAndEditionDoNotInventUtcCoverageSizeOrPermission()
    {
        var snapshot = Catalog(); var item = Assert.Single(snapshot.Items); AviationCatalog.Validate(snapshot);
        Assert.Equal(AviationProduct.TerminalArea, item.Product); Assert.Equal("09-03-2026", item.Edition);
        Assert.Null(item.EffectiveUtc); Assert.Null(item.ExpiresUtc); Assert.Null(item.Bounds); Assert.Null(item.Crs);
        Assert.Null(item.OriginalBytes); Assert.Null(item.OriginalSha256); Assert.Equal("Not verified", item.GeographicVerification);
        Assert.Contains("not a real-world", item.Attribution); Assert.Equal(64, snapshot.MetadataSha256.Length);
        var copy = JsonSerializer.Deserialize<AviationCatalogSnapshot>(JsonSerializer.Serialize(snapshot, WireJson.Options), WireJson.Options)!;
        Assert.Equal(snapshot.Items[0], copy.Items[0]); Assert.Equal(snapshot.MetadataSha256, copy.MetadataSha256);
    }
    [Theory]
    [InlineData("<a href=https://aeronav.faa.gov/visual/09-03-2026/tac-files/Boston_TAC.zip>GEO-TIFF</a>")]
    [InlineData("<a HREF=\"https://aeronav.faa.gov/visual/09-03-2026/tac-files/Boston_TAC.zip\">Original</a>")]
    public void OfficialQuotedAndUnquotedHtmlLinksAreBothParsed(string html)
    { Assert.Single(AviationCatalog.Charts(Bytes(html), Utc).Items); }
    [Theory]
    [InlineData("visual/09-03-2026/sectional-files/Boston.zip", AviationProduct.Sectional)]
    [InlineData("visual/09-03-2026/tac-files/Boston_TAC.zip", AviationProduct.TerminalArea)]
    [InlineData("visual/09-03-2026/heli_files/Boston_Heli.zip", AviationProduct.Helicopter)]
    [InlineData("enroute/09-03-2026/enr_l01.zip", AviationProduct.IfrLow)]
    [InlineData("enroute/09-03-2026/enr_h01.zip", AviationProduct.IfrHigh)]
    public void AllApprovedChartFamiliesHaveDistinctOfficialIdentities(string path, AviationProduct product)
    { Assert.Equal(product, AviationCatalog.OriginalIdentity("https://aeronav.faa.gov/" + path).Product); }
    [Theory]
    [InlineData("http://aeronav.faa.gov/visual/09-03-2026/tac-files/Boston_TAC.zip")]
    [InlineData("https://aeronav.faa.gov.evil.test/visual/09-03-2026/tac-files/Boston_TAC.zip")]
    [InlineData("https://aeronav.faa.gov:444/visual/09-03-2026/tac-files/Boston_TAC.zip")]
    [InlineData(Chart + "?token=secret")]
    [InlineData(Chart + "#fragment")]
    [InlineData("https://user@aeronav.faa.gov/visual/09-03-2026/tac-files/Boston_TAC.zip")]
    [InlineData("https://aeronav.faa.gov/visual/09-03-2026/tac-files/../tac-files/Boston_TAC.zip")]
    [InlineData("https://aeronav.faa.gov/visual/09-03-2026/tac-files/%42oston_TAC.zip")]
    [InlineData("https://aeronav.faa.gov/visual/99-03-2026/tac-files/Boston_TAC.zip")]
    [InlineData("https://aeronav.faa.gov/enroute/09-03-2026/delus1.zip")]
    [InlineData("https://aeronav.faa.gov/visual/09-03-2026/PDFs/Boston.pdf")]
    public void UnsafeOrUnsupportedOriginalReferencesFailClosed(string url)
    { Assert.Throws<InvalidDataException>(() => AviationCatalog.OriginalIdentity(url)); }
    [Fact]
    public void ChangedEmptyOversizedAndUnknownMetadataCannotBecomeReady()
    {
        Assert.Throws<InvalidDataException>(() => AviationCatalog.Charts(Bytes("<h1>Maintenance</h1>"), Utc));
        Assert.Throws<InvalidDataException>(() => AviationCatalog.Charts(new byte[AviationCatalog.MaximumMetadataBytes + 1], Utc));
        Assert.Throws<NotSupportedException>(() => AviationCatalog.Validate(Catalog() with { Version = 2 }));
        Assert.Throws<NotSupportedException>(() => AviationCatalog.Validate(Catalog().Items[0] with { Version = 2 }));
        Assert.Throws<InvalidDataException>(() => AviationCatalog.Validate(Catalog().Items[0] with { Product = AviationProduct.Sectional }));
        Assert.Throws<InvalidDataException>(() => AviationCatalog.Validate(Catalog().Items[0] with { OriginalBytes = 0 }));
        Assert.Throws<InvalidDataException>(() => AviationCatalog.Validate(Catalog().Items[0] with { Bounds = new(-181, 1, -170, 2), Crs = "EPSG:4326" }));
    }
    [Fact]
    public void OfficialNoticeSummaryDoesNotManufacturePolygonsAltitudeOrUtcActivation()
    {
        var snapshot = Notices(); AviationCatalog.Validate(snapshot); var notice = Assert.Single(snapshot.Notices);
        Assert.Equal("10/22/2026", notice.CreationDateText); Assert.Contains("Local", notice.Description);
        Assert.Equal(AviationCatalog.TfrReference, notice.OfficialUrl); Assert.StartsWith("Not provided", notice.GeometryStatus);
        Assert.Contains(snapshot.Limitations, l => l.Contains("cannot clear a route"));
        Assert.Throws<InvalidDataException>(() => AviationCatalog.Validate(snapshot with { Notices = [notice with { GeometryStatus = "Verified" }] }));
        Assert.Throws<NotSupportedException>(() => AviationCatalog.Validate(snapshot with { Version = 42 }));
        Assert.Throws<InvalidDataException>(() => AviationCatalog.Notices(Bytes("{\"error\":\"outage\"}"), Utc));
        Assert.Throws<InvalidDataException>(() => AviationCatalog.Notices(Bytes("[{\"notam_id\":\"../evil\"}]"), Utc));
        Assert.Empty(AviationCatalog.Notices(Bytes("[]"), Utc).Notices);
    }
    [Fact]
    public void SeparateMetadataSurvivesRestartWithLastSuccessDistinctFromFailedAttempt()
    {
        Guid id;
        using (var store = new AviationMetadataStore(root, create: true))
        {
            id = store.StoreId; Assert.Null(store.Catalog("vfr")); store.Save("vfr", Catalog()); store.Save(Notices());
            store.Attempt("vfr", Utc.AddMinutes(5), "ProviderUnavailable"); var status = store.Status("vfr");
            Assert.Equal(Utc, status.LastSuccessUtc); Assert.Equal(Utc.AddMinutes(5), status.LastAttemptUtc);
            Assert.Equal("ProviderUnavailable", status.Error); Assert.True(status.DatabaseBytes > 0);
            Assert.Throws<IOException>(() => new AviationMetadataStore(root));
            Assert.Throws<InvalidOperationException>(() => store.Save("vfr", Catalog() with { RetrievedUtc = Utc.AddSeconds(-1) }));
        }
        using var reopened = new AviationMetadataStore(root); Assert.Equal(id, reopened.StoreId);
        Assert.Equal(Catalog().MetadataSha256, reopened.Catalog("vfr")!.MetadataSha256);
        Assert.Single(reopened.Notices()!.Notices); Assert.False(File.Exists(Path.Combine(root, "science.db")));
        Assert.False(File.Exists(Path.Combine(root, "missions.db")));
        Assert.Throws<ArgumentException>(() => reopened.Attempt("vfr", Utc, "secret-token-in-error"));
        Assert.Throws<InvalidDataException>(() => reopened.Save("ifr", Catalog()));
    }
    [Fact]
    public void MissingStoreOrChangedIdentityNeverCreatesAnEmptyReplacement()
    {
        Assert.Throws<IOException>(() => new AviationMetadataStore(root)); Assert.False(Directory.Exists(root));
        Guid id; using (var store = new AviationMetadataStore(root, create: true)) id = store.StoreId;
        var manifest = Path.Combine(root, "aviation-store.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { Version = 1, Id = Guid.NewGuid() }));
        Assert.Throws<IOException>(() => new AviationMetadataStore(root, create: true));
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { Version = 1, Id = id }));
        File.Delete(Path.Combine(root, "aviation.db"));
        Assert.Throws<IOException>(() => new AviationMetadataStore(root, create: true));
        Assert.False(File.Exists(Path.Combine(root, "aviation.db")));
    }
    [Fact]
    public void FutureSchemaAndForeignDirectoriesArePreserved()
    {
        using (var store = new AviationMetadataStore(root, create: true)) store.Save("vfr", Catalog());
        using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "aviation.db")};Pooling=False"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery(); }
        Assert.Throws<NotSupportedException>(() => new AviationMetadataStore(root, create: true));
        var foreign = root + "-foreign"; Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "missions.db"), "preserve");
        try { Assert.Throws<IOException>(() => new AviationMetadataStore(foreign, create: true)); Assert.False(File.Exists(Path.Combine(foreign, "aviation.db"))); }
        finally { Directory.Delete(foreign, true); }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
