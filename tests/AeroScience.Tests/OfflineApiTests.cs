using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AeroScience.Tests;

public sealed class OfflineApiTests
{
    [Fact]
    public async Task DisconnectedStartupServesArchiveDetailsAndCompletePortableExport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aeroscience-offline", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MissionStore(Path.Combine(directory, "missions.db")); store.Initialize();
            var frame = new TelemetryFrame(DateTimeOffset.UtcNow, TelemetrySource.Simulator, "T-38", 32, -106, 8000, 3000, 160, 180, 162, 75, 71, 4, 0, -2000, MachNumber: .82);
            var id = store.Create("Offline archive", frame, frame.TimestampUtc, ScienceMode.Realistic); store.Append(id, frame);
            store.Finish(id, MissionStatus.Completed, frame.TimestampUtc.AddSeconds(2), "Done");
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                { ["AeroScience:DataDirectory"] = directory, ["AeroScience:EnableToolbarBridge"] = "false", ["AeroScience:RequireExistingDatabase"] = "true" }));
            });
            using var client = factory.CreateClient(new() { BaseAddress = new("http://localhost:5080") });
            client.DefaultRequestHeaders.Add("Origin", "http://localhost:5080");
            var session = await client.PostAsJsonAsync("/api/session", new { }); session.EnsureSuccessStatusCode();
            var csrf = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrf").GetString();
            client.DefaultRequestHeaders.Add("X-AeroScience-CSRF", csrf);
            Assert.Contains("AeroScience", await client.GetStringAsync("/"));
            var state = await client.GetFromJsonAsync<EngineSnapshot>("/api/state", WireJson.Options);
            Assert.NotEqual(ConnectionState.Connected, state!.Connection.State); Assert.Null(state.Telemetry);
            Assert.Single((await client.GetFromJsonAsync<Mission[]>("/api/missions", WireJson.Options))!);
            var detail = await client.GetFromJsonAsync<MissionDetail>($"/api/missions/{id}", WireJson.Options);
            Assert.Equal(ScienceMode.Realistic, detail!.Mission.Mode);
            var response = await client.GetAsync($"/api/missions/{id}/export"); response.EnsureSuccessStatusCode();
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            using var exported = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(.82, exported.RootElement.GetProperty("telemetry")[0].GetProperty("machNumber").GetDouble());
            Assert.Equal(id.ToString(), exported.RootElement.GetProperty("mission").GetProperty("id").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/missions", new { name = "No fake flight" })).StatusCode);
            Assert.Equal(1, store.Detail(id)!.Mission.SampleCount);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
