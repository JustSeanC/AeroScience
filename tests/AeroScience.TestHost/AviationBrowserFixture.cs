using System.Net;
using System.Text.Json;
namespace AeroScience.Tests;

// Synthetic metadata on the real host/service/SQLite path. Never a genuine FAA acceptance claim.
public sealed class AviationBrowserFixture : HttpMessageHandler
{
    public sealed class TestClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance() => Interlocked.Add(ref ticks, TimeSpan.TicksPerHour);
    }
    public TestClock Clock { get; } = new();
    public int Calls;
    public string Mode = "normal";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        if (Mode == "outage") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        if (request.RequestUri!.Host == "aeronav.faa.gov")
        {
            using var memory = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
            { var entry = zip.CreateEntry("synthetic.tif"); using var stream = entry.Open(); stream.Write(new byte[] { 73, 73, 42, 0, 0, 0, 0, 0 }); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(memory.ToArray()) });
        }
        var text = request.RequestUri!.Host == "tfr.faa.gov" ? JsonSerializer.Serialize(new[] {
            new { notam_id="6/9001", type="SYNTHETIC TEST",facility="ZZZ",state="XX",description="Synthetic notice fixture, Local date. Not a real restriction.",creation_date="10/23/2026" } }) :
            string.Join("\n", Enumerable.Range(0, 61).Select(i => $"<a href=https://aeronav.faa.gov/visual/09-03-2026/sectional-files/Synthetic_Sheet_{i:D3}.zip>GEO-TIFF</a>"));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Mode == "malformed" ? "Maintenance" : text) });
    }
}
