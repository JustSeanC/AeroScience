using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.SimConnect;
using AeroScience.Telemetry;
using AeroScience.XPlane;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class SimulatorIntegrationTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"aeroscience-p03-engine",Guid.NewGuid().ToString("N"));
    private sealed class NoSimulator : ITelemetryProvider
    {
        public async IAsyncEnumerable<TelemetryUpdate> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken=default)
        {await Task.Delay(Timeout.Infinite,cancellationToken);yield break;}
    }
    private WebApplicationFactory<Program> Host(string simulator="XPlane")=>new WebApplicationFactory<Program>().WithWebHostBuilder(builder=>{
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_,config)=>config.AddInMemoryCollection(new Dictionary<string,string?>{
            ["AeroScience:DataDirectory"]=directory,["AeroScience:ConfigurationDirectory"]=directory,
            ["AeroScience:Telemetry"]=simulator,["AeroScience:EnableToolbarBridge"]="true"}));
        builder.ConfigureTestServices(services=>{services.RemoveAll<ITelemetryProvider>();services.AddSingleton<ITelemetryProvider,NoSimulator>();});
    });
    private static HttpClient Client(WebApplicationFactory<Program> host)
    {var client=host.CreateClient(new(){BaseAddress=new("http://localhost:5080")});client.DefaultRequestHeaders.Add("Origin","http://localhost:5080");return client;}
    private static async Task Pair(HttpClient client)
    {using var reply=await client.PostAsJsonAsync("/api/session",new{});reply.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await reply.Content.ReadAsStringAsync());client.DefaultRequestHeaders.Add("X-AeroScience-CSRF",json.RootElement.GetProperty("csrf").GetString());}
    [Theory] [InlineData("SimConnect",typeof(SimConnectTelemetryProvider))] [InlineData("XPlane",typeof(XPlaneTelemetryProvider))]
    public async Task StartupSelectsExactlyOneProviderAndOnlyMsfsHostsTheToolbar(string source,Type type)
    {
        await using var app=EngineApplication.Create(["--AeroScience:DataDirectory="+directory,"--AeroScience:ConfigurationDirectory="+directory,"--AeroScience:Telemetry="+source]);
        Assert.IsType(type,Assert.Single(app.Services.GetServices<ITelemetryProvider>()));
        var workers=app.Services.GetServices<IHostedService>().ToArray();Assert.Single(workers.OfType<TelemetryWorker>());
        Assert.Equal(source=="SimConnect"?1:0,workers.OfType<ToolbarWorker>().Count());
        Assert.Equal(source=="SimConnect",app.Services.GetRequiredService<SimulatorAccessPolicy>().RecordingAllowed);
        // Neither provider's constructor opens a live simulator connection.
        Assert.Null(app.Services.GetRequiredService<MissionCoordinator>().Snapshot().ActiveMission);
    }
    [Fact] public async Task StandaloneStartupReadsTheSamePersistedSelectionAndProfileDefaults()
    {
        var preferences=new PreferenceStore(directory);preferences.SetSimulator(new(0,"xplane12",9012));
        await using var app=EngineApplication.Create(["--AeroScience:DataDirectory="+directory,"--AeroScience:ConfigurationDirectory="+directory]);
        var options=app.Services.GetRequiredService<EngineOptions>();Assert.Equal("XPlane",options.Telemetry);Assert.Equal(9012,options.XPlanePort);
        var profile=ApplicationProfile.Current.EngineOptions(preferences.Load());Assert.Equal("XPlane",profile.Telemetry);Assert.False(profile.EnableToolbarBridge);Assert.Equal(9012,profile.XPlanePort);
        Assert.Equal("SimConnect",new EngineOptions().Telemetry);
    }
    [Fact] public async Task SelectionSaveRequiresAuthenticationCsrfLoopbackAndPreservesEffectiveProviderUntilRestart()
    {
        using var host=Host();using var client=Client(host);const string path="/api/settings/simulator";
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(path)).StatusCode);await Pair(client);
        var before=(await client.GetFromJsonAsync<SimulatorSettingsView>(path,WireJson.Options))!;Assert.Equal("xplane12",before.Effective.Id);Assert.True(before.CanWrite);Assert.False(before.ProductionRecordingReady);
        var response=await client.PostAsJsonAsync(path,new SimulatorChange(0,"xplane12",9012),WireJson.Options);response.EnsureSuccessStatusCode();
        var saved=(await response.Content.ReadFromJsonAsync<SimulatorSettingsView>(WireJson.Options))!;Assert.True(saved.RestartRequired);Assert.Equal(8086,saved.Effective.XPlanePort);Assert.Equal(9012,saved.Preferences!.XPlanePort);
        Assert.Equal(8086,host.Services.GetRequiredService<EngineOptions>().XPlanePort);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(path,new SimulatorChange(0,"msfs2024",8086),WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync(path,new{expectedRevision=1,id="xplane12",xPlanePort=9012,host="remote.invalid"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync(path,new{id="xplane12",xPlanePort=9012})).StatusCode);
        var access=host.Services.GetRequiredService<AccessControl>();var remote=access.Pair(false,access.PairingCode)!;
        foreach(var method in new[]{"GET","POST"})
        {
            var body=Encoding.UTF8.GetBytes("{\"expectedRevision\":1,\"id\":\"msfs2024\",\"xPlanePort\":8086}");
            var result=await host.Server.SendAsync(context=>{
                context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
                context.Connection.RemoteIpAddress=IPAddress.Parse("192.168.1.42");context.Request.Scheme="http";context.Request.Host=new("localhost",5080);
                context.Request.Path=path;context.Request.Method=method;context.Request.ContentType="application/json";context.Request.Headers.Origin="http://localhost:5080";
                context.Request.Headers.Cookie=access.SessionCookieName+"="+remote.Token;context.Request.Headers["X-AeroScience-CSRF"]=remote.Csrf;
                if(method=="POST"){context.Request.Body=new MemoryStream(body);context.Request.ContentLength=body.Length;}
            });
            Assert.Equal(method=="GET"?200:403,result.Response.StatusCode);
            if(method=="GET"){var remoteView=await JsonSerializer.DeserializeAsync<SimulatorSettingsView>(result.Response.Body,WireJson.Options);Assert.False(remoteView!.CanWrite);}
        }
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path,new SimulatorChange(1,"msfs2024",8086))).StatusCode);
        client.DefaultRequestHeaders.Add("X-AeroScience-CSRF",remote.Csrf);client.DefaultRequestHeaders.Remove("Origin");client.DefaultRequestHeaders.Add("Origin","http://remote.invalid");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(path,new SimulatorChange(1,"msfs2024",8086))).StatusCode);
        Assert.Equal("xplane12",host.Services.GetRequiredService<EngineOptions>().SimulatorId);Assert.Equal(1,host.Services.GetRequiredService<PreferenceStore>().Simulator().Revision);
    }
    [Fact] public async Task XPlaneEndpointsAndStaleToolbarCannotRecordCollectOrStartLiveSharing()
    {
        using var host=Host();using var client=Client(host);await Pair(client);var flight=Guid.NewGuid();
        foreach(var input in new[]{new StartMissionRequest("Legacy recording"),new StartMissionRequest("Armed recording",ArmScience:true)})
            Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/missions",input,WireJson.Options)).StatusCode);
        foreach(var action in new[]{CollectionAction.StartData,CollectionAction.ResumeData,CollectionAction.RearmAutomation})
            Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"/api/missions/{flight}/collection/control",new CollectionControlRequest(1,flight,Guid.NewGuid(),0,action,null),WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/research/control",new ResearchCardControl(1,flight,Guid.NewGuid(),0,ResearchCardAction.BeginAttempt,Guid.NewGuid(),null),WireJson.Options)).StatusCode);
        var sharing=host.Services.GetRequiredService<LiveFlightSharing>();
        Assert.Contains("current, confirmed",(await Assert.ThrowsAsync<InvalidOperationException>(()=>sharing.Preview(Guid.NewGuid(),default))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>sharing.Start(new(Guid.NewGuid(),flight,1,"Pilot",true),default));
        foreach(var op in new[]{"state","start","science-start","science-resume"})
        {var json=host.Services.GetRequiredService<ToolbarCommandHandler>().Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),op),WireJson.Options));var reply=JsonSerializer.Deserialize<ToolbarReply>(json,WireJson.Options)!;Assert.False(reply.Ok);Assert.Contains("X-Plane",reply.Error);}
        Assert.Equal("Disabled",host.Services.GetRequiredService<ToolbarBridgeStatus>().Snapshot().State);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/desktop/toolbar",new{})).StatusCode);
        using var settings=JsonDocument.Parse(await client.GetStringAsync("/api/settings"));Assert.False(settings.RootElement.GetProperty("enableToolbarBridge").GetBoolean());Assert.Equal(JsonValueKind.Null,settings.RootElement.GetProperty("simConnect").ValueKind);
        Assert.Empty(host.Services.GetRequiredService<MissionStore>().List(10,0));
        foreach(var read in new[]{"/api/plans","/api/missions","/api/research/cards","/api/science/sources","/api/science/datasets"})Assert.True((await client.GetAsync(read)).IsSuccessStatusCode,read);
    }
    [Fact] public async Task RecordingAndProtectedMaintenanceRefuseChangesWithoutStoppingTheFlight()
    {
        using var host=Host("SimConnect");using var client=Client(host);await Pair(client);var coordinator=host.Services.GetRequiredService<MissionCoordinator>();
        var now=DateTimeOffset.UtcNow;coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic MSFS fixture",now),SimulatedTelemetryProvider.Generate(now,0)));
        var flight=coordinator.Start(new("Preserved MSFS flight",ArmScience:true));var before=coordinator.ScienceCollection(flight.Id).State!;
        var blocked=(await client.GetFromJsonAsync<SimulatorSettingsView>("/api/settings/simulator",WireJson.Options))!;
        Assert.False(blocked.CanWrite);Assert.Contains("Finish recording",blocked.Warning);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/settings/simulator",new SimulatorChange(0,"xplane12",8086),WireJson.Options)).StatusCode);
        Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);Assert.Equal(before,coordinator.ScienceCollection(flight.Id).State);
        coordinator.End(flight.Id);
        using(coordinator.EnterMaintenance())Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/settings/simulator",new SimulatorChange(0,"xplane12",8086),WireJson.Options)).StatusCode);
        var response=await client.PostAsJsonAsync("/api/settings/simulator",new SimulatorChange(0,"xplane12",8086),WireJson.Options);response.EnsureSuccessStatusCode();
        Assert.Equal("msfs2024",host.Services.GetRequiredService<EngineOptions>().SimulatorId);Assert.True(coordinator.LiveOperationsAllowed);
        Assert.NotNull(coordinator.Start("MSFS remains effective until companion restart"));
    }
    [Fact] public async Task CorruptSavedSettingsExposeRecoveryWithoutChangingTheActiveSourceOrOverwritingFiles()
    {
        using var host=Host();using var client=Client(host);await Pair(client);
        var preferences=host.Services.GetRequiredService<PreferenceStore>();const string corrupt="{broken";File.WriteAllText(preferences.SettingsPath,corrupt);
        var view=(await client.GetFromJsonAsync<SimulatorSettingsView>("/api/settings/simulator",WireJson.Options))!;
        Assert.Null(view.Preferences);Assert.False(view.CanWrite);Assert.Equal("xplane12",view.Effective.Id);Assert.Contains("Restore a settings backup",view.Warning);
        var response=await client.PostAsJsonAsync("/api/settings/simulator",new SimulatorChange(0,"msfs2024",8086),WireJson.Options);
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Contains("Restore a settings backup",await response.Content.ReadAsStringAsync());Assert.Equal(corrupt,File.ReadAllText(preferences.SettingsPath));
    }
    [Theory]
    [InlineData(ConnectionState.Disconnected,"LocalApiConnectionLost","simulator.waiting")]
    [InlineData(ConnectionState.Unavailable,"ApiAccessDenied","simulator.accessDenied")]
    [InlineData(ConnectionState.Unavailable,"UnsupportedApiVersion","simulator.unsupportedApi")]
    [InlineData(ConnectionState.Initializing,"WaitingForInitialSnapshot","simulator.waitingData")]
    [InlineData(ConnectionState.Initializing,"SnapshotSpanExceeded","simulator.stale")]
    public async Task ConnectionDetailsNeverImplyRecordingReadiness(ConnectionState state,string reason,string key)
    {
        using var host=Host();using var client=Client(host);await Pair(client);
        host.Services.GetRequiredService<MissionCoordinator>().Apply(new(new(state,TelemetrySource.Simulator,reason,DateTimeOffset.UtcNow),null));
        var view=(await client.GetFromJsonAsync<SimulatorSettingsView>("/api/settings/simulator",WireJson.Options))!;Assert.Equal(key,view.StatusKey);Assert.Equal("xplane12",view.Effective.Id);Assert.False(view.ProductionRecordingReady);Assert.False(view.ScienceIntegrationReady);
    }
    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature{public bool CanHaveBody=>true;}
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
