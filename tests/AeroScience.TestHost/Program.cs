using AeroScience.Engine;
using AeroScience.Telemetry;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Tests;

// This executable is built only for automated tests and never published with the app.
var app = EngineApplication.Create(args, services => services.AddSingleton<ITelemetryProvider>(sp =>
    new SimulatedTelemetryProvider(sp.GetRequiredService<EngineOptions>().SamplingIntervalMs, includeResearch: true)));
var options=app.Services.GetRequiredService<EngineOptions>();
app.Services.GetRequiredService<MapSettings>().InitializeDefaults(true); // Existing offline browser fixtures; new-install tests reset explicitly.
app.Services.GetRequiredService<ApplicationUpdates>().Configure(new(false)); // No real update traffic in synthetic browser tests.
app.MapPost("/api/testing/maps/new-install",()=>{File.Delete(Path.Combine(options.ConfigurationDirectory??options.DataDirectory,"maps.json"));return new {reset=true};});
app.MapPost("/api/testing/remote/{kind}",async (string kind,ScienceStore science,MissionCoordinator coordinator)=>
{
    var parsed=Enum.Parse<RemoteKind>(kind,true);
    if(coordinator.Snapshot().ActiveMission is {} active)coordinator.End(active.Id);
    var folder=Path.Combine(options.DataDirectory,"test-inputs",Guid.NewGuid().ToString("N"));
    await science.ImportSourceAsync(RemoteFixtureFiles.Create(folder,parsed));
    var id=RemoteAcquisition.Instruments.Single(p=>p.Value==parsed).Key;
    var mission=coordinator.Start(new StartMissionRequest("SYNTHETIC BROWSER FIXTURE — "+kind,Instruments:[new(id,true)]));
    var time=DateTimeOffset.UtcNow;var frame=new TelemetryFrame(time,TelemetrySource.Simulated,"SYNTHETIC browser fixture, no MSFS",40,-75,1000,328.0839895,140,150,160,0,0,0,0,0);
    coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic browser fixture",time),frame));
    using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(science.Products(mission.Id).Length==0)await Task.Delay(10,cancel.Token);
    coordinator.End(mission.Id);return new{missionId=mission.Id,product=science.Products(mission.Id).Single()};
});
app.Run();
