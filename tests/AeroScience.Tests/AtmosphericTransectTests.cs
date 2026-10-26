using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace AeroScience.Tests;

// Synthetic simulator measurements through real stores/controller; not installed MSFS acceptance.
public sealed class AtmosphericTransectTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-transect-"+Guid.NewGuid());
    readonly MissionStore missions;readonly ScienceStore science;readonly PersonalResearchCard card;
    readonly DateTimeOffset at=DateTimeOffset.Parse("2026-10-26T00:00:00Z");readonly Guid session=Guid.NewGuid();
    public AtmosphericTransectTests(){var paths=new StorageManager(new PreferenceStore(root)).Initialize();missions=new(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;card=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),AtmosphericTransectResearch.Template,ResearchEntryIntent.FlyNow)).Card;}
    Coordinates Position(int station,Coordinates? start=null)=>Geodesy.Direct(start??new(40,-75),90,station*600);
    TelemetryFrame Frame(int second,Coordinates point)=>new(at.AddSeconds(second),TelemetrySource.Simulated,"Synthetic geographic atmosphere",point.Latitude,point.Longitude,1000+second*10,null,100,100,100,0,0,0,0,0);
    ScienceObservation[] Rows(int locations=3,Coordinates? start=null)=>Enumerable.Range(0,locations*3).Select(i=>{
        var p=Position(i/3,start);var utc=at.AddSeconds(1+i);return new ScienceObservation(i+1,session,session,"ambient-temperature",utc,"temperature",20-i*.1,"degC",ScienceProvenance.SimulatorReported,"Synthetic retained thermometer",p.Latitude,p.Longitude,1000+i*10,ObservationQuality.Valid,utc,SessionId:session);
    }).ToArray();
    AtmosphericResearchSummary Calculate(IEnumerable<ScienceObservation> rows,DateTimeOffset[]? breaks=null)=>AtmosphericResearch.Calculate(rows,card,science.LibraryId,breaks??[]).Summary;
    Guid Flight(int locations=3,TelemetrySource source=TelemetrySource.Simulated){var plan=missions.CreateResearchPlan(card.Id,1);var id=missions.Create("Synthetic transect "+locations,Frame(0,Position(0)) with{Source=source},at,plan:plan);
        for(var i=0;i<=20;i++)missions.Append(id,Frame(i,Position(i/7)) with{Source=source});missions.Finish(id,MissionStatus.Completed,at.AddSeconds(20),"Synthetic end");
        var dataset=science.EnsureDataset(missions.Detail(id)!.Mission,new("ambient-temperature",true),missions.Research(id));science.Append(Rows(locations).Select(o=>o with{Id=0,MissionId=id,DatasetId=dataset.Id}).ToArray());return id;}
    PersonalResearchAttempt Evaluate(Guid flight)=>missions.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(20),CardId:card.Id),science:science);
    [Fact]public void ThirdStarterReusesAtomicPreparationAndDoesNotInventFlightRegimeTargets(){
        Assert.Equal(3,card.Version);Assert.Null(card.FlightRegime);Assert.Null(card.AtmosphericProfile);Assert.NotNull(card.AtmosphericTransect);
        var plan=missions.CreateResearchPlan(card.Id,1);Assert.Equal(MissionTemplateKind.AtmosphericTransect,plan.Template);Assert.Empty(plan.Waypoints??[]);Assert.Equal(4,plan.Instruments!.Count(i=>i.Enabled));Assert.True(plan.StartScienceArmed);Assert.Null(plan.Collection);
        Assert.Equal(card.Id,missions.PrepareResearchStarter(new(1,Guid.NewGuid(),AtmosphericTransectResearch.Template,ResearchEntryIntent.FlyNow)).Card.Id);
        var request=new ResearchStarterRequest(1,Guid.NewGuid(),AtmosphericTransectResearch.Template,ResearchEntryIntent.Customize,new(card.Id,1));var copy=missions.PrepareResearchStarter(request);
        Assert.NotEqual(card.Id,copy.Card.Id);Assert.Equal(copy.Plan.Id,missions.PrepareResearchStarter(request).Plan.Id);
        var revision=missions.SaveResearchCard(new("My regional question",null,copy.Card.Id,1,AtmosphericTransect:copy.Card.AtmosphericTransect! with{StationSpacingMetres=200}));
        Assert.Equal(2,revision.Revision);Assert.Equal(500,missions.ResearchCard(card.Id,1).AtmosphericTransect!.StationSpacingMetres);
        Assert.Throws<ArgumentException>(()=>missions.SaveResearchCard(new("Wrong type",null,card.Id,1,AtmosphericProfile:new())));
        Assert.Throws<ArgumentException>(()=>missions.SaveResearchCard(new("Mixed",new(100,5,1000,100,10),AtmosphericTransect:new())));
    }
    [Theory][InlineData(3,ResearchAttemptOutcome.CollectionComplete)][InlineData(2,ResearchAttemptOutcome.PartialCollection)][InlineData(0,ResearchAttemptOutcome.InsufficientEvidence)]
    public void GeographicCollectionRequiresRealMeasurementsAndAdequateSupportedCoverage(int locations,ResearchAttemptOutcome outcome){
        var s=Calculate(Rows(locations));Assert.Equal(outcome,AtmosphericResearch.Outcome(s,card,false).Outcome);Assert.Equal(locations,s.Transect!.UniqueStations);Assert.Equal(locations,s.Transect.CoveredStations);
        Assert.Equal(locations*3,s.ValidTemperatureReadings);Assert.Equal(0,s.Quantities.Single(q=>q.MeasurementId=="density").ValidReadings);
        if(locations>0)Assert.InRange(s.Transect.LinkedDistanceMetres,(locations-1)*600-.00001,(locations-1)*600+.00001);
        Assert.NotEqual(ResearchAttemptOutcome.CollectionComplete,AtmosphericResearch.Outcome(s,card,true).Outcome);
    }
    [Theory][InlineData("coordinates")][InlineData("missing-position")][InlineData("old-position")][InlineData("quality")][InlineData("units")][InlineData("derived")][InlineData("time-gaps")][InlineData("session")][InlineData("stationary")][InlineData("jumps")]
    public void InvalidOrDisconnectedEvidenceCannotCreateGeographicSuccess(string problem){
        var rows=Rows().Select((o,i)=>problem switch{
            "coordinates"=>o with{Latitude=91},"missing-position"=>o with{Longitude=null},"old-position"=>o with{PositionTimestampUtc=o.TimestampUtc.AddSeconds(-4)},
            "quality"=>o with{Quality=ObservationQuality.Stale},"units"=>o with{Unit="K"},"derived"=>o with{Provenance=ScienceProvenance.Derived},
            "time-gaps"=>o with{TimestampUtc=at.AddSeconds(i*10),PositionTimestampUtc=at.AddSeconds(i*10)},"session"=>o with{SessionId=Guid.NewGuid()},
            "stationary"=>o with{Latitude=40,Longitude=-75},"jumps"=>o with{Longitude=-75+i/3},_=>o});
        Assert.NotEqual(ResearchAttemptOutcome.CollectionComplete,AtmosphericResearch.Outcome(Calculate(rows),card,false).Outcome);
    }
    [Fact]public void RevisitsDoNotAddStationsAndSeparateLegsCannotCombineTheirGoals(){
        var revisits=Rows(6).Select((o,i)=>{var p=Position(i/3%2);return o with{Latitude=p.Latitude,Longitude=p.Longitude};});
        var summary=Calculate(revisits);Assert.Equal(2,summary.Transect!.UniqueStations);Assert.Equal(3000,summary.Transect.LinkedDistanceMetres,4);Assert.Equal(ResearchAttemptOutcome.PartialCollection,AtmosphericResearch.Outcome(summary,card,false).Outcome);
        var split=Calculate(Rows(),[at.AddSeconds(4),at.AddSeconds(7)]);Assert.Equal(3,split.Transect!.CoveredStations);Assert.Equal(1,split.Transect.MostCoveredStationsInOneLeg);Assert.Equal(3,split.Transect.TotalLegs);Assert.Equal(0,split.Transect.LinkedDistanceMetres);Assert.Null(split.Transect.CompleteLegId);
        Assert.Equal(ResearchAttemptOutcome.PartialCollection,AtmosphericResearch.Outcome(split,card,false).Outcome);
    }
    [Theory][InlineData(0,179.999)][InlineData(89.999,179.999)][InlineData(-45,-179.999)]
    public void WorldwideGeodesyAndDatelineStationsRemainExact(double latitude,double longitude){
        var rows=Rows(start:new(latitude,longitude));var summary=Calculate(rows);Assert.Equal(ResearchAttemptOutcome.CollectionComplete,AtmosphericResearch.Outcome(summary,card,false).Outcome);
        Assert.Equal(3,summary.Transect!.UniqueStations);Assert.Equal(1200,summary.Transect.LinkedDistanceMetres,5);
        var repeated=rows.Concat(rows.Select(o=>o with{Id=o.Id+100,TimestampUtc=o.TimestampUtc.AddSeconds(12),PositionTimestampUtc=o.PositionTimestampUtc!.Value.AddSeconds(12)}));
        Assert.Equal(3,Calculate(repeated).Transect!.UniqueStations);
    }
    [Fact]public void StoredEvidenceReopensOfflineComparesAndRejectsChangedOriginals(){
        var first=Evaluate(Flight());var second=Evaluate(Flight(2));var reopened=new MissionStore(missions.DatabasePath);reopened.Initialize();
        Assert.Equal(PreflightReadiness.Hash(first),PreflightReadiness.Hash(reopened.ResearchAttempt(first.Id)));Assert.Equal(3,first.Version);
        var comparison=reopened.CompareResearch(card.Id,new([new(first.Id,ResearchAttemptKind.PostflightInterval,first.Id),new(second.Id,ResearchAttemptKind.PostflightInterval,second.Id)]),science:science);
        Assert.All(comparison.Entries,e=>Assert.True(e.Atmosphere!.MatchesSavedEvidence));var evidence=reopened.ReviewAtmosphericEvidence(first,science);
        Assert.Equal(evidence.Preview.Select(o=>o.Id),evidence.TransectPoints!.Select(p=>p.ObservationId));Assert.NotEmpty(evidence.PlannedInstruments!);
        Assert.False(reopened.ReviewAtmosphericEvidence(first with{EndUtc=first.EndUtc.AddMilliseconds(1)},science).MatchesSavedEvidence);
        Assert.False(reopened.ReviewAtmosphericEvidence(first with{AtmosphericProfile=first.AtmosphericProfile! with{Transect=first.AtmosphericProfile!.Transect! with{LinkedDistanceMetres=99999}}},science).MatchesSavedEvidence);
        using var db=new SqliteConnection("Data Source="+science.DatabasePath);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE observations SET payload_json=json_set(payload_json,'$.longitude',-74) WHERE mission_id=$id";cmd.Parameters.AddWithValue("$id",first.FlightId.ToString());cmd.ExecuteNonQuery();
        var changed=reopened.ReviewAtmosphericEvidence(first,science);Assert.False(changed.MatchesSavedEvidence);Assert.Empty(changed.Preview);Assert.Null(changed.TransectPoints);
    }
    [Fact]public void PermanentScienceDeletionRemovesDependentTransectsButKeepsFlightsAndOtherScience(){
        var result=Evaluate(Flight());var other=Evaluate(Flight());var count=missions.StreamSamples(result.FlightId).Count();
        var preview=science.PreviewCollectedDeletion(new([result.FlightId]),missions);Assert.Empty(preview.Blockers);var coordinator=new MissionCoordinator(missions,1000);
        var deleted=science.DeleteCollectedScience(new(preview.Id,preview.SelectionHash),missions,coordinator.EnterMaintenance);Assert.Equal("Completed",deleted.State);
        Assert.Empty(missions.ResearchAttempts(flightId:result.FlightId));Assert.Throws<InvalidOperationException>(()=>Evaluate(result.FlightId));Assert.Equal(count,missions.StreamSamples(result.FlightId).Count());Assert.True(missions.ReviewAtmosphericEvidence(other,science).MatchesSavedEvidence);
    }
    [Theory][InlineData(40)][InlineData(89.9)]public void ActualControllerAcquiresGeographicScienceAndKeepsRecordingIndependent(double latitude){
        var time=at;using var research=new ResearchOperations(missions,science);var coordinator=new MissionCoordinator(missions,1000,research:research,utcNow:()=>time);
        void Observe(int second,int station,double? temperature){time=at.AddSeconds(second);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic atmosphere",time),Frame(second,Position(station,new(latitude,-75))),new(time,temperature,29.9,8,270,null)));}
        Observe(0,0,20);var flight=coordinator.Start(new("Actual transect controller",PlanId:missions.CreateResearchPlan(card.Id,1).Id));
        Assert.Equal(CollectionPhase.Armed,Assert.IsType<CollectionState>(coordinator.ScienceCollection(flight.Id).State).Phase);Observe(1,0,99);Assert.Empty(science.StreamObservations(flight.Id));
        var state=Assert.IsType<CollectionState>(coordinator.ScienceCollection(flight.Id).State);coordinator.ControlScience(new(1,flight.Id,Guid.NewGuid(),state.Revision,CollectionAction.StartData,state.CurrentSessionId));
        coordinator.SetSciencePaused(flight.Id,true);Observe(1,0,99);Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);coordinator.SetSciencePaused(flight.Id,false);
        for(var i=2;i<=5;i++)Observe(i,0,20);for(var i=6;i<=9;i++)Observe(i,1,18);for(var i=10;i<=13;i++)Observe(i,2,16);coordinator.End(flight.Id);
        var result=missions.EvaluateResearchAttempt(new(Guid.NewGuid(),flight.Id,at,time,CardId:card.Id),science:science);Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Outcome);
        Assert.DoesNotContain(science.StreamObservations(flight.Id),o=>o.Value==99);Assert.Equal(0,result.AtmosphericProfile!.Quantities.Single(q=>q.MeasurementId=="density").ValidReadings);
        Assert.Contains(science.StreamObservations(flight.Id),o=>o.SessionId is not null&&o.IntentId is not null);
    }
    [Fact]public void MixedNativeCardsKeepTypedCriteriaFrozenAndByteBounded(){
        var profile=missions.SaveResearchCard(new("Vertical",null,AtmosphericProfile:new()));var regime=missions.SaveResearchCard(new("Flight regime",new(100,5,1000,100,10)));
        var geographic=missions.SaveResearchCard(new(new string('漢',100),null,AtmosphericTransect:new(Question:new string('漢',500))));
        Assert.True(MissionStore.CompatibleResearchCards(profile,geographic));Assert.True(MissionStore.CompatibleResearchCards(regime,geographic));
        var plan=missions.SavePlan(new(Guid.NewGuid(),"Mixed research",ResearchCards:[new(profile.Id,1),new(regime.Id,1),new(geographic.Id,1)]));var coordinator=new MissionCoordinator(missions,1000);var flight=coordinator.Start(new("Native mixed",PlanId:plan.Id));var handler=new ToolbarCommandHandler(coordinator);int? offset=0;var selected=new HashSet<Guid>();
        while(offset is {} n){var reply=handler.Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"research-state",MissionId:flight.Id,Offset:n),WireJson.Options));Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(reply),1,8000);using var json=JsonDocument.Parse(reply);Assert.True(json.RootElement.GetProperty("ok").GetBoolean(),reply);
            var state=json.RootElement.GetProperty("research").Deserialize<ResearchFlightState>(WireJson.Options)!;Assert.Equal(1,state.PageSize);selected.Add(Assert.Single(state.Cards).Card.Id);offset=state.NextOffset;}
        Assert.Equal(3,selected.Count);Assert.Equal(1,missions.ResearchOperations(flight.Id).Cards.Single(c=>c.Card.Id==geographic.Id).Card.Revision);
    }
    [Fact]public void BoundedPreviewsCancellationAndUnsupportedProvenanceRemainProtected(){
        foreach(var r in new[]{new AtmosphericTransectDefinition(Version:2),new(StationSpacingMetres:double.NaN),new(RequiredStations:1),new(MinimumReadingsPerStation:1),new(MaximumStepMetres:0)})Assert.Throws<ArgumentException>(()=>AtmosphericTransectResearch.Validate(r));
        var rows=Enumerable.Range(0,5000).Select(i=>Rows(1)[0] with{Id=i+1,TimestampUtc=at.AddSeconds(i),PositionTimestampUtc=at.AddSeconds(i)});
        var calculated=AtmosphericResearch.Calculate(rows,card,science.LibraryId,[]);Assert.True(calculated.Downsampled);Assert.InRange(calculated.Preview.Length,1,1000);Assert.Equal(5000,calculated.Summary.ValidTemperatureReadings);Assert.Equal(calculated.Preview.Select(o=>o.Id),calculated.TransectPoints!.Select(p=>p.ObservationId));
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>AtmosphericResearch.Calculate(Rows(),card,science.LibraryId,[],cancel.Token));
        Assert.Throws<ArgumentException>(()=>Calculate(Rows().Select(o=>o with{Latitude=double.NaN}))); // Non-finite coordinates cannot enter the canonical original JSON evidence.
        Assert.Throws<InvalidOperationException>(()=>AtmosphericResearch.Calculate(Enumerable.Range(0,129).Select(i=>Rows(1)[0] with{Id=i+1}),card,science.LibraryId,[]));
        var a=Evaluate(Flight());var b=Evaluate(Flight(source:TelemetrySource.Simulator));Assert.Throws<ArgumentException>(()=>missions.CompareResearch(card.Id,new([new(a.Id,ResearchAttemptKind.PostflightInterval,a.Id),new(b.Id,ResearchAttemptKind.PostflightInterval,b.Id)]),science:science));
    }
    public void Dispose(){science.Dispose();SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
