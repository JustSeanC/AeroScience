using AeroScience.Engine;
using AeroScience.Telemetry;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Tests;

if (args.Length > 0 && args[0] == "--library-probe") { LibraryProbe.Run(args[1..]); return; }
if (args.Length > 0 && args[0] == "--source-deletion-probe") { await SourceDeletionProbe.Run(args[1..]); return; }
if (args.Length > 0 && args[0] == "--evidence-probe") { EvidenceProbe.Run(args[1..]); return; }
if (args.Length > 0 && args[0] == "--earthdata-vault-probe")
{
    if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("The real Windows credential-vault probe requires Windows.");
    var temporary=Path.Combine(Path.GetTempPath(),"aeroscience-vault-probe-"+Guid.NewGuid().ToString("N"));
    var first=new AeroScience.Desktop.WindowsEarthdataCredentials(Path.Combine(temporary,"production"));
    var isolated=new AeroScience.Desktop.WindowsEarthdataCredentials(Path.Combine(temporary,"validation"));
    const string synthetic="synthetic_cloud_test_credential_not_a_nasa_token";
    try
    {
        first.Save(synthetic);
        if(first.ReadToken()!=synthetic||isolated.ReadToken() is not null)throw new IOException("OS credential round-trip/profile isolation failed.");
        isolated.Save(synthetic+"_isolated");isolated.Clear();
        if(first.ReadToken()!=synthetic||isolated.ReadToken() is not null)throw new IOException("Disconnect crossed profile identities.");
        first.Clear();if(first.ReadToken() is not null)throw new IOException("Windows credential removal failed.");
        Console.WriteLine("PASS: real Windows Credential Manager write/read/profile isolation/removal with disposable synthetic data; no NASA authorization or installed-device acceptance.");
    }finally{first.Clear();isolated.Clear();}
    return;
}
if (args.Length > 0 && args[0] == "--profile-probe")
{
    var profile=ApplicationProfile.Current;
    if(!profile.Validation || profile.Port!=5085 || profile.ToolbarPackage!="aeroscience-validation-toolbar" || profile.CommandEvent==AeroScience.SimConnect.CommBusTransport.CommandEvent)
        throw new InvalidOperationException("The compiled validation profile was not isolated.");
    var preferences=profile.Preferences();var defaults=preferences.Load();
    profile.EngineOptions(defaults with{EnableLan=true,LanAddress="192.168.1.5"}).Validate();
    if(new AccessControl().SessionCookieName==AccessControl.CookieName)throw new InvalidOperationException("Validation reused the production browser cookie.");
    preferences.ValidateDirectory(defaults.MissionDirectory);preferences.ValidateDirectory(defaults.ScienceLibraryDirectory);
    try { preferences.ValidateDirectory(new ApplicationProfile(false).ConfigurationDirectory);throw new InvalidOperationException("Production path accepted."); } catch(IOException){}
    Console.WriteLine("Compiled validation profile verified: separate roots, port, package and CommBus; shared one-active desktop mutex. No installation or MSFS acceptance claimed.");return;
}

// This executable is built only for automated tests and never published with the app.
var app = EngineApplication.Create(args, services =>
{
    services.AddSingleton(sp=>new LibraryHttpFixture(sp.GetRequiredService<EngineOptions>().DataDirectory));
    services.AddSingleton<IEarthdataCredentials,FixtureEarthdata>();
    services.AddSingleton(sp=>{_=sp.GetRequiredService<StorageManagement>();return new AutomaticScientificData(sp.GetRequiredService<ScienceStore>(),sp.GetRequiredService<ScientificAccessSettings>(),new ScientificHttp(sp.GetRequiredService<LibraryHttpFixture>(),cacheEnabled:false));});
    // Keep Library HTTP sources isolated from unrelated legacy science fixtures even
    // when a later test intentionally rewrites normal application preferences.
    services.AddSingleton(sp=>{_=sp.GetRequiredService<StorageManagement>();return new ResearchOperations(sp.GetRequiredService<MissionStore>(),sp.GetRequiredService<ScienceStore>(),sp.GetRequiredService<ScienceUnitSettings>().Get,()=>
    {
        var preferences=sp.GetRequiredService<ScientificAccessSettings>().Get().Preferences;
        return preferences with{ExcludedSourceIds=(preferences.ExcludedSourceIds??[]).Concat(sp.GetRequiredService<LibraryHttpFixture>().InactiveSources).Distinct().ToArray()};
    });});
    services.AddSingleton(sp => new BrowserTelemetryProvider(sp.GetRequiredService<EngineOptions>().SamplingIntervalMs));
    services.AddSingleton<ITelemetryProvider>(sp => sp.GetRequiredService<BrowserTelemetryProvider>());
});
var options=app.Services.GetRequiredService<EngineOptions>();
app.Services.GetRequiredService<MapSettings>().InitializeDefaults(true); // Existing offline browser fixtures; new-install tests reset explicitly.
app.Services.GetRequiredService<ApplicationUpdates>().Configure(new(false)); // No real update traffic in synthetic browser tests.
app.MapPost("/api/testing/library/setup",(LibraryHttpFixture fixture,ScientificAccessSettings settings)=>{fixture.Enable();settings.Save(new(true,true));return new{synthetic=true};});
app.MapPost("/api/testing/nasa/connection",(IEarthdataCredentials credentials)=>{((FixtureEarthdata)credentials).Connected=true;return new{synthetic=true};});
// Synthetic SDK transport only; commands execute the production coordinator/SQLite handler.
app.MapPost("/api/testing/toolbar",(System.Text.Json.JsonElement command,ToolbarCommandHandler toolbar)=>Results.Text(toolbar.Handle(command.GetRawText()),"application/json"));
app.MapPost("/api/testing/library/teardown",async(LibraryHttpFixture fixture,ScienceStore science,AutomaticScientificData automatic)=>
{
    automatic.Configure(new());using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while(automatic.QueueSummary().Downloading>0)await Task.Delay(10,deadline.Token);
    fixture.Disable();var ids=science.Sources().Where(s=>s.Manifest.Notes?.Contains("SYNTHETIC NASA browser test",StringComparison.Ordinal)==true||s.Manifest.Acquisition?.ProviderMetadataJson is {} metadata&&(metadata.Contains("SYNTHETIC browser test service",StringComparison.Ordinal)||metadata.Contains("aeroscience:test_fixture",StringComparison.Ordinal))).Select(s=>s.Id).ToArray();
    fixture.InactiveSources=ids;automatic.Configure(new(ExcludedSourceIds:ids));return new{synthetic=true,excludedSources=ids.Length};
});
app.MapPost("/api/testing/library/mode/{mode}",(string mode,LibraryHttpFixture fixture)=>{fixture.Mode=mode;return new{synthetic=true,mode};});
// Actual production import/persistence with explicitly synthetic originals; no flight observations.
app.MapPost("/api/testing/sources/unreferenced/{kind}",async(string kind,ScienceStore science)=>{
    var folder=Path.Combine(options.DataDirectory,"synthetic-local-original-"+Guid.NewGuid().ToString("N"));
    var file=RemoteFixtureFiles.Create(folder,Enum.Parse<RemoteKind>(kind,true));var source=await science.ImportSourceAsync(file);return new{sourceId=source.Id,synthetic=true};
});
app.MapPost("/api/testing/preparation/local",async(ScienceStore science,AutomaticScientificData automatic)=>{
    var folder=Path.Combine(options.DataDirectory,"synthetic-preparation-original-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
    RemoteFixtureFiles.GeoTiff(Path.Combine(folder,"elevation.tif"),123.25f,west:-76.01,north:.05);
    var manifest=new ScientificSourceManifest(1,"SYNTHETIC 204-region reuse fixture",RemoteKind.Terrain,"Isolated browser fixture","Synthetic only","Not genuine scientific acceptance","synthetic-preparation-cell",DateTimeOffset.Parse("2020-01-01T00:00:00Z"),"Unknown",[new("elevation","elevation.tif","geotiff",Unit:"m")]);
    var source=await science.ImportSourceAsync(manifest,folder);automatic.RefreshLocalReadiness();return new{sourceId=source.Id,synthetic=true};
});
app.MapPost("/api/testing/maps/new-install",()=>{File.Delete(Path.Combine(options.ConfigurationDirectory??options.DataDirectory,"maps.json"));return new {reset=true};});
app.MapPost("/api/testing/workstation",async(WorkstationFixtureRequest request,ScienceStore science,MissionStore missions,MissionCoordinator coordinator,BrowserTelemetryProvider provider)=>
{
    // Keep automatic synthetic packets inside the same retained source while seeding.
    // Otherwise a default-position packet can invalidate the manually offered acquisition.
    using var location=provider.UseRemoteFixtureLocation();
    return await WorkstationFixtures.Create(request,science,missions,coordinator,new(options.DataDirectory));
});
app.MapPost("/api/testing/remote/{kind}",async (string kind,ScienceStore science,MissionCoordinator coordinator,BrowserTelemetryProvider provider)=>
{
    var parsed=Enum.Parse<RemoteKind>(kind,true);
    if(coordinator.Snapshot().ActiveMission is {} active)coordinator.End(active.Id);
    var folder=Path.Combine(options.DataDirectory,"test-inputs",Guid.NewGuid().ToString("N"));
    var manifestPath=RemoteFixtureFiles.Create(folder,parsed);var manifest=System.Text.Json.JsonSerializer.Deserialize<ScientificSourceManifest>(File.ReadAllText(manifestPath),WireJson.Options)!;
    await science.ImportSourceAsync(manifest,folder,reuseExisting:true);
    var id=RemoteAcquisition.Instruments.Single(p=>p.Value==parsed).Key;
    using var location = provider.UseRemoteFixtureLocation();
    var mission=coordinator.Start(new StartMissionRequest("SYNTHETIC BROWSER FIXTURE — "+kind,Instruments:[new(id,true)]));
    try
    {
    var time=DateTimeOffset.UtcNow;var frame=new TelemetryFrame(time,TelemetrySource.Simulated,"SYNTHETIC browser fixture, no MSFS",40,-75,1000,328.0839895,140,150,160,0,0,0,0,0);
    coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic browser fixture",time),frame));
    var deadline = System.Diagnostics.Stopwatch.StartNew();
    while(science.Products(mission.Id).Length==0)
    {
        if(deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Synthetic remote fixture did not publish: " + System.Text.Json.JsonSerializer.Serialize(coordinator.Snapshot().Research, WireJson.Options));
        await Task.Delay(10);
    }
    return new{missionId=mission.Id,product=science.Products(mission.Id).Single()};
    }
    finally { if(coordinator.Snapshot().ActiveMission?.Id == mission.Id) coordinator.End(mission.Id); }
});
app.MapPost("/api/testing/nasa/files/{family}",async(string family,ScientificImportSession session)=>
{
    var item=session.Snapshot().NasaSelection??throw new InvalidOperationException("Choose a NASA granule through the production endpoint first.");
    if(item.Provider!="nasa-"+family)throw new ArgumentException("Fixture family mismatch.");
    var folder=Path.Combine(options.DataDirectory,"nasa-test-inputs",Guid.NewGuid().ToString("N"));
    var header=NasaFixtureFiles.Cube(folder,item,family=="classic"?224:425);
    await session.Select(header);await session.Configure(new(Name:"SYNTHETIC NASA browser test — "+family,Notes:"SYNTHETIC NASA browser test; not genuine numeric or installed acceptance"));
    return session.Snapshot();
});
app.MapPost("/api/testing/guided/{kind}",async(string kind,ScientificImportSession session)=>
{
    var parsed=Enum.Parse<RemoteKind>(kind,true);var folder=Path.Combine(options.DataDirectory,"guided-test-inputs",Guid.NewGuid().ToString("N"));
    _=RemoteFixtureFiles.Create(folder,parsed);
    await session.Select(parsed==RemoteKind.Hyperspectral?Path.Combine(folder,"cube.hdr"):parsed==RemoteKind.Terrain?Path.Combine(folder,"elevation.tif"):folder,parsed);
    await session.Configure(new(Name:"SYNTHETIC UI TEST FIXTURE — "+kind,Provider:"Isolated automated browser fixture",License:"Synthetic test data; not a genuine scientific observation",Notes:"Test-only generated values; no Windows/MSFS acceptance",Unit:parsed==RemoteKind.Terrain?"m":"reflectance",VerticalDatum:"Unknown",SceneId:"synthetic-fixture",Scale:.0001,Offset:0));
    return session.Snapshot();
});
app.MapPost("/api/testing/sources/regions",async(ScienceStore science)=>
{
    for(var i=0;i<32;i++){var folder=Path.Combine(options.DataDirectory,"grouped-test-regions",Guid.NewGuid().ToString("N"));var manifest=RemoteFixtureFiles.Create(folder,RemoteKind.Terrain);var value=System.Text.Json.JsonSerializer.Deserialize<ScientificSourceManifest>(File.ReadAllText(manifest),WireJson.Options)!;PreferenceStore.WriteAtomic(manifest,value with{Notes="SYNTHETIC grouping fixture region "+i});await science.ImportSourceAsync(manifest);}
    return new{synthetic=true,imported=32};
});
app.Run();
