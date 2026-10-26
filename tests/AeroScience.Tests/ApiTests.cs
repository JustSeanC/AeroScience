using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using AeroScience.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace AeroScience.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "aeroscience-api", Guid.NewGuid().ToString());
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;
    private readonly TaskCompletionSource stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration stoppedRegistration;
    public ApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services => services.AddSingleton<ITelemetryProvider>(new SimulatedTelemetryProvider(250)));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["AeroScience:DataDirectory"] = directory, ["AeroScience:SamplingIntervalMs"] = "250" }));
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:5080") });
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5080");
    }
    [Fact] public async Task HealthReportsTheDevelopmentAssemblyVersionWithoutStartingRecording()
    {
        using var health=JsonDocument.Parse(await client.GetStringAsync("/api/health"));
        Assert.Equal("0.7.0",health.RootElement.GetProperty("version").GetString());
        Assert.Equal(new Version(0,7,0,0),typeof(EngineApplication).Assembly.GetName().Version);
        await Pair(client);
        using var missions=JsonDocument.Parse(await client.GetStringAsync("/api/missions"));
        Assert.Equal(0,missions.RootElement.GetArrayLength());
    }
    [Fact] public async Task PersonalResearchUsesExistingSessionCsrfAndRejectsUnsupportedCardInput()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/research/cards")).StatusCode);
        await Pair(client);
        var input = new SaveResearchCardRequest("API personal card", new(120,5,3000,100,30));
        var response = await client.PostAsJsonAsync("/api/research/cards", input, WireJson.Options); response.EnsureSuccessStatusCode();
        var card = (await response.Content.ReadFromJsonAsync<PersonalResearchCard>(WireJson.Options))!;
        var planResponse = await client.PostAsJsonAsync($"/api/research/cards/{card.Id}/plan", new ResearchCardReference(card.Id,card.Revision), WireJson.Options);
        planResponse.EnsureSuccessStatusCode(); var plan = (await planResponse.Content.ReadFromJsonAsync<MissionPlan>(WireJson.Options))!;
        Assert.Equal(new(card.Id,1), plan.ResearchCard); Assert.DoesNotContain(plan.Instruments!, c => c.Enabled);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/research/cards", input with { FlightRegime = input.FlightRegime with { HoldSeconds = 0 } }, WireJson.Options)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/research/cards", input, WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/research/attempts", new ResearchAttemptRequest(Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow), WireJson.Options)).StatusCode);
    }
    [Fact] public async Task PersonalCardOperationsKeepHostAuthorityCsrfAndRequiredCommandIdentities()
    {
        var flight=Guid.NewGuid();
        foreach(var path in new[]{ $"/api/missions/{flight}/research-operations",$"/api/missions/{flight}/research-attempts" })
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(path)).StatusCode);
        await Pair(client);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/research/control",new{flightId=flight,requestId=Guid.NewGuid(),action="activate",cardId=Guid.NewGuid()},WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync($"/api/missions/{flight}/research-attempts?offset=-1")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/research/control",new ResearchCardControl(1,flight,Guid.NewGuid(),0,ResearchCardAction.Activate,Guid.NewGuid(),null),WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/api/research/recorded-attempts/{Guid.NewGuid()}/evaluate",new ResearchEvaluationCommand(Guid.NewGuid()),WireJson.Options)).StatusCode);
    }
    [Fact] public async Task ResearchReviewUsesAuthenticatedBoundedReadsAndCsrfForEvidenceComparison()
    {
        var id=Guid.NewGuid();foreach(var path in new[]{ $"/api/research/cards/{id}/overview",$"/api/research/cards/{id}/history",$"/api/research/recorded-attempts/{id}" })
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(path)).StatusCode);
        await Pair(client);var store=factory.Services.GetRequiredService<MissionStore>();var card=store.SaveResearchCard(new("API overview",new(120,5,3000,100,2)));
        var overview=await client.GetFromJsonAsync<ResearchOverview>($"/api/research/cards/{card.Id}/overview",WireJson.Options);Assert.Equal(0,overview!.EvaluatedAttempts);Assert.False(overview.CurrentRevisionDemonstrated);
        var page=await client.GetFromJsonAsync<ResearchHistoryPage>($"/api/research/cards/{card.Id}/history",WireJson.Options);Assert.Empty(page!.Items);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync($"/api/research/cards/{card.Id}/history?offset=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/research/cards/{card.Id}/comparison",new ResearchComparisonRequest([]),WireJson.Options)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync($"/api/research/cards/{card.Id}/comparison",new ResearchComparisonRequest([]),WireJson.Options)).StatusCode);
    }
    [Fact] public async Task ResearchStarterPreparationUsesSessionCsrfAndNeverStartsOrSelectsRecording()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/research/starters")).StatusCode);await Pair(client);
        var choices=await client.GetFromJsonAsync<ResearchStarter[]>("/api/research/starters",WireJson.Options);Assert.Equal("flight-regime",Assert.Single(choices!).Id);
        var input=new ResearchStarterRequest(1,Guid.NewGuid(),"flight-regime",ResearchEntryIntent.FlyNow);
        var response=await client.PostAsJsonAsync("/api/research/starters/prepare",input,WireJson.Options);response.EnsureSuccessStatusCode();var prepared=(await response.Content.ReadFromJsonAsync<PreparedResearchStarter>(WireJson.Options))!;
        var retry=await client.PostAsJsonAsync("/api/research/starters/prepare",input,WireJson.Options);retry.EnsureSuccessStatusCode();Assert.True((await retry.Content.ReadFromJsonAsync<PreparedResearchStarter>(WireJson.Options))!.Reused);
        var state=await client.GetFromJsonAsync<EngineSnapshot>("/api/state",WireJson.Options);Assert.Null(state!.ActiveMission);Assert.Null(state.SelectedPlanId);
        Assert.Equal(prepared.Card.Id,prepared.Plan.ResearchCard!.Id);client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/research/starters/prepare",input,WireJson.Options)).StatusCode);
    }
    [Fact] public async Task AviationMetadataRequiresSessionAndCsrfAndKeepsReadsOfflineAndBounded()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/aviation/catalog/vfr")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/aviation/downloads")).StatusCode);
        await Pair(client);
        var page = await client.GetFromJsonAsync<AviationReferencePage>("/api/aviation/catalog/vfr", WireJson.Options);
        Assert.Equal("Not searched", page!.Status); Assert.Empty(page.Charts); Assert.Null(page.Cache);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/aviation/catalog/vfr?offset=50000")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/aviation/catalog/arbitrary-provider")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/aviation/downloads?offset=-1")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/aviation/configuration", new AviationConsentRequest(true), WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/aviation/catalog/tfr/refresh", new {}, WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/aviation/downloads/preview", new AviationPreviewRequest("vfr", "invalid", 1048576, 1048576), WireJson.Options)).StatusCode);
        using var missions = JsonDocument.Parse(await client.GetStringAsync("/api/missions"));
        Assert.Equal(0, missions.RootElement.GetArrayLength());
    }
    [Fact]public async Task LibraryCatalogAndJobControlsRetainSessionCsrfAndInputBoundaries()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/science/library/jobs")).StatusCode);await Pair(client);
        var providers=await client.GetFromJsonAsync<ScientificCatalogProvider[]>("/api/science/library/providers",WireJson.Options);Assert.Equal(new[]{"copernicus","landsat","nasa-classic","nasa-emit","nasa-ng","sentinel-2","usgs-3dep"},providers!.Select(p=>p.Id).Order().ToArray());
        var q=new ScientificCatalogQuery("landsat",new(-75,40,-74.98,40.02),CachedOnly:true);
        var page=await client.PostAsJsonAsync("/api/science/library/catalog",q,WireJson.Options);page.EnsureSuccessStatusCode();Assert.Equal("Not searched",(await page.Content.ReadFromJsonAsync<ScientificCatalogPage>(WireJson.Options))!.Status);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/science/library/preview",new ScientificLibrarySelection(99,[],[]),WireJson.Options)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/science/library/catalog",q,WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/science/hyperspectral/nasa",new NasaSelectionRequest("nasa-classic","G1-ORNL_CLOUD",new string('a',64)),WireJson.Options)).StatusCode);
    }
    [Fact]public async Task EarthdataConnectionUsesExistingSessionCsrfAndReturnsStatusOnly()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/science/earthdata")).StatusCode);
        await Pair(client);using var status=JsonDocument.Parse(await client.GetStringAsync("/api/science/earthdata"));
        Assert.Equal(new[]{"available","connected","message"},status.RootElement.EnumerateObject().Select(p=>p.Name).Order().ToArray());Assert.False(status.RootElement.GetProperty("connected").GetBoolean());
        var request=new NasaDownloadRequest(new("nasa-emit","G1-LPCLOUD",new string('a',64)),10485760);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/science/library/nasa/preview",request,WireJson.Options)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/science/earthdata/disconnect",new{})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/desktop/earthdata-connect",new{})).StatusCode);
    }
    private async Task Pair(HttpClient display)
    {
        var response = await display.PostAsJsonAsync("/api/session", new { code = (string?)null }); response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        display.DefaultRequestHeaders.Add("X-AeroScience-CSRF", data.GetProperty("csrf").GetString());
    }
    [Fact]public async Task ExternalSourceDeletionRequiresCsrfAndTrustedLocalAccessEvenWithAnExactReview()
    {
        await Pair(client);var science=factory.Services.GetRequiredService<ScienceStore>();
        var original=RemoteFixtureFiles.Create(Path.Combine(directory,"synthetic-owned-source"),RemoteKind.Terrain);var source=await science.ImportSourceAsync(original);
        var preview=await client.PostAsJsonAsync("/api/science/sources/deletion/preview",new SourceDeletionRequest([source.Id]),WireJson.Options);preview.EnsureSuccessStatusCode();var reviewed=(await preview.Content.ReadFromJsonAsync<SourceDeletionPreview>(WireJson.Options))!;Assert.Equal(1,reviewed.EligibleSources);
        var execute=new SourceDeletionExecution(reviewed.Id,reviewed.SelectionHash);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/science/sources/deletion/execute",execute,WireJson.Options)).StatusCode);
        var access=factory.Services.GetRequiredService<AccessControl>();var paired=access.Pair(false,access.PairingCode)!;
        var payload=JsonSerializer.SerializeToUtf8Bytes(execute,WireJson.Options);
        var remote=await factory.Server.SendAsync(context=>{
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new KnownRequestBody());context.Connection.RemoteIpAddress=IPAddress.Parse("192.168.1.40");context.Request.Scheme="http";context.Request.Host=new("localhost",5080);context.Request.Method="POST";context.Request.Path="/api/science/sources/deletion/execute";context.Request.ContentType="application/json";context.Request.ContentLength=payload.Length;context.Request.Body=new MemoryStream(payload);context.Request.Headers.Origin="http://localhost:5080";context.Request.Headers.Cookie=access.SessionCookieName+"="+paired.Token;context.Request.Headers["X-AeroScience-CSRF"]=paired.Csrf;
        });
        Assert.Equal(403,remote.Response.StatusCode);Assert.Single(science.Sources());
    }
    [Fact]public async Task RetainedSourceApiPagesAndSearchesTheFullIndexWithLowercaseCategories()
    {
        await Pair(client);var science=factory.Services.GetRequiredService<ScienceStore>();var input=RemoteFixtureFiles.Create(Path.Combine(directory,"page-fixture"),RemoteKind.Terrain);var manifest=ScienceStore.ReadSourceManifest(input);
        for(var n=0;n<4;n++)await science.ImportSourceAsync(manifest with{SceneId="page-"+n},Path.GetDirectoryName(input)!);
        var first=await client.GetFromJsonAsync<JsonElement>("/api/science/sources/page?kind=terrain&limit=2",WireJson.Options);Assert.Equal(4,first.GetProperty("total").GetInt64());Assert.Equal(2,first.GetProperty("items").GetArrayLength());
        var next=first.GetProperty("next").GetInt64();var through=first.GetProperty("through").GetInt64();var second=await client.GetFromJsonAsync<JsonElement>($"/api/science/sources/page?kind=terrain&limit=2&after={next}&through={through}",WireJson.Options);Assert.Equal(2,second.GetProperty("items").GetArrayLength());Assert.Equal(JsonValueKind.Null,second.GetProperty("next").ValueKind);
        var collection=await client.GetFromJsonAsync<JsonElement>("/api/science/source-collections?kind=terrain",WireJson.Options);Assert.Equal(4,collection.GetProperty("items")[0].GetProperty("regions").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/science/sources/page?kind=invented")).StatusCode);Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/science/sources/page?limit=1000")).StatusCode);
    }
    [Fact]public async Task CollectedScienceDeletionRequiresReviewedHashAndCsrfAndPreservesRawRecording()
    {
        await Pair(client);var coordinator=factory.Services.GetRequiredService<MissionCoordinator>();var missions=factory.Services.GetRequiredService<MissionStore>();var science=factory.Services.GetRequiredService<ScienceStore>();
        // Isolate deterministic input from the hosted simulator fixture's reconnect loop.
        foreach(var worker in factory.Services.GetServices<IHostedService>().OfType<TelemetryWorker>())await worker.StopAsync(default);
        var first=SimulatedTelemetryProvider.Generate(DateTimeOffset.UtcNow,0);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic fixture",first.TimestampUtc),first,new(first.TimestampUtc,15,29,5,90,.002)));
        var mission=coordinator.Start(new("Synthetic deletion API fixture",Instruments:[new("ambient-temperature",true)]));var frame=first with{TimestampUtc=first.TimestampUtc.AddSeconds(1)};coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic fixture",frame.TimestampUtc),frame,new(frame.TimestampUtc,15,29,5,90,.002)));coordinator.End(mission.Id);
        var samples=missions.StreamSamples(mission.Id).Count();Assert.True(samples>0);Assert.NotEmpty(science.StreamObservations(mission.Id));
        var response=await client.PostAsJsonAsync("/api/science/collected/deletion/preview",new CollectedScienceDeletionRequest([mission.Id]),WireJson.Options);response.EnsureSuccessStatusCode();var preview=(await response.Content.ReadFromJsonAsync<CollectedScienceDeletionPreview>(WireJson.Options))!;Assert.Empty(preview.Blockers);
        var execute=new SourceDeletionExecution(preview.Id,preview.SelectionHash);client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/science/collected/deletion/execute",execute,WireJson.Options)).StatusCode);Assert.NotEmpty(science.StreamObservations(mission.Id));
        await Pair(client);var deleted=await client.PostAsJsonAsync("/api/science/collected/deletion/execute",execute,WireJson.Options);deleted.EnsureSuccessStatusCode();var result=(await deleted.Content.ReadFromJsonAsync<CollectedScienceDeletionResult>(WireJson.Options))!;Assert.True(result.State=="Completed",result.Warning);Assert.Empty(science.StreamObservations(mission.Id));Assert.True(missions.ScienceDeleted(mission.Id));Assert.Empty(missions.InstrumentDiagnostics(mission.Id));Assert.Equal(samples,missions.StreamSamples(mission.Id).Count());
    }
    private sealed class KnownRequestBody:Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature {public bool CanHaveBody=>true;}
    [Fact]public async Task MissionStartReadinessIsAuthenticatedReadOnlyAndChecksPlanRevisionAtStartup()
    {
        var store=factory.Services.GetRequiredService<MissionStore>();var plans=factory.Services.GetRequiredService<ScientificPlanData>();
        var automatic=factory.Services.GetRequiredService<AutomaticScientificData>();automatic.Pause(true);
        var plan=store.SavePlan(PreparationScopeTests.LongPlan());
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync($"/api/plans/{plan.Id}/preparation/start-readiness")).StatusCode);
        await Pair(client);var unknown=await client.GetFromJsonAsync<MissionStartReadiness>($"/api/plans/{plan.Id}/preparation/start-readiness",WireJson.Options);Assert.Equal("NotAssessed",unknown!.Status);Assert.Null(unknown.Preparation);Assert.Empty(automatic.Snapshot());
        plans.Prepare(plan,true);var ready=await client.GetFromJsonAsync<MissionStartReadiness>($"/api/plans/{plan.Id}/preparation/start-readiness",WireJson.Options);Assert.Equal(408,ready!.Preparation!.Awaiting);Assert.All(ready.Instruments!,i=>Assert.Equal(204,i.Total));
        var revision=await client.GetFromJsonAsync<JsonElement>($"/api/plans/{plan.Id}/revision");Assert.Equal(ready.PlanRevision,revision.GetProperty("planRevision").GetString());
        var coordinator=factory.Services.GetRequiredService<MissionCoordinator>();coordinator.SelectPlan(plan.Id);
        var request=new StartMissionRequest("Incomplete API flight",PlanId:plan.Id,ArmScience:true,ExpectedPlanRevision:ready.PlanRevision,CheckSelectedPlan:true);
        var bad=await client.PostAsJsonAsync("/api/missions",request with{ExpectedPlanRevision=new string('0',64)},WireJson.Options);Assert.Equal(HttpStatusCode.Conflict,bad.StatusCode);Assert.Empty(store.List(20));
        var access=factory.Services.GetRequiredService<AccessControl>();var paired=access.Pair(false,access.PairingCode)!;
        var remote=await factory.Server.SendAsync(context=>{context.Connection.RemoteIpAddress=IPAddress.Parse("192.168.1.40");context.Request.Scheme="http";context.Request.Host=new("localhost",5080);context.Request.Method="GET";context.Request.Path=$"/api/plans/{plan.Id}/preparation/start-readiness";context.Request.Headers.Cookie=access.SessionCookieName+"="+paired.Token;});Assert.Equal(200,remote.Response.StatusCode);
        var created=await client.PostAsJsonAsync("/api/missions",request,WireJson.Options);Assert.Equal(HttpStatusCode.Created,created.StatusCode);Assert.Single(store.List(20));Assert.Equal(408,plans.StartReadiness(plan).Preparation!.Awaiting);
    }
    [Fact] public async Task MissionIntelligenceUsesAuthenticatedReadOnlyEvidenceAndBoundedPages()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/mission-intelligence")).StatusCode);
        await Pair(client);var before=await client.GetFromJsonAsync<MissionIntelligence>("/api/mission-intelligence",WireJson.Options);
        Assert.Null(before!.Flight.ActiveMission);Assert.Null(before.Performance);Assert.Null(before.Collection);
        var response=await client.PostAsJsonAsync("/api/missions",new StartMissionRequest("Intelligence API fixture",Instruments:[new("ambient-temperature",true)],ArmScience:true),WireJson.Options);response.EnsureSuccessStatusCode();
        var flight=(await response.Content.ReadFromJsonAsync<Mission>(WireJson.Options))!;
        var view=await client.GetFromJsonAsync<MissionIntelligence>("/api/mission-intelligence",WireJson.Options);
        Assert.Equal(flight.Id,view!.Flight.ActiveMission!.Id);Assert.Equal(CollectionPhase.Armed,view.Collection!.State!.Phase);
        Assert.Empty(view.Collection.Sessions);Assert.Contains(view.Collection.Actions,a=>a.Action==CollectionAction.StartData&&a.Available);
        Assert.Empty(await client.GetFromJsonAsync<ScienceCollectionSession[]>($"/api/missions/{flight.Id}/collection/sessions?limit=1&offset=0",WireJson.Options)??[]);
        Assert.NotNull(await client.GetFromJsonAsync<CollectionEvent[]>($"/api/missions/{flight.Id}/collection/events?limit=1&after=0",WireJson.Options));
        Assert.Equal(flight.Id,(await client.GetFromJsonAsync<EngineSnapshot>("/api/state",WireJson.Options))!.ActiveMission!.Id);
        (await client.PostAsJsonAsync($"/api/missions/{flight.Id}/end",new{})).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task CalibrationDefinitionsUseExistingSessionCsrfAndValidationWithoutChangingRawContracts()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/instruments/calibration")).StatusCode);
        await Pair(client);
        var catalog = await client.GetFromJsonAsync<JsonElement>("/api/instruments/calibration");
        Assert.Equal(ScienceCatalog.Definitions.Length, catalog.GetProperty("instruments").GetArrayLength());
        Assert.True(catalog.GetProperty("canConfigure").GetBoolean());
        var definition = CalibrationTests.Definition();
        var saved = await client.PostAsJsonAsync("/api/instruments/calibration", definition, WireJson.Options); saved.EnsureSuccessStatusCode();
        var result = (await saved.Content.ReadFromJsonAsync<CalibrationDefinitionRevision>(WireJson.Options))!;
        Assert.Equal(EvidenceContracts.ContentHash(definition), result.Sha256);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/instruments/calibration", definition with { Version = 99 }, WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/instruments/calibration", definition with { Limitations = ["Replace old revision"] }, WireJson.Options)).StatusCode);
        var invalid = definition with { InstrumentId = "thermal-imager" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/instruments/calibration", invalid, WireJson.Options)).StatusCode);
        (await client.PostAsJsonAsync("/api/instruments", new[] { new InstrumentConfiguration("ambient-temperature", true, Calibration: definition) }, WireJson.Options)).EnsureSuccessStatusCode();
        var configuration = await client.GetFromJsonAsync<InstrumentConfiguration[]>("/api/instruments/configuration", WireJson.Options);
        Assert.Equal(result.Sha256, EvidenceContracts.ContentHash(configuration!.Single(c => c.Enabled).Calibration!));
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/instruments/calibration", definition with { Revision = 2 }, WireJson.Options)).StatusCode);
    }

    [Fact]
    public async Task GeographicConfigurationAndDraftExportsRetainSessionAndCsrfProtection()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/maps/client")).StatusCode);
        await Pair(client);
        var configuration = await client.GetFromJsonAsync<JsonElement>("/api/maps/settings");
        Assert.Equal("streets", configuration.GetProperty("configuration").GetProperty("preferences").GetProperty("layer").GetString());
        Assert.True(configuration.GetProperty("configuration").GetProperty("requiresPrivacyChoice").GetBoolean());
        Assert.False(configuration.GetProperty("configuration").TryGetProperty("protectedKey", out _));
        Assert.False(configuration.GetProperty("configuration").TryGetProperty("publicApiKey", out _));
        var offline = await client.GetFromJsonAsync<JsonElement>("/api/maps/client"); Assert.Equal(0, offline.GetProperty("tiles").GetArrayLength());
        var plan = new MissionPlan(Guid.NewGuid(), "Draft <&>", Waypoints: [new("Custom", 0, 179.99)], CruisingAltitudeFeet: 7500);
        var exported = await client.PostAsJsonAsync("/api/routes/export", new RouteExportRequest(plan), WireJson.Options);
        exported.EnsureSuccessStatusCode();
        var result = await exported.Content.ReadFromJsonAsync<RouteExportResult>(WireJson.Options);
        Assert.Contains("<AppVersionMajor>12</AppVersionMajor>", result!.Content);
        Assert.Contains("Draft &lt;&amp;&gt;", result.Content);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plans/{plan.Id}")).StatusCode); // preview never silently saves a draft
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/routes/export", new RouteExportRequest(plan), WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/maps/settings", new MapSettingsRequest(false), WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/maps/layer", new MapLayerRequest("offline"))).StatusCode);
    }

    [Fact]
    public async Task ResearchEndpointsPreserveSnapshotsExportsAndRequestProtection()
    {
        await Pair(client);
        var plan = new MissionPlan(Guid.NewGuid(), "Offline plan", MissionTemplateKind.ObservationFlight,
            Waypoints: [new("A", 34, -117), new("B", 35, -118)], Instruments: []);
        var saved = await client.PostAsJsonAsync("/api/plans", plan, WireJson.Options); saved.EnsureSuccessStatusCode();
        var start = await client.PostAsJsonAsync("/api/missions", new StartMissionRequest("Planned", PlanId: plan.Id), WireJson.Options); start.EnsureSuccessStatusCode();
        var mission = (await start.Content.ReadFromJsonAsync<Mission>(WireJson.Options))!;
        (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/markers", new MarkerRequest(Notes: "Actual contextual marker"), WireJson.Options)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/end", new { })).EnsureSuccessStatusCode();
        var review = await client.GetFromJsonAsync<JsonElement>($"/api/missions/{mission.Id}/review");
        Assert.Equal("Offline plan", review.GetProperty("research").GetProperty("planSnapshot").GetProperty("name").GetString());
        foreach (var format in new[] { "flight.csv", "events.csv", "gpx", "kml", "report.txt" })
            (await client.GetAsync($"/api/missions/{mission.Id}/export/{format}")).EnsureSuccessStatusCode();
        foreach (var route in new[] { "/api/data/clear-flight", "/api/science/clear" })
            Assert.Contains((await client.PostAsJsonAsync(route, new { })).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/plans", plan, WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/desktop/data-management", new { })).StatusCode);
    }

    [Fact]
    public async Task ApiRecordsMissionStreamsStateAndRetainsHistory()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
        await Pair(client);
        var response = await client.PostAsJsonAsync("/api/missions", new { name = "API survey" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var mission = (await response.Content.ReadFromJsonAsync<Mission>(WireJson.Options))!;
        using var streamResponse = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        using var streamReader = new StreamReader(await streamResponse.Content.ReadAsStreamAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal("event: state", await streamReader.ReadLineAsync(timeout.Token));
        var line = await streamReader.ReadLineAsync(timeout.Token);
        var state = JsonSerializer.Deserialize<EngineSnapshot>(line![6..], WireJson.Options)!;
        Assert.Equal(mission.Id, state.ActiveMission!.Id);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var current = await client.GetFromJsonAsync<EngineSnapshot>("/api/state", WireJson.Options);
            if (current!.ActiveMission!.SampleCount >= 2) break;
            await Task.Delay(100);
        }
        var end = await client.PostAsJsonAsync($"/api/missions/{mission.Id}/end", new { }); end.EnsureSuccessStatusCode();
        var detail = await client.GetFromJsonAsync<MissionDetail>($"/api/missions/{mission.Id}", WireJson.Options);
        Assert.Equal(MissionStatus.Completed, detail!.Mission.Status); Assert.True(detail.Mission.SampleCount >= 2);
        var samples = await client.GetFromJsonAsync<TelemetryFrame[]>($"/api/missions/{mission.Id}/telemetry", WireJson.Options);
        Assert.Equal(detail.Mission.SampleCount, samples!.Length);
    }

    [Fact]
    public async Task DisplaysShareStateAndMutationRequiresCsrfAndSameOrigin()
    {
        await Pair(client);
        using var second = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:5080") });
        second.DefaultRequestHeaders.Add("Origin", "http://localhost:5080"); await Pair(second);
        var create = await client.PostAsJsonAsync("/api/missions", new { name = "Shared mission", mode = "realistic" }); create.EnsureSuccessStatusCode();
        var mission = (await create.Content.ReadFromJsonAsync<Mission>(WireJson.Options))!;
        var state = await second.GetFromJsonAsync<EngineSnapshot>("/api/state", WireJson.Options);
        Assert.Equal(mission.Id, state!.ActiveMission!.Id);
        Assert.Equal(ScienceMode.Realistic, state.ActiveMission.Mode);
        Assert.Equal(ScienceMode.Realistic, (await second.GetFromJsonAsync<Mission[]>("/api/missions", WireJson.Options))!.Single().Mode);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync("/api/missions", new { name = "Duplicate" })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/end", new { })).StatusCode);
        second.DefaultRequestHeaders.Remove("Origin"); second.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await second.PostAsJsonAsync($"/api/missions/{mission.Id}/end", new { })).StatusCode);
        using var hostile = new HttpRequestMessage(HttpMethod.Get, "/api/health"); hostile.Headers.Host = "evil.example:5080";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(hostile)).StatusCode);
    }

    [Fact]
    public async Task SharedBrowserSessionKeepsExistingCsrfValidAndUnitsPresetsRemainProtected()
    {
        await Pair(client);
        var before = client.DefaultRequestHeaders.GetValues("X-AeroScience-CSRF").Single();
        var repeated = await client.PostAsJsonAsync("/api/session", new { code = (string?)null });
        var session = await repeated.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(before, session.GetProperty("csrf").GetString());
        var units = ScienceUnits.Scientific with { Temperature = "kelvin" }; var changed = await client.PostAsJsonAsync("/api/display/units", units, WireJson.Options); changed.EnsureSuccessStatusCode();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/display/units"); Assert.Equal("kelvin", settings.GetProperty("preferences").GetProperty("temperature").GetString());
        var preset = new InstrumentPreset(Guid.Empty, "API independent", null, [new("ambient-wind", true, 2000)]);
        var created = await client.PostAsJsonAsync("/api/presets", preset, WireJson.Options); created.EnsureSuccessStatusCode(); var saved = await created.Content.ReadFromJsonAsync<InstrumentPreset>(WireJson.Options); Assert.NotEqual(Guid.Empty, saved!.Id);
        Assert.Single((await client.GetFromJsonAsync<InstrumentPreset[]>("/api/presets", WireJson.Options))!);
        client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF"); Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/presets", preset, WireJson.Options)).StatusCode);
    }

    [Fact]
    public void StartedApiOwnsDirectoryUntilAwaitedShutdown()
    {
        Assert.Throws<IOException>(()=>{using var duplicate=new DataDirectoryLease(new EngineOptions{DataDirectory=directory});});
        // DisposeAsync must observe ApplicationStopped, join disposal, and reacquire
        // the same exclusive lease before deleting this test's directory.
    }

    public async Task InitializeAsync()
    {
        var lifetime=factory.Services.GetRequiredService<IHostApplicationLifetime>();
        stoppedRegistration=lifetime.ApplicationStopped.Register(()=>stopped.TrySetResult());
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration=lifetime.ApplicationStarted.Register(()=>started.TrySetResult());
        using var response=await client.GetAsync("/api/health");response.EnsureSuccessStatusCode();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);
    }
    public async Task DisposeAsync()
    {
        var services=factory.Services;
        var ownership=services.GetRequiredService<DataDirectoryLease>();
        client.Dispose(); await factory.DisposeAsync();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));stoppedRegistration.Dispose();
        // The deferred TestServer host can finish before its application provider's
        // asynchronous singleton cleanup. Join the actual provider before deleting files.
        if(services is IAsyncDisposable owned)await owned.DisposeAsync();
        // DeferredHost/provider disposal can already be in progress: a second provider
        // DisposeAsync may return before the first cleanup completes. Observe the actual
        // lease's release, without releasing it early or retrying around a live writer.
        await ownership.Released.WaitAsync(TimeSpan.FromSeconds(10));
        // Ownership is released before cleanup; do not mask a live engine with delete retries.
        using(var lease=new DataDirectoryLease(new EngineOptions{DataDirectory=directory})) { }
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
