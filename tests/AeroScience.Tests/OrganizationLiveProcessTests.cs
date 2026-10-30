using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
using Xunit.Abstractions;
namespace AeroScience.Tests;

public sealed partial class OrganizationConnectionIntegrationTests
{
    readonly ITestOutputHelper liveMetrics;
    public OrganizationConnectionIntegrationTests(ITestOutputHelper output)=>liveMetrics=output;

    [PostgresFact]public async Task LiveSharingUsesIndependentOrganizationConsentAndCannotReconnectAnOldFlightAfterServerRestart()
    {
        await using var first=await SubmissionFixture.Create();await using var second=await SubmissionFixture.Create();
        var one=await Connect(first.Desktop,first.Server,OrganizationDesktopPermission.ShareLiveFlight);
        var two=await Connect(first.Desktop,second.Server,OrganizationDesktopPermission.ShareLiveFlight);
        var coordinator=new MissionCoordinator(first.Store,1000);var at=DateTimeOffset.UtcNow;var frame=SubmissionFixture.Frame(at) with{IndicatedAirspeedKnots=-.2};coordinator.Apply(new(new(ConnectionState.Connected,frame.Source,"Synthetic live fixture",at),frame));var flight=coordinator.Start(new("PRIVATE NON-SHARED MISSION NAME"));
        var sharing=new LiveFlightSharing(first.Desktop,coordinator,first.Store);Assert.Empty(sharing.Status());
        await sharing.Start(new(one,flight.Id,0,"First organization public label",true),default);await sharing.Tick(default);
        Assert.Single((await ReviewGet<LiveDashboard>(first.Server,"/api/membership/live")).Flights);Assert.Empty((await ReviewGet<LiveDashboard>(second.Server,"/api/membership/live")).Flights);
        await sharing.Start(new(two,flight.Id,0,"Second organization public label",true),default);await sharing.Tick(default);
        var firstView=await ReviewGet<LiveDashboard>(first.Server,"/api/membership/live");Assert.Null(Assert.Single(firstView.Flights).Current!.IndicatedAirspeedKnots);Assert.NotNull(Assert.Single(firstView.Flights).Current!.Latitude);var secondView=await ReviewGet<LiveDashboard>(second.Server,"/api/membership/live");Assert.Equal("Second organization public label",Assert.Single(secondView.Flights).DisplayLabel);Assert.DoesNotContain("First organization",JsonSerializer.Serialize(secondView));Assert.DoesNotContain("PRIVATE NON-SHARED",JsonSerializer.Serialize(firstView));
        using(var forged=new HttpClient{BaseAddress=new(second.Server.Origin)}){forged.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",first.Vault.Read(one));Assert.Equal(HttpStatusCode.Unauthorized,(await forged.GetAsync("/api/member/live")).StatusCode);}
        await first.Restart();await sharing.Tick(default);Assert.Empty((await ReviewGet<LiveDashboard>(first.Server,"/api/membership/live")).Flights);Assert.Single((await ReviewGet<LiveDashboard>(second.Server,"/api/membership/live")).Flights);Assert.False(sharing.Status().Single(s=>s.ConnectionId==one).Status=="Sharing");
        Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);coordinator.End(flight.Id);await sharing.Tick(default);Assert.Empty((await ReviewGet<LiveDashboard>(second.Server,"/api/membership/live")).Flights);
        Assert.Equal("Stopped",sharing.Status().Single(s=>s.ConnectionId==two).Status);
    }

    [PostgresFact]public async Task LiveCapacityAndParallelReadsHaveBoundedCurrentStateAndProduceMeasuredLocalResourceEvidence()
    {
        await using var f=await SubmissionFixture.Create();var connection=await Connect(f.Desktop,f.Server,OrganizationDesktopPermission.ShareLiveFlight);var identity=(await f.Desktop.List(default)).Single(r=>r.Id==connection);
        using var native=new HttpClient{BaseAddress=new(f.Server.Origin)};native.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",f.Vault.Read(connection));
        var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var heap=GC.GetTotalMemory(false);var timer=Stopwatch.StartNew();var sessions=new List<LiveSharingSession>();long bytes=0;
        for(var i=0;i<64;i++){
            var request=new LiveSharingStart(1,Guid.NewGuid(),identity.OrganizationId,Guid.NewGuid(),DateTimeOffset.UtcNow.AddSeconds(-1),0,"Synthetic load flight "+i,[],true);
            using var response=await native.PostAsJsonAsync("/api/member/live/start",request,WireJson.Options);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());var session=(await response.Content.ReadFromJsonAsync<LiveSharingSession>(WireJson.Options))!;sessions.Add(session);
            var update=OrganizationLiveContractTests.Update(session.Id,DateTimeOffset.UtcNow);bytes+=JsonSerializer.SerializeToUtf8Bytes(update,WireJson.Options).Length;
            using var sent=await native.PostAsJsonAsync("/api/member/live/update",update,WireJson.Options);Assert.True(sent.IsSuccessStatusCode,await sent.Content.ReadAsStringAsync());
        }
        using(var overflow=await native.PostAsJsonAsync("/api/member/live/start",new LiveSharingStart(1,Guid.NewGuid(),identity.OrganizationId,Guid.NewGuid(),DateTimeOffset.UtcNow,0,"Overflow",[],true),WireJson.Options))Assert.Equal(HttpStatusCode.Conflict,overflow.StatusCode);
        var reads=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>ReviewGet<LiveDashboard>(f.Server,"/api/membership/live")));Assert.All(reads,r=>Assert.Equal(64,r.Flights.Length));var viewBytes=JsonSerializer.SerializeToUtf8Bytes(reads[0],WireJson.Options).Length;Assert.True(viewBytes<1024*1024);timer.Stop();process.Refresh();
        var measurement=new{scope="Actual loopback Kestrel + PostgreSQL. 64 synthetic live sessions from one test identity, eight concurrent authorized reads; not 64 installed aircraft or Railway performance. CPU/heap include the in-process test client and server.",sessions=64,concurrentReads=8,elapsedMilliseconds=timer.Elapsed.TotalMilliseconds,combinedProcessCpuMilliseconds=(process.TotalProcessorTime-cpu).TotalMilliseconds,managedHeapDeltaBytes=GC.GetTotalMemory(false)-heap,combinedProcessWorkingSetBytes=process.WorkingSet64,updatePayloadBytes=bytes,averageUpdateBytes=bytes/64d,dashboardPayloadBytes=viewBytes,estimatedUpdateBytesPerSecond=bytes/5d,estimatedEightViewerBytesPerSecond=8d*viewBytes/5d};
        liveMetrics.WriteLine(JsonSerializer.Serialize(measurement));
        var report=System.Environment.GetEnvironmentVariable("AEROSCIENCE_LIVE_METRICS_FILE");if(report is not null)await File.WriteAllTextAsync(report,JsonSerializer.Serialize(measurement,new JsonSerializerOptions{WriteIndented=true}));
        foreach(var session in sessions){using var stopped=await native.PostAsJsonAsync("/api/member/live/stop",new LiveSharingStop(1,Guid.NewGuid(),session.Id,1),WireJson.Options);Assert.True(stopped.IsSuccessStatusCode);}
        Assert.Empty((await ReviewGet<LiveDashboard>(f.Server,"/api/membership/live")).Flights);
    }
}
