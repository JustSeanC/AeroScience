using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

public sealed class PersonalResearchReviewTests : IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-review-"+Guid.NewGuid());
    readonly MissionStore store;readonly MissionCoordinator coordinator;
    DateTimeOffset time=DateTimeOffset.Parse("2026-10-26T00:00:00Z"),origin;
    readonly PersonalResearchCard card;Mission? flight;int flights;
    TelemetrySource source=TelemetrySource.Simulator;
    public PersonalResearchReviewTests(){store=new(Path.Combine(root,"missions.db"));store.Initialize();coordinator=new(store,1000,utcNow:()=>time);card=store.SaveResearchCard(new("Repeated experiment",new(120,5,3000,100,2)));}
    void Observe(double seconds,double speed=120,bool paused=false){time=origin.AddSeconds(seconds);var f=new TelemetryFrame(time,source,"Synthetic review fixture",40,-75,3000,null,speed,speed,speed,0,0,0,0,0,paused);coordinator.Apply(new(new(ConnectionState.Connected,source,"Synthetic unit evidence",time),f));}
    void Start(PersonalResearchCard? definition=null){origin=DateTimeOffset.Parse("2026-10-26T00:00:00Z").AddMinutes(++flights);Observe(0);var plan=store.CreateResearchPlan((definition??card).Id,(definition??card).Revision);flight=coordinator.Start(new("Review flight "+flights,PlanId:plan.Id));}
    ResearchCardResult Control(ResearchCardAction action){var state=coordinator.PersonalResearch(flight!.Id);return coordinator.ControlPersonalResearch(new(1,flight.Id,Guid.NewGuid(),state.Revision,action,card.Id,state.Cards[0].OpenAttempt?.Id));}
    Guid Attempt(int first,int last,double speed=120,bool interrupt=false){Observe(first,speed);if(!coordinator.PersonalResearch(flight!.Id).Cards[0].Active)Control(ResearchCardAction.Activate);var id=Control(ResearchCardAction.BeginAttempt).Receipt.AttemptId!.Value;for(var i=first+1;i<=last;i++)Observe(i,speed);Control(interrupt?ResearchCardAction.Deactivate:ResearchCardAction.EndAttempt);return id;}
    void End()=>coordinator.End(flight!.Id);
    PersonalResearchAttempt Evaluate(Guid id)=>store.EvaluateRecordedResearchAttempt(id,Guid.NewGuid());
    static ResearchAttemptSelection Select(ResearchHistoryItem row)=>new(row.Id,row.Kind,row.Result?.Id);
    ResearchComparison Compare(params ResearchHistoryItem[] rows)=>store.CompareResearch(card.Id,new(rows.Select(Select).ToArray()));

    [Fact]public void OverviewSeparatesRecordedAttemptsIntervalsEvaluationsAndInterruptedActivity()
    {
        Start();var pass=Attempt(0,4);var interrupted=Attempt(5,8,interrupt:true);Attempt(9,12);End();
        Evaluate(pass);Evaluate(pass);Evaluate(interrupted);
        var request=new ResearchAttemptRequest(Guid.NewGuid(),flight!.Id,origin,origin.AddSeconds(4));store.EvaluateResearchAttempt(request);store.EvaluateResearchAttempt(request with{RequestId=Guid.NewGuid()});
        var view=store.ResearchOverview(card.Id);Assert.Equal(3,view.MarkedAttempts);Assert.Equal(1,view.PostflightIntervals);Assert.Equal(3,view.EvaluatedAttempts);
        Assert.Equal(2,view.Pass);Assert.Equal(0,view.Fail);Assert.Equal(1,view.InsufficientEvidence);Assert.Equal(1,view.AwaitingEvaluation);Assert.Equal(1,view.Interrupted);Assert.Equal(0,view.InProgress);Assert.Equal(5,view.SavedEvaluations);
        Assert.True(view.CurrentRevisionDemonstrated);Assert.Null(view.MostRecent!.Result);Assert.Equal(origin.AddSeconds(9),view.MostRecent.StartUtc);
        var history=store.ResearchHistory(card.Id);Assert.Equal(4,history.Total);Assert.Equal(4,history.Items.Length);Assert.Single(history.Items,i=>i.Kind==ResearchAttemptKind.PostflightInterval);
    }
    [Fact]public void DifferentFlightsAndRevisionsRetainTheirRequirementsAndOriginalResults()
    {
        Start();var a=Attempt(0,4);End();Evaluate(a);var first=store.StreamSamples(flight!.Id).ToArray();
        var second=store.SaveResearchCard(new(card.Name,card.FlightRegime! with{AirspeedKnots=150,HoldSeconds=3},card.Id,1));Start(second);var b=Attempt(0,5);End();Evaluate(b);
        var view=store.ResearchOverview(card.Id);Assert.Equal(1,view.Pass);Assert.Equal(1,view.Fail);Assert.False(view.CurrentRevisionDemonstrated);Assert.Equal(2,view.MostRecent!.Card.Revision);
        var comparison=Compare(store.ResearchHistory(card.Id).Items);Assert.True(comparison.DifferentRevisions);Assert.True(comparison.DifferentRequirements);
        Assert.Equal(2,comparison.Entries.Select(e=>e.Attempt.FlightId).Distinct().Count());Assert.All(comparison.Entries,e=>Assert.True(e.Evidence!.MatchesSavedEvidence));
        Assert.Equal(first,store.StreamSamples(comparison.Entries.Single(e=>e.Attempt.Card.Revision==1).Attempt.FlightId));Assert.Contains(comparison.Notices,n=>n.Contains("Different card revisions"));
    }
    [Fact]public void MissingChangedAndGappedEvidenceIsDetectedWithoutRescoringHistoricalResults()
    {
        Start();var a=Attempt(0,4);End();var passed=Evaluate(a);var originalFlight=flight!.Id;
        Start();Control(ResearchCardAction.Activate);var b=Control(ResearchCardAction.BeginAttempt).Receipt.AttemptId!.Value;Observe(1);Observe(4);Control(ResearchCardAction.EndAttempt);End();Evaluate(b);
        var before=Compare(store.ResearchHistory(card.Id).Items);Assert.Equal(1,before.Entries.Single(e=>e.Attempt.Id==b).Evidence!.GapCount);
        Assert.Contains(before.Entries.Single(e=>e.Attempt.Id==b).Evidence!.Breaks,b=>b.Seconds==3);
        Sql("DELETE FROM telemetry WHERE mission_id='"+originalFlight+"'");
        var after=Compare(store.ResearchHistory(card.Id).Items);var missing=after.Entries.Single(e=>e.Attempt.Id==a);
        Assert.False(missing.Evidence!.MatchesSavedEvidence);Assert.Empty(missing.Evidence.Preview);Assert.Equal(ResearchAttemptOutcome.Pass,missing.Attempt.Result!.Outcome);
        Assert.False(store.ResearchOverview(card.Id).CurrentRevisionDemonstrated);Assert.Equal(passed,store.ResearchAttempts(flightId:originalFlight).Single());
        Assert.Contains(after.Notices,n=>n.Contains("does not match"));
    }
    [Fact]public void EqualCountsWithModifiedOriginalTelemetryStillFailTheSavedHash()
    {
        Start();var a=Attempt(0,4);var b=Attempt(5,9);End();Evaluate(a);Evaluate(b);
        Sql("UPDATE telemetry SET payload_json=json_set(payload_json,'$.indicatedAirspeedKnots',121) WHERE mission_id='"+flight!.Id+"'");
        Assert.All(Compare(store.ResearchHistory(card.Id).Items).Entries,e=>{Assert.False(e.Evidence!.MatchesSavedEvidence);Assert.Equal(e.Evidence.ExpectedSamples,e.Evidence.RetainedSamples);});
    }
    [Fact]public void PauseEdgesAndUnobservedIntervalBoundariesRemainVisibleAndHashVerified()
    {
        Start();var a=Attempt(0,4);Observe(5);var b=Control(ResearchCardAction.BeginAttempt).Receipt.AttemptId!.Value;Observe(6,paused:true);Observe(7);Observe(8);Control(ResearchCardAction.EndAttempt);End();Evaluate(a);Evaluate(b);
        var paused=Compare(store.ResearchHistory(card.Id).Items).Entries.Single(e=>e.Attempt.Id==b);
        Assert.True(paused.Evidence!.MatchesSavedEvidence);Assert.True(paused.Evidence.PauseOrConnectionEdges>0);Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,paused.Attempt.Result!.Outcome);
        Start();Observe(4);Observe(5);time=origin.AddSeconds(10);End();
        var interval=store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight!.Id,origin,origin.AddSeconds(10)));
        var selected=store.ResearchHistory(card.Id).Items.Where(r=>r.Result?.Id==interval.Id||r.Id==a).ToArray();
        var boundary=Compare(selected).Entries.Single(e=>e.Attempt.Result?.Id==interval.Id);
        Assert.True(boundary.Evidence!.MatchesSavedEvidence);Assert.Equal(2,boundary.Evidence.GapCount);
        Assert.Contains(boundary.Evidence.Breaks,b=>b.Kind.Contains("beginning"));Assert.Contains(boundary.Evidence.Breaks,b=>b.Kind.Contains("end of"));
    }
    [Fact]public void RenamedRevisionDoesNotInventChangedNumericalRequirements()
    {
        Start();var a=Attempt(0,4);End();Evaluate(a);var renamed=store.SaveResearchCard(new("Renamed experiment",card.FlightRegime,card.Id,1));
        Start(renamed);var b=Attempt(0,4);End();Evaluate(b);var compared=Compare(store.ResearchHistory(card.Id).Items);
        Assert.True(compared.DifferentRevisions);Assert.False(compared.DifferentRequirements);Assert.Contains(compared.Notices,n=>n.Contains("requirements are identical"));
    }
    [Fact]public void DemoEvidenceAndDifferentSourcesCannotBecomeAnInstalledFlightDemonstration()
    {
        source=TelemetrySource.Simulated;Start();var a=Attempt(0,4);End();Evaluate(a);
        Assert.False(store.ResearchOverview(card.Id).CurrentRevisionDemonstrated);Assert.True(store.ResearchOverview(card.Id).CurrentRevisionDemoPass);
        source=TelemetrySource.Simulator;Start();var b=Attempt(0,4);End();Evaluate(b);
        var comparison=Compare(store.ResearchHistory(card.Id).Items);Assert.True(comparison.DifferentSources);Assert.Contains(comparison.Notices,n=>n.Contains("Synthetic/demo"));
    }
    [Fact]public void OpenAndInterruptedUnevaluatedAttemptsHaveNoInventedOutcomeOrHold()
    {
        Start();Attempt(0,2,interrupt:true);Observe(3);Control(ResearchCardAction.Activate);Control(ResearchCardAction.BeginAttempt);
        var view=store.ResearchOverview(card.Id);Assert.Equal(2,view.MarkedAttempts);Assert.Equal(1,view.InProgress);Assert.Equal(1,view.Interrupted);Assert.Equal(1,view.AwaitingEvaluation);
        Assert.Equal(0,view.EvaluatedAttempts);Assert.Equal(0,view.Pass);Assert.Equal(0,view.Fail);
        Assert.All(Compare(store.ResearchHistory(card.Id).Items).Entries,e=>{Assert.Null(e.Attempt.Result);Assert.Null(e.Evidence);});End();
    }
    [Fact]public void PagingAndCountsCoverTheEntireHistoryAndRestartDoesNotRewriteRawEvidence()
    {
        Start();Observe(1);for(int i=2;i<=95;i++)Observe(i);End();
        for(int i=0;i<45;i++)store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight!.Id,origin.AddSeconds(i*2+1),origin.AddSeconds(i*2+3)));
        var fingerprint=StorageManager.DatabaseFingerprint(store.DatabasePath);var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();
        var pages=new[]{reopened.ResearchHistory(card.Id),reopened.ResearchHistory(card.Id,20),reopened.ResearchHistory(card.Id,40)};
        Assert.Equal(new[]{20,20,5},pages.Select(p=>p.Items.Length));Assert.Null(pages[2].NextOffset);Assert.Equal(45,pages.SelectMany(p=>p.Items).Select(i=>i.Id).Distinct().Count());
        Assert.Equal(45,reopened.ResearchOverview(card.Id).PostflightIntervals);Assert.Equal(fingerprint,StorageManager.DatabaseFingerprint(store.DatabasePath));
        Assert.Throws<ArgumentException>(()=>store.ResearchHistory(card.Id,-1));
    }
    [Fact]public void StaleSelectionsForeignCardsDeletedFlightsBoundsAndCancellationFailHonestly()
    {
        Start();var a=Attempt(0,4);var b=Attempt(5,9);End();Evaluate(a);Evaluate(b);var rows=store.ResearchHistory(card.Id).Items;var picks=rows.Select(Select).ToArray();
        Evaluate(a);Assert.Throws<InvalidOperationException>(()=>store.CompareResearch(card.Id,new(picks)));
        var other=store.SaveResearchCard(new("Other experiment",card.FlightRegime));Assert.Throws<ArgumentException>(()=>store.CompareResearch(other.Id,new(picks)));
        var current=store.ResearchHistory(card.Id).Items.Select(Select).ToArray();
        Assert.Throws<ArgumentException>(()=>store.CompareResearch(card.Id,new([current[0],current[0]])));
        Assert.Throws<ArgumentException>(()=>store.CompareResearch(card.Id,new(Enumerable.Repeat(current[0],9).ToArray())));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();Assert.Throws<OperationCanceledException>(()=>store.CompareResearch(card.Id,new(current),cancelled.Token));
        store.ClearFlightData("DELETE FLIGHT DATA");Assert.Equal(0,store.ResearchOverview(card.Id).MarkedAttempts);Assert.Empty(store.ResearchHistory(card.Id).Items);
        Assert.Throws<ArgumentException>(()=>store.CompareResearch(card.Id,new(current)));Assert.NotNull(store.ResearchCard(card.Id));
    }
    void Sql(string sql){using var db=new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
    public void Dispose(){if(coordinator.Snapshot().ActiveMission is {} active)coordinator.End(active.Id);SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
