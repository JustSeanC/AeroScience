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
    public bool FastSlowTransfers;
    public string Mode = "normal";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        if (Mode == "outage") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        if (request.RequestUri!.Host == "nfdc.faa.gov")
        {
            if (request.RequestUri.AbsoluteUri is SyntheticAirspace.ClassUrl or SyntheticAirspace.SaaUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsoluteUri == SyntheticAirspace.ClassUrl ? SyntheticAirspace.ClassArchive() : SyntheticAirspace.SaaArchive()) });
            var family = request.RequestUri.AbsolutePath.Contains("_APT_", StringComparison.Ordinal) ? "APT" : request.RequestUri.AbsolutePath.Contains("_NAV_", StringComparison.Ordinal) ? "NAV" : "FIX";
            var data = SyntheticNasr.Archive(family);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data) { Headers = { ContentRange = new(0, data.Length - 1, data.Length) } } });
        }
        if (request.RequestUri!.AbsoluteUri == AeroScience.Missions.AviationCatalog.NasrUrl)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<a href='./../NASR_Subscription/2026-10-01'>Synthetic advertised cycle</a>") });
        if (request.RequestUri!.AbsoluteUri.StartsWith(AeroScience.Missions.AviationCatalog.NasrUrl, StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Mode == "airspace" ? string.Join("\n", new[]{SyntheticAirspace.ClassUrl,SyntheticAirspace.SaaUrl}.Select(url=>"<a href='"+url+"'>Synthetic airspace original</a>")) : string.Join("\n", new[]{"APT","NAV","FIX"}.Select(f => "<a href='"+SyntheticNasr.Url(f)+"'>Synthetic original</a>"))) });
        if (request.RequestUri!.Host == "aeronav.faa.gov")
        {
            using var memory = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
            { var entry = zip.CreateEntry("synthetic.tif"); entry.LastWriteTime = new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero); using var stream = entry.Open(); var data = new byte[Mode == "slow" ? 2097152 : 8]; new Random(42).NextBytes(data); new byte[] { 73, 73, 42, 0 }.CopyTo(data, 0); stream.Write(data); }
            var content = Mode == "slow" ? (HttpContent)new StreamContent(new SlowRead(memory.ToArray(), () => FastSlowTransfers)) : new ByteArrayContent(memory.ToArray());
            content.Headers.ContentLength = memory.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        var text = request.RequestUri!.Host == "tfr.faa.gov" ? JsonSerializer.Serialize(new[] {
            new { notam_id="6/9001", type="SYNTHETIC TEST",facility="ZZZ",state="XX",description="Synthetic notice fixture, Local date. Not a real restriction.",creation_date="10/23/2026" } }) :
            string.Join("\n", Enumerable.Range(0, 61).Select(i => $"<a href=https://aeronav.faa.gov/visual/09-03-2026/sectional-files/Synthetic_Sheet_{i:D3}.zip>GEO-TIFF</a>"));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Mode == "malformed" ? "Maintenance" : text) });
    }
    private sealed class SlowRead(byte[] content, Func<bool> fast) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { if (!fast()) await Task.Delay(50, token); return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 65536)], token); }
    }
}
