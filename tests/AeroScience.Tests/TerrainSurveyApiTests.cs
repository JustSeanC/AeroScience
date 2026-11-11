using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class ApiTests
{
    [Fact] public async Task TerrainSurveyJobsRetainPairingCsrfCompletedArchiveAndBoundedReadContracts()
    {
        var missing=Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync($"/api/missions/{missing}/terrain-surveys",new{})).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync($"/api/science/artifacts/{missing}/terrain-chunks")).StatusCode);
        await Pair(client);client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/api/missions/{missing}/terrain-surveys",new{})).StatusCode);
        await Pair(client);
        var missions=factory.Services.GetRequiredService<MissionStore>();var science=factory.Services.GetRequiredService<ScienceStore>();
        var utc=DateTimeOffset.UtcNow;var id=missions.Create("SYNTHETIC legacy archive; no active simulator needed",null,utc);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"/api/missions/{id}/terrain-surveys",new{})).StatusCode);
        missions.Finish(id,MissionStatus.Completed,utc.AddSeconds(1),"Synthetic fixture");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/missions/{id}/terrain-surveys",new TerrainSurveyOptions(MaximumOutputBytes:long.MaxValue),WireJson.Options)).StatusCode);
        using var response=await client.PostAsJsonAsync($"/api/missions/{id}/terrain-surveys",new{});response.EnsureSuccessStatusCode();
        var job=(await response.Content.ReadFromJsonAsync<AnalysisJob>(WireJson.Options))!;
        Assert.Equal("terrain-corridor",job.Kind);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while(job.State is "Queued" or "Running")
        {await Task.Delay(10,timeout.Token);job=(await client.GetFromJsonAsync<AnalysisJob>($"/api/analysis/jobs/{job.Id}",WireJson.Options,timeout.Token))!;}
        Assert.Equal("Completed",job.State);var artifact=Assert.Single(job.Artifacts);
        var status=(await client.GetFromJsonAsync<TerrainSurveyStatus>($"/api/science/artifacts/{artifact}/terrain-status",WireJson.Options))!;Assert.Equal("Current",status.State);
        var page=(await client.GetFromJsonAsync<TerrainSurveyChunkPage>($"/api/science/artifacts/{artifact}/terrain-chunks",WireJson.Options))!;Assert.Empty(page.Chunks);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync($"/api/science/artifacts/{artifact}/terrain-chunks?limit=9")).StatusCode);
        var records=(await client.GetFromJsonAsync<TerrainSurveyRecord[]>($"/api/science/artifacts/{artifact}",WireJson.Options))!;
        Assert.Equal("Unavailable",records[^1].Summary!.State);Assert.Empty(records[0].Header!.Sources);Assert.False(records[0].Header!.GrantsOriginalScienceCredit);
        Assert.Empty(science.StreamObservations(id));Assert.Empty(missions.PerformanceReports(id));Assert.Empty(science.Sources());
        missions.AddEvent(id,utc.AddSeconds(1),"connection","SYNTHETIC archived input changed");
        Assert.Equal("Invalidated",(await client.GetFromJsonAsync<TerrainSurveyStatus>($"/api/science/artifacts/{artifact}/terrain-status",WireJson.Options))!.State);
        Assert.Equal(HttpStatusCode.Conflict,(await client.GetAsync($"/api/science/artifacts/{artifact}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await client.GetAsync($"/api/science/artifacts/{artifact}/terrain-chunks")).StatusCode);
    }
}
