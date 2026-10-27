using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;
// All input is explicitly synthetic; the store/coordinator/recorder and raw evidence are real.
public sealed class AutomaticAttemptTests:IDisposable
{
 readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-auto-attempt-"+Guid.NewGuid());
 readonly MissionStore store;readonly MissionCoordinator coordinator;readonly DateTimeOffset start=DateTimeOffset.Parse("2026-10-27T00:00:00Z");DateTimeOffset time;int tick;
 PersonalResearchCard card=null!;Mission flight=null!;bool processing=true;
 static FlightRegimeDefinition Rule(bool speed=true,bool altitude=false,string policy="all",double hold=3)=>new(120,5,3000,100,hold,new(AirspeedRequired:speed,AltitudeRequired:altitude,Policy:policy));
 public AutomaticAttemptTests(){store=new(Path.Combine(root,"missions.db"));store.Initialize();time=start;coordinator=new(store,1000,utcNow:()=>time);}
 TelemetryFrame Frame(int at,double ias=160,double altitude=3500,bool paused=false)=>new(start.AddSeconds(at),TelemetrySource.Simulated,"Explicit P12 fixture",40,-75,altitude,null,ias,150,150,0,0,0,0,0,paused);
 void Observe(int at,double ias=160,double altitude=3500,bool paused=false){tick=at;time=start.AddSeconds(at);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Fixture",time),Frame(at,ias,altitude,paused)));if(processing)store.ProcessAutomaticAttempts(flight.Id);}
 ResearchCardResult Control(ResearchCardAction action,Guid? point=null,Guid? request=null){var state=coordinator.PersonalResearch(flight.Id);return coordinator.ControlPersonalResearch(new(1,flight.Id,request??Guid.NewGuid(),state.Revision,action,card.Id,state.Cards.Single(c=>c.Card.Id==card.Id).OpenAttempt?.Id,point));}
 void Begin(FlightTestProcedureDefinition? procedure=null,FlightRegimeDefinition? rule=null){card=store.SaveResearchCard(new("Automatic local research",procedure is null?rule??Rule():null,Procedure:procedure));var frame=Frame(0);coordinator.Apply(new(new(ConnectionState.Connected,frame.Source,"Fixture",start),frame));flight=coordinator.Start(new("Continuous detection flight",PlanId:store.CreateResearchPlan(card.Id,1).Id));Control(ResearchCardAction.Activate);Control(ResearchCardAction.EnableAutomatic,procedure?.Points[0].Id);}
 RecordedResearchAttempt[] Attempts()=>store.RecordedResearchAttempts(flight.Id);
 void Drain(){for(var i=0;i<12;i++)store.ProcessAutomaticAttempts(flight.Id);}
 void Finish(bool interrupted=false){time=start.AddSeconds(++tick);if(interrupted)coordinator.Interrupt();else coordinator.End(flight.Id);Drain();store.EvaluateAutomaticResults(flight.Id);}
 [Fact]public void SuccessfulDetectionWaitsForMeasuredExitAndRetainsDistinctFailuresAndSuccesses()
 {
  Begin();Observe(1,120);Observe(2,120);Assert.Equal("Attempt Detected",coordinator.PersonalResearch(flight.Id).Cards.Single().Detection!.Status);Observe(3,120);Observe(4,120);
  var first=Assert.Single(Attempts());Assert.Equal(ResearchAttemptOutcome.Pass,first.PointEvidence!.Outcome);Assert.Equal(ResearchAttemptStatus.Recorded,first.Status);Assert.NotNull(first.Automatic);
  for(var i=5;i<=12;i++)Observe(i,120);Assert.Single(Attempts()); // one long hold is one attempt
  Observe(13,160);Observe(14,160);Observe(15,160);for(var i=16;i<=22;i++)Observe(i,128); // inside near band, outside strict target
  Assert.Equal(2,Attempts().Length);Assert.Equal(ResearchAttemptOutcome.Fail,Attempts()[1].PointEvidence!.Outcome);
  Observe(23,160);Observe(24,160);Observe(25,160);Observe(26,120);Observe(27,120);Observe(28,120);Observe(29,120);Finish();
  Assert.Equal(new[]{ResearchAttemptOutcome.Pass,ResearchAttemptOutcome.Fail,ResearchAttemptOutcome.Pass},Attempts().Select(a=>a.PointEvidence!.Outcome));Assert.All(store.ResearchAttempts(card.Id),a=>Assert.True(store.ReviewFlightTestEvidence(a).MatchesSavedEvidence));Assert.Single(store.List());
 }
 [Theory][InlineData("pause")][InlineData("gap")][InlineData("off")][InlineData("crash")]
 public void InterruptedOrMissingOriginalEvidenceNeverBecomesAnInventedFailureOrPass(string boundary)
 {
  Begin();Observe(1,120);Observe(2,120);
  if(boundary=="pause")Observe(3,120,paused:true);else if(boundary=="gap")Observe(5,120);else if(boundary=="off")Control(ResearchCardAction.DisableAutomatic);
  Finish(boundary=="crash");var a=Assert.Single(Attempts());Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,a.PointEvidence!.Outcome);Assert.Equal(ResearchAttemptStatus.Interrupted,a.Status);Assert.NotNull(a.EndEventId);
 }
 [Fact]public void ManualTakeoverWinsWithoutDuplicateQualificationAndRequiresFreshSeparation()
 {
  Begin();Observe(1,120);Observe(2,120);var detected=Assert.Single(Attempts()).Id;Control(ResearchCardAction.BeginAttempt);Observe(3,120);Observe(4,120);Observe(5,120);Control(ResearchCardAction.EndAttempt);Observe(6,120);Observe(7,120);
  var rows=Attempts();Assert.Equal(2,rows.Length);Assert.Equal(detected,rows[0].Id);Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,rows[0].PointEvidence!.Outcome);Assert.Null(rows[1].Automatic);Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);
  Observe(8,160);Observe(9,160);Observe(10,160);Observe(11,120);Observe(12,120);Observe(13,120);Observe(14,120);Finish();Assert.Equal(3,Attempts().Length);
 }
 [Fact]public void DelayedWorkerAndOfflineReplayUseTheSameOriginalBoundariesAndIdsWithoutDuplicates()
 {
  Begin();processing=false;for(var i=1;i<=4;i++)Observe(i,120);Control(ResearchCardAction.BeginAttempt);for(var i=5;i<=8;i++)Observe(i,120);Control(ResearchCardAction.EndAttempt);for(var i=9;i<=11;i++)Observe(i,160);for(var i=12;i<=15;i++)Observe(i,120);Finish();
  var before=Attempts().Select(a=>JsonSerializer.Serialize(a,WireJson.Options)).ToArray();Assert.Equal(3,before.Length);Assert.Equal(ResearchAttemptOutcome.Pass,Attempts()[0].PointEvidence!.Outcome); // completed before manual boundary, despite worker lag
  store.RecoverAutomaticAttempts(flight.Id);Drain();store.EvaluateAutomaticResults(flight.Id);Assert.Equal(before,Attempts().Select(a=>JsonSerializer.Serialize(a,WireJson.Options)));Assert.Equal(2,store.ResearchAttempts(card.Id).Length);
 }
 [Fact]public void OrderedProcedureRequiresDistinctRepetitionsAndPointResultsCannotPretendWholeCardCompletion()
 {
  var p=new FlightTestProcedureDefinition(1,true,[new(Guid.NewGuid(),Rule()),new(Guid.NewGuid(),Rule(false,true),2)]);Begin(p);
  Assert.Throws<InvalidOperationException>(()=>Control(ResearchCardAction.EnableAutomatic,p.Points[1].Id));for(var i=1;i<=4;i++)Observe(i,120);
  Control(ResearchCardAction.EnableAutomatic,p.Points[1].Id);Observe(5,120,3000);Observe(6,120,3000);Observe(7,120,3000);Observe(8,120,3000);for(var i=9;i<=15;i++)Observe(i,120,3000);Assert.Equal(1,coordinator.PersonalResearch(flight.Id).Cards.Single().Procedure!.Points[1].CompletedRepetitions);
  Observe(16,120,3500);Observe(17,120,3500);Observe(18,120,3500);for(var i=19;i<=22;i++)Observe(i,120,3000);Finish();
  var rows=store.ResearchAttempts(card.Id);Assert.Equal(4,rows.Length);Assert.Equal(3,rows.Count(a=>a.QuantitativePoint is not null));Assert.Equal(ResearchAttemptOutcome.Pass,Assert.Single(rows,a=>a.Procedure is not null).Outcome);Assert.All(rows,a=>Assert.True(store.ReviewProcedureEvidence(a).MatchesSavedEvidence));
 }
 [Theory][InlineData("all",ResearchAttemptOutcome.Fail)][InlineData("any",ResearchAttemptOutcome.Pass)]
 public void AndAndOrUseTheProvenSingleSignalContinuousWindows(string policy,ResearchAttemptOutcome expected)
 {
  var p=new FlightTestProcedureDefinition(1,false,[new(Guid.NewGuid(),Rule(true,true,policy)),new(Guid.NewGuid(),Rule())]);Begin(p);for(var i=1;i<=8;i++)Observe(i,128,3000);Finish();Assert.Equal(expected,Assert.Single(Attempts()).PointEvidence!.Outcome);
 }
 [Fact]public void ThresholdOscillationWithoutTwoConsecutiveNearReadingsDoesNotInventAttempts()
 {
  Begin();for(var i=1;i<=30;i++)Observe(i,i%2==0?120:132);Finish();Assert.Empty(Attempts());
 }
 [Fact]public void RevisionsAndOriginalConsentRemainFrozenAndRecoveryDoesNotArmAnUnselectedFlight()
 {
  Begin();var revision=store.SaveResearchCard(new(card.Name,Rule(hold:20),card.Id,1));Assert.Equal(2,revision.Revision);for(var i=1;i<=4;i++)Observe(i,120);Finish();Assert.Equal(ResearchAttemptOutcome.Pass,Assert.Single(Attempts()).PointEvidence!.Outcome);Assert.Equal(1,Assert.Single(store.ResearchAttempts(card.Id)).Card.Revision);
  var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();reopened.RecoverAutomaticAttempts(flight.Id);for(var i=0;i<4;i++)reopened.ProcessAutomaticAttempts(flight.Id);Assert.Single(reopened.RecordedResearchAttempts(flight.Id));Assert.Equal(1,reopened.ResearchOperations(flight.Id).Cards.Single().Card.Revision);
 }
 [Fact]public void RepeatedEnableAndOffOnCannotTurnOneUnbrokenHoldIntoMoreAttempts()
 {
  Begin();Observe(1,120);Observe(2,120);Observe(3,120);Observe(4,120);Control(ResearchCardAction.EnableAutomatic);for(var i=5;i<=10;i++)Observe(i,120);Assert.Single(Attempts());
  Control(ResearchCardAction.DisableAutomatic);Control(ResearchCardAction.EnableAutomatic);for(var i=11;i<=16;i++)Observe(i,120);Assert.Single(Attempts());Observe(17,160);Observe(18,160);Observe(19,160);for(var i=20;i<=23;i++)Observe(i,120);Finish();Assert.Equal(2,Attempts().Length);
 }
 [Fact]public void DisconnectionWithoutAnotherRecordedFrameFinalizesAnIncompleteAttempt()
 {
  Begin();Observe(1,120);Observe(2,120);time=start.AddSeconds(3);tick=3;coordinator.Apply(new(new(ConnectionState.Disconnected,TelemetrySource.Simulated,"Disconnected fixture",time),null));store.ProcessAutomaticAttempts(flight.Id);
  var a=Assert.Single(Attempts());Assert.Equal(ResearchAttemptStatus.Interrupted,a.Status);Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,a.PointEvidence!.Outcome);Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);Finish();
 }
 [Theory][InlineData(121)][InlineData(200)]
 public void ReplayDetectsChangedOriginalNumbersEvenWhenTimingOrRecognitionDisappears(double changed)
 {
  Begin();for(var i=1;i<=4;i++)Observe(i,120);Finish();var original=JsonSerializer.Serialize(Assert.Single(Attempts()),WireJson.Options);
  using(var db=new SqliteConnection("Data Source="+store.DatabasePath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE telemetry SET payload_json=json_set(payload_json,'$.indicatedAirspeedKnots',$value) WHERE mission_id=$flight";cmd.Parameters.AddWithValue("$value",changed);cmd.Parameters.AddWithValue("$flight",flight.Id.ToString());cmd.ExecuteNonQuery();}
  store.RecoverAutomaticAttempts(flight.Id);Drain();var detection=store.ResearchOperations(flight.Id).Cards.Single().Detection!;Assert.False(detection.Enabled);Assert.Equal("Insufficient Evidence",detection.Status);Assert.Null(detection.LastOutcome);Assert.Equal(original,JsonSerializer.Serialize(Assert.Single(Attempts()),WireJson.Options));
 }
 [Theory][InlineData("timestamp")][InlineData("source")][InlineData("aircraft")][InlineData("invalid")]
 public void RetainedSequenceAnomaliesAndOriginChangesCannotInventSuccess(string problem)
 {
  var point=new FlightTestProcedurePoint(Guid.NewGuid(),Rule());var detector=new FlightAttemptDetection(Guid.NewGuid(),point,new());detector.Add(1,Frame(1,120));detector.Add(2,Frame(2,120));var bad=Frame(3,120);bad=problem switch{"timestamp"=>bad with{TimestampUtc=start.AddSeconds(2)},"source"=>bad with{Source=TelemetrySource.Simulator},"aircraft"=>bad with{Aircraft="Other"},_=>bad with{IndicatedAirspeedKnots=double.NaN}};detector.Add(3,bad);var closed=Assert.Single(detector.Drain(),e=>e.Closed);Assert.Equal(ResearchAttemptStatus.Interrupted,closed.CompletionStatus);Assert.True(detector.State.WaitingExit);
 }
 [Fact]public void OrRecognitionCannotAlternateUnsupportedApproachBranchesToInventAnAttempt()
 {
  var point=new FlightTestProcedurePoint(Guid.NewGuid(),Rule(true,true,"any"));var detector=new FlightAttemptDetection(Guid.NewGuid(),point,new());for(var i=1;i<=20;i++)detector.Add(i,i%2==0?Frame(i,120,3500):Frame(i,160,3000));Assert.Empty(detector.Drain());
 }
 [Fact]public void AtomicDetectionRollbackAndRestartReuseOriginalDeterministicIdentities()
 {
  Begin();processing=false;for(var i=1;i<=4;i++)Observe(i,120);store.EvidenceFault=stage=>{if(stage=="automatic-detection-before-commit")throw new IOException("Injected interruption before atomic publication.");};Assert.Throws<IOException>(()=>store.ProcessAutomaticAttempts(flight.Id));Assert.Empty(Attempts());store.EvidenceFault=null;
  var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();reopened.ProcessAutomaticAttempts(flight.Id);var a=Assert.Single(Attempts());Assert.Equal(ResearchAttemptOutcome.Pass,a.PointEvidence!.Outcome);reopened.ProcessAutomaticAttempts(flight.Id);Assert.Equal(a.Id,Assert.Single(Attempts()).Id);Finish();
 }
 [Fact]public void CollectedScienceDeletionPreservesIndependentQuantitativeFlightEvidenceAndDetectionConsent()
 {
  Begin();for(var i=1;i<=4;i++)Observe(i,120);Finish();var result=Assert.Single(store.ResearchAttempts(card.Id));store.DeleteCollectedScience(flight.Id);Assert.True(store.ReviewFlightTestEvidence(result).MatchesSavedEvidence);store.RecoverAutomaticAttempts(flight.Id);Drain();Assert.Single(Attempts());Assert.NotEmpty(store.StreamSamples(flight.Id));Assert.Equal(result,store.ResearchAttempt(result.Id));
 }
 [Fact]public void MoreThanFiveHundredRetainedAttemptsStayPagedAndRepeatedWorkDoesNotDuplicateThem()
 {
  Begin(rule:Rule(hold:1));processing=false;for(var attempt=0;attempt<510;attempt++){Observe(++tick,120);Observe(++tick,120);for(var i=0;i<3;i++)Observe(++tick,160);}time=start.AddSeconds(++tick);coordinator.End(flight.Id);for(var page=0;page<72;page++)store.ProcessAutomaticAttempts(flight.Id);
  long count=0;var ids=new HashSet<Guid>();for(var offset=0;;offset+=20){var rows=store.RecordedResearchAttempts(flight.Id,offset);foreach(var a in rows){Assert.Equal(ResearchAttemptOutcome.Pass,a.PointEvidence!.Outcome);Assert.True(ids.Add(a.Id));count++;}if(rows.Length<20)break;}Assert.Equal(510,count);store.ProcessAutomaticAttempts(flight.Id);Assert.Equal(510,store.ResearchOverview(card.Id).MarkedAttempts);
 }
 [Fact]public void SimultaneousCompatibleCardsRetainIndependentIdentitiesAndOneFailedReplayDoesNotSuppressTheOther()
 {
  var a=store.SaveResearchCard(new("First automatic card",Rule()));var b=store.SaveResearchCard(new("Independent altitude card",Rule(false,true)));
  var plan=store.SavePlan(new(Guid.NewGuid(),"Two compatible cards",ResearchCards:[new(a.Id,1),new(b.Id,1)]));
  coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Fixture",time),Frame(0)));flight=coordinator.Start(new("Multiple detection",PlanId:plan.Id));
  foreach(var c in new[]{a,b}){card=c;Control(ResearchCardAction.Activate);Control(ResearchCardAction.EnableAutomatic);}
  for(var i=1;i<=4;i++)Observe(i,120,3000);Finish();var results=store.ResearchAttempts();Assert.Equal(2,results.Length);Assert.Equal(2,Attempts().Select(r=>r.Automatic!.SessionId).Distinct().Count());
  using(var db=new SqliteConnection("Data Source="+store.DatabasePath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE events SET message='Changed automatic end event' WHERE id=$id";cmd.Parameters.AddWithValue("$id",Attempts().Single(r=>r.CardId==a.Id).EndEventId);cmd.ExecuteNonQuery();}
  store.RecoverAutomaticAttempts(flight.Id);Drain();store.EvaluateAutomaticResults(flight.Id);var states=store.ResearchOperations(flight.Id).Cards;Assert.Equal("Insufficient Evidence",states.Single(c=>c.Card.Id==a.Id).Detection!.Status);Assert.NotEqual("Insufficient Evidence",states.Single(c=>c.Card.Id==b.Id).Detection!.Status);Assert.Equal(2,store.ResearchAttempts().Length);Assert.True(store.ReviewFlightTestEvidence(results.Single(r=>r.Card.Id==b.Id)).MatchesSavedEvidence);
 }
 [Fact]public void AnOrPointCanQualifyOneSupportedSignalWithoutInventingItsMissingBranch()
 {
  var definition=Rule(true,true,"any");var detector=new FlightAttemptDetection(Guid.NewGuid(),new(Guid.NewGuid(),definition),new());for(var i=1;i<=4;i++)detector.Add(i,Frame(i,double.NaN,3000));
  var end=Assert.Single(detector.Drain(),e=>e.Closed);Assert.Equal(ResearchAttemptStatus.Recorded,end.CompletionStatus);Assert.Equal(4,end.LastSequence);
 }
 [Fact]public void DelayedManualTakeoverAtTheSameUtcUsesDifferentOriginalSequencesForRepetitionCredit()
 {
  var p=new FlightTestProcedureDefinition(1,false,[new(Guid.NewGuid(),Rule(),2),new(Guid.NewGuid(),Rule(false,true))]);Begin(p);processing=false;for(var i=1;i<=4;i++)Observe(i,120);Control(ResearchCardAction.BeginAttempt,p.Points[0].Id);for(var i=5;i<=8;i++)Observe(i,120);Control(ResearchCardAction.EndAttempt);Finish();
  Assert.Equal(2,Attempts().Length);Assert.All(Attempts(),a=>Assert.Equal(ResearchAttemptOutcome.Pass,a.PointEvidence!.Outcome));Assert.Equal(2,store.ResearchOperations(flight.Id).Cards.Single().Procedure!.Points[0].CompletedRepetitions);
  var manual=store.EvaluateRecordedResearchAttempt(Attempts().Single(a=>a.Automatic is null).Id,Guid.NewGuid());Assert.True(store.ReviewProcedureEvidence(manual).MatchesSavedEvidence);Assert.Equal(ResearchAttemptOutcome.Pass,manual.Outcome);
 }
 [Fact]public void ArmedAndOpenAutomaticNativeRepliesKeepTheEstablishedUtf8Bound()
 {
  var p=FlightTestProcedureTests.Definition(hold:20);p=p with{Points=p.Points.Select(a=>a with{Conditions=a.Conditions with{TestPoint=a.Conditions.TestPoint! with{Name=new string('漢',100),Description=new string('漢',500)}}}).Concat(p.Points.Select(a=>a with{Id=Guid.NewGuid()})).ToArray()};Begin(p);Observe(1,120);Observe(2,120);
  var reply=new ToolbarCommandHandler(coordinator).Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"research-state",MissionId:flight.Id),WireJson.Options));Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(reply),1,8000);using var json=JsonDocument.Parse(reply);Assert.True(json.RootElement.GetProperty("ok").GetBoolean(),reply);Assert.Contains("Attempt Detected",reply);
 }
 public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
