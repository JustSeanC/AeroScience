using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Xunit;

namespace AeroScience.Tests;

// Real provider assembler + the existing coordinator, stores and collection
// controller. All protocol data/stores are disposable synthetic fixtures.
public sealed class XPlaneScienceIntegrationTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"aeroscience-p04-science",Guid.NewGuid().ToString("N"));
    private readonly XPlaneAssemblerFixture fixture=new();
    private readonly MissionStore store;
    private readonly ScienceStore science;
    private readonly ResearchOperations research;
    private readonly MissionCoordinator coordinator;
    private int phase=1;
    public XPlaneScienceIntegrationTests()
    {
        store=new(Path.Combine(directory,"missions.db"));store.Initialize();var library=Path.Combine(directory,"science");Directory.CreateDirectory(library);
        PreferenceStore.WriteAtomic(Path.Combine(library,StorageManager.LibraryManifest),new{libraryId=Guid.NewGuid(),formatVersion=1});
        science=new(library);science.Initialize();store.ScienceLibrary=science;var policy=new SimulatorAccessPolicy("xplane12",()=>fixture.Time.GetUtcNow());
        research=new(store,science,simulatorAccess:policy);research.Configure([new("ambient-temperature",true),new("ambient-pressure",true),new("ambient-wind",true),new("ambient-density",true)]);
        coordinator=new(store,1000,research:research,utcNow:()=>fixture.Time.GetUtcNow(),simulatorAccess:policy);
        fixture.Seed();fixture.Confirm();coordinator.Apply(fixture.Assembler.Snapshot()!);
    }
    private TelemetryUpdate Tick()
    {fixture.Time.Advance(TimeSpan.FromSeconds(1));fixture.Confirm(++phase);var update=fixture.Assembler.Snapshot()!;coordinator.Apply(update);return update;}
    private Mission Start(bool armed=false)=>coordinator.Start(new("Synthetic X-Plane flight",ArmScience:armed));
    private CollectionCommitResult Control(Guid flight,CollectionAction action)
    {var s=store.CollectionCheckpoint(flight)!;return coordinator.ControlScience(new(1,flight,Guid.NewGuid(),s.Revision,action,s.CurrentSessionId));}

    [Fact] public void HostQualificationUsesTheExactProviderMapWithoutAnIndependentDatarefFallback()
    {
        foreach(var mapping in SimulatorAccessPolicy.Datarefs)
            Assert.Equal(mapping.Value,AeroScience.XPlane.XPlaneDatarefs.All.Single(r=>r.Key==mapping.Key).Name);
        var reference=InstrumentCalibration.DescribeForSimulator("ambient-temperature","xplane12").Reference;
        Assert.Equal(EvidenceContracts.ContentHash(reference),EvidenceContracts.ContentHash(InstrumentCalibration.ValidateSelection(reference,"ambient-temperature")));
        Assert.Equal("aeroscience.calibration.xplane-web-v1.ambient-temperature",reference.Id);
        Assert.Null(science.Estimate([new("ambient-temperature",true)],10,"xplane12").EstimatedBytes);
    }

    [Fact] public void QualifiedRecordingAndFiveAtmosphericMeasurementsPreserveSourceEpochAndTiming()
    {
        Assert.True(coordinator.LiveOperationsAllowed);var mission=Start();var update=Tick();
        var sample=Assert.Single(store.Samples(mission.Id,10,0));Assert.Equal("xplane12",sample.SimulatorOrigin!.SimulatorId);
        Assert.Equal(fixture.Epoch,sample.SimulatorContext!.ConnectionEpoch);Assert.Equal(sample.TimestampUtc,sample.SimulatorContext.HostReceivedUtc);
        Assert.Null(sample.SimulatorContext.Clock!.Year);Assert.Null(sample.Environment);Assert.Null(sample.Guidance);
        var values=science.StreamObservations(mission.Id).ToArray();Assert.Equal(5,values.Length);
        Assert.All(values,value=>{Assert.Equal("xplane12",value.SimulatorOrigin!.SimulatorId);Assert.Equal(fixture.Epoch,value.SimulatorContext!.ConnectionEpoch);
            Assert.Contains("X-Plane 12",value.Source);Assert.DoesNotContain("MSFS",value.Source);Assert.DoesNotContain("SimConnect",value.Source);
            Assert.Equal(ScienceProvenance.SimulatorReported,value.Provenance);Assert.NotNull(value.SessionId);Assert.NotNull(value.IntentId);});
        Assert.Equal(15,values.Single(v=>v.MeasurementId=="temperature").Value);
        Assert.Equal(101325/3386.389,values.Single(v=>v.MeasurementId=="pressure").Value,6);
        Assert.Equal(10/.5144444444444445,values.Single(v=>v.MeasurementId=="wind-speed").Value,6);
        Assert.Equal(270,values.Single(v=>v.MeasurementId=="wind-direction").Value);
        Assert.Equal(1.225/515.3788183931961,values.Single(v=>v.MeasurementId=="density").Value,9);
        var session=Assert.Single(store.CollectionSessions(mission.Id));Assert.Equal("xplane12",session.SimulatorOrigin!.SimulatorId);
        Assert.All(session.Configurations.Where(c=>c.Settings.Enabled),c=>{Assert.Contains("X-Plane",c.Specification!.Definition.DataSource);Assert.DoesNotContain("flightsimulator.com",string.Join(" ",c.Specification.References));});
        Assert.All(science.Datasets(mission.Id),dataset=>{Assert.Contains("xplane12",dataset.ProvenanceJson);Assert.DoesNotContain("SimConnect",dataset.ProvenanceJson);});
        coordinator.Apply(update);Assert.Single(store.Samples(mission.Id,10,0));Assert.Equal(5,science.StreamObservations(mission.Id).Count());
    }
    [Fact] public void ArmedRecordingPauseResumeAndMultipleSessionsRemainIndependent()
    {
        var mission=Start(armed:true);Tick();Assert.Empty(science.StreamObservations(mission.Id));
        Assert.Equal(CollectionPhase.Armed,store.CollectionCheckpoint(mission.Id)!.Phase);
        Control(mission.Id,CollectionAction.StartData);Tick();Assert.Equal(5,science.StreamObservations(mission.Id).Count());
        Control(mission.Id,CollectionAction.PauseData);Tick();Assert.Equal(5,science.StreamObservations(mission.Id).Count());Assert.Equal(3,store.Detail(mission.Id)!.Mission.SampleCount);
        Control(mission.Id,CollectionAction.ResumeData);Tick();Assert.Equal(10,science.StreamObservations(mission.Id).Count());
        Control(mission.Id,CollectionAction.EndData);Tick();Control(mission.Id,CollectionAction.StartData);Tick();
        Assert.Equal(2,store.CollectionSessions(mission.Id).Length);Assert.Equal(15,science.StreamObservations(mission.Id).Count());
        coordinator.End(mission.Id);Assert.All(store.CollectionSessions(mission.Id),s=>Assert.Equal(CollectionSessionStatus.Ended,s.Status));
    }
    [Fact] public async Task SimulatorPauseAndReceiptRateJitterCannotJoinHoldOrNavigationSegments()
    {
        var mission=Start();Tick();Assert.Equal(5,science.StreamObservations(mission.Id).Count());
        fixture.Api.Overrides["paused"]=1;Tick();Assert.Single(store.Samples(mission.Id,10,0));Assert.Equal(5,science.StreamObservations(mission.Id).Count());
        fixture.Api.Overrides["paused"]=0;Tick();var samples=store.Samples(mission.Id,10,0);Assert.Equal(2,samples.Count);Assert.True(samples[1].ContinuityBreak);
        Assert.True(store.Track(mission.Id).Points[1].BreakBefore);Assert.Equal(10,science.StreamObservations(mission.Id).Count());
        fixture.Api.Overrides["actual-rate"]=1.001;Tick();Assert.NotNull(coordinator.Snapshot().ActiveMission);coordinator.End(mission.Id);
        using var gpx=new MemoryStream();await ResearchExports.GpxAsync(store,mission.Id,gpx,default);var gx=System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(gpx.ToArray()));
        Assert.Equal(2,gx.Descendants().Count(e=>e.Name.LocalName=="trkseg"));Assert.Contains("xplane12",gx.ToString());
        using var kml=new MemoryStream();await ResearchExports.KmlAsync(store,mission.Id,kml,default);var kx=System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(kml.ToArray()));
        Assert.Equal(2,kx.Descendants().Count(e=>e.Name.LocalName=="LineString"));Assert.Contains("xplane12",kx.ToString());
    }
    [Fact] public void NativeAtmosphericValuesAreRetainedAndCannotBeReplacedWithANormalizedGuess()
    {
        var mission=Start();Tick();var pressure=science.StreamObservations(mission.Id).Single(v=>v.MeasurementId=="pressure");
        var native=Assert.Single(pressure.SimulatorContext!.ReportedValues!,v=>v.Channel=="pressure");Assert.Equal("Pa",native.Unit);Assert.Equal(101325,native.Value);
        Assert.Equal(pressure.Value,native.Value/3386.389,12);Assert.True(SimulatorAccessPolicy.QualifiedObservation(pressure));
        Assert.Throws<ArgumentException>(()=>science.Append([pressure with{Value=101325}]));
        Assert.Throws<ArgumentException>(()=>science.Append([pressure with{SimulatorContext=pressure.SimulatorContext with{ReportedValues=pressure.SimulatorContext.ReportedValues!.Where(v=>v.Channel!="pressure").ToArray()}}]));
    }
    [Fact] public void UnsupportedCloudMissionCanBeRecordedArmedButCannotStartCloudScience()
    {
        var plan=store.SavePlan(new(Guid.NewGuid(),"Unsupported cloud fixture",MissionTemplateKind.CloudStructureSurvey,Instruments:[new("ambient-temperature",true)]));
        Assert.Throws<InvalidOperationException>(()=>coordinator.Start(new("Cloud fixture",PlanId:plan.Id)));Assert.Empty(store.List());
        var mission=coordinator.Start(new("Cloud fixture recording",PlanId:plan.Id,ArmScience:true));
        Assert.Contains("cloud channels",Assert.Throws<InvalidOperationException>(()=>Control(mission.Id,CollectionAction.StartData)).Message);
        var controller=new ScienceCollectionController(store,mission.Id,()=>research.Configurations);var state=controller.State;
        Assert.Throws<InvalidOperationException>(()=>controller.Control(new(1,mission.Id,Guid.NewGuid(),state.Revision,CollectionAction.StartData,null),fixture.Time.GetUtcNow(),CollectionEventOrigin.RouteTrigger,Guid.NewGuid()));
        Assert.Empty(store.CollectionSessions(mission.Id));
        Tick();Assert.Empty(science.StreamObservations(mission.Id));Assert.Single(store.Samples(mission.Id,10,0));
    }
    [Fact] public void MissingAtmosphericChannelsAndAglNeverBecomeZeroOrGroundElevation()
    {
        fixture.Api.Overrides["temperature"]=-999d;fixture.Api.RawOverrides["pressure"]="null";fixture.Api.RawOverrides["agl"]="null";
        // Temperature is outside the provider's documented numeric range.
        Tick();var mission=Start();Tick();var sample=Assert.Single(store.Samples(mission.Id,10,0));Assert.Null(sample.AltitudeAglFeet);Assert.Null(sample.Environment);
        Assert.DoesNotContain(science.StreamObservations(mission.Id),v=>v.MeasurementId is "temperature" or "pressure");
        Assert.All(science.StreamObservations(mission.Id),v=>Assert.NotEqual("ground-elevation",v.MeasurementId));
    }
    [Fact] public void ProviderFlagsCannotOverrideMissingOrMixedAgeConfirmation()
    {
        var update=Tick();var context=update.SampleContext!;
        var flags=context.Capabilities with{ProductionRecordingReady=true,ScienceIntegrationReady=true};
        coordinator.Apply(update with{SampleContext=context with{Capabilities=flags,CoreSnapshotSpanMilliseconds=501}});
        Assert.False(coordinator.LiveOperationsAllowed);Assert.Throws<InvalidOperationException>(()=>Start());Assert.Empty(store.List());
        coordinator.Apply(update with{SampleContext=context with{Capabilities=flags,Fields=context.Fields.Select(f=>f with{LastConfirmedUtc=context.ConfirmationStartedUtc.AddSeconds(-1)}).ToArray()}});
        Assert.False(coordinator.LiveOperationsAllowed);Assert.Throws<InvalidOperationException>(()=>Start());
    }
    [Theory] [InlineData("epoch")] [InlineData("aircraft")] [InlineData("clock")] [InlineData("replay")] [InlineData("position")] [InlineData("rate")] [InlineData("gap")] [InlineData("disconnect")]
    public void DiscontinuitiesInterruptWithoutJoiningScientificEvidence(string transition)
    {
        var mission=Start();var update=Tick();var original=JsonSerializer.Serialize(store.Samples(mission.Id,10,0),WireJson.Options);var observations=science.StreamObservations(mission.Id).ToArray();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));fixture.Confirm(++phase);var next=fixture.Assembler.Snapshot()!;
        next=transition switch{
            "epoch"=>next with{Connection=next.Connection with{Origin=next.Connection.Origin! with{ConnectionEpoch=Guid.NewGuid()}}},
            "aircraft"=>next with{Frame=next.Frame! with{Aircraft="Another aircraft"}},
            "clock"=>next with{SampleContext=next.SampleContext! with{Clock=next.SampleContext.Clock! with{FlightSeconds=0}}},
            "replay"=>next with{SampleContext=next.SampleContext! with{Clock=next.SampleContext.Clock! with{Replay=true}}},
            "position"=>next with{Frame=next.Frame! with{Latitude=50}},
            "rate"=>next with{SampleContext=next.SampleContext! with{Clock=next.SampleContext.Clock! with{ActualRate=2}}},
            "gap"=>next with{Frame=null},
            "disconnect"=>next with{Connection=next.Connection with{State=ConnectionState.Disconnected},Frame=null},_=>throw new InvalidOperationException()};
        coordinator.Apply(next);Assert.Null(coordinator.Snapshot().ActiveMission);Assert.Equal(MissionStatus.Interrupted,store.Detail(mission.Id)!.Mission.Status);
        Assert.Equal(original,JsonSerializer.Serialize(store.Samples(mission.Id,10,0),WireJson.Options));Assert.Equal(observations,science.StreamObservations(mission.Id));
        Assert.Equal(CollectionSessionStatus.Interrupted,Assert.Single(store.CollectionSessions(mission.Id)).Status);
        Assert.Contains(store.Detail(mission.Id)!.Events,e=>e.Kind=="connection"&&e.Message.Contains("previous"));
        coordinator.Apply(next);Assert.Null(coordinator.Snapshot().ActiveMission);
    }
    [Fact] public void GeometryCannotBeEnabledByUniversalModeOrByCallingTheGeometryEntryPoint()
    {
        research.Configure([new("terrain-scanner",true)]);
        Assert.Throws<InvalidOperationException>(()=>Start());Assert.Empty(store.List());
        var mission=Start(armed:true);Assert.Throws<InvalidOperationException>(()=>Control(mission.Id,CollectionAction.StartData));
        Assert.Throws<InvalidOperationException>(()=>SensorGeometry.Footprint(coordinator.Snapshot().Telemetry!));
        Tick();Assert.Empty(science.StreamObservations(mission.Id));Assert.Empty(science.Products(mission.Id));
    }
    [Fact] public async Task SourceAwareArchiveAndSeparateFlightsShareOneLibraryWithoutRewritingPriorBytes()
    {
        var first=Start();Tick();coordinator.End(first.Id);var original=science.StreamObservations(first.Id).ToArray();
        var before=JsonSerializer.Serialize(store.Samples(first.Id,10,0),WireJson.Options);
        Tick();var next=Start();Tick();coordinator.End(next.Id);
        Assert.NotEqual(first.Id,next.Id);Assert.Equal(8,science.Datasets().Count);Assert.Equal(original,science.StreamObservations(first.Id));
        Assert.Equal(before,JsonSerializer.Serialize(store.Samples(first.Id,10,0),WireJson.Options));
        using var output=new MemoryStream();await store.ExportJsonAsync(first.Id,output);using var json=JsonDocument.Parse(output.ToArray());
        Assert.Equal(3,json.RootElement.GetProperty("formatVersion").GetInt32());Assert.Equal("xplane12",json.RootElement.GetProperty("mission").GetProperty("simulatorOrigin").GetProperty("simulatorId").GetString());
        Assert.Equal("xplane12",json.RootElement.GetProperty("simulatorOriginEvidence").GetProperty("origin").GetProperty("simulatorId").GetString());
        Assert.Equal(64,json.RootElement.GetProperty("simulatorOriginEvidence").GetProperty("sha256").GetString()!.Length);
        Assert.Equal(json.RootElement.GetProperty("simulatorOriginEvidence").GetProperty("sha256").GetString(),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json.RootElement.GetProperty("simulatorOriginEvidence").GetProperty("origin").GetRawText()))).ToLowerInvariant());
        var fresh=new MissionStore(store.DatabasePath);fresh.Initialize();Assert.Equal(fixture.Epoch,fresh.Detail(first.Id)!.Mission.SimulatorOrigin!.ConnectionEpoch);
    }
    [Fact] public void UnknownOrAcceleratedClockRatesCannotEarnContinuousHoldCredit()
    {
        var sample=coordinator.Snapshot().Telemetry!;var rule=new FlightRegimeDefinition(120,10,3280,100,2);
        Assert.True(FlightRegimeEvaluation.SupportedSample(sample,rule));
        foreach(var rate in new double?[]{null,2})Assert.False(FlightRegimeEvaluation.SupportedSample(sample with{SimulatorContext=sample.SimulatorContext! with{Clock=sample.SimulatorContext.Clock! with{ActualRate=rate}}},rule));
        Assert.True((sample with{GroundspeedKnots=0,AltitudeAglFeet=null}).IsValid);
    }
    public void Dispose(){research.Dispose();science.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
