using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Xunit;
namespace AeroScience.Tests;

public sealed class GeographicCollectionTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-geographic-collection-"+Guid.NewGuid());
    readonly MissionStore store;readonly ScienceStore science;readonly ResearchOperations research;readonly MissionCoordinator coordinator;
    DateTimeOffset clock=DateTimeOffset.UtcNow;
    public GeographicCollectionTests(){var settings=new StorageManager(new PreferenceStore(root)).Initialize();store=new(Path.Combine(settings.MissionDirectory,"missions.db"));store.Initialize();science=new(settings.ScienceLibraryDirectory);science.Initialize();store.ScienceLibrary=science;research=new(store,science);coordinator=new(store,1000,research:research,utcNow:()=>clock);}
    static SurveyArea Area(string name,double lon)=>new(name,[new(39.999,lon-.001),new(39.999,lon+.001),new(40.001,lon+.001),new(40.001,lon-.001)]);
    MissionPlan Plan(bool boundary=true)
    {
        var p=new MissionPlan(Guid.NewGuid(),"Synthetic A/B study",MissionTemplateKind.GeographicSurvey,
            Instruments:[new("ambient-temperature",true)],Waypoints:[new("End A",40,-74.997,Id:Guid.NewGuid()),new("End B",40,-74.987,Id:Guid.NewGuid())],SurveyAreas:[Area("A",-75),Area("B",-74.99)]);
        var areas=p.SurveyAreas!.Select((a,i)=>new CollectionSurveyArea(Guid.NewGuid(),i,EvidenceContracts.SurveyGeometryHash(a))).ToArray();
        CollectionTrigger Trigger(CollectionAction action,CollectionTriggerKind kind,Guid target,string key)=>new(Guid.NewGuid(),Guid.NewGuid(),action,kind,target,1,20,kind==CollectionTriggerKind.DirectionalGate?90:null,key);
        var c=new CollectionPlan(1,p.Id,InitialCollectionPolicy.Armed,[Trigger(CollectionAction.StartData,CollectionTriggerKind.AreaEntry,areas[0].Id,"A"),Trigger(CollectionAction.EndData,CollectionTriggerKind.DirectionalGate,p.Waypoints![0].Id!.Value,"A"),Trigger(CollectionAction.StartData,CollectionTriggerKind.AreaEntry,areas[1].Id,"B"),Trigger(CollectionAction.EndData,CollectionTriggerKind.DirectionalGate,p.Waypoints[1].Id!.Value,"B")],new(5,6),boundary?new(areas.Select(a=>a.Id).ToArray(),true,true,2,0):null,areas);
        return store.SavePlan(p with{Collection=c});
    }
    Mission Start(MissionPlan plan)=>coordinator.Start(new("Automated synthetic flight",PlanId:plan.Id));
    void At(double lon,int seconds=1,double lat=40,bool paused=false){clock=clock.AddSeconds(seconds);var f=SimulatedTelemetryProvider.Generate(clock,0) with{Latitude=lat,Longitude=lon,GroundspeedKnots=400,IsPaused=paused};coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic geographic evidence only",clock),f,new(clock,18,29,5,90,.002)));}
    CollectionCommitResult Do(Guid mission,CollectionAction action){var s=store.CollectionCheckpoint(mission)!;return coordinator.ControlScience(new(1,mission,Guid.NewGuid(),s.Revision,action,s.CurrentSessionId));}
    [Fact] public void BackendAloneCollectsTwoAreasWithBoundaryPausesExplicitEndsAndExactEvidence()
    {
        var p=Plan();var flight=Start(p);
        foreach(var lon in new[]{-75.003,-75.0015,-75.0005,-74.9995,-74.9985,-74.9995,-74.9985,-74.9965,-74.9945,-74.9925,-74.9905,-74.9895,-74.9885,-74.9865})At(lon);
        var sessions=store.CollectionSessions(flight.Id);Assert.Equal(2,sessions.Length);Assert.All(sessions,s=>{Assert.Equal(CollectionSessionStatus.Ended,s.Status);Assert.Equal(CollectionEndReason.Trigger,s.EndReason);});
        Assert.Equal(new[]{"A","B"},sessions.Select(s=>s.PlannedSessionKey));Assert.Equal(CollectionPhase.BetweenSessions,store.CollectionCheckpoint(flight.Id)!.Phase);
        Assert.Equal(14,store.StreamSamples(flight.Id).Count());Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);
        var rows=science.StreamObservations(flight.Id).ToArray();Assert.Equal(5,rows.Length);Assert.Equal(3,rows.Count(r=>r.SessionId==sessions[0].Id));Assert.Equal(2,rows.Count(r=>r.SessionId==sessions[1].Id));
        var events=store.CollectionEvents(flight.Id,limit:100).Where(e=>e.Origin is CollectionEventOrigin.RouteTrigger or CollectionEventOrigin.Boundary).ToArray();
        Assert.Equal(8,events.Length);Assert.All(events,e=>{Assert.NotEmpty(e.Evidence);Assert.NotNull(e.CollectionPolicy);foreach(var link in e.Evidence)Assert.Equal(link,MissionStore.DescribeCollectionInput(store.CollectionInput(flight.Id,link.Id)!));});
        var keys=events.Select(e=>e.OccurrenceId).ToArray();Assert.Equal(keys.Length,keys.Distinct().Count());At(-74.9865,0);Assert.Equal(events.Length,store.CollectionEvents(flight.Id,limit:100).Count(e=>e.Origin is CollectionEventOrigin.RouteTrigger or CollectionEventOrigin.Boundary));
        coordinator.End(flight.Id);Assert.Equal(MissionStatus.Completed,store.Detail(flight.Id)!.Mission.Status);
    }
    [Fact] public void ManualEndSuppressesLaterStartUntilExplicitRearmAndNeverReplaysConsumedStart()
    {
        var p=Plan(false);var f=Start(p);At(-75.003);At(-75.0005);Do(f.Id,CollectionAction.EndData);
        foreach(var lon in new[]{-74.9985,-74.9965,-74.9945,-74.9925,-74.9905})At(lon);
        Assert.Single(store.CollectionSessions(f.Id));Assert.Contains(p.Collection!.Triggers[2].OccurrenceId,store.CollectionCheckpoint(f.Id)!.SuppressedOccurrenceIds!);
        Do(f.Id,CollectionAction.RearmAutomation);At(-74.9905);Assert.Single(store.CollectionSessions(f.Id));
        var b=Do(f.Id,CollectionAction.StartData).Session!;Assert.Equal(2,b.Sequence);Assert.True(store.CollectionCheckpoint(f.Id)!.ManualAutomationHold);
    }
    [Theory][InlineData("gap")][InlineData("jump")][InlineData("stale")][InlineData("disconnect")][InlineData("out-of-order")][InlineData("pause")]
    public void DisruptedEvidenceCannotSkipAcrossTriggersAndNeedsRearm(string kind)
    {
        var f=Start(Plan());At(-75.003);
        if(kind=="disconnect")coordinator.Apply(new(new(ConnectionState.Disconnected,TelemetrySource.Simulated,"Synthetic disconnect",clock),null));
        if(kind=="stale"){
            var old=clock.AddMinutes(-1);var frame=SimulatedTelemetryProvider.Generate(old,0) with{Latitude=40,Longitude=-75.0005};
            coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic stale",clock),frame));
        }else At(kind=="jump"?-74.99:-75.0005,kind=="gap"?20:kind=="out-of-order"?-1:1,paused:kind=="pause");
        var state=store.CollectionCheckpoint(f.Id)!;Assert.True(state.ContinuityHold);Assert.Empty(store.CollectionSessions(f.Id));
        Do(f.Id,CollectionAction.RearmAutomation);At(-75.0005);Assert.Empty(store.CollectionSessions(f.Id)); // default requires a witnessed entry
        At(-75.002);At(-75.0005);Assert.Single(store.CollectionSessions(f.Id));
    }
    [Fact] public void InitialInsideIsExplicitAndExitNeverInfersEnd()
    {
        var p=Plan();p=store.SavePlan(p with{Collection=p.Collection! with{Triggers=p.Collection.Triggers.Select(t=>t.Action==CollectionAction.StartData?t with{InitialInside=InitialInsidePolicy.StartWhenInitiallyInside}:t).ToArray()}});
        var f=Start(p);At(-75);Assert.Single(store.CollectionSessions(f.Id));At(-75.002);Assert.Equal(CollectionPhase.Paused,store.CollectionCheckpoint(f.Id)!.Phase);
        Assert.Null(store.CollectionSessions(f.Id).Single().EndedUtc);Assert.Equal(f.Id,coordinator.Snapshot().ActiveMission!.Id);
    }
    [Fact] public void PointVisitsAreObservedRepeatedPassesAreDistinctAndWrongWayGateDoesNotFire()
    {
        var p=Plan(false);var point=p.Waypoints![0];var start=new CollectionTrigger(Guid.NewGuid(),Guid.NewGuid(),CollectionAction.StartData,CollectionTriggerKind.Waypoint,point.Id!.Value,1,20);
        var end=start with{Id=Guid.NewGuid(),OccurrenceId=Guid.NewGuid(),Action=CollectionAction.EndData,Pass=2};
        p=store.SavePlan(p with{Collection=p.Collection! with{Triggers=[start,end]}});var f=Start(p);At(-74.998);At(-74.996);Assert.Empty(store.CollectionSessions(f.Id)); // segment crosses, but no observed point visit
        At(-74.997);var a=store.CollectionCheckpoint(f.Id)!.CurrentSessionId;Assert.NotNull(a);At(-74.996);At(-74.997);Assert.Null(store.CollectionCheckpoint(f.Id)!.CurrentSessionId);
        Assert.Equal(2,store.CollectionCheckpoint(f.Id)!.FiredOccurrenceIds!.Length);
    }
    [Fact] public void DatelineMembershipAndUnsupportedGeometryAreExplicitAndLegacyPlansStayManual()
    {
        var area=new SurveyArea("Dateline",[new(-.001,179.999),new(-.001,-179.999),new(.001,-179.999),new(.001,179.999)]);
        Assert.True(MissionAnalysis.Inside(new(0,180),area.Vertices));Assert.False(MissionAnalysis.Inside(new(0,179.99),area.Vertices));
        var p=Plan();var polar=p with{SurveyAreas=[Area("Polar",-75) with{Vertices=area.Vertices.Select(v=>v with{Latitude=v.Latitude+85}).ToArray()},p.SurveyAreas![1]]};
        Assert.Throws<ArgumentException>(()=>CollectionPlanning.Bind(polar));
        var legacy=JsonSerializer.Deserialize<MissionPlan>("{\"id\":\""+Guid.NewGuid()+"\",\"name\":\"Legacy\"}",WireJson.Options)!;Assert.Null(legacy.Collection);
        Assert.DoesNotContain("collection",JsonSerializer.Serialize(legacy,WireJson.Options));
    }
    [Fact] public void SavedBindingsRejectChangedGeometryAndAutomaticEndOnAreaExit()
    {
        var p=Plan();var moved=p with{SurveyAreas=[Area("Moved",-76),p.SurveyAreas![1]]};Assert.Throws<ArgumentException>(()=>store.SavePlan(moved));
        Assert.NotEqual(p.Collection!.SurveyAreas![0].GeometrySha256,CollectionPlanning.Bind(moved).SurveyAreas![0].GeometrySha256);
        Assert.Throws<ArgumentException>(()=>store.SavePlan(p with{Collection=p.Collection with{Triggers=[p.Collection.Triggers[0] with{Action=CollectionAction.EndData,Kind=CollectionTriggerKind.AreaExit}]}}));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void CrossingOrderIsGeographicAndCoincidentEndSuppressesStart(bool coincident)
    {
        var p=Plan(false);var startId=Guid.NewGuid();var endId=Guid.NewGuid();var secondId=Guid.NewGuid();
        var route=new[]{new ResearchWaypoint("Start A",40,-75,Id:startId),new ResearchWaypoint("End A",40,-74.999,Id:endId),new ResearchWaypoint("Start B",40,coincident?-74.999:-74.998,Id:secondId)};
        CollectionTrigger Trigger(Guid target,CollectionAction action,string key,CollectionTriggerKind kind=CollectionTriggerKind.DirectionalGate)=>new(Guid.NewGuid(),Guid.NewGuid(),action,kind,target,1,20,kind==CollectionTriggerKind.DirectionalGate?90:null,key);
        p=store.SavePlan(p with{Waypoints=route,Collection=p.Collection! with{Triggers=[Trigger(secondId,CollectionAction.StartData,"B"),Trigger(endId,CollectionAction.EndData,"A"),Trigger(startId,CollectionAction.StartData,"A",CollectionTriggerKind.Waypoint)]}});
        var f=Start(p);At(-75);At(-74.9975);Assert.Equal(coincident?1:2,store.CollectionSessions(f.Id).Length);
        Assert.Equal(coincident?CollectionPhase.BetweenSessions:CollectionPhase.Collecting,store.CollectionCheckpoint(f.Id)!.Phase);
        var decisions=store.CollectionEvents(f.Id,limit:100).Where(e=>e.Origin==CollectionEventOrigin.RouteTrigger).ToArray();
        Assert.Equal(CollectionAction.EndData,decisions[1].Action);Assert.Equal(CollectionAction.StartData,decisions[2].Action);
        Assert.Equal(coincident?CollectionEventOutcome.Suppressed:CollectionEventOutcome.Applied,decisions[2].Outcome);
    }
    [Fact] public void WrongDirectionDoesNotEndAndDatelineGateUsesWgs84Bracketing()
    {
        var p=Plan(false);var start=Guid.NewGuid();var end=Guid.NewGuid();
        var triggers=new[]{new CollectionTrigger(Guid.NewGuid(),Guid.NewGuid(),CollectionAction.StartData,CollectionTriggerKind.Waypoint,start,1,20),
            new CollectionTrigger(Guid.NewGuid(),Guid.NewGuid(),CollectionAction.EndData,CollectionTriggerKind.DirectionalGate,end,1,20,90)};
        p=store.SavePlan(p with{Waypoints=[new("Start",40,-75,Id:start),new("Gate",40,-75.001,Id:end)],Collection=p.Collection! with{Triggers=triggers}});
        var f=Start(p);At(-75);At(-75.002);Assert.Equal(CollectionPhase.Collecting,store.CollectionCheckpoint(f.Id)!.Phase);At(-75.0005);Assert.Equal(CollectionPhase.BetweenSessions,store.CollectionCheckpoint(f.Id)!.Phase);coordinator.End(f.Id);
        p=store.SavePlan(p with{Waypoints=[new("Start",0,179.999,Id:start),new("Gate",0,180,Id:end)]});f=Start(p);At(179.999,lat:0);At(-179.999,lat:0);
        Assert.Equal(CollectionPhase.BetweenSessions,store.CollectionCheckpoint(f.Id)!.Phase);Assert.Single(store.CollectionSessions(f.Id));
    }
    [Fact] public void DwellAndHysteresisPreventGrazesAndManualPauseCannotAutoResume()
    {
        var p=Plan();p=store.SavePlan(p with{Collection=p.Collection! with{Boundary=p.Collection.Boundary! with{DwellSeconds=2,HysteresisMetres=10}}});
        var f=Start(p);At(-75.003);At(-75.0005);Assert.Empty(store.CollectionSessions(f.Id));At(-75.0005,2);Assert.Single(store.CollectionSessions(f.Id));
        At(-74.99905);At(-74.99895);At(-74.99905);Assert.Equal(CollectionPhase.Collecting,store.CollectionCheckpoint(f.Id)!.Phase); // within 10 m of edge
        Do(f.Id,CollectionAction.PauseData);At(-75.002);At(-75.002,2);At(-75);At(-75,2);
        Assert.Equal(CollectionPhase.Paused,store.CollectionCheckpoint(f.Id)!.Phase);Assert.True(store.CollectionCheckpoint(f.Id)!.ManualAutomationHold);
        Do(f.Id,CollectionAction.RearmAutomation);Assert.Equal(CollectionPhase.Paused,store.CollectionCheckpoint(f.Id)!.Phase);
        Do(f.Id,CollectionAction.ResumeData);Assert.Equal(CollectionPhase.Collecting,store.CollectionCheckpoint(f.Id)!.Phase);
    }
    [Fact] public void BoundaryReceiptsHaveTheirOwnBoundWithoutExpandingSavedTriggerBudget()
    {
        var keys=Enumerable.Range(0,129).Select(_=>Guid.NewGuid()).ToArray();
        var state=new CollectionState(1,Guid.NewGuid(),1,CollectionPhase.Armed,CollectionAuthority.Automatic,FiredOccurrenceIds:keys);
        EvidenceContracts.Validate(state);Assert.Equal(128,EvidenceContracts.MaximumTriggers);
        Assert.Throws<ArgumentException>(()=>EvidenceContracts.Validate(state with{FiredOccurrenceIds=Enumerable.Range(0,4097).Select(_=>Guid.NewGuid()).ToArray()}));
    }
    public void Dispose(){coordinator.Interrupt();research.Dispose();science.Dispose();Directory.Delete(root,true);}
}
