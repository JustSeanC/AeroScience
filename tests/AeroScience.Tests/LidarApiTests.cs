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
    [Fact]public async Task HistoricalPointApiUsesRealQueuePairingCsrfLoopbackAndOriginalEvidenceBoundaries()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync($"/api/missions/{Guid.NewGuid()}/lidar/jobs")).StatusCode);await Pair(client);client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/api/lidar/jobs/{Guid.NewGuid()}/control",new{})).StatusCode);await Pair(client);
        var missions=factory.Services.GetRequiredService<MissionStore>();var science=factory.Services.GetRequiredService<ScienceStore>();var f=await AeroScience.TestHost.LidarWorkflowFixture.Create(missions,science,directory);var j=science.LidarJob(f.JobId);var command=new TerrainElevationControl(Guid.NewGuid(),j.Revision,"start");
        var start=await client.PostAsJsonAsync($"/api/lidar/jobs/{j.Id}/control",command,WireJson.Options);start.EnsureSuccessStatusCode();var receipt=await start.Content.ReadAsStringAsync();var again=await client.PostAsJsonAsync($"/api/lidar/jobs/{j.Id}/control",command,WireJson.Options);Assert.Equal(receipt,await again.Content.ReadAsStringAsync());
        var timer=System.Diagnostics.Stopwatch.StartNew();while(science.LidarJob(j.Id).State is "Queued" or "Running"){if(timer.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException();await Task.Delay(25);}var done=science.LidarJob(j.Id);Assert.True(done.State=="PartiallyComplete",done.Message);Assert.Equal(2,done.RetainedPoints);
        Assert.Empty(science.StreamObservations(f.MissionId));Assert.Empty(science.Products(f.MissionId));Assert.Empty(missions.PerformanceReports(f.MissionId));var tile=science.LidarCheckpoints(j.Id).First();
        var points=await client.GetAsync($"/api/science/artifacts/{tile.ArtifactId}/lidar-points?limit=30");points.EnsureSuccessStatusCode();using var body=JsonDocument.Parse(await points.Content.ReadAsStringAsync());Assert.Equal(2,body.RootElement.GetProperty("points").GetArrayLength());Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync($"/api/science/artifacts/{tile.ArtifactId}/lidar-points?limit=1000")).StatusCode);
        var access=factory.Services.GetRequiredService<AccessControl>();var pair=access.Pair(false,access.PairingCode)!;var payload=JsonSerializer.SerializeToUtf8Bytes(command,WireJson.Options);var remote=await factory.Server.SendAsync(c=>{c.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new KnownRequestBody());c.Connection.RemoteIpAddress=IPAddress.Parse("192.168.1.40");c.Request.Scheme="http";c.Request.Host=new("localhost",5080);c.Request.Method="POST";c.Request.Path=$"/api/lidar/jobs/{j.Id}/control";c.Request.ContentType="application/json";c.Request.ContentLength=payload.Length;c.Request.Body=new MemoryStream(payload);c.Request.Headers.Origin="http://localhost:5080";c.Request.Headers.Cookie=access.SessionCookieName+"="+pair.Token;c.Request.Headers["X-AeroScience-CSRF"]=pair.Csrf;});Assert.Equal(403,remote.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"/api/lidar/jobs/{j.Id}/control",command with{RequestId=Guid.NewGuid()},WireJson.Options)).StatusCode);
    }
    [Fact]public async Task LidarDiscoveryDoesNotBypassOfflineConsentOrCreateOriginalScience()
    {
        await Pair(client);var settings=factory.Services.GetRequiredService<ScientificAccessSettings>();settings.Save(new(false,false));var response=await client.PostAsJsonAsync("/api/science/lidar/discovery",new LidarDiscoveryRequest(new(-93.61,41.59,-93.59,41.61)),WireJson.Options);Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);var science=factory.Services.GetRequiredService<ScienceStore>();Assert.Empty(science.Products());
    }
}
