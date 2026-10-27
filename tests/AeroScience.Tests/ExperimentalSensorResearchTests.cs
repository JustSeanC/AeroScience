using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

// Synthetic scientific inputs in actual local stores. No installed MSFS/hardware acceptance is implied.
public sealed class ExperimentalSensorResearchTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-sensor-"+Guid.NewGuid());
    readonly MissionStore missions;readonly ScienceStore science;readonly PersonalResearchCard card;
    readonly DateTimeOffset at=DateTimeOffset.Parse("2026-10-27T00:00:00Z");
    readonly Guid flight=Guid.NewGuid(),dataset=Guid.NewGuid(),session=Guid.NewGuid();
    public ExperimentalSensorResearchTests(){var p=new StorageManager(new PreferenceStore(root)).Initialize();missions=new(Path.Combine(p.MissionDirectory,"missions.db"));missions.Initialize();science=new(p.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;card=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),ExperimentalSensorResearch.Template,ResearchEntryIntent.FlyNow)).Card;}
    ScienceObservation Input(int i,double? value=null)=>new(i+1,dataset,flight,"ambient-temperature",at.AddSeconds(i),"temperature",value??(i<10?10:20),"degC",ScienceProvenance.SimulatorReported,"Synthetic original SIM temperature",null,null,null,ObservationQuality.Valid,SessionId:session);
    (ExperimentalSensorSummary Summary,string Hash,ExperimentalSensorPoint[] Preview,bool Downsampled) Calculate(IEnumerable<ScienceObservation>? rows=null,ExperimentalSensorDefinition? rule=null,DateTimeOffset[]? breaks=null,int end=30,CancellationToken token=default)
        =>ExperimentalSensorResearch.Calculate(rows??Enumerable.Range(0,31).Select(i=>Input(i)),card with{ExperimentalSensor=rule??card.ExperimentalSensor},science.LibraryId,breaks??[],new(Guid.NewGuid(),flight,at,at.AddSeconds(end)),token);
    [Fact]public void AnalyticStepIsCausalDeterministicUnclippedAndKeepsExactInputIdentity()
    {
        var c=Calculate(rule:card.ExperimentalSensor! with{ResponseSeconds=5,Gain=2,OffsetCelsius=-1});var points=c.Preview;
        Assert.Equal(19,points[10].ModeledCelsius);Assert.Equal(39,points[10].TargetCelsius);Assert.Equal(20,points[10].InputCelsius);
        Assert.Equal(39-20*Math.Exp(-1d/5),points[11].ModeledCelsius,12);
        Assert.Equal(39-20*Math.Exp(-1),points[15].ModeledCelsius,12);
        Assert.Equal(11,points[10].ObservationId);Assert.Equal(dataset,points[10].DatasetId);Assert.Equal(session,points[10].SessionId);Assert.Equal(at.AddSeconds(10),points[10].TimestampUtc);
        Assert.Equal(c.Hash,Calculate(rule:card.ExperimentalSensor! with{Gain=2,OffsetCelsius=-1}).Hash);
        Assert.Equal(ResearchAttemptOutcome.Fail,ExperimentalSensorResearch.Outcome(c.Summary,card.ExperimentalSensor!,false).Outcome);
        var negative=Calculate(Enumerable.Range(0,31).Select(i=>Input(i,-20)),card.ExperimentalSensor! with{Gain=3,OffsetCelsius=-50,MinimumInputSpanCelsius=0});Assert.All(negative.Preview,p=>Assert.Equal(-110,p.ModeledCelsius));
    }
    [Fact]public void DeclaredCoverageAndTrackingCriteriaSeparatePassFailPartialAndInsufficient()
    {
        var r=card.ExperimentalSensor! with{MaximumTrackingErrorCelsius=15};var c=Calculate(rule:r);
        Assert.Equal(ResearchAttemptOutcome.Pass,ExperimentalSensorResearch.Outcome(c.Summary,r,false).Outcome);
        Assert.Equal(ResearchAttemptOutcome.Fail,ExperimentalSensorResearch.Outcome(c.Summary,r with{MaximumTrackingErrorCelsius=1},false).Outcome);
        Assert.Equal(ResearchAttemptOutcome.PartialCollection,ExperimentalSensorResearch.Outcome(c.Summary,r,true).Outcome);
        Assert.Equal(ResearchAttemptOutcome.PartialCollection,ExperimentalSensorResearch.Outcome(Calculate(Enumerable.Range(0,31).Select(i=>Input(i,15))).Summary,r,false).Outcome);
        Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,ExperimentalSensorResearch.Outcome(Calculate([]).Summary,r,false).Outcome);
        Assert.Equal(ResearchAttemptOutcome.PartialCollection,ExperimentalSensorResearch.Outcome(Calculate(end:40).Summary,r,false).Outcome);
    }
    [Theory][InlineData("quality")][InlineData("units")][InlineData("modeled")][InlineData("calibrated")][InlineData("source")][InlineData("range")][InlineData("gap")][InlineData("session")][InlineData("dataset")][InlineData("duplicate-time")][InlineData("foreign-flight")]
    public void UnsupportedOriginalsNeverCreateAnUnbrokenPass(string kind)
    {
        var rows=Enumerable.Range(0,31).Select(i=>Input(i)).ToArray();var bad=rows[12];rows[12]=kind switch{
            "quality"=>bad with{Quality=ObservationQuality.Stale},"units"=>bad with{Unit="K"},"modeled"=>bad with{Provenance=ScienceProvenance.InstrumentModeled},
            "calibrated"=>bad with{Provenance=ScienceProvenance.Derived},"source"=>bad with{Source=""},"range"=>bad with{Value=200},
            "gap"=>bad with{TimestampUtc=at.AddSeconds(20)},"session"=>bad with{SessionId=Guid.NewGuid()},"dataset"=>bad with{DatasetId=Guid.NewGuid()},
            "foreign-flight"=>bad with{MissionId=Guid.NewGuid()},_=>bad with{TimestampUtc=rows[11].TimestampUtc}};
        var c=Calculate(rows);Assert.False(c.Summary.IntervalCovered);Assert.NotEqual(ResearchAttemptOutcome.Pass,ExperimentalSensorResearch.Outcome(c.Summary,card.ExperimentalSensor!,false).Outcome);
    }
    [Fact]public void PauseEdgesResetStateSamplingAndNeverJoinUnsupportedIntervals()
    {
        var c=Calculate(breaks:[at,at.AddSeconds(12.5),at.AddSeconds(30)]);Assert.Equal(2,c.Summary.Segments);Assert.Equal(1,c.Summary.Breaks);
        Assert.Equal(20,c.Preview.Single(p=>p.ObservationId==14).ModeledCelsius);Assert.False(c.Summary.IntervalCovered);
        var sampled=Calculate(rule:card.ExperimentalSensor! with{SamplingSeconds=2});Assert.Equal(16,sampled.Preview.Length);Assert.Equal(Enumerable.Range(0,16).Select(i=>2*i+1L),sampled.Preview.Select(p=>p.ObservationId));
    }
    [Fact]public void NumericBoundsAndCancellationAreStrictAndPreviewIsBoundedWithoutChangingAssessment()
    {
        foreach(var rule in new[]{new ExperimentalSensorDefinition(Version:2),new(ResponseSeconds:double.NaN),new(Gain:0),new(OffsetCelsius:51),new(MinimumInputCelsius:80,MaximumInputCelsius:60),new(SamplingSeconds:31),new(MinimumSamples:2),new(MaximumGapSeconds:0),new(MaximumTrackingErrorCelsius:double.PositiveInfinity),new(Question:"invalid\nquestion")})Assert.Throws<ArgumentException>(()=>ExperimentalSensorResearch.Validate(rule));
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>Calculate(token:cancel.Token));
        var many=Calculate(Enumerable.Range(0,6001).Select(i=>Input(i,i%30)),end:6000);Assert.True(many.Downsampled);Assert.InRange(many.Preview.Length,1,1000);Assert.Equal(6001,many.Summary.ModeledSamples);Assert.Equal(6000,many.Summary.SupportedSeconds);
        var changed=Calculate(Enumerable.Range(0,6001).Select(i=>Input(i,i==5001?20:i%30)),end:6000);Assert.NotEqual(many.Hash,changed.Hash);
    }
    TelemetryFrame Frame(int i)=>new(at.AddSeconds(i),TelemetrySource.Simulated,"Synthetic sensor fixture",40,-75,1000,null,100,100,100,0,0,0,0,0);
    PersonalResearchAttempt Saved(bool readings=true)
    {
        var plan=missions.CreateResearchPlan(card.Id,1);var id=missions.Create("Sensor evidence flight",Frame(0),at,plan:plan);
        for(var i=0;i<=30;i++)missions.Append(id,Frame(i));missions.Finish(id,MissionStatus.Completed,at.AddSeconds(30),"Synthetic end");
        if(readings){var d=science.EnsureDataset(missions.Detail(id)!.Mission,new("ambient-temperature",true),missions.Research(id));science.Append(Enumerable.Range(0,31).Select(i=>Input(i,i<10?15:16) with{Id=0,MissionId=id,DatasetId=d.Id}).ToArray());}
        return missions.EvaluateResearchAttempt(new(Guid.NewGuid(),id,at,at.AddSeconds(30),CardId:card.Id),science:science);
    }
    [Fact]public void StarterReusesExistingAcquisitionAndDefinitionsRemainFrozenAcrossFlightsAndRestart()
    {
        Assert.Equal(6,card.Version);Assert.Null(card.FlightRegime);var plan=missions.CreateResearchPlan(card.Id,1);Assert.True(plan.StartScienceArmed);var instrument=Assert.Single(plan.Instruments!,i=>i.Enabled);Assert.Equal("ambient-temperature",instrument.InstrumentId);Assert.Equal(1000,instrument.IntervalMs);
        var another=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),ExperimentalSensorResearch.Template,ResearchEntryIntent.FlyNow));Assert.Equal(card.Id,another.Card.Id);
        var customized=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),ExperimentalSensorResearch.Template,ResearchEntryIntent.Customize,new(card.Id,1)));Assert.NotEqual(card.Id,customized.Card.Id);
        var a=Saved();var b=Saved();Assert.Equal(ResearchAttemptOutcome.Pass,a.Outcome);Assert.True(missions.ReviewExperimentalSensorEvidence(a,science).MatchesSavedEvidence);
        missions.SaveResearchCard(new("Changed sensor",null,card.Id,1,ExperimentalSensor:card.ExperimentalSensor! with{Gain=2}));var reopened=new MissionStore(missions.DatabasePath);reopened.Initialize();
        Assert.Equal(1,reopened.ResearchAttempt(a.Id).Card.Revision);Assert.Equal(1,reopened.ResearchAttempt(a.Id).Card.ExperimentalSensor!.Gain);
        var compare=reopened.CompareResearch(card.Id,new([new(a.Id,ResearchAttemptKind.PostflightInterval,a.Id),new(b.Id,ResearchAttemptKind.PostflightInterval,b.Id)]),science:science);
        Assert.All(compare.Entries,e=>Assert.True(e.ExperimentalSensor!.MatchesSavedEvidence));Assert.False(compare.DifferentRequirements);
        Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,Saved(false).Outcome);
        Assert.Throws<ArgumentException>(()=>missions.SaveResearchCard(new("Ambiguous",new(100,5,3000,100,30),ExperimentalSensor:new())));
        Assert.True(MissionStore.CompatibleResearchCards(card,missions.SaveResearchCard(new("Regime",new(100,5,3000,100,30)))));
    }
    [Fact]public void ChangedInputsConfigurationWindowOrSavedOutcomeAreNotPresentedAsVerified()
    {
        var result=Saved();Assert.False(missions.ReviewExperimentalSensorEvidence(result with{Outcome=ResearchAttemptOutcome.Fail},science).MatchesSavedEvidence);
        Assert.False(missions.ReviewExperimentalSensorEvidence(result with{Card=result.Card with{ExperimentalSensor=result.Card.ExperimentalSensor! with{Gain=2}}},science).MatchesSavedEvidence);
        Assert.False(missions.ReviewExperimentalSensorEvidence(result with{EndUtc=result.EndUtc.AddMilliseconds(1)},science).MatchesSavedEvidence);
        using var db=new SqliteConnection("Data Source="+science.DatabasePath);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE observations SET payload_json=json_set(payload_json,'$.value',77) WHERE mission_id=$id";cmd.Parameters.AddWithValue("$id",result.FlightId.ToString());cmd.ExecuteNonQuery();
        var review=missions.ReviewExperimentalSensorEvidence(result,science);Assert.False(review.MatchesSavedEvidence);Assert.Empty(review.Preview);
    }
    [Fact]public void PermanentScienceDeletionPurgesDependentResultsAndBlocksTelemetryBackfill()
    {
        var a=Saved();var b=Saved();var before=missions.StreamSamples(a.FlightId).Count();var preview=science.PreviewCollectedDeletion(new([a.FlightId]),missions);Assert.Empty(preview.Blockers);
        var deleted=science.DeleteCollectedScience(new(preview.Id,preview.SelectionHash),missions,new MissionCoordinator(missions,1000).EnterMaintenance);Assert.Equal("Completed",deleted.State);
        Assert.Empty(missions.ResearchAttempts(flightId:a.FlightId));Assert.Throws<InvalidOperationException>(()=>missions.ReviewExperimentalSensorEvidence(a,science));Assert.Equal(before,missions.StreamSamples(a.FlightId).Count());
        Assert.Throws<InvalidOperationException>(()=>missions.EvaluateResearchAttempt(new(Guid.NewGuid(),a.FlightId,at,at.AddSeconds(30),CardId:card.Id),science:science));Assert.True(missions.ReviewExperimentalSensorEvidence(b,science).MatchesSavedEvidence);
    }
    [Fact]public void NativeFullUnicodeDefinitionsAndMultipleCompatibleCardsUseExistingBoundedPagesAndAttempts()
    {
        var first=missions.SaveResearchCard(new(new string('漢',100),null,ExperimentalSensor:new(Question:new string('漢',500))));
        var second=missions.SaveResearchCard(new("Second model",null,ExperimentalSensor:new(ResponseSeconds:2)));
        var plan=missions.SavePlan(new(Guid.NewGuid(),"Compatible modeled cards",Instruments:[new("ambient-temperature",true)],ResearchCards:[new(first.Id,1),new(second.Id,1)]));
        var clock=at;var coordinator=new MissionCoordinator(missions,1000,utcNow:()=>clock);var flight=coordinator.Start(new("Native model controls",PlanId:plan.Id));var native=new ToolbarCommandHandler(coordinator);var visited=new HashSet<Guid>();int? offset=0;
        while(offset is {} page){var reply=native.Handle(System.Text.Json.JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"research-state",MissionId:flight.Id,Offset:page),WireJson.Options));Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(reply),1,8000);
            using var json=System.Text.Json.JsonDocument.Parse(reply);Assert.True(json.RootElement.GetProperty("ok").GetBoolean(),reply);var state=System.Text.Json.JsonSerializer.Deserialize<ResearchFlightState>(json.RootElement.GetProperty("research"),WireJson.Options)!;Assert.Equal(1,state.PageSize);visited.Add(Assert.Single(state.Cards).Card.Id);offset=state.NextOffset;}
        Assert.Equal(2,visited.Count);
        ResearchCardReceipt Control(PersonalResearchCard c,ResearchCardAction action,int time){clock=at.AddSeconds(time);var state=missions.ResearchOperations(flight.Id);return missions.ControlResearchCard(new(1,flight.Id,Guid.NewGuid(),state.Revision,action,c.Id,state.Cards.Single(x=>x.Card.Id==c.Id).OpenAttempt?.Id),at.AddSeconds(time));}
        Control(first,ResearchCardAction.Activate,0);Control(second,ResearchCardAction.Activate,0);
        Control(first,ResearchCardAction.BeginAttempt,1);Control(second,ResearchCardAction.BeginAttempt,1);Control(first,ResearchCardAction.EndAttempt,2);Control(first,ResearchCardAction.BeginAttempt,3);Control(first,ResearchCardAction.EndAttempt,4);Control(second,ResearchCardAction.Deactivate,5);
        Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);Assert.Equal(3,missions.RecordedResearchAttempts(flight.Id).Length);Assert.True(missions.ResearchOperations(flight.Id).Cards.Single(x=>x.Card.Id==first.Id).Active);
        coordinator.End(flight.Id);Assert.All(missions.ResearchOperations(flight.Id).Cards,c=>Assert.False(c.Active));
        var original=missions.ResearchCard(first.Id);Assert.Throws<NotSupportedException>(()=>MissionStore.CompatibleResearchCards(original with{Version=5},second));
    }
    public void Dispose(){science.Dispose();SqliteConnection.ClearAllPools();try{Directory.Delete(root,true);}catch(IOException){}}
}
