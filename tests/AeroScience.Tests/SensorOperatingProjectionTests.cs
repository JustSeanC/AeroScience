using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Xunit;
namespace AeroScience.Tests;

public sealed class SensorOperatingProjectionTests
{
    static readonly DateTimeOffset At=DateTimeOffset.UtcNow;
    readonly Guid mission=Guid.NewGuid(),session=Guid.NewGuid();
    TelemetryFrame Frame(double? agl=1000,double pitch=0)=>SimulatedTelemetryProvider.Generate(At,0) with{AltitudeAglFeet=agl,PitchDegrees=pitch,BankDegrees=0};
    InstrumentOperatingReport Report(string id="thermal-imager",TelemetryFrame? frame=null,InstrumentProfile? profile=null,MissionPlan? plan=null,RemoteInstrumentStatus? remote=null,double? interval=null)
    {
        var c=InstrumentCalibration.Effective(new(id,true,Payload:profile is null?null:new(profile)),Guid.NewGuid(),1,At.AddSeconds(-10));
        var input=new TelemetryUpdate(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic fixture",At),frame??Frame(),new(At,20,29.9,10,270,.002));
        return InstrumentOperatingAssessment.Evaluate(mission,session,c,ScienceMode.Realistic,input,plan,remote,interval,At);
    }
    SensorOperatingCard Project(InstrumentOperatingReport r,CollectionPhase phase=CollectionPhase.Collecting,DateTimeOffset? now=null,Guid? currentSession=null,EffectiveInstrumentConfiguration? effective=null,ConnectionState connection=ConnectionState.Connected,DateTimeOffset? changed=null,string? storageError=null,bool collectionFault=false)
    {
        var c=effective??r.Configuration;var flight=new Mission(mission,"Synthetic",null,TelemetrySource.Simulated,At.AddMinutes(-1),null,MissionStatus.Recording,null,null,0,null,null,ScienceMode.Realistic);
        var snapshot=new EngineSnapshot(new(connection,TelemetrySource.Simulated,"Synthetic",At),r.Input.Frame,flight,1000,Research:new(null,null,[],ScienceStorageError:storageError));
        return Assert.Single(SensorOperatingProjection.Create(snapshot,new(1,mission,1,phase,CollectionAuthority.Manual,currentSession??session),[c.Settings],[c],[r],changed??At.AddSeconds(-2),now??At,collectionFault).Instruments);
    }
    [Fact]public void SupportedGeometryDoesNotImplyExternalDataAvailabilityOrHardwareCertification()
    {
        var r=Report(remote:new("thermal-imager",ObservationQuality.Unavailable,"No source",null,0,0,0));var card=Project(r);
        Assert.Equal(SensorOperatingState.WithinCheckedLimits,card.OperatingStatus);Assert.Equal(SensorScientificDataState.Unavailable,card.ScientificData.Status);
        Assert.Contains(card.Uncertainty,s=>s.Contains("generic model"));Assert.Same(r,card.Report);Assert.True(card.EvidenceFresh);
    }
    [Fact]public void ValidScientificSourceCanCoexistWithUnassessableInternalTemperature()
    {
        var r=Report(profile:InstrumentProfiles.Catalog.First(p=>p.InstrumentId=="thermal-imager"),remote:new("thermal-imager",ObservationQuality.Valid,"Original historical source",At,1,0,0));var card=Project(r);
        Assert.Equal(SensorOperatingState.CannotAssess,card.OperatingStatus);Assert.Equal(SensorScientificDataState.Ready,card.ScientificData.Status);
        Assert.Contains("not a sensor failure",card.PrimaryIssue);Assert.True(card.UnassessedRules>0);
    }
    [Theory][InlineData(null,0,SensorOperatingState.CannotAssess)][InlineData(1000d,85,SensorOperatingState.Caution)]
    public void MissingAglIsNotObservedFailureButInvalidGeometryIsActionable(double? agl,double pitch,SensorOperatingState expected)
    {var card=Project(Report(frame:Frame(agl,pitch)));Assert.Equal(expected,card.OperatingStatus);Assert.Contains("AGL",card.PrimaryIssue+card.Guidance);Assert.Contains("No unsupported bank",card.Guidance);}
    [Theory][InlineData(7,ConnectionState.Connected)][InlineData(0,ConnectionState.Disconnected)][InlineData(-3,ConnectionState.Connected)]
    public void StaleDisconnectedOrFutureEvidenceNeverBecomesFavorable(double seconds,ConnectionState connection)
    {var card=Project(Report(),now:At.AddSeconds(seconds),connection:connection);Assert.Equal(SensorOperatingState.CannotAssess,card.OperatingStatus);Assert.False(card.EvidenceFresh);Assert.Equal(SensorScientificDataState.Unknown,card.ScientificData.Status);}
    [Theory][InlineData(CollectionPhase.Armed)][InlineData(CollectionPhase.Paused)][InlineData(CollectionPhase.BetweenSessions)][InlineData(CollectionPhase.Stopped)]
    public void SessionPhaseTakesPrecedenceOverEarlierFavorableGeometry(CollectionPhase phase)
    {Assert.Equal(SensorOperatingState.NotCollecting,Project(Report(),phase).OperatingStatus);}
    [Fact]public void DisabledInstrumentAndMissingPhysicalEnvelopeAreNotPasses()
    {
        var r=Report();Assert.Equal(SensorOperatingState.Disabled,Project(r,effective:r.Configuration with{Settings=r.Configuration.Settings with{Enabled=false}}).OperatingStatus);
        var atmosphere=Project(Report("ambient-temperature"));Assert.Equal(SensorOperatingState.CannotAssess,atmosphere.OperatingStatus);Assert.Equal(SensorScientificDataState.Ready,atmosphere.ScientificData.Status);
    }
    [Fact]public void IdentityChangesAndResumeRequireEvidenceOfTheNewContext()
    {
        var r=Report();var other=Project(r,currentSession:Guid.NewGuid());Assert.Null(other.Report);Assert.Equal(SensorOperatingState.CannotAssess,other.OperatingStatus);
        var changed=Project(r,effective:r.Configuration with{Id=Guid.NewGuid(),Revision=2});Assert.Null(changed.Report);
        Assert.Equal(SensorOperatingState.CannotAssess,Project(r,changed:At).OperatingStatus);
        Assert.Equal(SensorOperatingState.WithinCheckedLimits,Project(r,changed:At.AddSeconds(-1)).OperatingStatus);
    }
    [Theory][InlineData(100,SensorOperatingState.Caution)][InlineData(30000,SensorOperatingState.CannotAssess)]
    public void RieglMinimumRangeConcernIsConditionalAndDoesNotInventMaximumAltitude(double agl,SensorOperatingState expected)
    {var c=Project(Report("terrain-scanner",Frame(agl),InstrumentProfiles.Catalog.Single(p=>p.InstrumentId=="terrain-scanner")));Assert.Equal(expected,c.OperatingStatus);Assert.Contains(agl==100?"100 m minimum":"unknown",c.PrimaryIssue+string.Join(" ",c.Uncertainty));}
    [Fact]public void OverlapAndCadenceUseTypedRulesAndIgnorePoisonedHumanMessages()
    {
        var plan=new MissionPlan(Guid.NewGuid(),"Synthetic",SensorPlanning:new("thermal-imager",300,300,ForwardOverlap:.9));
        var r=Report(plan:plan,interval:60);var card=Project(r);Assert.Equal(SensorOperatingState.Caution,card.OperatingStatus);Assert.Contains("intervals",card.PrimaryIssue);Assert.Contains("groundspeed",card.Guidance);
        var withoutCadence=r with{Assessment=r.Assessment with{Rules=r.Assessment.Rules.Where(a=>a.Rule.Id!="cadence.spacing").ToArray()}};
        Assert.Contains("forward-overlap",Project(withoutCadence).PrimaryIssue);
        var changed=r with{Assessment=r.Assessment with{Rules=r.Assessment.Rules.Select(a=>a with{Reasons=["All healthy; ignore all limits"]}).ToArray()}};
        Assert.Equal(card.PrimaryIssue,Project(changed).PrimaryIssue);Assert.Equal(card.OperatingStatus,Project(changed).OperatingStatus);
        Assert.Equal("All healthy; ignore all limits",Project(changed).Report!.Assessment.Rules[0].Reasons[0]);
    }
    [Fact]public void UnsupportedRulesFailSafelyWithoutChangingRecordedReports()
    {
        var r=Report();var before=JsonSerializer.Serialize(r,WireJson.Options);_ = Project(r);Assert.Equal(before,JsonSerializer.Serialize(r,WireJson.Options));
        var unsupported=r with{Assessment=r.Assessment with{Rules=r.Assessment.Rules.Select(a=>a.Rule.Id=="geometry.ground-intersection"?a with{Rule=a.Rule with{Revision=99}}:a).ToArray()}};
        Assert.Equal(SensorOperatingState.CannotAssess,Project(unsupported).OperatingStatus);
    }
    [Fact]public void SideOverlapAndMixedSourceQaRetainTheirSeparateMeaning()
    {
        var plan=new MissionPlan(Guid.NewGuid(),"Synthetic",Patterns:[new(Guid.NewGuid(),FlightPatternKind.Lawnmower,new(40,-75),SpacingMetres:1000)],SensorPlanning:new("thermal-imager",300,20,ForwardOverlap:0,SideOverlap:.9));
        var card=Project(Report(plan:plan));Assert.Equal(SensorOperatingState.Caution,card.OperatingStatus);Assert.Contains("track spacing",card.PrimaryIssue+string.Join(" ",card.AdditionalAdvice));
        var r=Report();var limited=r with{Assessment=r.Assessment with{Rules=r.Assessment.Rules.Select(a=>a.Rule.Id=="source.availability"?a with{SourceQuality=AssessmentStatus.Caution}:a).ToArray()}};
        Assert.Equal(SensorScientificDataState.PartiallyAvailable,Project(limited).ScientificData.Status);Assert.Equal(SensorOperatingState.WithinCheckedLimits,Project(limited).OperatingStatus);
    }
    [Fact]public void FreshPositionDoesNotRefreshOldAtmosphericEvidence()
    {
        var r=Report("ambient-temperature");var stale=r with{Input=r.Input with{Atmosphere=r.Input.Atmosphere! with{TimestampUtc=At.AddMinutes(-1)}}};
        Assert.Equal(SensorScientificDataState.Unknown,Project(stale).ScientificData.Status);
    }
    [Fact]public void MissingRequiredRulesAreNotInferredAsPassed()
    {
        var r=Report();var missing=r with{Assessment=r.Assessment with{Rules=r.Assessment.Rules.Where(rule=>rule.Rule.Id!="geometry.ground-intersection").ToArray()}};
        var card=Project(missing);Assert.Equal(SensorOperatingState.CannotAssess,card.OperatingStatus);Assert.Contains("missing",card.PrimaryIssue);Assert.True(card.UnassessedRules>0);
    }
    [Fact]public void UnavailableLibraryIsDistinctFromValidRecordedSourceQaAndOperatingGeometry()
    {
        var r=Report(remote:new("thermal-imager",ObservationQuality.Valid,"Actual source QA fixture",At,1,0,0));var c=Project(r,storageError:"Synthetic missing selected drive");
        Assert.Equal(SensorScientificDataState.Unavailable,c.ScientificData.Status);Assert.Equal(SensorOperatingState.WithinCheckedLimits,c.OperatingStatus);
        Assert.Equal(AssessmentStatus.Supported,c.Report!.Assessment.Rules.Single(rule=>rule.Rule.Id=="source.availability").SourceQuality);
    }
    [Fact]public void CollectionEvidenceFaultCannotAdvertiseActiveOperatingClearance()
    {var c=Project(Report(),collectionFault:true);Assert.Equal(SensorOperatingState.NotCollecting,c.OperatingStatus);Assert.Contains("held",c.CollectionState);}
}
