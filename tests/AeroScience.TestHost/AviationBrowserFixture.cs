using System.Net;
using System.Text.Json;
using AeroScience.Missions;
namespace AeroScience.Tests;

// Synthetic metadata on the real host/service/SQLite path. Never a genuine FAA acceptance claim.
public sealed class AviationBrowserFixture : HttpMessageHandler
{
    public static AviationChartDefinition ChartDefinition
    {
        get
        {
            var data=new byte[8];new Random(42).NextBytes(data);new byte[]{73,73,42,0}.CopyTo(data,0);
            var sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
            return new("synthetic.tif",sha,10,10,[new(1,1),new(9,1),new(9,9),new(1,9),new(1,1)],"Synthetic browser chart interior. Not genuine FAA geographic acceptance.");
        }
    }
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
    private string Cycle=>Mode=="airspace-volumes"?"2026-11-26":Mode=="airspace-composite"?"2026-10-29":"2026-10-01";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        if (Mode == "outage") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        if(Mode=="tfr-originals"&&request.RequestUri!.Host=="tfr.faa.gov")
        {
            var uri=request.RequestUri.AbsoluteUri;byte[] bytes;
            if(uri==AviationTfr.NoticesUrl)bytes=TfrOriginalFiles.Read("notices");
            else if(uri==AviationTfr.NoShapeUrl)bytes=TfrOriginalFiles.Read("no-shape");
            else if(uri==AviationTfr.GeometryUrl)bytes=TfrOriginalFiles.Read("geometry");
            else{var id=new[]{"6/2070","6/2069","6/2014","6/9169"}.Single(id=>uri==AviationTfrOperations.Url(id)||uri==AviationTfrOperations.Url(id,true));bytes=TfrOriginalFiles.Packet(id,uri.EndsWith(".aixm50",StringComparison.Ordinal));}
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
        }
        if(Mode=="tfr"&&request.RequestUri!.Host=="tfr.faa.gov")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(request.RequestUri.AbsoluteUri==AeroScience.Missions.AviationTfr.NoticesUrl?SyntheticTfr.Notices():request.RequestUri.AbsoluteUri==AeroScience.Missions.AviationTfr.NoShapeUrl?SyntheticTfr.NoShape():SyntheticTfr.Geometry())});
        if (request.RequestUri!.Host == "nfdc.faa.gov")
        {
            if (request.RequestUri.AbsoluteUri == SyntheticAirspace.ClassUrl || request.RequestUri.AbsoluteUri == SyntheticAirspace.SaaUrl || request.RequestUri.AbsoluteUri == SyntheticAirspace.SaaUrl.Replace("2026-10-01",Cycle))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsoluteUri == SyntheticAirspace.ClassUrl ? SyntheticAirspace.ClassArchive() : SyntheticAirspace.SaaArchive(Mode=="airspace-volumes"?"composite-vertical":Mode=="airspace-composite"?"composite-nested":"circle")) });
            var family = request.RequestUri.AbsolutePath.Contains("_APT_", StringComparison.Ordinal) ? "APT" : request.RequestUri.AbsolutePath.Contains("_NAV_", StringComparison.Ordinal) ? "NAV" : "FIX";
            var data = SyntheticNasr.Archive(family);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data) { Headers = { ContentRange = new(0, data.Length - 1, data.Length) } } });
        }
        if (request.RequestUri!.AbsoluteUri == AeroScience.Missions.AviationCatalog.NasrUrl)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<a href='./../NASR_Subscription/"+Cycle+"'>Synthetic advertised cycle</a>") });
        if (request.RequestUri!.AbsoluteUri.StartsWith(AeroScience.Missions.AviationCatalog.NasrUrl, StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Mode is "airspace-composite" or "airspace-volumes" ? "<a href=\""+SyntheticAirspace.SaaUrl.Replace("2026-10-01",Cycle)+"\">Synthetic composite original</a>" : Mode == "airspace" ? string.Join("\n", new[]{SyntheticAirspace.ClassUrl,SyntheticAirspace.SaaUrl}.Select(url=>"<a href='"+url+"'>Synthetic airspace original</a>")) : string.Join("\n", new[]{"APT","NAV","FIX"}.Select(f => "<a href='"+SyntheticNasr.Url(f)+"'>Synthetic original</a>"))) });
        if (request.RequestUri!.Host == "aeronav.faa.gov")
        {
            using var memory = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                var entry = zip.CreateEntry("synthetic.tif"); entry.LastWriteTime = new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero);
                using(var stream = entry.Open()){var data = new byte[Mode == "slow" ? 2097152 : 8]; new Random(42).NextBytes(data); new byte[] { 73, 73, 42, 0 }.CopyTo(data, 0); stream.Write(data);}
                if(Mode=="charts"){var inset=zip.CreateEntry("unreviewed-inset.tif");using var stream=inset.Open();stream.Write(new byte[]{73,73,42,0,0,0,0,0});}
            }
            var content = Mode == "slow" ? (HttpContent)new StreamContent(new SlowRead(memory.ToArray(), () => FastSlowTransfers)) : new ByteArrayContent(memory.ToArray());
            content.Headers.ContentLength = memory.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        var text = request.RequestUri!.Host == "tfr.faa.gov" ? JsonSerializer.Serialize(new[] {
            new { notam_id="6/9001", type="SYNTHETIC TEST",facility="ZZZ",state="XX",description="Synthetic notice fixture, Local date. Not a real restriction.",creation_date="10/23/2026" } }) :
            Mode=="charts"?"<a href=https://aeronav.faa.gov/visual/09-03-2026/sectional-files/Chart_Browser_Sheet.zip>GEO-TIFF</a>":
            string.Join("\n", Enumerable.Range(0, 61).Select(i => $"<a href=https://aeronav.faa.gov/visual/09-03-2026/sectional-files/Synthetic_Sheet_{i:D3}.zip>GEO-TIFF</a>"));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Mode == "malformed" ? "Maintenance" : text) });
    }
    private sealed class SlowRead(byte[] content, Func<bool> fast) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { if (!fast()) await Task.Delay(50, token); return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 65536)], token); }
    }
}

public sealed class SyntheticChartRenderer(AviationBrowserFixture fixture):IAviationChartRenderer
{
    public bool Available=>fixture.Mode=="charts";
    public AviationChartRaster.Description Describe(string path,AviationChartDefinition definition,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!File.Exists(path)||new FileInfo(path).Length!=8)throw new IOException("Synthetic chart original unavailable.");
        return new(10,10,1,"Synthetic browser CRS, not a real FAA chart",[new(-75.8,40.2),new(-75.2,40.2),new(-75.2,39.8),new(-75.8,39.8),new(-75.8,40.2)]);
    }
    public byte[] Tile(string path,AviationChartDefinition definition,int zoom,int x,int y,CancellationToken token)
    {token.ThrowIfCancellationRequested();return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGMwaDjwHwAExAJwNooHGQAAAABJRU5ErkJggg==");}
}
