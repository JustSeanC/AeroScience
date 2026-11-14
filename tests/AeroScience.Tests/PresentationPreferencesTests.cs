using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroScience.Tests;

public sealed class PresentationPreferencesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "aeroscience-p02b", Guid.NewGuid().ToString("N"));
    [Fact] public void OldSettingsAndUnknownFieldsSurviveNarrowSaveAndRestart()
    {
        var store = new PreferenceStore(directory); Directory.CreateDirectory(directory);
        var missionPath=Path.Combine(directory,"retained-missions");
        var original = JsonSerializer.SerializeToNode(new AppPreferences(missionPath, Path.Combine(directory,"retained-science"), true, true, "192.168.1.4", 5000, "retained-community", true))!.AsObject();
        original.Remove("Presentation"); // A genuine pre-P02b settings file has no section.
        original["FutureOwnerSetting"] = new JsonObject { ["value"] = "Zoë 東京 µm $&" };
        File.WriteAllText(store.SettingsPath, original.ToJsonString()); var before = File.ReadAllText(store.SettingsPath);
        Assert.Equal(new PresentationPreferences(), store.Presentation()); Assert.Equal(before, File.ReadAllText(store.SettingsPath));
        var saved = store.SetPresentation(new(0, "dark", "fr-FR")); Assert.Equal(1, saved.Revision);
        Assert.Equal(saved, new PreferenceStore(directory).Presentation());
        var result = JsonNode.Parse(File.ReadAllText(store.SettingsPath))!.AsObject(); result.Remove("Presentation"); Assert.True(JsonNode.DeepEquals(original, result));
        Assert.False(Directory.Exists(missionPath));
    }
    [Fact] public void DefaultsAndAllFiveCandidateIdentitiesRemainHonest()
    {
        Assert.Equal("auto", new PreferenceStore(directory).Presentation().Theme);
        Assert.False(Directory.Exists(directory)); Assert.Equal(5, PresentationText.Locales.Count); Assert.Equal(4, PresentationText.Locales.Count(l => l.Preview));
        foreach (var locale in PresentationText.Locales) Assert.Equal(locale.Id, PresentationText.EffectiveLocale(locale.Id));
        var name = "Zoë 東京 <x> $& µm";
        Assert.Equal("Close " + name, PresentationText.Text("common.closeNamed", new Dictionary<string, object> { ["name"] = name }));
        Assert.Equal("2 saved observations", PresentationText.Text("science.savedObservations", new Dictionary<string, object> { ["count"] = 2 }));
    }
    [Fact] public void ConcurrentStoreInstancesRejectStaleRevisionAndDoNotTouchOtherProfiles()
    {
        var stores = Enumerable.Range(0, 8).Select(_ => new PreferenceStore(directory)).ToArray(); int success = 0;
        Parallel.ForEach(stores, store => { try { store.SetPresentation(new(0, "light", "es-ES")); Interlocked.Increment(ref success); } catch (InvalidOperationException) { } });
        Assert.Equal(1, success); Assert.Equal(1, stores[0].Presentation().Revision);
        var other = new PreferenceStore(Path.Combine(directory, "other-profile")); Assert.Equal(new PresentationPreferences(), other.Presentation()); Assert.False(File.Exists(other.SettingsPath));
        Assert.Throws<ArgumentException>(() => stores[0].SetPresentation(new(null, "auto", "en-US")));
        Assert.Throws<ArgumentException>(() => stores[0].SetPresentation(new(1, "unsafe", "en-US")));
        Assert.Throws<ArgumentException>(() => stores[0].SetPresentation(new(1, "dark", "unsupported")));
    }
    [Fact] public void CorruptFutureAndEscapedProfilesCannotBeOverwritten()
    {
        var store = new PreferenceStore(directory); Directory.CreateDirectory(directory);
        File.WriteAllText(store.SettingsPath, "{broken"); var bytes = File.ReadAllBytes(store.SettingsPath);
        Assert.Throws<JsonException>(() => store.SetPresentation(new(0, "light", "de-DE"))); Assert.Equal(bytes, File.ReadAllBytes(store.SettingsPath));
        store.Save(new(directory, Path.Combine(directory, "science"), Presentation: new(Version: 9)));
        bytes = File.ReadAllBytes(store.SettingsPath); Assert.Equal("auto", store.Presentation().Theme);
        Assert.Throws<InvalidOperationException>(() => store.SetPresentation(new(0, "light", "de-DE"))); Assert.Equal(bytes, File.ReadAllBytes(store.SettingsPath));
        var escaped = new PreferenceStore(Path.Combine(directory, "outside"), Path.Combine(directory, "confined"));
        Assert.Throws<IOException>(() => escaped.SetPresentation(new(0, "dark", "en-US"))); Assert.False(Directory.Exists(escaped.DirectoryPath));
    }
    [Fact] public void NativeMetadataIsOptionalBoundedAndNeverAuthorizesCommands()
    {
        var store = new MissionStore(Path.Combine(directory, "missions.db")); store.Initialize(); var coordinator = new MissionCoordinator(store, 1000);
        string Request() => JsonSerializer.Serialize(new ToolbarCommand(1, Guid.NewGuid(), "state"), WireJson.Options);
        var legacy = new ToolbarCommandHandler(coordinator); Assert.DoesNotContain("\"presentation\"", legacy.Handle(Request()));
        var handler = new ToolbarCommandHandler(coordinator, presentation: () => new(1, PresentationText.CatalogVersion, "pt-BR", CatalogIdentity: PresentationText.CatalogIdentity));
        var reply = JsonSerializer.Deserialize<ToolbarReply>(handler.Handle(Request()), WireJson.Options)!;
        Assert.Equal("pt-BR", reply.Presentation!.Locale); Assert.Contains("presentation-1", reply.Capabilities!); Assert.Null(coordinator.Snapshot().ActiveMission);
        var rejected = new ToolbarCommandHandler(coordinator, authorize: _ => "Validation toolbar source mismatch", presentation: () => new(1, PresentationText.CatalogVersion, "de-DE"));
        reply = JsonSerializer.Deserialize<ToolbarReply>(rejected.Handle(Request()), WireJson.Options)!; Assert.False(reply.Ok); Assert.Null(reply.Presentation);
        var broken = new ToolbarCommandHandler(coordinator, presentation: () => throw new IOException("Profile unavailable"));
        Assert.True(JsonSerializer.Deserialize<ToolbarReply>(broken.Handle(Request()), WireJson.Options)!.Ok);
        Assert.True(Encoding.UTF8.GetByteCount(handler.Handle(Request())) <= 8000);
        Assert.False(JsonSerializer.Deserialize<ToolbarReply>(handler.Handle(new string('é', 600)), WireJson.Options)!.Ok);
    }
    [Fact] public void TightNativePacketsOmitAppearanceBeforeChangingTheExistingIntelligencePayload()
    {
        var store = new MissionStore(Path.Combine(directory, "missions.db")); store.Initialize(); var coordinator = new MissionCoordinator(store, 1000);
        var summary=ToolbarIntelligenceProjection.Create(coordinator.Snapshot(),null) with {TriggerStatus=""};
        var request=JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"state"),WireJson.Options);
        var baseline=new ToolbarCommandHandler(coordinator,intelligence:(_,_)=>summary);
        var overhead=Encoding.UTF8.GetByteCount(baseline.Handle(request));
        summary=summary with {TriggerStatus=new string('x',7990-overhead)};
        var oldReply=JsonSerializer.Deserialize<ToolbarReply>(baseline.Handle(request),WireJson.Options)!;
        var current=new ToolbarCommandHandler(coordinator,intelligence:(_,_)=>summary,presentation:()=>new(1,PresentationText.CatalogVersion,"pt-BR",20,PresentationText.CatalogIdentity));
        var bytes=current.Handle(request);var reply=JsonSerializer.Deserialize<ToolbarReply>(bytes,WireJson.Options)!;
        Assert.True(Encoding.UTF8.GetByteCount(bytes)<=8000);Assert.Null(reply.Presentation);
        Assert.Equal(oldReply.Intelligence!.TriggerStatus,reply.Intelligence!.TriggerStatus);
        Assert.Equal(oldReply.Intelligence.DetailsOmitted,reply.Intelligence.DetailsOmitted);
    }
    [Fact] public void NativeTranslationsUseFiveResourcesWithExactParametersAndBoundedPluralParity()
    {
        var culture=System.Globalization.CultureInfo.CurrentCulture;
        Assert.NotEqual(PresentationText.SourceIdentity,PresentationText.CatalogIdentity);
        var immutable="Zoë 東京 <x> $& µm 2026-11-14T01:02:03.123Z "+new string('a',64);
        var closes=new[]{"Close","Cerrar","Fermer","Schließen","Fechar"};
        for(var index=0;index<PresentationText.Locales.Count;index++)
        {
            var locale=PresentationText.Locales[index];
            Assert.Equal(closes[index],PresentationText.Text("ui.aviationReference.messageClose",locale:locale.Id));
            Assert.Contains(immutable,PresentationText.Text("common.closeNamed",new Dictionary<string,object>{{"name",immutable}},locale.Id));
            Assert.Contains("DELETE SCIENCE DATA",PresentationText.Text("windows.deletion.typePhrase",new Dictionary<string,object>{{"phrase","DELETE SCIENCE DATA"}},locale.Id));
            Assert.Equal(locale.Id=="fr-FR"||locale.Id=="pt-BR"?"one":"other",PresentationText.PluralCategory(locale.Id,0));
            Assert.Equal("one",PresentationText.PluralCategory(locale.Id,1)); Assert.Equal("other",PresentationText.PluralCategory(locale.Id,2));
            Assert.Equal(locale.Id is "es-ES" or "fr-FR" or "pt-BR"?"many":"other",PresentationText.PluralCategory(locale.Id,1000000));
            Assert.Equal(PresentationText.Text("common.messageUnavailable"),PresentationText.Text("science.savedObservations",new Dictionary<string,object>{{"count","1"}},locale.Id));
            Assert.Equal(PresentationText.Text("common.messageUnavailable"),PresentationText.Text("science.savedObservations",new Dictionary<string,object>{{"count",9007199254740992L}},locale.Id));
        }
        Assert.Same(culture,System.Globalization.CultureInfo.CurrentCulture);
        Assert.Equal("en-US",PresentationText.EffectiveLocale("unsupported"));
        foreach(var invalid in new[]{"{}","null","{\"version\":\"wrong\"}","{\"version\":20,\"locale\":\"fr-FR\",\"sourceHash\":\"stale\",\"messages\":{}}"})
        {using var json=JsonDocument.Parse(invalid); Assert.False(PresentationText.IsCompatibleCatalog(json.RootElement,"fr-FR"));}
    }
    [Fact] public void ReadOnlyCardPollCarriesVerifiedAppearanceWhenTheFullPacketMustRetainItsScienceInstead()
    {
        var store=new MissionStore(Path.Combine(directory,"missions.db"));store.Initialize();var coordinator=new MissionCoordinator(store,1000);
        var flight=coordinator.Start(new StartMissionRequest("Native source remains original",ArmScience:true));
        var summary=ToolbarIntelligenceProjection.Create(coordinator.Snapshot(),null) with{TriggerStatus=""};
        var plain=new ToolbarCommandHandler(coordinator,intelligence:(_,_)=>summary);
        var stateRequest=JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"state"),WireJson.Options);
        summary=summary with{TriggerStatus=new string('x',7990-Encoding.UTF8.GetByteCount(plain.Handle(stateRequest)))};
        var handler=new ToolbarCommandHandler(coordinator,intelligence:(_,_)=>summary,presentation:()=>new(1,PresentationText.CatalogVersion,"fr-FR",3,PresentationText.CatalogIdentity));
        var state=JsonSerializer.Deserialize<ToolbarReply>(handler.Handle(stateRequest),WireJson.Options)!;
        Assert.Null(state.Presentation);Assert.Equal(summary.TriggerStatus,state.Intelligence!.TriggerStatus);
        using var json=JsonDocument.Parse(handler.Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"research-state",MissionId:flight.Id),WireJson.Options)));
        var reply=json.RootElement;Assert.Equal("fr-FR",reply.GetProperty("presentation").GetProperty("locale").GetString());Assert.Equal(PresentationText.CatalogIdentity,reply.GetProperty("presentation").GetProperty("catalogIdentity").GetString());
        Assert.Equal(state.EngineInstance,reply.GetProperty("engineInstance").GetGuid());Assert.Equal(flight.Id,reply.GetProperty("research").GetProperty("flightId").GetGuid());
        Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);Assert.True(Encoding.UTF8.GetByteCount(reply.GetRawText())<=8000);
        Assert.Empty(coordinator.ScienceCollection(flight.Id).Sessions);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

public sealed partial class ApiTests
{
    [Fact] public async Task PresentationSettingsUseSessionCsrfLocalPcAndRevisionWithoutChangingScience()
    {
        const string path = "/api/settings/presentation";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        await Pair(client); var store = factory.Services.GetRequiredService<PreferenceStore>();
        var coordinator=factory.Services.GetRequiredService<MissionCoordinator>();
        var flight=coordinator.Start(new StartMissionRequest("Synthetic preference preservation",ArmScience:true));
        Assert.NotNull(coordinator.ScienceCollection(flight.Id).State);
        var phase=coordinator.ScienceCollection(flight.Id).State!.Phase;
        var before = await client.GetFromJsonAsync<JsonElement>("/api/state");
        var response = await client.PostAsJsonAsync(path, new { expectedRevision = 0, theme = "dark", locale = "fr-FR" }); response.EnsureSuccessStatusCode();
        var view = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(view.GetProperty("canWrite").GetBoolean()); Assert.Equal("fr-FR", view.GetProperty("effectiveLocale").GetString());
        Assert.Equal(PresentationText.CatalogIdentity, view.GetProperty("catalogIdentity").GetString());
        Assert.Equal("fr-FR", store.Presentation().Locale); Assert.Equal(1, store.Presentation().Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { expectedRevision = 0, theme = "light", locale = "en-US" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { expectedRevision = 1, theme = "light", locale = "en-US", missionDirectory = "hostile" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { theme = "light", locale = "en-US" })).StatusCode);
        var csrf = client.DefaultRequestHeaders.GetValues("X-AeroScience-CSRF").Single(); client.DefaultRequestHeaders.Remove("X-AeroScience-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new { expectedRevision = 1, theme = "light", locale = "en-US" })).StatusCode); client.DefaultRequestHeaders.Add("X-AeroScience-CSRF", csrf);
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "https://wrong.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new { expectedRevision = 1, theme = "light", locale = "en-US" })).StatusCode);
        var access = factory.Services.GetRequiredService<AccessControl>(); var paired = access.Pair(false, access.PairingCode)!;
        foreach (var method in new[] { "GET", "POST" })
        {
            var body = Encoding.UTF8.GetBytes("{\"expectedRevision\":1,\"theme\":\"light\",\"locale\":\"en-US\"}");
            var result = await factory.Server.SendAsync(context => {
                context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new KnownRequestBody());
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.42"); context.Request.Scheme = "http"; context.Request.Host = new("localhost", 5080);
                context.Request.Path = path; context.Request.Method = method; context.Request.ContentType = "application/json";
                context.Request.Headers.Origin = "http://localhost:5080"; context.Request.Headers.Cookie = access.SessionCookieName + "=" + paired.Token; context.Request.Headers["X-AeroScience-CSRF"] = paired.Csrf;
                if (method == "POST") { context.Request.Body = new MemoryStream(body); context.Request.ContentLength = body.Length; }
            });
            Assert.Equal(method == "GET" ? 200 : 403, result.Response.StatusCode);
            if (method == "GET") { using var remote = await JsonDocument.ParseAsync(result.Response.Body); Assert.False(remote.RootElement.GetProperty("canWrite").GetBoolean()); }
        }
        Assert.Equal(1, store.Presentation().Revision); var after = await client.GetFromJsonAsync<JsonElement>("/api/state");
        Assert.Equal(before.GetProperty("activeMission").GetProperty("id").GetString(), after.GetProperty("activeMission").GetProperty("id").GetString());
        Assert.Equal(phase,coordinator.ScienceCollection(flight.Id).State!.Phase);
        Assert.Equal("guided", store.Presentation().Experience); Assert.Equal("balanced", store.Presentation().Density);
    }
}
