using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace AeroScience.Tests;

// Explicit synthetic scientific inputs and actual local stores/controller; not installed MSFS acceptance.
public sealed class AtmosphericResearchTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-atmospheric-research-"+Guid.NewGuid());
    readonly MissionStore missions;readonly ScienceStore science;readonly PersonalResearchCard card;
    readonly DateTimeOffset at=DateTimeOffset.Parse("2026-10-26T00:00:00Z");
    public AtmosphericResearchTests(){var paths=new StorageManager(new PreferenceStore(root)).Initialize();missions=new(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;card=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),AtmosphericResearch.Template,ResearchEntryIntent.FlyNow)).Card;}
    TelemetryFrame Frame(int second,double altitude=1000)=>new(at.AddSeconds(second),TelemetrySource.Simulated,"Synthetic atmospheric fixture",40,-75,altitude,null,100,100,100,0,0,0,0,0);
    Guid Flight(int bands=3,bool observations=true,TelemetrySource source=TelemetrySource.Simulated)
    {
        var plan=missions.CreateResearchPlan(card.Id,card.Revision);var id=missions.Create("Atmospheric investigation",Frame(0) with{Source=source},at,plan:plan);
        for(var i=0;i<=20;i++)missions.Append(id,Frame(i,1000+i*50) with{Source=source});
        missions.Finish(id,MissionStatus.Completed,at.AddSeconds(20),"Synthetic fixture end");
        if(observations){var mission=missions.Detail(id)!.Mission;var context=missions.Research(id);var rows=new List<ScienceObservation>();
            foreach(var quantity in AtmosphericResearch.Quantities){var data=science.EnsureDataset(mission,new(quantity.Instrument,true),context);
                for(var band=0;band<bands;band++)for(var n=0;n<3;n++){var utc=at.AddSeconds(1+band*4+n);var value=quantity.Measurement switch{"temperature"=>20-band*3+n*.1,"pressure"=>29-band,"wind-speed"=>5+band,"wind-direction"=>270,_=>.002};
                    rows.Add(new(0,data.Id,id,quantity.Instrument,utc,quantity.Measurement,value,quantity.Unit,ScienceProvenance.SimulatorReported,"Synthetic test fixture / "+ScienceCatalog.Definitions.Single(d=>d.Id==quantity.Instrument).Measurements.Single(m=>m.Id==quantity.Measurement).SimulatorVariable,40,-75,1100+band*500,ObservationQuality.Valid,utc,SessionId:Guid.Parse("11111111-1111-4111-8111-111111111111")));}}
            science.Append(rows);
        }return id;
    }
    PersonalResearchAttempt Evaluate(Guid flight)=>missions.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(20),CardId:card.Id),science:science);
    [Fact]public void StarterIsARealAtmosphericDefinitionWithExistingInstrumentsAndNoFlightRegimeCoercion()
    {
        Assert.Equal(2,card.Version);Assert.Null(card.FlightRegime);Assert.NotNull(card.AtmosphericProfile);var plan=missions.CreateResearchPlan(card.Id,1);
        Assert.Equal(MissionTemplateKind.AtmosphericProfile,plan.Template);Assert.Equal(4,plan.Instruments!.Count(i=>i.Enabled));Assert.All(plan.Instruments!.Where(i=>i.Enabled),i=>Assert.StartsWith("ambient-",i.InstrumentId));
        var copy=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),AtmosphericResearch.Template,ResearchEntryIntent.Customize,new(card.Id,1)));
        Assert.NotEqual(card.Id,copy.Card.Id);Assert.Equal(card.AtmosphericProfile,copy.Card.AtmosphericProfile);
        var revision=missions.SaveResearchCard(new("Smaller achievable bands",null,copy.Card.Id,1,copy.Card.AtmosphericProfile! with{BandWidthFeet=200}));
        Assert.Equal(2,revision.Revision);Assert.Equal(500,missions.ResearchCard(card.Id,1).AtmosphericProfile!.BandWidthFeet);
        Assert.Throws<ArgumentException>(()=>missions.SaveResearchCard(new("Wrong kind",new(100,5,3000,100,30),copy.Card.Id,2)));
        Assert.True(MissionStore.CompatibleResearchCards(card,missions.SaveResearchCard(new("Performance",new(100,5,3000,100,30)))));
    }
    [Theory][InlineData(3,true,ResearchAttemptOutcome.CollectionComplete)][InlineData(2,true,ResearchAttemptOutcome.PartialCollection)][InlineData(0,false,ResearchAttemptOutcome.InsufficientEvidence)]
    public void CollectionUsesRetainedScienceRatherThanClimbOrRawAtmosphere(int bands,bool retained,ResearchAttemptOutcome outcome)
    {
        var flight=Flight(bands,retained);var result=Evaluate(flight);Assert.Equal(outcome,result.Outcome);Assert.Equal(2,result.Version);Assert.Equal(bands*3,result.AtmosphericProfile!.ValidTemperatureReadings);
        Assert.Equal(AtmosphericResearch.Evaluator,result.EvaluatorVersion);Assert.Equal(0,result.LongestSupportedHoldSeconds);Assert.Equal(21,missions.StreamSamples(flight).Count());
        var review=missions.ReviewAtmosphericEvidence(result,science);Assert.True(review.MatchesSavedEvidence);Assert.Equal(bands*15,review.Preview.Length);
        Assert.All(review.Preview,o=>Assert.Equal(ObservationQuality.Valid,o.Quality));Assert.DoesNotContain(result.Outcome,new[]{ResearchAttemptOutcome.Pass,ResearchAttemptOutcome.Fail});
        Assert.Equal(result.Evidence.Sha256,missions.EvaluateResearchAttempt(new(result.Id,flight,at,at.AddSeconds(20),CardId:card.Id),science:science).Evidence.Sha256);
    }
    [Theory][InlineData("quality")][InlineData("units")][InlineData("derived")][InlineData("location")][InlineData("old-position")][InlineData("wrong-instrument")][InlineData("gaps")][InlineData("session")]
    public void UnsupportedMissingOrNoncontiguousEvidenceCannotCompleteCollection(string problem)
    {
        var flight=Flight();var rows=science.StreamObservations(flight).Where(o=>o.MeasurementId=="temperature").ToArray();
        var changed=rows.Select((o,i)=>problem switch{
            "quality"=>o with{Quality=ObservationQuality.Stale},"units"=>o with{Unit="K"},"derived"=>o with{Provenance=ScienceProvenance.Derived},
            "location"=>o with{AltitudeMslFeet=null},"old-position"=>o with{PositionTimestampUtc=o.TimestampUtc.AddSeconds(-4)},
            "wrong-instrument"=>o with{InstrumentId="thermal-imager"},"gaps"=>o with{TimestampUtc=at.AddSeconds(i*10)},
            "session"=>o with{SessionId=Guid.NewGuid()},_=>o}).ToArray();
        var result=AtmosphericResearch.Calculate(changed,card,science.LibraryId,[]);Assert.NotEqual(ResearchAttemptOutcome.CollectionComplete,AtmosphericResearch.Outcome(result.Summary,card.AtmosphericProfile!,false).Outcome);
    }
    [Fact]public void SciencePauseEdgesBreakShortRunsAndUnvisitedBandsStayMissing()
    {
        var flight=Flight();var rows=science.StreamObservations(flight).ToArray();var pause=AtmosphericResearch.Calculate(rows,card,science.LibraryId,[at.AddSeconds(2),at.AddSeconds(6),at.AddSeconds(10)]);
        Assert.Equal(3,pause.Summary.MajorGaps);Assert.All(pause.Summary.Bands,b=>Assert.False(b.Covered));
        var sparse=AtmosphericResearch.Calculate(rows.Where(o=>o.AltitudeMslFeet!=1600),card,science.LibraryId,[]);
        Assert.Contains(sparse.Summary.Bands,b=>!b.Covered&&b.TemperatureReadings==0);Assert.Equal(ResearchAttemptOutcome.PartialCollection,AtmosphericResearch.Outcome(sparse.Summary,card.AtmosphericProfile!,false).Outcome);
        var bounded=AtmosphericResearch.Calculate(rows,card,science.LibraryId,[at,at.AddSeconds(20)]);Assert.Equal(0,bounded.Summary.MajorGaps); // Normal session start/end are not internal measurement gaps.
    }
    [Fact]public void RevisionsRestartComparisonAndOriginalHashesRemainBoundToTheirActualEvidence()
    {
        var first=Evaluate(Flight());var second=Evaluate(Flight(2));
        var reopened=new MissionStore(missions.DatabasePath);reopened.Initialize();
        Assert.Equal(PreflightReadiness.Hash(first),PreflightReadiness.Hash(reopened.ResearchAttempt(first.Id)));
        var overview=reopened.ResearchOverview(card.Id);Assert.Equal(1,overview.CompletedCollections);Assert.Equal(1,overview.PartialCollections);Assert.Equal(0,overview.Pass);
        var request=new ResearchComparisonRequest([new(first.Id,ResearchAttemptKind.PostflightInterval,first.Id),new(second.Id,ResearchAttemptKind.PostflightInterval,second.Id)]);
        var comparison=reopened.CompareResearch(card.Id,request,science:science);Assert.Equal(2,comparison.Entries.Length);Assert.All(comparison.Entries,e=>Assert.True(e.Atmosphere!.MatchesSavedEvidence));
        Assert.False(reopened.ReviewAtmosphericEvidence(first with{AtmosphericProfile=first.AtmosphericProfile! with{ValidTemperatureReadings=999}},science).MatchesSavedEvidence);
        Assert.False(reopened.ReviewAtmosphericEvidence(first with{Outcome=ResearchAttemptOutcome.PartialCollection},science).MatchesSavedEvidence);
        Assert.False(reopened.ReviewAtmosphericEvidence(first with{EndUtc=first.EndUtc.AddMilliseconds(1)},science).MatchesSavedEvidence);
        using(var db=new SqliteConnection("Data Source="+science.DatabasePath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE observations SET payload_json=json_set(payload_json,'$.value',999) WHERE mission_id=$id AND measurement_id='temperature'";cmd.Parameters.AddWithValue("$id",first.FlightId.ToString());cmd.ExecuteNonQuery();}
        var audit=reopened.ReviewAtmosphericEvidence(first,science);Assert.False(audit.MatchesSavedEvidence);Assert.Empty(audit.Preview);Assert.Equal(first.Evidence.Sha256,reopened.ResearchAttempt(first.Id).Evidence.Sha256);
    }
    [Fact]public void CollectedScienceDeletionPurgesAtmosphericResultsAndCannotRegenerateFromFlightContext()
    {
        var result=Evaluate(Flight());var unrelated=Evaluate(Flight());var before=missions.StreamSamples(result.FlightId).Count();var review=science.PreviewCollectedDeletion(new([result.FlightId]),missions);Assert.Empty(review.Blockers);
        var coordinator=new MissionCoordinator(missions,1000);var deleted=science.DeleteCollectedScience(new(review.Id,review.SelectionHash),missions,coordinator.EnterMaintenance);Assert.True(deleted.State=="Completed",deleted.Warning);
        Assert.Empty(missions.ResearchAttempts(flightId:result.FlightId));Assert.Throws<ArgumentException>(()=>missions.ResearchAttempt(result.Id));Assert.Equal(before,missions.StreamSamples(result.FlightId).Count());
        Assert.Throws<InvalidOperationException>(()=>Evaluate(result.FlightId));Assert.True(missions.ReviewAtmosphericEvidence(unrelated,science).MatchesSavedEvidence);
    }
    [Fact]public void BoundsCancellationAndProvenanceAreNotWeakenedForNewResearchType()
    {
        foreach(var rule in new[]{new AtmosphericResearchDefinition(Version:2),new(BandWidthFeet:double.NaN),new(RequiredBands:1),new(MinimumReadingsPerBand:1),new(MaximumGapSeconds:0)})Assert.Throws<ArgumentException>(()=>AtmosphericResearch.Validate(rule));
        var first=Evaluate(Flight());using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>missions.ReviewAtmosphericEvidence(first,science,cancel.Token));
        var real=Evaluate(Flight(source:TelemetrySource.Simulator));Assert.Throws<ArgumentException>(()=>missions.CompareResearch(card.Id,new([new(first.Id,ResearchAttemptKind.PostflightInterval,first.Id),new(real.Id,ResearchAttemptKind.PostflightInterval,real.Id)]),science:science));
        var many=Enumerable.Range(0,5000).Select(i=>new ScienceObservation(i+1,Guid.NewGuid(),first.FlightId,"ambient-temperature",at.AddSeconds(i),"temperature",15,"degC",ScienceProvenance.SimulatorReported,"Synthetic fixture",40,-75,1000+i%1000,ObservationQuality.Valid,at.AddSeconds(i)));
        var bounded=AtmosphericResearch.Calculate(many,card,science.LibraryId,[]);Assert.True(bounded.Downsampled);Assert.InRange(bounded.Preview.Length,1,1000);Assert.Equal(5000,bounded.Summary.ValidTemperatureReadings);
    }
    [Fact]public void ActualControllerCollectionRetainsOriginalAtmosphereAndPauseDoesNotStopFlight()
    {
        var time=at;using var operations=new ResearchOperations(missions,science);var coordinator=new MissionCoordinator(missions,1000,research:operations,utcNow:()=>time);
        void Observe(int second,double altitude,double? temperature){time=at.AddSeconds(second);var frame=Frame(second,altitude);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic fixture",time),frame,new(time,temperature,29.8,8,270,null)));}
        var plan=missions.CreateResearchPlan(card.Id,1);Observe(0,1100,20);var flight=coordinator.Start(new("Actual controlled collection",PlanId:plan.Id));
        for(var i=1;i<=4;i++)Observe(i,1100,20);coordinator.SetSciencePaused(flight.Id,true);Observe(5,1600,null);Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);coordinator.SetSciencePaused(flight.Id,false);
        for(var i=6;i<=9;i++)Observe(i,1600,17);for(var i=10;i<=13;i++)Observe(i,2100,14);coordinator.End(flight.Id);
        var result=missions.EvaluateResearchAttempt(new(Guid.NewGuid(),flight.Id,at,time,CardId:card.Id),science:science);
        Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Outcome);Assert.True(result.AtmosphericProfile!.MajorGaps>0);Assert.True(result.AtmosphericProfile.ValidTemperatureReadings>=9);
        Assert.Equal(0,result.AtmosphericProfile.Quantities.Single(q=>q.MeasurementId=="density").ValidReadings);Assert.Contains(science.StreamObservations(flight.Id),o=>o.SessionId is not null&&o.IntentId is not null);
    }
    [Fact]public void NativeAtmosphericDefinitionsRemainExactWithinUtf8BoundsAndAllPagesRemainReachable()
    {
        var cards=Enumerable.Range(0,4).Select(_=>missions.SaveResearchCard(new(new string('漢',100),null,AtmosphericProfile:new(Question:new string('漢',500))))).ToArray();
        var plan=missions.SavePlan(new(Guid.NewGuid(),"Native atmospheric bounds",Instruments:[],ResearchCards:cards.Select(c=>new ResearchCardReference(c.Id,c.Revision)).ToArray()));
        var coordinator=new MissionCoordinator(missions,1000);var flight=coordinator.Start(new("Native exact definitions",PlanId:plan.Id));var native=new ToolbarCommandHandler(coordinator);var visited=new HashSet<Guid>();int? offset=0;
        while(offset is {} at){var reply=native.Handle(System.Text.Json.JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"research-state",MissionId:flight.Id,Offset:at),WireJson.Options));Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(reply),1,8000);
            using var json=System.Text.Json.JsonDocument.Parse(reply);Assert.True(json.RootElement.GetProperty("ok").GetBoolean(),reply);var state=json.RootElement.GetProperty("research").Deserialize<ResearchFlightState>(WireJson.Options)!;Assert.Equal(1,state.PageSize);var selected=Assert.Single(state.Cards);Assert.Equal(new string('漢',500),selected.Card.AtmosphericProfile!.Question);visited.Add(selected.Card.Id);offset=state.NextOffset;}
        Assert.Equal(cards.Length,visited.Count);Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);coordinator.End(flight.Id);
    }
    public void Dispose(){science.Dispose();SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
