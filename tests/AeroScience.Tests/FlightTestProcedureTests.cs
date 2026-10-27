using System.Text.Json;
using System.Text;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;

// Deterministic simulator fixtures and actual existing store/coordinator, not installed MSFS acceptance.
public sealed class FlightTestProcedureTests:IDisposable
{
 readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-procedure-"+Guid.NewGuid());
 readonly MissionStore store;readonly MissionCoordinator coordinator;readonly DateTimeOffset start=DateTimeOffset.Parse("2026-10-27T00:00:00Z");
 DateTimeOffset time;int second;TelemetrySource recordedSource=TelemetrySource.Simulated;string aircraft="Explicit P11 simulator fixture";PersonalResearchCard card=null!;Mission flight=null!;
 public static FlightTestProcedureDefinition Definition(bool ordered=true,string policy="all",int hold=10)=>new(1,ordered,[
  new(Guid.Parse("11111111-1111-4111-8111-111111111111"),new(120,5,3000,100,hold,new(Name:"Low-Speed Stability",Description:"Demonstrate IAS.",AltitudeRequired:false,Policy:policy))),
  new(Guid.Parse("22222222-2222-4222-8222-222222222222"),new(120,5,3000,100,hold,new(Name:"Altitude Demonstration",Description:"Demonstrate altitude.",AirspeedRequired:false)),2)]);
 public FlightTestProcedureTests(){store=new(Path.Combine(root,"missions.db"));store.Initialize();time=start;coordinator=new(store,1000,utcNow:()=>time);}
 TelemetryFrame Frame(int i,double speed=120,double altitude=3000,bool paused=false)=>new(start.AddSeconds(i),recordedSource,aircraft,40,-75,altitude,null,speed,150,150,0,0,0,0,0,paused);
 void Observe(int i,double speed=120,double altitude=3000,bool paused=false){second=i;time=start.AddSeconds(i);coordinator.Apply(new(new(ConnectionState.Connected,recordedSource,"Fixture",time),Frame(i,speed,altitude,paused)));}
 void Begin(FlightTestProcedureDefinition? definition=null){card=store.SaveResearchCard(new("Operating Envelope Demonstration",null,Procedure:definition??Definition()));Observe(0);flight=coordinator.Start(new("One continuous procedure flight",PlanId:store.CreateResearchPlan(card.Id,card.Revision).Id));Control(ResearchCardAction.Activate);}
 ResearchCardResult Control(ResearchCardAction action,int? point=null,Guid? requestId=null){var s=coordinator.PersonalResearch(flight.Id);return coordinator.ControlPersonalResearch(new(1,flight.Id,requestId??Guid.NewGuid(),s.Revision,action,card.Id,s.Cards.Single().OpenAttempt?.Id,point is {} i?card.Procedure!.Points[i].Id:null));}
 RecordedResearchAttempt Attempt(int point,int duration=10,double speed=120,double altitude=3000,bool paused=false){Observe(second+1,speed,altitude);var id=Control(ResearchCardAction.BeginAttempt,point).Receipt.AttemptId!.Value;var first=second;for(var i=1;i<=duration;i++)Observe(first+i,speed,altitude,paused&&i==duration/2);Control(ResearchCardAction.EndAttempt);return store.RecordedResearchAttempt(id);}
 PersonalResearchAttempt Finish(){Observe(second+1);coordinator.End(flight.Id);return store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight.Id,flight.StartedUtc,time,CardId:card.Id));}
 [Fact]public void ExampleRequiresOrderedIasThenTwoDistinctAltitudeAttemptsAndReopensOffline()
 {
  Begin();Assert.Throws<ArgumentException>(()=>Control(ResearchCardAction.BeginAttempt));var a=Attempt(0);Assert.Equal(ResearchAttemptOutcome.Pass,a.PointEvidence!.Outcome);Assert.Equal(card.Procedure!.Points[0].Id,a.TestPointId);
  var b=Attempt(1,25);var progress=coordinator.PersonalResearch(flight.Id).Cards.Single().Procedure!;Assert.Equal(1,progress.Points[1].CompletedRepetitions);Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,progress.Outcome); // a 25 s hold is still just one repetition
  var c=Attempt(1);var result=Finish();Assert.Equal(ResearchAttemptOutcome.Pass,result.Outcome);Assert.Equal(new[]{1,2},result.Procedure!.Points.Select(p=>p.CompletedRepetitions));Assert.True(store.ReviewProcedureEvidence(result).MatchesSavedEvidence);Assert.Equal(3,result.Procedure.AttemptCount);Assert.Equal(3,new[]{a.Id,b.Id,c.Id}.Distinct().Count());
  var changed=store.SaveResearchCard(new(card.Name,null,card.Id,1,Procedure:card.Procedure with{Ordered=false}));Assert.Equal(2,changed.Revision);var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();Assert.Equal(1,reopened.ResearchAttempt(result.Id).Card.Revision);Assert.True(reopened.ReviewProcedureEvidence(result).MatchesSavedEvidence);Assert.Single(reopened.List());Assert.Equal(3,reopened.RecordedResearchAttempts(flight.Id).Length);
 }
 [Fact]public void EarlyAndFailedAttemptsStayVisibleAndDoNotRetroactivelyQualify()
 {
  Begin();Attempt(1);Attempt(0,speed:150);Attempt(0);Attempt(1);var result=Finish();Assert.NotEqual(ResearchAttemptOutcome.Pass,result.Outcome);Assert.Equal(1,result.Procedure!.Points[1].CompletedRepetitions);var review=store.ReviewProcedureEvidence(result);Assert.True(review.MatchesSavedEvidence);Assert.Equal(4,review.Preview.Length);Assert.False(review.Preview[0].Qualified);Assert.Contains("not retroactively",review.Preview[0].Qualification);Assert.Contains(review.Preview,a=>a.Attempt.PointEvidence!.Outcome==ResearchAttemptOutcome.Fail);Assert.Equal(ResearchAttemptOutcome.Pass,review.Preview[0].Attempt.PointEvidence!.Outcome);
 }
 [Fact]public void IndependentOrderAndMultipleRetriesUseDistinctExplicitIdentities()
 {
  Begin(Definition(false));Attempt(1);Attempt(1);Attempt(0);var r=Finish();Assert.Equal(ResearchAttemptOutcome.Pass,r.Outcome);Assert.All(r.Procedure!.Points,p=>Assert.Equal(ResearchAttemptOutcome.Pass,p.Outcome));
 }
 [Theory][InlineData("all",ResearchAttemptOutcome.Fail)][InlineData("any",ResearchAttemptOutcome.Pass)]
 public void AndAndOrShareOriginalHoldRules(string policy,ResearchAttemptOutcome expected)
 {
  var r=Definition(policy:policy).Points[0].Conditions with{TestPoint=new(AirspeedRequired:true,AltitudeRequired:true,Policy:policy)};var w=new FlightRegimeEvaluation.PointWindow(r,start,start.AddSeconds(10));for(var i=0;i<=10;i++)w.Add(Frame(i,150,3000));var e=w.Finish(Guid.NewGuid(),new(0,0,11,"hash","plan",TelemetrySource.Simulated),false);Assert.Equal(expected,e.Outcome);Assert.Equal(ResearchAttemptOutcome.Fail,e.Conditions[0].Outcome);Assert.Equal(ResearchAttemptOutcome.Pass,e.Conditions[1].Outcome);
 }
 [Fact]public void OrCannotJoinAlternatingConditionsButCanUseOneSupportedBranch()
 {
  var r=Definition().Points[0].Conditions with{TestPoint=new(Policy:"any")};var w=new FlightRegimeEvaluation.PointWindow(r,start,start.AddSeconds(10));for(var i=0;i<=10;i++)w.Add(i<6?Frame(i,120,4000):Frame(i,150,3000));Assert.Equal(ResearchAttemptOutcome.Fail,w.Finish(Guid.NewGuid(),new(0,0,11,"h","p",null),false).Outcome);
  w=new(r,start,start.AddSeconds(10));for(var i=0;i<=10;i++)w.Add(Frame(i,double.NaN,3000));var e=w.Finish(Guid.NewGuid(),new(0,0,11,"h","p",null),false);Assert.Equal(ResearchAttemptOutcome.Pass,e.Outcome);Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,e.Conditions[0].Outcome);Assert.Equal(ResearchAttemptOutcome.Pass,e.Conditions[1].Outcome);
 }
 [Theory][InlineData("pause")][InlineData("gap")][InlineData("source")][InlineData("aircraft")][InlineData("missing")][InlineData("interrupted")]
 public void IncompleteInputsCannotQualifyEvenWithAnEarlierHold(string issue)
 {
  var r=Definition().Points[0].Conditions;var w=new FlightRegimeEvaluation.PointWindow(r,start,start.AddSeconds(15));for(var i=0;i<=15;i++){if(issue=="gap"&&i==13)continue;var f=Frame(i);if(i==13)f=issue switch{"pause"=>f with{IsPaused=true},"source"=>f with{Source=TelemetrySource.Simulator},"aircraft"=>f with{Aircraft="Other"},"missing"=>f with{IndicatedAirspeedKnots=-1},_=>f};w.Add(f);}Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,w.Finish(Guid.NewGuid(),new(0,0,1,"h","p",null),issue=="interrupted").Outcome);
 }
 [Fact]public void InterruptedAttemptAndRestartRemainInsufficientAndDoNotStopNormalRecording()
 {
  Begin(Definition(hold:2));Observe(1);Control(ResearchCardAction.BeginAttempt,0);Observe(2);Control(ResearchCardAction.Deactivate);Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);Control(ResearchCardAction.Activate);Observe(3);Control(ResearchCardAction.BeginAttempt,1);Observe(4);coordinator.Interrupt();store.RecoverInterrupted();var r=store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight.Id,start,start.AddSeconds(4),CardId:card.Id));Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,r.Outcome);Assert.All(store.RecordedResearchAttempts(flight.Id),a=>Assert.Equal(ResearchAttemptStatus.Interrupted,a.Status));Assert.True(store.ReviewProcedureEvidence(r).MatchesSavedEvidence);
 }
 [Fact]public void DefinitionAndEvidenceTamperingCannotBecomeVerified()
 {
  Begin(Definition(false,hold:1));Attempt(0,2);Attempt(1,2);Attempt(1,2);var r=Finish();Assert.True(store.ReviewProcedureEvidence(r).MatchesSavedEvidence);Assert.False(store.ReviewProcedureEvidence(r with{Outcome=ResearchAttemptOutcome.Fail}).MatchesSavedEvidence);Assert.False(store.ReviewProcedureEvidence(r with{Card=r.Card with{Procedure=r.Card.Procedure! with{Ordered=true}}}).MatchesSavedEvidence);
  using(var db=new SqliteConnection("Data Source="+store.DatabasePath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE telemetry SET payload_json=json_set(payload_json,'$.indicatedAirspeedKnots',500) WHERE mission_id=$flight";cmd.Parameters.AddWithValue("$flight",flight.Id.ToString());cmd.ExecuteNonQuery();}Assert.False(store.ReviewProcedureEvidence(r).MatchesSavedEvidence);
 }
 [Fact]public void ValidationRejectsUnknownComplexityAndOldJsonStaysUnchanged()
 {
  var d=Definition();foreach(var bad in new[]{d with{Version=2},d with{Points=[d.Points[0]]},d with{Points=[d.Points[0],d.Points[0]]},d with{Points=[d.Points[0] with{Repetitions=0},d.Points[1]]},d with{Points=[d.Points[0] with{Conditions=d.Points[0].Conditions with{TestPoint=new(Policy:"script")}},d.Points[1]]}})Assert.Throws<ArgumentException>(()=>store.SaveResearchCard(new("Bad",null,Procedure:bad)));
  var future=JsonSerializer.Deserialize<FlightTestProcedureDefinition>(JsonSerializer.Serialize(d,WireJson.Options).Replace("\"ordered\":true","\"ordered\":true,\"automaticDetection\":true"),WireJson.Options)!;Assert.NotNull(future.Extensions);Assert.Throws<ArgumentException>(()=>store.SaveResearchCard(new("Future",null,Procedure:future)));var legacy=store.SaveResearchCard(new("P10",new(120,5,3000,100,3,new())));Assert.DoesNotContain("procedure",JsonSerializer.Serialize(legacy,WireJson.Options));Assert.Equal(7,legacy.Version);
 }
 [Fact]public void ComparisonKeepsRevisionsRequirementsAndOriginalEvidenceAndNativePacketsBounded()
 {
  Begin(Definition(false,hold:1));Attempt(0,2);Attempt(1,2);Attempt(1,2);var a=Finish();card=store.SaveResearchCard(new(card.Name,null,card.Id,1,Procedure:card.Procedure! with{Ordered=true}));Observe(second+1);flight=coordinator.Start(new("Repeat procedure",PlanId:store.CreateResearchPlan(card.Id,2).Id));Control(ResearchCardAction.Activate);Attempt(0,2);Attempt(1,2);Attempt(1,2);var b=Finish();var compare=store.CompareResearch(card.Id,new([new(a.Id,ResearchAttemptKind.PostflightInterval,a.Id),new(b.Id,ResearchAttemptKind.PostflightInterval,b.Id)]));Assert.All(compare.Entries,e=>Assert.True(e.Procedure!.MatchesSavedEvidence));Assert.True(compare.DifferentRevisions);Assert.True(compare.DifferentRequirements);
  var definition=Definition();definition=definition with{Points=definition.Points.Select(p=>p with{Conditions=p.Conditions with{TestPoint=p.Conditions.TestPoint! with{Name=new string('漢',100),Description=new string('漢',500)}}}).Concat(definition.Points.Select(p=>p with{Id=Guid.NewGuid()})).ToArray()};
  card=store.SaveResearchCard(new(new string('漢',100),null,Procedure:definition));Observe(second+1);flight=coordinator.Start(new("Native large procedure",PlanId:store.CreateResearchPlan(card.Id,1).Id));var reply=new ToolbarCommandHandler(coordinator).Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"research-state",MissionId:flight.Id),WireJson.Options));Assert.InRange(Encoding.UTF8.GetByteCount(reply),1,8000);using var json=JsonDocument.Parse(reply);Assert.True(json.RootElement.GetProperty("ok").GetBoolean(),reply);coordinator.End(flight.Id);
 }
 [Fact]public void OverlongPointCanBeClosedWithoutBlockingFlightAndCancelledReviewCannotPublishSuccess()
 {
  Begin(Definition(hold:1));Observe(1);Control(ResearchCardAction.BeginAttempt,0);Observe(7300);Control(ResearchCardAction.EndAttempt);
  Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);var a=Assert.Single(store.RecordedResearchAttempts(flight.Id));Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,a.PointEvidence!.Outcome);Assert.Contains("two-hour",a.PointEvidence.Explanation);
  var r=Finish();using var c=new CancellationTokenSource();c.Cancel();Assert.Throws<OperationCanceledException>(()=>store.ReviewProcedureEvidence(r,c.Token));
 }
 [Theory][InlineData(true)][InlineData(false)]public void DifferentAircraftOrSimulatorSourcesCannotCombineAcrossRepetitions(bool changeSource)
 {
  Begin(Definition(hold:1));Attempt(0,2);if(changeSource)recordedSource=TelemetrySource.Simulator;else aircraft="Different aircraft";
  Attempt(1,2);Attempt(1,2);var result=Finish();Assert.NotEqual(ResearchAttemptOutcome.Pass,result.Outcome);Assert.Equal(0,result.Procedure!.Points[1].CompletedRepetitions);Assert.Contains("source/aircraft",result.Procedure.Points[1].Explanation);
 }
 [Fact]public void ExistingSchemaTenReceivesTheAdditiveProcedureReadIndexWithoutRewritingCards()
 {
  var card=store.SaveResearchCard(new("Retained P10",new(120,5,3000,100,10,new())));var original=JsonSerializer.Serialize(card,WireJson.Options);
  using(var db=new SqliteConnection("Data Source="+store.DatabasePath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP INDEX personal_card_attempts_point_review";cmd.ExecuteNonQuery();}
  store.Initialize();Assert.Equal(original,JsonSerializer.Serialize(store.ResearchCard(card.Id),WireJson.Options));
  using var reopened=new SqliteConnection("Data Source="+store.DatabasePath);reopened.Open();using var check=reopened.CreateCommand();check.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='personal_card_attempts_point_review'";Assert.Equal(1L,check.ExecuteScalar());check.CommandText="PRAGMA user_version";Assert.Equal(10L,check.ExecuteScalar());
 }
 [Theory][InlineData("point")][InlineData("end")][InlineData("status")]
 public void OriginalControlEventsPreventRetaggedPointsExtendedWindowsOrInterruptedStatusUpgrades(string corruption)
 {
  var definition=Definition(hold:1);definition=definition with{Points=[definition.Points[0] with{Conditions=definition.Points[0].Conditions with{TestPoint=definition.Points[0].Conditions.TestPoint! with{Name=definition.Points[1].Id.ToString()}}},definition.Points[1]]};
  Begin(definition);var original=Attempt(0,2);var r=Finish();
  using(var db=new SqliteConnection("Data Source="+store.DatabasePath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText=corruption switch{
    "point"=>"UPDATE personal_card_attempts SET payload_json=json_set(payload_json,'$.testPointId',$value) WHERE id=$id",
    "end"=>"UPDATE personal_card_attempts SET payload_json=json_set(payload_json,'$.endUtc',$value) WHERE id=$id",
    _=>"UPDATE personal_card_attempts SET payload_json=json_set(payload_json,'$.status',$value) WHERE id=$id"};
   cmd.Parameters.AddWithValue("$id",original.Id.ToString());cmd.Parameters.AddWithValue("$value",corruption=="point"?definition.Points[1].Id.ToString():corruption=="end"?time.ToString("O"):"interrupted");cmd.ExecuteNonQuery();}
  Assert.Throws<InvalidOperationException>(()=>store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight.Id,flight.StartedUtc,time,CardId:card.Id)));
 }
 public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
