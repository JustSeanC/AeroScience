using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Xunit;

namespace AeroScience.Tests;

public sealed class GeographicIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-geographic-" + Guid.NewGuid());
    private readonly MissionStore store;
    public GeographicIntegrationTests() { var settings=new StorageManager(new PreferenceStore(root)).Initialize();store=new(Path.Combine(settings.MissionDirectory,"missions.db"));store.Initialize(); }
    private static MissionPlan Plan(int count=3) => new(Guid.NewGuid(),"Research <&> route",Departure:"KJFK",Destination:"",Description:"A & B",CruisingAltitudeFeet:8500,
        Waypoints:Enumerable.Range(0,count).Select(i=>new ResearchWaypoint("=SURV<"+i+">", 36.0716666667+i*.0001,-75.9521666667+i*.0001,i==0?-250:null,Instructions:"Observe <&> safely")).ToArray(),SurveyAreas:[new("Area <&>",[new(36,-76),new(36.1,-76),new(36.1,-75.9)])]);
    [Theory]
    [InlineData("decimal",1e-8)] [InlineData("ddm",2e-6)] [InlineData("dms",3e-7)] [InlineData("icao",.008334)] [InlineData("simbrief",.000139)]
    public void CoordinateFormatsRoundTripHemisphereCarriesAndPolarDatelinePositions(string format,double tolerance)
    {
        foreach(var latitude in new[]{true,false})foreach(var n in new[]{-0.0,0,36.0716666667,-36.0716666667,89.999999999,-89.999999999,latitude?90:180,latitude?-90:-180})
        {
            var text=GeographicCoordinates.Angle(n,latitude,format);var decoded=GeographicCoordinates.ParseAngle(text,latitude,format);
            Assert.InRange(Math.Abs(n-decoded),0,tolerance);if(format!="decimal")Assert.DoesNotContain("60.000",text);
        }
        Assert.Equal("N3604.3000",GeographicCoordinates.Angle(36.0716666667,true,"ddm"));
        Assert.Equal("W07557.1300",GeographicCoordinates.Angle(-75.9521666667,false,"ddm"));
    }
    [Fact]
    public void EfbUsesCurrentRequiredFieldsOptionalAirportsUniqueUserFixesAndSignedOptionalAltitude()
    {
        var plan=Plan();var result=RouteExports.Generate(new(plan));var xml=XDocument.Parse(result.Content);
        var fp=xml.Root!.Element("FlightPlan.FlightPlan")!;
        Assert.Equal("SimBase.Document",xml.Root.Name.LocalName);Assert.Equal("12",fp.Element("AppVersion")!.Element("AppVersionMajor")!.Value);
        Assert.Equal("VFR",fp.Element("FPType")!.Value);Assert.Equal("8500",fp.Element("CruisingAlt")!.Value);Assert.Equal("KJFK",fp.Element("DepartureID")!.Value);Assert.Null(fp.Element("DestinationID"));
        Assert.Equal(plan.Name,fp.Element("Title")!.Value);Assert.Equal("A & B",fp.Element("Descr")!.Value);
        var points=fp.Elements("ATCWaypoint").ToArray();Assert.Equal(3,points.Length);Assert.Equal(3,points.Select(p=>p.Element("ICAO")!.Element("ICAOIdent")!.Value).Distinct().Count());
        Assert.All(points,p=>{Assert.Equal("User",p.Element("ATCWaypointType")!.Value);Assert.InRange(p.Element("ICAO")!.Element("ICAOIdent")!.Value.Length,1,8);});
        Assert.EndsWith(",-000250.00",points[0].Element("WorldPosition")!.Value);Assert.Contains(", W",points[0].Element("WorldPosition")!.Value);Assert.Single(points[1].Element("WorldPosition")!.Value,c=>c==',');
        Assert.Equal(plan.Waypoints![0].Name,points[0].Attribute("id")!.Value);
        Assert.Throws<ArgumentException>(()=>RouteExports.Generate(new(plan with { CruisingAltitudeFeet=null })));
        Assert.Throws<ArgumentException>(()=>RouteExports.Generate(new(plan with { Departure="Descriptive airport name" })));
        var local=RouteExports.Generate(new(plan with { Departure=null,Destination=null }));Assert.DoesNotContain("DepartureID",local.Content);
    }
    [Fact]
    public void LongRouteRequiresExplicitAcknowledgementAndRetainsAllVertices()
    {
        var plan=Plan(1000);Assert.Throws<ArgumentException>(()=>RouteExports.Generate(new(plan)));
        var output=RouteExports.Generate(new(plan,AcknowledgeLongRoute:true));Assert.Equal(1000,XDocument.Parse(output.Content).Descendants("ATCWaypoint").Count());Assert.Contains(output.Preview.Warnings,s=>s.Contains("all waypoints"));Assert.Equal(1000,plan.Waypoints!.Length);
    }
    [Fact]
    public void PlannedExportsPreserveOrderMetadataPrecisionAndDistinguishMissingAltitudeWithoutInventedTimes()
    {
        var plan=Plan();var csv=RouteExports.Generate(new(plan,"csv"));Assert.Contains("latitude_deg",csv.Content);Assert.Contains("'＝".Replace('＝','='),csv.Content);Assert.Contains("36.07166667",csv.Content);Assert.Contains("target_altitude_ft_msl",csv.Content);
        var gpx=XDocument.Parse(RouteExports.Generate(new(plan,"gpx")).Content);XNamespace g="http://www.topografix.com/GPX/1/1";
        Assert.Equal(3,gpx.Descendants(g+"rtept").Count());Assert.Empty(gpx.Descendants(g+"time"));Assert.Equal(-76.2,double.Parse(gpx.Descendants(g+"rtept").First().Element(g+"ele")!.Value,System.Globalization.CultureInfo.InvariantCulture),6);Assert.Null(gpx.Descendants(g+"rtept").Last().Element(g+"ele"));
        var kml=XDocument.Parse(RouteExports.Generate(new(plan,"kml")).Content);XNamespace k="http://www.opengis.net/kml/2.2";Assert.Single(kml.Descendants(k+"Polygon"));Assert.Contains("-75.95216667,36.07166667",kml.Descendants(k+"Point").First().Element(k+"coordinates")!.Value);
        Assert.Throws<ArgumentException>(()=>RouteExports.Generate(new(plan,"route",RouteStyle:"named")));
        var named=plan with { Waypoints=plan.Waypoints!.Select((w,i)=>w with { NavigationIdentifier="FIX"+i }).ToArray()};Assert.Equal("FIX0 DCT FIX1 DCT FIX2",RouteExports.Generate(new(named,"route",RouteStyle:"named")).Content);
        Assert.Contains("360418N0755708W",RouteExports.Generate(new(plan,"route","simbrief")).Content);
        Assert.Throws<ArgumentException>(()=>RouteExports.Generate(new(plan,"route","decimal")));
        Assert.Contains(RouteExports.Generate(new(plan,"route","icao")).Preview.Warnings,w=>w.Contains("one minute"));
    }
    [Fact]
    public void GeographicPlanChangesCannotRewriteRecordedSnapshotsAndLegacyRecordsRemainReadable()
    {
        var saved=store.SavePlan(Plan());Assert.All(saved.Waypoints!,w=>Assert.NotNull(w.Id));
        var coordinator=new MissionCoordinator(store,1000);var mission=coordinator.Start(new("Snapshot",PlanId:saved.Id));coordinator.End(mission.Id);
        var snapshot=JsonSerializer.Serialize(store.Research(mission.Id).PlanSnapshot,WireJson.Options);
        store.SavePlan(saved with { Waypoints=saved.Waypoints!.Reverse().ToArray(),CruisingAltitudeFeet=20000,FlightRules="IFR" });
        Assert.Equal(snapshot,JsonSerializer.Serialize(store.Research(mission.Id).PlanSnapshot,WireJson.Options));
        var legacy=JsonSerializer.Deserialize<MissionPlan>("{\"id\":\""+Guid.NewGuid()+"\",\"name\":\"Legacy\",\"waypoints\":[{\"name\":\"A\",\"latitude\":0,\"longitude\":0}]}",WireJson.Options)!;
        Assert.Equal("VFR",legacy.FlightRules);Assert.Null(legacy.CruisingAltitudeFeet);Assert.Equal(1,RouteExports.Generate(new(legacy,"gpx")).Preview.Waypoints);
    }
    [Fact]
    public void CommBusChunksUnicodeThousandPointRoutesAndNeverSendsWholeGeometryInLiveState()
    {
        var saved=store.SavePlan(Plan(1000) with { Name=new string('測',80),Waypoints=Plan(1000).Waypoints!.Select(w=>w with { Name=new string('測',60),Instructions=new string('量',120) }).ToArray() });
        var coordinator=new MissionCoordinator(store,1000);var handler=new ToolbarCommandHandler(coordinator);
        string Send(string op,string? revision=null,int offset=0)=>handler.Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),op,PlanId:saved.Id,Revision:revision,Offset:offset),WireJson.Options));
        var state=Send("map-state");Assert.True(Encoding.UTF8.GetByteCount(state)<8192);using var doc=JsonDocument.Parse(state);var map=doc.RootElement.GetProperty("map");Assert.Equal(1003,map.GetProperty("items").GetInt32());Assert.Equal(1000,map.GetProperty("waypoints").GetInt32());Assert.False(map.TryGetProperty("waypoints",out var full)&&full.ValueKind==JsonValueKind.Array);
        var revision=map.GetProperty("revision").GetString();var offset=0;var names=new List<string>();
        while(offset<1003){var reply=Send("map-chunk",revision,offset);Assert.True(Encoding.UTF8.GetByteCount(reply)<8192);using var chunk=JsonDocument.Parse(reply);var body=chunk.RootElement.GetProperty("map");var next=body.GetProperty("nextOffset").GetInt32();Assert.True(next>offset);names.AddRange(body.GetProperty("items").EnumerateArray().Where(p=>p.GetProperty("kind").GetString()=="waypoint").Select(p=>p.GetProperty("id").GetString()!));offset=next;}
        Assert.Equal(1000,names.Distinct().Count());Assert.Equal(saved.Waypoints!.Select(w=>w.Id.ToString()),names);
        Assert.Contains("Route revision changed",Send("map-chunk","old-revision"));
        Assert.Contains("Invalid route chunk offset",Send("map-chunk",revision,-1));
    }
    [Fact]
    public void MapUsesImmutableActivePlanBoundedTrackFreshPositionAndResetsOnMissionChange()
    {
        var saved=store.SavePlan(Plan(3));var coordinator=new MissionCoordinator(store,1000);var navigation=new RouteMapService(coordinator);var t=DateTimeOffset.UtcNow.AddSeconds(-2);
        var mission=coordinator.Start(new("Live",PlanId:saved.Id));
        for(var i=0;i<150;i++){var frame=SimulatedTelemetryProvider.Generate(t.AddSeconds(i),i);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"test",t),frame));}
        var map=navigation.State(null,null);Assert.Equal(mission.Id,map.MissionId);Assert.InRange(map.Track.Length,1,24);Assert.True(map.Downsampled);
        var revision=map.Revision;store.SavePlan(saved with { Name="Changed after start" });Assert.Equal(revision,navigation.State(null,null).Revision);
        coordinator.Apply(new(new(ConnectionState.Disconnected,TelemetrySource.Simulated,"test",DateTimeOffset.UtcNow),null));Assert.Null(navigation.State(null,map.NextSequence).Aircraft);
        coordinator.End(mission.Id);Assert.Equal("none",navigation.State(null,null).Revision);Assert.Null(navigation.State(null,null).MissionId);
    }
    [Fact]
    public async Task OfflineAndMissingKeyNeverContactAProviderAndPreferencesPersist()
    {
        var transport=new ProviderHandler();using var http=new HttpClient(transport);var settings=new MapSettings(root,new TestProtector(),http);
        Assert.Equal("streets",settings.Get().Preferences.Layer);Assert.True(settings.Get().RequiresPrivacyChoice);Assert.Empty((await settings.Client()).Tiles);Assert.Equal(0,transport.Calls);
        Assert.Throws<ArgumentException>(()=>settings.Save(new(true,"streets")));
        Assert.Throws<ArgumentException>(()=>settings.Save(new(true,"satellite",PrivacyAccepted:true)));
        settings.Save(new(true,"streets",PrivacyAccepted:true));Assert.Single((await settings.Client()).Tiles);Assert.Equal(0,transport.Calls);
        settings.SelectLayer("offline");Assert.Empty((await settings.Client()).Tiles);Assert.Equal("offline",new MapSettings(root,new TestProtector(),http).Get().Preferences.Layer);
        Assert.Equal(0,transport.Calls);
    }
    [Theory] [InlineData(200)] [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(503)]
    public async Task AuthenticatedRasterMetadataHonorsProviderFailureAndNeverLeaksKeysInErrors(int status)
    {
        const string key="PUBLIC_TEST_KEY_NOT_A_REAL_CREDENTIAL";var transport=new ProviderHandler{Status=(HttpStatusCode)status};using var http=new HttpClient(transport);var protector=new TestProtector();var settings=new MapSettings(root,protector,http);
        settings.Save(new(true,"satellite-labels",PublicApiKey:key,PrivacyAccepted:true));Assert.DoesNotContain(key,File.ReadAllText(Path.Combine(root,"maps.json")));
        var result=await settings.Client();Assert.Equal(status==200?2:1,transport.Calls);
        if(status==200){Assert.Equal(2,result.Tiles.Length);Assert.Contains("Provider attribution",result.Tiles[0].Attribution);Assert.Contains("token=",result.Tiles[0].Url);Assert.Null(result.Error);}
        else{Assert.Equal("offline",result.Layer);Assert.Empty(result.Tiles);Assert.DoesNotContain(key,result.Error!);}
        settings.Save(new(false,"offline"));Assert.Empty((await settings.Client()).Tiles);Assert.Equal(status==200?2:1,transport.Calls);
    }
    [Fact]
    public void NoBackupDeletionKeepsExistingBackupsSettingsUnrelatedFilesAndIndependentScience()
    {
        var pref=new PreferenceStore(root);var settings=pref.Load();using var science=new ScienceStore(settings.ScienceLibraryDirectory);science.Initialize();
        var coordinator=new MissionCoordinator(store,1000,research:new(store,science));var m=coordinator.Start(new("Delete",Instruments:[new("ambient-temperature",true)]));var now=DateTimeOffset.UtcNow;var frame=SimulatedTelemetryProvider.Generate(now,0);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"test",now),frame,new(now,15,29.92,0,0,.0023)));coordinator.End(m.Id);
        var backup=Path.Combine(root,"Backups","old.sqlite");Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.WriteAllText(backup,"existing backup");var unrelated=Path.Combine(root,"unrelated.txt");File.WriteAllText(unrelated,"keep");var configuration=File.ReadAllBytes(pref.SettingsPath);
        var preview=store.PreviewFlightClear(false);Assert.Equal(1,preview.Missions);Assert.Equal(0,preview.Plans);Assert.True(preview.ApproximateBytes>=new FileInfo(store.DatabasePath).Length);
        var sciencePreview=science.PreviewClear();Assert.Equal(1,sciencePreview.Datasets);Assert.Equal(1,sciencePreview.Observations);Assert.True(sciencePreview.ApproximateBytes>=new FileInfo(science.DatabasePath).Length);
        Assert.False(ArchiveClearPolicy.DefaultBackupRequested);Assert.Throws<InvalidOperationException>(()=>store.ClearFlightData("wrong phrase"));Assert.Single(store.List());
        var selection=ArchiveClearPolicy.ChooseBackup(ArchiveClearPolicy.DefaultBackupRequested,()=>throw new Exception("No-backup must never open a picker"),_=>throw new Exception("No backup validation expected"));
        Assert.False(selection.Cancelled);Assert.Null(selection.Directory);store.ClearFlightData("DELETE FLIGHT DATA");Assert.Empty(store.List());Assert.Single(science.StreamObservations(m.Id));Assert.Equal("existing backup",File.ReadAllText(backup));Assert.Equal("keep",File.ReadAllText(unrelated));Assert.Equal(configuration,File.ReadAllBytes(pref.SettingsPath));
        var before=StorageManager.DatabaseFingerprint(science.DatabasePath,true);var cancelled=ArchiveClearPolicy.ChooseBackup(true,()=>null,_=>throw new Exception("Cancelled backup must not validate or delete"));Assert.True(cancelled.Cancelled);Assert.Equal(before,StorageManager.DatabaseFingerprint(science.DatabasePath,true));
        Assert.Contains("irreversible",ArchiveClearPolicy.ConfirmationWarning(null));Assert.Contains("separate",ArchiveClearPolicy.ConfirmationWarning(root+"-backup"));
    }
    [Fact]
    public void ReducedTracksKeepActualGapBoundariesWhileRecentAndIncrementalPagesStayBounded()
    {
        var coordinator=new MissionCoordinator(store,1000);var mission=coordinator.Start("Track gap");var start=DateTimeOffset.UtcNow.AddMinutes(-4);
        for(var i=0;i<120;i++){var time=start.AddSeconds(i+(i>=60?20:0));var f=SimulatedTelemetryProvider.Generate(time,i);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"test fixture",time),f));}
        var reduced=store.Track(mission.Id,12);Assert.True(reduced.Downsampled);Assert.InRange(reduced.Points.Length,3,12);Assert.Single(reduced.Points,p=>p.BreakBefore);
        var first=store.Track(mission.Id,59,0);var second=store.Track(mission.Id,24,first.NextSequence);Assert.Single(second.Points,p=>p.BreakBefore);
        var recent=store.Track(mission.Id,24,recent:true);Assert.Equal(24,recent.Points.Length);Assert.Equal(97,recent.Points[0].Sequence);Assert.Equal(120,recent.NextSequence);Assert.DoesNotContain(recent.Points,p=>p.BreakBefore);
        coordinator.End(mission.Id);
    }
    [Fact]
    public void LiveMapPacketsStayBoundedAndUnplannedMissionChangesHaveDistinctRevisions()
    {
        var coordinator=new MissionCoordinator(store,1000);var handler=new ToolbarCommandHandler(coordinator);var mission=coordinator.Start("Unplanned map");var start=DateTimeOffset.UtcNow.AddSeconds(-120);
        for(var i=0;i<120;i++){var time=start.AddSeconds(i);var f=SimulatedTelemetryProvider.Generate(time,i);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"fixture",time),f));}
        for(var i=0;i<4;i++)coordinator.AddMarker(mission.Id,new("observation",new string('測',1000)));
        var reply=handler.Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"map-state"),WireJson.Options));Assert.InRange(Encoding.UTF8.GetByteCount(reply),1,8000);using var parsed=JsonDocument.Parse(reply);Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean());Assert.Equal(24,parsed.RootElement.GetProperty("map").GetProperty("track").GetArrayLength());
        var revision=parsed.RootElement.GetProperty("map").GetProperty("revision").GetString();Assert.NotEqual("none",revision);coordinator.End(mission.Id);var next=coordinator.Start("Another unplanned mission");
        var state=new RouteMapService(coordinator).State(null,null);Assert.NotEqual(revision,state.Revision);Assert.Empty(state.Track);coordinator.End(next.Id);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ImageryUsesSourceCopyrightWhenNeededAndRejectsForeignTileHosts(bool malicious)
    {
        var handler=new ProviderHandler{Metadata=true,Malicious=malicious};using var http=new HttpClient(handler);var protector=new TestProtector();
        var settings=new MapSettings(root,protector,http);settings.Save(new(true,"satellite",PublicApiKey:"PUBLIC_FIXTURE_NOT_A_REAL_KEY",PrivacyAccepted:true));
        var result=await settings.Client();
        if(malicious){Assert.Equal(1,handler.Calls);Assert.Empty(result.Tiles);Assert.NotNull(result.Error);}
        else {Assert.Equal(2,handler.Calls);Assert.Contains("Actual metadata copyright",result.Tiles.Single().Attribution);var restarted=new MapSettings(root,protector,http);Assert.Single((await restarted.Client()).Tiles);}
    }
    [Fact]
    public async Task EmptyProviderTileMetadataFallsBackWithoutExposingThePublicKey()
    {
        var handler=new ProviderHandler{EmptyTiles=true};using var http=new HttpClient(handler);var settings=new MapSettings(root,new TestProtector(),http);
        settings.Save(new(true,"satellite",PublicApiKey:"PUBLIC_FIXTURE_NOT_A_REAL_KEY",PrivacyAccepted:true));var result=await settings.Client();
        Assert.Equal("offline",result.Layer);Assert.Empty(result.Tiles);Assert.NotNull(result.Error);Assert.DoesNotContain("PUBLIC_FIXTURE",result.Error);Assert.Equal(1,handler.Calls);
    }
    [Fact]
    public void NativeStartOfSelectedSavedPlanRetainsItsModeAndImmutableGeometry()
    {
        var plan=store.SavePlan(Plan() with { Mode=ScienceMode.Realistic });var coordinator=new MissionCoordinator(store,1000);var handler=new ToolbarCommandHandler(coordinator);
        var response=handler.Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"start",Name:plan.Name,Mode:ScienceMode.Universal,PlanId:plan.Id),WireJson.Options));
        using var json=JsonDocument.Parse(response);Assert.True(json.RootElement.GetProperty("ok").GetBoolean());var mission=coordinator.Snapshot().ActiveMission!;Assert.Equal(ScienceMode.Realistic,mission.Mode);
        Assert.Equal(plan.Id,store.Research(mission.Id).PlanSnapshot!.Id);Assert.Equal(plan.Waypoints,store.Research(mission.Id).PlanSnapshot!.Waypoints);
        store.SavePlan(plan with { Name="Edited later",Waypoints=[] });Assert.Equal(plan.Waypoints,store.Research(mission.Id).PlanSnapshot!.Waypoints);coordinator.End(mission.Id);
    }
    private sealed class TestProtector : IMapKeyProtector
    {
        private readonly byte[] key=RandomNumberGenerator.GetBytes(32);
        public string Protect(string value){var bytes=Encoding.UTF8.GetBytes(value);for(var i=0;i<bytes.Length;i++)bytes[i]^=key[i%key.Length];return Convert.ToBase64String(bytes);}
        public string Unprotect(string value){var bytes=Convert.FromBase64String(value);for(var i=0;i<bytes.Length;i++)bytes[i]^=key[i%key.Length];return Encoding.UTF8.GetString(bytes);}
    }
    private sealed class ProviderHandler : HttpMessageHandler
    {
        public int Calls;public HttpStatusCode Status=HttpStatusCode.OK;public bool Metadata;public bool Malicious;public bool EmptyTiles;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;
            if(request.RequestUri!.Host=="static-map-tiles-api.arcgis.com")return Task.FromResult(new HttpResponseMessage(Status){Content=new StringContent("{\"copyrightText\":\"Actual label credits\"}")});
            if(request.RequestUri!.Host=="ibasemaps-api.arcgis.com")return Task.FromResult(new HttpResponseMessage(Status){Content=new StringContent("{\"copyrightText\":\"Actual metadata copyright\"}")});
            Assert.Equal("basemapstyles-api.arcgis.com",request.RequestUri.Host);
            var source=new { type="raster",tiles=EmptyTiles?Array.Empty<string>():new[]{Malicious?"https://not-a-provider.invalid/{z}/{y}/{x}":"https://ibasemaps-api.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"},attribution=Metadata?null:"Provider attribution",maxzoom=19};
            return Task.FromResult(new HttpResponseMessage(Status){Content=new StringContent(JsonSerializer.Serialize(new{sources=new{imagery=source}}))});
        }
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
