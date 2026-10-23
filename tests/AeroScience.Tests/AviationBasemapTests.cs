using System.Net;
using System.Text.Json;
using AeroScience.Engine;
using Xunit;

namespace AeroScience.Tests;

// Provider raster metadata is synthetic; actual key/tiles and installed display acceptance remain separate.
public sealed class AviationBasemapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-basemap-060", Guid.NewGuid().ToString("N"));
    private sealed class Protector : IMapKeyProtector { public string Protect(string v) => "protected:" + v; public string Unprotect(string v) => v[10..]; }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls; public int Crs = 3857; public string Format = "JPEG"; public string Attribution = "Actual provider credit fixture";
        public HttpStatusCode Code = HttpStatusCode.OK; public readonly List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(Code) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                copyrightText = Attribution, tileInfo = new { rows = 256, cols = 256, format = Format,
                    spatialReference = new { wkid = Crs }, origin = new { x = -20037508.3427892, y = 20037508.3427892 },
                    lods = Enumerable.Range(0, 20).Select(level => new { level, resolution = 156543.03392804097 / Math.Pow(2, level) }) }
            })) });
        }
    }
    [Theory] [InlineData("esri-outdoor", "outdoor")] [InlineData("esri-navigation", "navigation")]
    public async Task DocumentedStaticRasterStylesKeepKeyConsentAttributionAndSavedIdentities(string layer, string style)
    {
        var handler = new Handler(); using var http = new HttpClient(handler); var settings = new MapSettings(root, new Protector(), http);
        Assert.Throws<ArgumentException>(() => settings.Save(new(true, layer, PrivacyAccepted: true)));
        settings.Save(new(true, layer, PublicApiKey: "PUBLIC_SYNTHETIC_KEY_NOT_REAL", PrivacyAccepted: true));
        var map = await settings.Client(); Assert.Equal(layer, map.Layer); var tile = Assert.Single(map.Tiles);
        Assert.Contains("/" + style + "/static/tile/{z}/{y}/{x}", tile.Url); Assert.Contains("Powered by Esri", tile.Attribution);
        Assert.Contains("Actual provider credit", tile.Attribution); Assert.Equal(19, tile.MaxZoom); Assert.Null(map.Error);
        Assert.Single((await settings.Client()).Tiles); Assert.Equal(1, handler.Calls);
        Assert.EndsWith("/arcgis/" + style + "/static", handler.Paths[0]);
        var restarted = new MapSettings(root, new Protector(), http); Assert.Equal(layer, restarted.Get().Preferences.Layer);
        settings.SelectLayer("streets"); Assert.True(settings.Get().KeyConfigured);
        settings.Save(new(false, "offline")); Assert.Empty((await settings.Client(layer)).Tiles); Assert.Equal(1, handler.Calls);
    }
    [Theory] [InlineData(4326, "JPEG", 200)] [InlineData(3857, "PBF", 200)] [InlineData(3857, "JPEG", 429)]
    public async Task UnsupportedVectorOrProjectionAndProviderQuotaFailToOfflineWithoutKeyLeaks(int crs, string format, int code)
    {
        var handler = new Handler { Crs = crs, Format = format, Code = (HttpStatusCode)code }; using var http = new HttpClient(handler);
        var settings = new MapSettings(root, new Protector(), http); settings.Save(new(true, "esri-outdoor", PublicApiKey: "PUBLIC_TEST_SECRET", PrivacyAccepted: true));
        var map = await settings.Client(); Assert.Equal("offline", map.Layer); Assert.Empty(map.Tiles); Assert.DoesNotContain("PUBLIC_TEST_SECRET", map.Error!);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
