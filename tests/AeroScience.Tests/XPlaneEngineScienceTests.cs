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
        using var host=Host();using var client=Client(host);await Pair(client);
        var coordinator=host.Services.GetRequiredService<MissionCoordinator>();var store=host.Services.GetRequiredService<MissionStore>();
        var fixture=new XPlaneAssemblerFixture();fixture.Seed();fixture.Confirm();
        void Apply()
        {
            var packet=fixture.Assembler.Snapshot()!;var context=packet.SampleContext!;var shift=DateTimeOffset.UtcNow-context.ConfirmationCompletedUtc;
            var shifted=context with{HostReceivedUtc=context.HostReceivedUtc+shift,ConfirmationStartedUtc=context.ConfirmationStartedUtc+shift,
                ConfirmationCompletedUtc=context.ConfirmationCompletedUtc+shift,
                Fields=context.Fields.Select(f=>f with{LastChangedUtc=f.LastChangedUtc+shift,LastReceivedUtc=f.LastReceivedUtc+shift,LastConfirmedUtc=f.LastConfirmedUtc+shift}).ToArray(),
                ReportedValues=context.ReportedValues!.Select(v=>v with{ConfirmedUtc=v.ConfirmedUtc+shift}).ToArray()};
            coordinator.Apply(packet with{Frame=packet.Frame! with{TimestampUtc=packet.Frame.TimestampUtc+shift},
                Atmosphere=packet.Atmosphere! with{TimestampUtc=packet.Atmosphere.TimestampUtc+shift},SampleContext=shifted});
        }
        Apply();var settings=(await client.GetFromJsonAsync<SimulatorSettingsView>("/api/settings/simulator",WireJson.Options))!;
        Assert.True(settings.ProductionRecordingReady);Assert.Equal("simulator.qualifiedConnected",settings.StatusKey);
        var response=await client.PostAsJsonAsync("/api/missions",new StartMissionRequest("Synthetic P04 API",Instruments:[new("ambient-temperature",true)],ArmScience:true),WireJson.Options);
        response.EnsureSuccessStatusCode();var mission=(await response.Content.ReadFromJsonAsync<Mission>(WireJson.Options))!;
        Assert.Equal("xplane12",mission.SimulatorOrigin!.SimulatorId);Assert.Equal(CollectionPhase.Armed,store.CollectionCheckpoint(mission.Id)!.Phase);
        var state=store.CollectionCheckpoint(mission.Id)!;
        var start=await client.PostAsJsonAsync($"/api/missions/{mission.Id}/collection/control",new CollectionControlRequest(1,mission.Id,Guid.NewGuid(),state.Revision,CollectionAction.StartData,null),WireJson.Options);
        start.EnsureSuccessStatusCode();await Task.Delay(1050);fixture.Time.Advance(TimeSpan.FromSeconds(1));fixture.Confirm(2);Apply();
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
