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
    [Fact] public async Task TerrainElevationApiRequiresPairingCsrfLoopbackAndRevisionBoundCommands()
    {
        var missing=Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync($"/api/terrain-elevation/jobs/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync($"/api/missions/{missing}/terrain-elevation/jobs",new{})).StatusCode);
        await Pair(client);client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/api/terrain-elevation/jobs/{missing}/control",new{})).StatusCode);await Pair(client);
        var missions=factory.Services.GetRequiredService<MissionStore>();var science=factory.Services.GetRequiredService<ScienceStore>();var utc=DateTimeOffset.UtcNow;
        var id=missions.Create("SYNTHETIC empty archived terrain review",null,utc);missions.Finish(id,MissionStatus.Completed,utc.AddSeconds(1),"Synthetic fixture");var geometry=ContinuousTerrainSurveys.Build(science,missions,id);
        var response=await client.PostAsJsonAsync($"/api/missions/{id}/terrain-elevation/jobs",new TerrainElevationRequest(geometry.Id,[]),WireJson.Options);response.EnsureSuccessStatusCode();var job=(await response.Content.ReadFromJsonAsync<TerrainElevationJob>(WireJson.Options))!;
        Assert.Equal("Reviewed",job.State);Assert.Empty(job.Sources);Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"/api/terrain-elevation/jobs/{job.Id}/control",new TerrainElevationControl(Guid.NewGuid(),job.Revision,"start"),WireJson.Options)).StatusCode);
        var cancel=new TerrainElevationControl(Guid.NewGuid(),job.Revision,"cancel");var cancelled=await client.PostAsJsonAsync($"/api/terrain-elevation/jobs/{job.Id}/control",cancel,WireJson.Options);cancelled.EnsureSuccessStatusCode();
        var access=factory.Services.GetRequiredService<AccessControl>();var paired=access.Pair(false,access.PairingCode)!;var payload=JsonSerializer.SerializeToUtf8Bytes(cancel,WireJson.Options);
        var remote=await factory.Server.SendAsync(context=>{
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new KnownRequestBody());context.Connection.RemoteIpAddress=IPAddress.Parse("192.168.1.40");context.Request.Scheme="http";context.Request.Host=new("localhost",5080);context.Request.Method="POST";context.Request.Path=$"/api/terrain-elevation/jobs/{job.Id}/control";context.Request.ContentType="application/json";context.Request.ContentLength=payload.Length;context.Request.Body=new MemoryStream(payload);context.Request.Headers.Origin="http://localhost:5080";context.Request.Headers.Cookie=access.SessionCookieName+"="+paired.Token;context.Request.Headers["X-AeroScience-CSRF"]=paired.Csrf;
        });Assert.Equal(403,remote.Response.StatusCode);
        var duplicate=await client.PostAsJsonAsync($"/api/terrain-elevation/jobs/{job.Id}/control",cancel,WireJson.Options);Assert.Equal(await cancelled.Content.ReadAsStringAsync(),await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"/api/terrain-elevation/jobs/{job.Id}/control",cancel with{RequestId=Guid.NewGuid()},WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/missions/{id}/terrain-elevation/jobs",new TerrainElevationRequest(geometry.Id,[],new(TileSize:33)),WireJson.Options)).StatusCode);
        Assert.Empty(science.StreamObservations(id));Assert.Empty(missions.PerformanceReports(id));
    }
}
