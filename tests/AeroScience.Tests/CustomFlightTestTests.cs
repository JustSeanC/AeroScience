using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

// Synthetic recorded telemetry in the real existing mission/evidence store; not installed MSFS acceptance.
public sealed class CustomFlightTestTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-custom-point-"+Guid.NewGuid());
    readonly MissionStore store;readonly DateTimeOffset at=DateTimeOffset.Parse("2026-10-27T00:00:00Z");
    static FlightRegimeDefinition Rule(bool speed=true,bool altitude=true)=>new(120,5,3000,100,3,new(AirspeedRequired:speed,AltitudeRequired:altitude));
    public CustomFlightTestTests(){store=new(Path.Combine(root,"missions.db"));store.Initialize();}
    TelemetryFrame Frame(int i,double speed=120,double altitude=3000)=>new(at.AddSeconds(i),TelemetrySource.Simulated,"Explicit custom flight fixture",40,-75,altitude,null,speed,150,150,0,0,0,0,0);
    [Theory][InlineData(true,true,120,3000,ResearchAttemptOutcome.Pass)][InlineData(true,true,126,3000,ResearchAttemptOutcome.Fail)]
    [InlineData(true,true,120,3101,ResearchAttemptOutcome.Fail)][InlineData(true,false,120,99999,ResearchAttemptOutcome.Pass)]
    [InlineData(false,true,-1,3000,ResearchAttemptOutcome.Pass)][InlineData(true,false,-1,3000,ResearchAttemptOutcome.InsufficientEvidence)]
    public void SelectedConditionsUseTheSameContinuousWindowAndUnselectedSignalsAreNotRequired(bool speed,bool altitude,double ias,double msl,ResearchAttemptOutcome expected)
    {
        var window=new FlightRegimeEvaluation.Window(Rule(speed,altitude),at,at.AddSeconds(4));for(var i=0;i<=4;i++)window.Add(Frame(i,ias,msl));Assert.Equal(expected,window.Finish(false).Outcome);
        Assert.Equal(FlightRegimeEvaluation.CustomVersion,FlightRegimeEvaluation.VersionFor(Rule(speed,altitude)));
    }
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public void MissingUnselectedNumericSignalDoesNotContaminateTheRequiredSignal(bool speed,bool altitude)
    {
        var window=new FlightRegimeEvaluation.Window(Rule(speed,altitude),at,at.AddSeconds(4));for(var i=0;i<=4;i++)window.Add(Frame(i,speed?120:double.NaN,altitude?3000:double.NaN));Assert.Equal(ResearchAttemptOutcome.Pass,window.Finish(false).Outcome);
        // The recorder itself still retains only its supported serializable frames; this does not synthesize a missing whole frame.
    }
    [Theory][InlineData("gap")][InlineData("paused")][InlineData("source")][InlineData("aircraft")][InlineData("missing-selected")][InlineData("backwards")][InlineData("interrupted")][InlineData("late-start")][InlineData("trailing-gap")]
    public void EvenAnEarlierQualifiedHoldCannotPassAnIncompleteCustomInterval(string problem)
    {
        var end=problem=="trailing-gap"?10:8;var w=new FlightRegimeEvaluation.Window(Rule(),at,at.AddSeconds(end));
        for(var i=0;i<=8;i++){
            if(problem=="gap"&&i==5||problem=="late-start"&&i<2)continue;
            var f=Frame(i);if(i==5)f=problem switch{"paused"=>f with{IsPaused=true},"source"=>f with{Source=TelemetrySource.Simulator},"aircraft"=>f with{Aircraft="Changed aircraft"},"missing-selected"=>f with{IndicatedAirspeedKnots=double.NaN},"backwards"=>f with{TimestampUtc=at},_=>f};w.Add(f);
        }
        Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,w.Finish(problem=="interrupted").Outcome);
        var legacy=new FlightRegimeEvaluation.Window(Rule() with{TestPoint=null},at,at.AddSeconds(8));for(var i=0;i<=8;i++)legacy.Add(Frame(i) with{IsPaused=i==5});Assert.Equal(ResearchAttemptOutcome.Pass,legacy.Finish(false).Outcome); // historical v1 policy unchanged
    }
    [Fact]public void BoundaryAndSimultaneousAndDoNotAccumulateSeparateSuccessfulConditions()
    {
        var w=new FlightRegimeEvaluation.Window(Rule(),at,at.AddSeconds(6));for(var i=0;i<=6;i++)w.Add(i<3?Frame(i,120,3200):Frame(i,140,3000));Assert.Equal(ResearchAttemptOutcome.Fail,w.Finish(false).Outcome);Assert.Equal(0,w.LongestHold);
        var boundary=new FlightRegimeEvaluation.Window(Rule(),at,at.AddSeconds(3));for(var i=0;i<=3;i++)boundary.Add(Frame(i,125,3100));Assert.Equal(ResearchAttemptOutcome.Pass,boundary.Finish(false).Outcome);
    }
    [Fact]public void UnknownFieldsVersionsOrOperationsArePreservedOnReadAndRejectedBeforeSaving()
    {
        var json=JsonSerializer.Serialize(Rule(),WireJson.Options);var unknown=JsonSerializer.Deserialize<FlightRegimeDefinition>(json[..^1]+",\"groups\":[{\"signal\":\"mach\"}]}",WireJson.Options)!;Assert.NotNull(unknown.Extensions);Assert.Contains("groups",JsonSerializer.Serialize(unknown,WireJson.Options));
        Assert.Throws<ArgumentException>(()=>store.SaveResearchCard(new("Unsupported custom",unknown)));
        foreach(var p in new[]{new CustomFlightTestPoint(Version:2),new(Policy:"or"),new(AirspeedRequired:false,AltitudeRequired:false),new(Name:""),new(Description:new string('a',501))})Assert.Throws<ArgumentException>(()=>store.SaveResearchCard(new("Invalid",Rule() with{TestPoint=p})));
        var future=JsonSerializer.Deserialize<FlightRegimeDefinition>(json.Replace("\"policy\":\"all\"","\"policy\":\"all\",\"repetitions\":2"),WireJson.Options)!;Assert.Contains("repetitions",JsonSerializer.Serialize(future,WireJson.Options));Assert.Throws<ArgumentException>(()=>store.SaveResearchCard(new("Future",future)));Assert.Empty(store.ResearchCards());
        Assert.DoesNotContain("testPoint",JsonSerializer.Serialize(Rule() with{TestPoint=null},WireJson.Options));
    }
    [Fact]public void OnlySelectedConfigurationValuesAreRequiredButNoNonfiniteOrEmptyDefinitionsAreAccepted()
    {
        FlightRegimeEvaluation.Validate(Rule(false,true) with{AirspeedKnots=0,AirspeedToleranceKnots=0});FlightRegimeEvaluation.Validate(Rule(true,false) with{AltitudeMslFeet=0,AltitudeToleranceFeet=0});
        foreach(var r in new[]{Rule() with{HoldSeconds=0},Rule() with{AirspeedToleranceKnots=-1},Rule() with{AltitudeToleranceFeet=0},Rule(false,true) with{AirspeedKnots=double.NaN}})Assert.Throws<ArgumentException>(()=>FlightRegimeEvaluation.Validate(r));
    }
    (Guid Flight,PersonalResearchAttempt Result) Saved(PersonalResearchCard card,double speed=120,double altitude=3000)
    {
        var plan=store.CreateResearchPlan(card.Id,card.Revision);var id=store.Create("Custom point recorded fixture",Frame(0,speed,altitude),at,plan:plan);for(var i=0;i<=5;i++)store.Append(id,Frame(i,speed,altitude));store.Finish(id,MissionStatus.Completed,at.AddSeconds(5),"Fixture end");return(id,store.EvaluateResearchAttempt(new(Guid.NewGuid(),id,at,at.AddSeconds(5),CardId:card.Id)));
    }
    [Fact]public void CustomCardsPrepareViaTheExistingPlannerAndFreezeRevisionsAcrossRestartComparisonAndAudit()
    {
        var card=store.SaveResearchCard(new("My authored test",Rule()));Assert.Equal(7,card.Version);Assert.Equal(FlightRegimeEvaluation.CustomTemplate,card.TemplateId);var (flight,a)=Saved(card);Assert.Equal(ResearchAttemptOutcome.Pass,a.Outcome);Assert.Equal(7,a.Version);Assert.True(store.ReviewFlightTestEvidence(a).MatchesSavedEvidence);
        var revision=store.SaveResearchCard(new(card.Name,Rule() with{TestPoint=Rule().TestPoint! with{Description="A revised question."}},card.Id,1));Assert.Equal(2,revision.Revision);var (_,b)=Saved(revision,140);Assert.Equal(ResearchAttemptOutcome.Fail,b.Outcome);
        var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();Assert.Equal(1,reopened.ResearchAttempt(a.Id).Card.Revision);Assert.Equal(6,reopened.StreamSamples(flight).Count());Assert.Equal(Rule().TestPoint!.Description,reopened.ResearchAttempt(a.Id).Card.FlightRegime!.TestPoint!.Description);
        var compare=reopened.CompareResearch(card.Id,new([new(a.Id,ResearchAttemptKind.PostflightInterval,a.Id),new(b.Id,ResearchAttemptKind.PostflightInterval,b.Id)]));Assert.True(compare.DifferentRevisions);Assert.True(compare.DifferentRequirements);Assert.All(compare.Entries,e=>Assert.True(e.Evidence!.MatchesSavedEvidence));Assert.Equal(2,reopened.ResearchOverview(card.Id).EvaluatedAttempts);
        Assert.False(reopened.ReviewFlightTestEvidence(a with{Card=a.Card with{FlightRegime=Rule() with{HoldSeconds=1}}}).MatchesSavedEvidence);Assert.False(reopened.ReviewFlightTestEvidence(a with{Outcome=ResearchAttemptOutcome.Fail}).MatchesSavedEvidence);Assert.False(reopened.ReviewFlightTestEvidence(a with{LongestSupportedHoldSeconds=999}).MatchesSavedEvidence);
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>reopened.ReviewFlightTestEvidence(a,cancel.Token));
        Assert.Throws<ArgumentException>(()=>store.SaveResearchCard(new(card.Name,Rule() with{TestPoint=null},card.Id,2)));
    }
    [Fact]public void MslOnlyCanBeRecordedAndEvaluatedWithoutAUsableIasAndWithoutAScientificLibrary()
    {
        var card=store.SaveResearchCard(new("Only altitude",Rule(false,true)));var (_,r)=Saved(card,-1);Assert.Equal(ResearchAttemptOutcome.Pass,r.Outcome);Assert.True(store.ReviewFlightTestEvidence(r).MatchesSavedEvidence);
    }
    [Fact]public void ConflictsApplyOnlyToDimensionsRequiredByBothCardsAndNativeFullDefinitionsStayBounded()
    {
        var speed=store.SaveResearchCard(new("IAS only",Rule(true,false)));var altitude=store.SaveResearchCard(new("MSL only",Rule(false,true)));Assert.True(MissionStore.CompatibleResearchCards(speed,altitude));
        var conflicting=store.SaveResearchCard(new("Incompatible IAS",Rule(true,false) with{AirspeedKnots=200}));Assert.False(MissionStore.CompatibleResearchCards(speed,conflicting));Assert.True(MissionStore.CompatibleResearchCards(altitude,conflicting));
        var text=new string('漢',500);var full=store.SaveResearchCard(new(new string('漢',100),Rule() with{TestPoint=new(Name:new string('漢',100),Description:text)}));
        var coordinator=new MissionCoordinator(store,1000);var flight=coordinator.Start(new("Custom native packet",PlanId:store.CreateResearchPlan(full.Id,1).Id));var reply=new ToolbarCommandHandler(coordinator).Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"research-state",MissionId:flight.Id),WireJson.Options));Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(reply),1,8000);using var json=JsonDocument.Parse(reply);Assert.True(json.RootElement.GetProperty("ok").GetBoolean(),reply);var state=json.RootElement.GetProperty("research").Deserialize<ResearchFlightState>(WireJson.Options)!;Assert.Equal(1,state.PageSize);Assert.Equal(text,Assert.Single(state.Cards).Card.FlightRegime!.TestPoint!.Description);coordinator.End(flight.Id);
    }
    [Fact]public void FutureCardEnvelopesRemainReadableButCannotBeDowngradedOrPrepared()
    {
        var card=store.SaveResearchCard(new("Future stored card",Rule()));using(var db=new SqliteConnection("Data Source="+store.DatabasePath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE personal_research_cards SET payload_json=json_set(payload_json,'$.version',8,'$.futurePoints',json('[{\"name\":\"Preserve me\"}]')) WHERE id=$id";cmd.Parameters.AddWithValue("$id",card.Id.ToString());cmd.ExecuteNonQuery();}
        var future=store.ResearchCard(card.Id);Assert.Equal(8,future.Version);Assert.NotNull(future.Extensions);Assert.Contains("Preserve me",JsonSerializer.Serialize(future,WireJson.Options));
        Assert.Throws<NotSupportedException>(()=>store.SaveResearchCard(new(card.Name,Rule(),card.Id,1)));Assert.Throws<NotSupportedException>(()=>store.CreateResearchPlan(card.Id,1));Assert.Equal(1,store.ResearchCard(card.Id).Revision);
        var input=JsonSerializer.Deserialize<SaveResearchCardRequest>("{\"name\":\"Unsupported envelope\",\"flightRegime\":"+JsonSerializer.Serialize(Rule(),WireJson.Options)+",\"points\":[{\"name\":\"Not discarded\"}]}",WireJson.Options)!;
        Assert.NotNull(input.Extensions);Assert.Throws<ArgumentException>(()=>store.SaveResearchCard(input));
    }
    public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
