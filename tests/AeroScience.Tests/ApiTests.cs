using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using AeroScience.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;

namespace AeroScience.Tests;

public sealed class ApiTests : IDisposable
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
    [InlineData("0.0.0.0")][InlineData("8.8.8.8")][InlineData("127.0.0.1")]
    public void LanBindingRefusesPublicAndWildcardAddresses(string address) => Assert.Throws<InvalidOperationException>(() =>
        new EngineOptions { EnableLan = true, LanAddress = address }.Validate());

    public void Dispose()
    {
        client.Dispose(); factory.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
