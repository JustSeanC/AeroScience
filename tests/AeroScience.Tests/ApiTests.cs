using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using AeroScience.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;

namespace AeroScience.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "aeroscience-api", Guid.NewGuid().ToString());
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    public ApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services => services.AddSingleton<ITelemetryProvider>(new SimulatedTelemetryProvider(250)));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["AeroScience:DataDirectory"] = directory, ["AeroScience:SamplingIntervalMs"] = "250" }));
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:5080") });
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5080");
    }
    private async Task Pair(HttpClient display)
    {
        var response = await display.PostAsJsonAsync("/api/session", new { code = (string?)null }); response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        display.DefaultRequestHeaders.Add("X-AeroScience-CSRF", data.GetProperty("csrf").GetString());
    }

    [Fact]
    public async Task GeographicConfigurationAndDraftExportsRetainSessionAndCsrfProtection()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/maps/client")).StatusCode);
        await Pair(client);
        var configuration = await client.GetFromJsonAsync<JsonElement>("/api/maps/settings");
        Assert.Equal("streets", configuration.GetProperty("configuration").GetProperty("preferences").GetProperty("layer").GetString());
        Assert.True(configuration.GetProperty("configuration").GetProperty("requiresPrivacyChoice").GetBoolean());
        Assert.False(configuration.GetProperty("configuration").TryGetProperty("protectedKey", out _));
        Assert.False(configuration.GetProperty("configuration").TryGetProperty("publicApiKey", out _));
        var offline = await client.GetFromJsonAsync<JsonElement>("/api/maps/client"); Assert.Equal(0, offline.GetProperty("tiles").GetArrayLength());
        var plan = new MissionPlan(Guid.NewGuid(), "Draft <&>", Waypoints: [new("Custom", 0, 179.99)], CruisingAltitudeFeet: 7500);
        var exported = await client.PostAsJsonAsync("/api/routes/export", new RouteExportRequest(plan), WireJson.Options);
        exported.EnsureSuccessStatusCode();
        var result = await exported.Content.ReadFromJsonAsync<RouteExportResult>(WireJson.Options);
        Assert.Contains("<AppVersionMajor>12</AppVersionMajor>", result!.Content);
        Assert.Contains("Draft &lt;&amp;&gt;", result.Content);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plans/{plan.Id}")).StatusCode); // preview never silently saves a draft
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/routes/export", new RouteExportRequest(plan), WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/maps/settings", new MapSettingsRequest(false), WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/maps/layer", new MapLayerRequest("offline"))).StatusCode);
    }

    [Fact]
    public async Task ResearchEndpointsPreserveSnapshotsExportsAndRequestProtection()
    {
        await Pair(client);
        var plan = new MissionPlan(Guid.NewGuid(), "Offline plan", MissionTemplateKind.ObservationFlight,
            Waypoints: [new("A", 34, -117), new("B", 35, -118)], Instruments: []);
        var saved = await client.PostAsJsonAsync("/api/plans", plan, WireJson.Options); saved.EnsureSuccessStatusCode();
        var start = await client.PostAsJsonAsync("/api/missions", new StartMissionRequest("Planned", PlanId: plan.Id), WireJson.Options); start.EnsureSuccessStatusCode();
        var mission = (await start.Content.ReadFromJsonAsync<Mission>(WireJson.Options))!;
        (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/markers", new MarkerRequest(Notes: "Actual contextual marker"), WireJson.Options)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/end", new { })).EnsureSuccessStatusCode();
        var review = await client.GetFromJsonAsync<JsonElement>($"/api/missions/{mission.Id}/review");
        Assert.Equal("Offline plan", review.GetProperty("research").GetProperty("planSnapshot").GetProperty("name").GetString());
        foreach (var format in new[] { "flight.csv", "events.csv", "gpx", "kml", "report.txt" })
            (await client.GetAsync($"/api/missions/{mission.Id}/export/{format}")).EnsureSuccessStatusCode();
        foreach (var route in new[] { "/api/data/clear-flight", "/api/science/clear" })
            Assert.Contains((await client.PostAsJsonAsync(route, new { })).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/plans", plan, WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/desktop/data-management", new { })).StatusCode);
    }

    [Fact]
    public async Task ApiRecordsMissionStreamsStateAndRetainsHistory()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
        await Pair(client);
        var response = await client.PostAsJsonAsync("/api/missions", new { name = "API survey" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var mission = (await response.Content.ReadFromJsonAsync<Mission>(WireJson.Options))!;
        using var streamResponse = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        using var streamReader = new StreamReader(await streamResponse.Content.ReadAsStreamAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("event: state", await streamReader.ReadLineAsync(timeout.Token));
        var line = await streamReader.ReadLineAsync(timeout.Token);
        var state = JsonSerializer.Deserialize<EngineSnapshot>(line![6..], WireJson.Options)!;
        Assert.Equal(mission.Id, state.ActiveMission!.Id);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var current = await client.GetFromJsonAsync<EngineSnapshot>("/api/state", WireJson.Options);
            if (current!.ActiveMission!.SampleCount >= 2) break;
            await Task.Delay(100);
        }
        var end = await client.PostAsJsonAsync($"/api/missions/{mission.Id}/end", new { }); end.EnsureSuccessStatusCode();
        var detail = await client.GetFromJsonAsync<MissionDetail>($"/api/missions/{mission.Id}", WireJson.Options);
        Assert.Equal(MissionStatus.Completed, detail!.Mission.Status); Assert.True(detail.Mission.SampleCount >= 2);
        var samples = await client.GetFromJsonAsync<TelemetryFrame[]>($"/api/missions/{mission.Id}/telemetry", WireJson.Options);
        Assert.Equal(detail.Mission.SampleCount, samples!.Length);
    }

    [Fact]
    public async Task DisplaysShareStateAndMutationRequiresCsrfAndSameOrigin()
    {
        await Pair(client);
        using var second = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:5080") });
        second.DefaultRequestHeaders.Add("Origin", "http://localhost:5080"); await Pair(second);
        var create = await client.PostAsJsonAsync("/api/missions", new { name = "Shared mission", mode = "realistic" }); create.EnsureSuccessStatusCode();
        var mission = (await create.Content.ReadFromJsonAsync<Mission>(WireJson.Options))!;
        var state = await second.GetFromJsonAsync<EngineSnapshot>("/api/state", WireJson.Options);
        Assert.Equal(mission.Id, state!.ActiveMission!.Id);
        Assert.Equal(ScienceMode.Realistic, state.ActiveMission.Mode);
        Assert.Equal(ScienceMode.Realistic, (await second.GetFromJsonAsync<Mission[]>("/api/missions", WireJson.Options))!.Single().Mode);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync("/api/missions", new { name = "Duplicate" })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/end", new { })).StatusCode);
        second.DefaultRequestHeaders.Remove("Origin"); second.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await second.PostAsJsonAsync($"/api/missions/{mission.Id}/end", new { })).StatusCode);
        using var hostile = new HttpRequestMessage(HttpMethod.Get, "/api/health"); hostile.Headers.Host = "evil.example:5080";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(hostile)).StatusCode);
    }

    [Fact]
    public async Task SharedBrowserSessionKeepsExistingCsrfValidAndUnitsPresetsRemainProtected()
    {
        await Pair(client);
        var before = client.DefaultRequestHeaders.GetValues("X-AeroScience-CSRF").Single();
        var repeated = await client.PostAsJsonAsync("/api/session", new { code = (string?)null });
        var session = await repeated.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(before, session.GetProperty("csrf").GetString());
        var units = ScienceUnits.Scientific with { Temperature = "kelvin" }; var changed = await client.PostAsJsonAsync("/api/display/units", units, WireJson.Options); changed.EnsureSuccessStatusCode();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/display/units"); Assert.Equal("kelvin", settings.GetProperty("preferences").GetProperty("temperature").GetString());
        var preset = new InstrumentPreset(Guid.Empty, "API independent", null, [new("ambient-wind", true, 2000)]);
        var created = await client.PostAsJsonAsync("/api/presets", preset, WireJson.Options); created.EnsureSuccessStatusCode(); var saved = await created.Content.ReadFromJsonAsync<InstrumentPreset>(WireJson.Options); Assert.NotEqual(Guid.Empty, saved!.Id);
        Assert.Single((await client.GetFromJsonAsync<InstrumentPreset[]>("/api/presets", WireJson.Options))!);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF"); Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/presets", preset, WireJson.Options)).StatusCode);
    }

    [Fact]
    public void RemotePairingRequiresCodeExpiresAcrossRestartAndLimitsAttempts()
    {
        var access = new AccessControl(); Assert.Null(access.Pair(false, "WRONG")); Assert.Null(access.Pair(false, null));
        var session = access.Pair(false, access.PairingCode); Assert.NotNull(session);
        Assert.NotEqual(access.PairingCode, new AccessControl().PairingCode);
        for (var i = 0; i < 5; i++) Assert.True(access.AllowAttempt("192.168.1.2"));
        Assert.False(access.AllowAttempt("192.168.1.2"));
        Assert.True(access.AllowAttempt("192.168.1.3"));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("8.8.8.8")]
    [InlineData("127.0.0.1")]
    public void LanBindingRefusesPublicAndWildcardAddresses(string address) => Assert.Throws<InvalidOperationException>(() =>
        new EngineOptions { EnableLan = true, LanAddress = address }.Validate());

    public Task InitializeAsync()=>Task.CompletedTask;
    public async Task DisposeAsync()
    {
        var services=factory.Services;
        client.Dispose(); await factory.DisposeAsync();
        // The deferred TestServer host can finish before its application provider's
        // asynchronous singleton cleanup. Join the actual provider before deleting files.
        if(services is IAsyncDisposable owned)await owned.DisposeAsync();
        // Ownership is released before cleanup; do not mask a live engine with delete retries.
        using(var lease=new DataDirectoryLease(new EngineOptions{DataDirectory=directory})) { }
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
