using System.Net;
using System.Text;
using AeroScience.Engine;
using Xunit;

namespace AeroScience.Tests;

// Real service/store + deterministic HTTP transport; not genuine FAA or hardware acceptance.
public sealed class AviationReferenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-aviation-reference", Guid.NewGuid().ToString("N"));
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 10, 23, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
    private sealed class Transport : HttpMessageHandler
    {
        public int Calls; public HttpStatusCode Code = HttpStatusCode.OK;
        public string Body = "<a href='https://aeronav.faa.gov/visual/09-03-2026/tac-files/Boston_TAC.zip'>Synthetic metadata</a>";
        public readonly List<string> Urls = [];
        public Func<CancellationToken, Task>? Wait;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Urls.Add(request.RequestUri!.AbsoluteUri); if (Wait is not null) await Wait(token);
            Assert.Null(request.Headers.Authorization);
            return new(Code) { Content = new StringContent(Body, Encoding.UTF8, "text/html") };
        }
    }
    [Fact]
    public async Task ConsentFreeReadsAreOfflineAndNeverCreateAStoreOrTouchProviders()
    {
        var handler = new Transport(); using var http = new HttpClient(handler); using var aviation = new AviationReference(root, root, http);
        Assert.False(aviation.Configuration().OnlineEnabled); Assert.Equal("Not searched", aviation.Page("vfr").Status);
        Assert.Empty(aviation.Page("vfr").Charts); Assert.False(Directory.Exists(root));
        await Assert.ThrowsAsync<InvalidOperationException>(() => aviation.Refresh("vfr"));
        await Assert.ThrowsAsync<ArgumentException>(() => aviation.Refresh("https://evil.test"));
        Assert.Throws<ArgumentException>(() => aviation.Page("vfr", 1)); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task OptInFetchesOnlyOfficialMetadataAndReopensOfflineWithoutCreatingAMission()
    {
        var handler = new Transport(); using var http = new HttpClient(handler); var clock = new Clock();
        using (var aviation = new AviationReference(root, root, http, clock))
        {
            aviation.Configure(true); var page = await aviation.Refresh("vfr"); Assert.Equal("Cached", page.Status);
            Assert.Single(page.Charts); Assert.Equal(1, page.Total); Assert.Equal(clock.Utc, page.Cache!.LastSuccessUtc);
            Assert.Equal("Not searched", aviation.Page("tfr").Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => aviation.Refresh("vfr"));
            Assert.Equal(1, handler.Calls); Assert.Equal(AeroScience.Missions.AviationCatalog.VfrUrl, Assert.Single(handler.Urls));
            aviation.Configure(false); Assert.Equal("Offline cached", aviation.Page("vfr").Status);
        }
        using var reopened = new AviationReference(root, root, http, clock);
        Assert.Single(reopened.Page("vfr").Charts); Assert.Equal(1, handler.Calls);
        Assert.False(File.Exists(Path.Combine(root, "missions.db"))); Assert.False(File.Exists(Path.Combine(root, "ScienceLibrary", "science.db")));
    }
    [Theory]
    [InlineData(429, "RateLimited")]
    [InlineData(503, "ProviderUnavailable")]
    [InlineData(403, "NotAuthorized")]
    [InlineData(302, "InvalidMetadata")]
    public async Task OutageOrRedirectPreservesPreviousCacheWithoutLeakingUrls(int code, string expected)
    {
        var handler = new Transport(); using var http = new HttpClient(handler); var clock = new Clock();
        using var aviation = new AviationReference(root, root, http, clock); aviation.Configure(true); await aviation.Refresh("vfr");
        var success = clock.Utc; clock.Utc = clock.Utc.AddMinutes(6); handler.Code = (HttpStatusCode)code;
        handler.Body = "secret-provider-url-token-must-not-be-persisted"; var page = await aviation.Refresh("vfr");
        Assert.Equal("Stale cached", page.Status); Assert.Single(page.Charts); Assert.Equal(expected, page.Cache!.Error);
        Assert.Equal(success, page.Cache.LastSuccessUtc); Assert.Equal(clock.Utc, page.Cache.LastAttemptUtc);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(page));
    }
    [Fact]
    public async Task InvalidOrOversizedMetadataDoesNotReplaceLastSuccessfulInventory()
    {
        var handler = new Transport(); using var http = new HttpClient(handler); var clock = new Clock();
        using var aviation = new AviationReference(root, root, http, clock); aviation.Configure(true); await aviation.Refresh("vfr");
        foreach (var body in new[] { "<h1>Maintenance</h1>", new string('a', 4194305) })
        {
            clock.Utc = clock.Utc.AddMinutes(6); handler.Body = body; var page = await aviation.Refresh("vfr");
            Assert.Equal("InvalidMetadata", page.Cache!.Error); Assert.Single(page.Charts);
        }
    }
    [Fact]
    public async Task RevokedConsentAndConcurrentRefreshCannotPublishOrStartAnotherRequest()
    {
        var handler = new Transport(); using var http = new HttpClient(handler); using var aviation = new AviationReference(root, root, http);
        aviation.Configure(true); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Wait = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var pending = aviation.Refresh("vfr"); await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => aviation.Refresh("ifr"));
        aviation.Configure(false); release.SetResult(); var page = await pending;
        Assert.Empty(page.Charts); Assert.Null(page.Cache!.LastSuccessUtc); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task RemovedConfiguredDriveDoesNotBecomeAnEmptyReplacement()
    {
        var handler = new Transport(); using var http = new HttpClient(handler); using var aviation = new AviationReference(root, root, http);
        aviation.Configure(true); await aviation.Refresh("vfr"); var store = Path.Combine(root, "AviationReference");
        Directory.Move(store, store + "-disconnected");
        Assert.Throws<IOException>(() => aviation.Page("vfr")); Assert.False(Directory.Exists(store));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
