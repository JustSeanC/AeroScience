using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class SimulatorIntegrationTests
{
    [Fact] public async Task QualifiedXPlaneApiRecordsAndCollectsWhileMsfsCommandsAndLiveSharingRemainIsolated()
    {
        var fixture=new XPlaneAssemblerFixture();
        using var host=Host(clock:fixture.Time);using var client=Client(host);await Pair(client);
        // Keep real host-level age guards active as well as the controlled
        // simulator/controller clock. Seed only after startup/pairing completes.
        fixture.Time.StartUtc=DateTimeOffset.UtcNow;fixture.Seed();fixture.Confirm();
        var coordinator=host.Services.GetRequiredService<MissionCoordinator>();var store=host.Services.GetRequiredService<MissionStore>();
        void Apply()=>coordinator.Apply(fixture.Assembler.Snapshot()!);
        Apply();var settings=(await client.GetFromJsonAsync<SimulatorSettingsView>("/api/settings/simulator",WireJson.Options))!;
        Assert.True(settings.ProductionRecordingReady);Assert.Equal("simulator.qualifiedConnected",settings.StatusKey);
        var response=await client.PostAsJsonAsync("/api/missions",new StartMissionRequest("Synthetic P04 API",Instruments:[new("ambient-temperature",true)],ArmScience:true),WireJson.Options);
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());var mission=(await response.Content.ReadFromJsonAsync<Mission>(WireJson.Options))!;
        Assert.Equal("xplane12",mission.SimulatorOrigin!.SimulatorId);Assert.Equal(CollectionPhase.Armed,store.CollectionCheckpoint(mission.Id)!.Phase);
        var state=store.CollectionCheckpoint(mission.Id)!;
        var start=await client.PostAsJsonAsync($"/api/missions/{mission.Id}/collection/control",new CollectionControlRequest(1,mission.Id,Guid.NewGuid(),state.Revision,CollectionAction.StartData,null),WireJson.Options);
        start.EnsureSuccessStatusCode();fixture.Time.Advance(TimeSpan.FromSeconds(1));fixture.Confirm(2);Apply();
        Assert.Null(coordinator.Snapshot().RecordingStop);
        Assert.Single(store.Samples(mission.Id,10,0));Assert.Single(host.Services.GetRequiredService<ScienceStore>().StreamObservations(mission.Id));
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/settings/simulator",new SimulatorChange(0,"msfs2024",8086),WireJson.Options)).StatusCode);
        var unavailable=await Assert.ThrowsAsync<InvalidOperationException>(()=>host.Services.GetRequiredService<LiveFlightSharing>().Preview(Guid.NewGuid(),default));
        Assert.Contains("source-aware live protocol",unavailable.Message);
        foreach(var op in new[]{"start","science-start","science-resume"})
        {
            var raw=host.Services.GetRequiredService<ToolbarCommandHandler>().Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),op),WireJson.Options));
            Assert.False(JsonSerializer.Deserialize<ToolbarReply>(raw,WireJson.Options)!.Ok);
        }
        Assert.Equal("Disabled",host.Services.GetRequiredService<ToolbarBridgeStatus>().Snapshot().State);
        (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/end",new{},WireJson.Options)).EnsureSuccessStatusCode();
        using var result=JsonDocument.Parse(await client.GetStringAsync($"/api/missions/{mission.Id}"));
        Assert.Equal("xplane12",result.RootElement.GetProperty("mission").GetProperty("simulatorOrigin").GetProperty("simulatorId").GetString());
        foreach(var path in new[]{"/api/plans","/api/science/sources","/api/science/datasets"})Assert.True((await client.GetAsync(path)).IsSuccessStatusCode);
    }
}
