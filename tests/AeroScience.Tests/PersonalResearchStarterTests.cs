using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace AeroScience.Tests;

public sealed class PersonalResearchStarterTests : IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-starter-"+Guid.NewGuid());
    readonly MissionStore store;
    readonly DateTimeOffset at=DateTimeOffset.Parse("2026-10-26T12:00:00Z");
    public PersonalResearchStarterTests(){store=new(Path.Combine(root,"missions.db"));store.Initialize();}
    static ResearchStarterRequest Request(ResearchEntryIntent intent=ResearchEntryIntent.FlyNow,ResearchCardReference? card=null)=>new(1,Guid.NewGuid(),"flight-regime",intent,card);
    [Fact]public void DefaultsCreateOneLocalCardAndPlanWithoutRecordingSelectionOrExternalWork()
    {
        var starter=Assert.Single(MissionStore.ResearchStarters(),s=>s.Id=="flight-regime");FlightRegimeEvaluation.Validate(starter.Example);
        var prepared=store.PrepareResearchStarter(Request());Assert.Equal(starter.Example,prepared.Card.FlightRegime);Assert.False(prepared.Reused);
        Assert.Equal(prepared.Card.Id,prepared.Plan.ResearchCard!.Id);Assert.Equal(new[]{prepared.Plan.ResearchCard},prepared.Plan.ResearchCards);
        Assert.DoesNotContain(prepared.Plan.Instruments!,i=>i.Enabled);Assert.Empty(prepared.Plan.Waypoints??[]);Assert.Empty(store.List());
        var coordinator=new MissionCoordinator(store,1000,requireFreshTelemetry:true);Assert.Null(coordinator.Snapshot().SelectedPlanId);Assert.Null(coordinator.Snapshot().ActiveMission);
        coordinator.SelectPlan(prepared.Plan.Id);Assert.Throws<InvalidOperationException>(()=>coordinator.Start(new(prepared.Plan.Name,PlanId:prepared.Plan.Id)));
        Assert.Equal(prepared.Plan.Id,coordinator.Snapshot().SelectedPlanId);Assert.Empty(store.List());Assert.Equal(11,MissionStore.CurrentSchemaVersion);
    }
    [Fact]public async Task ConcurrentRetriesAndRestartReturnOnePreparedCopyWithoutDuplicateCardsOrPlans()
    {
        var request=Request(ResearchEntryIntent.Customize);
        var prepared=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>Task.Run(()=>store.PrepareResearchStarter(request))));
        Assert.Single(store.ResearchCards());Assert.Single(store.Plans());Assert.Single(prepared,p=>!p.Reused);
        Assert.All(prepared,p=>{Assert.Equal(request.RequestId,p.Plan.Id);Assert.Equal(request.RequestId,p.Card.Id);});
        var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();Assert.True(reopened.PrepareResearchStarter(request).Reused);
        Assert.Throws<InvalidOperationException>(()=>store.PrepareResearchStarter(request with{Intent=ResearchEntryIntent.FlyNow}));
    }
    [Fact]public void RepeatedDefaultFlightsReuseOneUnchangedLocalExperimentAcrossPreparations()
    {
        var first=store.PrepareResearchStarter(Request());var second=store.PrepareResearchStarter(Request());
        Assert.Equal(first.Card,second.Card);Assert.NotEqual(first.Plan.Id,second.Plan.Id);Assert.Single(store.ResearchCards());Assert.Equal(2,store.Plans().Count);
        store.SaveResearchCard(new(first.Card.Name,first.Card.FlightRegime! with{AirspeedKnots=140},first.Card.Id,1));
        var fresh=store.PrepareResearchStarter(Request());Assert.NotEqual(first.Card.Id,fresh.Card.Id);Assert.Equal(120,fresh.Card.FlightRegime!.AirspeedKnots);
    }
    [Fact]public void CustomizeCopiesExactDefinitionAndNeverRevisesOriginalCardPlanOrHistoricEvidence()
    {
        var card=store.SaveResearchCard(new("Existing experiment",new(140,6,4500,150,3)));var plan=store.CreateResearchPlan(card.Id,1);
        var flight=store.Create("Recorded original",null,at,plan:plan);store.Finish(flight,MissionStatus.Completed,at.AddSeconds(3),"Fixture");
        var snapshot=JsonSerializer.Serialize(store.Research(flight).PlanSnapshot,WireJson.Options);
        var request=Request(ResearchEntryIntent.Customize,new(card.Id,1));var copy=store.PrepareResearchStarter(request);
        Assert.NotEqual(card.Id,copy.Card.Id);Assert.Equal(card.FlightRegime,copy.Card.FlightRegime);Assert.Equal(new(card.Id,1),copy.Plan.ResearchStarter!.OriginalCard);
        var revised=store.SaveResearchCard(new(copy.Card.Name,copy.Card.FlightRegime! with{AirspeedKnots=150},copy.Card.Id,1));
        var edited=store.SavePlan(copy.Plan with{Name="My independent plan",Waypoints=[new("Start",40,-75),new("End",40.1,-75)],Instruments=[new("ambient-temperature",true)],ResearchCard=new(revised.Id,2),ResearchCards=[new(revised.Id,2)]});
        var replay=store.PrepareResearchStarter(request);Assert.True(replay.Reused);Assert.Equal(PreflightReadiness.Hash(edited),PreflightReadiness.Hash(replay.Plan));Assert.Equal(2,replay.Card.Revision);
        Assert.Equal(card,store.ResearchCard(card.Id));Assert.Equal(PreflightReadiness.Hash(plan),PreflightReadiness.Hash(store.Plan(plan.Id)));Assert.Equal(snapshot,JsonSerializer.Serialize(store.Research(flight).PlanSnapshot,WireJson.Options));
    }
    [Fact]public void FlyNowReusesSelectedSavedRevisionButDoesNotChangeActiveRecording()
    {
        var card=store.SaveResearchCard(new("Reusable",new(120,5,3000,100,3)));
        store.SaveResearchCard(new(card.Name,card.FlightRegime! with{HoldSeconds=4},card.Id,1));
        var prepared=store.PrepareResearchStarter(Request(card:new(card.Id,1)));Assert.Equal(card,prepared.Card);Assert.Equal(2,store.ResearchCards()[0].Revision);Assert.Single(store.ResearchCards());
        var coordinator=new MissionCoordinator(store,1000);var recording=coordinator.Start(new("Still recording",PlanId:prepared.Plan.Id));
        var next=store.PrepareResearchStarter(Request(ResearchEntryIntent.Customize,new(card.Id,1)));
        Assert.Equal(recording.Id,coordinator.Snapshot().ActiveMission!.Id);Assert.Empty(store.RecordedResearchAttempts(recording.Id));Assert.All(store.ResearchOperations(recording.Id).Cards,c=>Assert.False(c.Active));
        Assert.Throws<InvalidOperationException>(()=>coordinator.SelectPlan(next.Plan.Id));coordinator.End(recording.Id);
    }
    [Fact]public void InterruptedPreparationRollsBackBothRowsAndCanRetryWithoutPartialDuplicates()
    {
        var request=Request();store.EvidenceFault=point=>{if(point=="research-starter-before-commit")throw new IOException("Synthetic interrupted preparation");};
        Assert.Throws<IOException>(()=>store.PrepareResearchStarter(request));Assert.Empty(store.ResearchCards());Assert.Empty(store.Plans());
        store.EvidenceFault=null;var result=store.PrepareResearchStarter(request);Assert.False(result.Reused);Assert.Single(store.Plans());Assert.Single(store.ResearchCards());
    }
    [Fact]public void RemovedPlansAndCollidingIdentitiesAreNotSilentlyOverwrittenOrResurrected()
    {
        var request=Request();var prepared=store.PrepareResearchStarter(request);Assert.True(store.DeletePlan(prepared.Plan.Id));
        Assert.Throws<InvalidOperationException>(()=>store.PrepareResearchStarter(request));Assert.Null(store.Plan(prepared.Plan.Id));Assert.NotNull(store.ResearchCard(prepared.Card.Id));
        var other=Request();var unrelated=store.SavePlan(new(other.RequestId,"Other saved plan"));
        Assert.Throws<InvalidOperationException>(()=>store.PrepareResearchStarter(other));Assert.Equal(PreflightReadiness.Hash(unrelated),PreflightReadiness.Hash(store.Plan(unrelated.Id)));
    }
    [Fact]public void UnsupportedInputsAndForgedPreparationProvenanceFailWithoutChangingStoredPlans()
    {
        foreach(var request in new[]{Request() with{Version=2},Request() with{StarterVersion=2},Request() with{RequestId=Guid.Empty},Request() with{Intent=ResearchEntryIntent.Unknown},Request(card:new(Guid.Empty,0))})
            Assert.Throws<ArgumentException>(()=>store.PrepareResearchStarter(request));
        Assert.Throws<NotSupportedException>(()=>store.PrepareResearchStarter(Request() with{StarterId="atmosphere-not-implemented"}));Assert.Empty(store.Plans());Assert.Empty(store.ResearchCards());
        var prepared=store.PrepareResearchStarter(Request());Assert.Throws<ArgumentException>(()=>store.SavePlan(prepared.Plan with{ResearchStarter=prepared.Plan.ResearchStarter! with{RequestSha256="forged"}}));
        Assert.Throws<ArgumentException>(()=>store.SavePlan(prepared.Plan with{Id=Guid.NewGuid()}));Assert.Equal(PreflightReadiness.Hash(prepared.Plan),PreflightReadiness.Hash(store.Plan(prepared.Plan.Id)));
        var legacy=new MissionPlan(Guid.NewGuid(),"Legacy plan");Assert.DoesNotContain("researchStarter",JsonSerializer.Serialize(legacy,WireJson.Options));
    }
    public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
