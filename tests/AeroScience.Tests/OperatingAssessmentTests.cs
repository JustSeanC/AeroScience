using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Xunit;
namespace AeroScience.Tests;

public sealed class OperatingAssessmentTests
{
    private static readonly DateTimeOffset At=DateTimeOffset.UtcNow;
    private static TelemetryFrame Frame(double pitch=0,double bank=0,double? agl=1000) => SimulatedTelemetryProvider.Generate(At,0) with{PitchDegrees=pitch,BankDegrees=bank,AltitudeAglFeet=agl};
    private static InstrumentOperatingReport Assess(InstrumentConfiguration config,TelemetryFrame frame,ScienceMode mode=ScienceMode.Realistic,MissionPlan? plan=null)
    {
        var effective=InstrumentCalibration.Effective(config,Guid.NewGuid(),1,At);
        return InstrumentOperatingAssessment.Evaluate(Guid.NewGuid(),null,effective,mode,
            new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic operating test",frame.TimestampUtc),frame,new(frame.TimestampUtc,80,29,10,270,.002)),plan,evaluatedUtc:At);
    }
    [Theory]
    [InlineData(ScienceMode.Realistic)]
    [InlineData(ScienceMode.Universal)]
    public void ModesShareRawValuesAndSeparateGeometryComplianceAndSourceQa(ScienceMode mode)
    {
        var frame=Frame();var json=JsonSerializer.Serialize(frame,WireJson.Options);
        var report=Assess(new("thermal-imager",true),frame,mode);
        var geometry=report.Assessment.Rules.Single(r=>r.Rule.Id=="geometry.ground-intersection");
        Assert.Equal(AssessmentStatus.Supported,geometry.DerivationValidity);
        Assert.Equal(AssessmentStatus.NotAssessed,geometry.SourceQuality);
        Assert.Equal(AssessmentStatus.Unknown,report.Assessment.Rules.Single(r=>r.Rule.Id=="source.availability").SourceQuality);
        Assert.Equal(json,JsonSerializer.Serialize(frame,WireJson.Options));Assert.Equal(mode,report.Mode);
    }
    [Fact]
    public void InvalidGroundRaysDoNotBecomeValidFootprintsAndMissingAglIsNotFailure()
    {
        Assert.Equal(AssessmentStatus.Invalid,Assess(new("thermal-imager",true),Frame(85)).Assessment.Rules.Single(r=>r.Rule.Id=="geometry.ground-intersection").DerivationValidity);
        Assert.Equal(AssessmentStatus.NotAssessed,Assess(new("thermal-imager",true),Frame(agl:null)).Assessment.Rules.Single(r=>r.Rule.Id=="geometry.ground-intersection").DerivationValidity);
        Assert.Equal(AssessmentStatus.NotAssessed,Assess(new("thermal-imager",true),Frame() with{TimestampUtc=At.AddSeconds(-20)}).Assessment.Rules.Single(r=>r.Rule.Id=="geometry.ground-intersection").DerivationValidity);
    }
    [Fact]
    public void AmbientTemperatureNeverBecomesInternalPayloadTemperature()
    {
        var profile=InstrumentProfiles.Catalog.First(p=>p.InstrumentId=="thermal-imager");
        var report=Assess(new("thermal-imager",true,Payload:new(profile)),Frame());
        var temperature=report.Assessment.Rules.Single(r=>r.Rule.Id=="environment.payload-temperature");
        Assert.Null(temperature.ObservedValue);Assert.Equal(AssessmentStatus.Unknown,temperature.Compliance);
        Assert.Contains("not a proxy",string.Join(" ",temperature.Reasons));
    }
    [Fact]
    public void ConditionalRieglExamplesNeverBecomeUniversalMaximumAltitudeLimits()
    {
        var profile=InstrumentProfiles.Catalog.Single(p=>p.InstrumentId=="terrain-scanner");
        var report=Assess(new("terrain-scanner",true,Payload:new(profile)),Frame(agl:30000));
        var rule=report.Assessment.Rules.Single(r=>r.Rule.Id=="riegl.conditional-range");
        Assert.Equal(AssessmentStatus.Unknown,rule.Compliance);Assert.Contains("reflectance",string.Join(" ",rule.Reasons));
        Assert.Equal(AssessmentStatus.Caution,Assess(new("terrain-scanner",true,Payload:new(profile)),Frame(agl:100)).Assessment.Rules.Single(r=>r.Rule.Id=="riegl.conditional-range").Compliance);
    }
    [Fact]
    public void ModeledOverlapIsComparedOnlyToAnApplicableSavedTarget()
    {
        var plan=new MissionPlan(Guid.NewGuid(),"Synthetic",SensorPlanning:new("thermal-imager",300,300,ForwardOverlap:.9));
        var report=Assess(new("thermal-imager",true,60000),Frame(),plan:plan);
        var overlap=report.Assessment.Rules.Single(r=>r.Rule.Id=="planning.forward-overlap");
        Assert.Equal(AssessmentStatus.Caution,overlap.Compliance);Assert.Equal(AssessmentStatus.NotAssessed,overlap.SourceQuality);
        Assert.Contains("not valid source-pixel coverage",string.Join(" ",overlap.Reasons));
    }
    [Fact]
    public void ArchivedAssessmentsRetainExactSettingsInputsAndReplayCutoff()
    {
        var root=Path.Combine(Path.GetTempPath(),"aeroscience-operating-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();var id=store.Create("Synthetic",null,At,ScienceMode.Realistic);
            var report=Assess(new("ambient-temperature",true,Calibration:CalibrationTests.Definition()),Frame());
            report=report with{Assessment=report.Assessment with{MissionId=id}};
            store.SaveOperatingReports(id,[report]);
            var archived=new MissionStore(store.DatabasePath).OperatingReports(id,At.AddSeconds(1)).Single();
            Assert.Equal(EvidenceContracts.ContentHash(report.Configuration.Calibration!),EvidenceContracts.ContentHash(archived.Configuration.Calibration!));
            Assert.Equal(80,archived.Input.Atmosphere!.TemperatureCelsius);Assert.Empty(store.OperatingReports(id,At.AddSeconds(-1)));
            Assert.Throws<InvalidOperationException>(()=>store.SaveOperatingReports(id,[report with{Input=report.Input with{Frame=Frame(30)}}]));
        }
        finally{Directory.Delete(root,true);}
    }
}
