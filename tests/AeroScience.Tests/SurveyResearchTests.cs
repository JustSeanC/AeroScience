using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;

public sealed class SurveyResearchTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-survey-"+Guid.NewGuid());readonly MissionStore missions;readonly ScienceStore science;
    readonly DateTimeOffset at=DateTimeOffset.UtcNow.AddMinutes(-2);
    public SurveyResearchTests(){var p=new StorageManager(new PreferenceStore(root)).Initialize();missions=new(Path.Combine(p.MissionDirectory,"missions.db"));missions.Initialize();science=new(p.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;}
    PersonalResearchCard Card(int mask=7,int? age=null)=>missions.SaveResearchCard(new("Survey "+mask,null,Survey:new(1,Enum.GetValues<SurveyMeasurement>().Where((_,i)=>(mask&(1<<i))!=0).ToArray(),MaximumSourceAgeDays:age)));
    TelemetryFrame Frame(int n)=>new(at.AddSeconds(n),TelemetrySource.Simulated,"Synthetic survey test",40.001,-75.004+n*.001,1000,null,100,100,100,0,0,0,0,0);
    Guid Flight(PersonalResearchCard card,bool air=true){var plan=missions.CreateResearchPlan(card.Id,card.Revision);var id=missions.Create("Synthetic survey flight",Frame(0),at,plan:plan);for(var i=0;i<=20;i++)missions.Append(id,Frame(i));missions.Finish(id,MissionStatus.Completed,at.AddSeconds(20),"Synthetic end");
        if(air&&card.Survey!.Measurements.Contains(SurveyMeasurement.AirTemperature)){var dataset=science.EnsureDataset(missions.Detail(id)!.Mission,new("ambient-temperature",true),missions.Research(id));science.Append(Enumerable.Range(1,4).Select(i=>new ScienceObservation(0,dataset.Id,id,"ambient-temperature",at.AddSeconds(i),"temperature",15+i,"degC",ScienceProvenance.SimulatorReported,"Synthetic simulator air temperature",40.001,-75.004+i*.001,1000,ObservationQuality.Valid,at.AddSeconds(i))).ToArray());}return id;}
    PersonalResearchAttempt Evaluate(Guid flight,PersonalResearchCard card)=>missions.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(20),CardId:card.Id),science:science);
    Task<ScientificSource> Source(RemoteKind kind,string datum="NAVD88",bool cloudy=false)=>SurveyFixture.Source(science,Path.Combine(root,Guid.NewGuid().ToString()),kind,datum,cloudy);
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)][InlineData(7)]
    public async Task AllSelectionsAndCombinationsUseTheirOwnQuantitiesAndOriginalEvidence(int mask){var card=Card(mask);var flight=Flight(card);
        if((mask&2)!=0)SurveyFixture.Publish(science,missions,flight,await Source(RemoteKind.Terrain),at.AddSeconds(5));if((mask&4)!=0)SurveyFixture.Publish(science,missions,flight,await Source(RemoteKind.Thermal),at.AddSeconds(5));
        var result=Evaluate(flight,card);Assert.Equal(4,result.Version);Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Outcome);Assert.Null(result.AtmosphericProfile);Assert.Equal(card.Survey!.Measurements.Length,result.Survey!.Objectives.Length);
        var review=missions.ReviewSurveyEvidence(result,science);Assert.True(review.MatchesSavedEvidence);Assert.All(result.Survey.Objectives,o=>Assert.Equal(ResearchAttemptOutcome.CollectionComplete,o.Outcome));
        foreach(var sample in review.Preview)Assert.Equal(sample.Measurement switch{SurveyMeasurement.TerrainElevation=>"m",SurveyMeasurement.AirTemperature=>"degC",_=>"K"},sample.Unit);
        foreach(var sample in review.Preview.Where(s=>s.Measurement==SurveyMeasurement.TerrainElevation))Assert.Equal(123.25,sample.Value);
        foreach(var sample in review.Preview.Where(s=>s.Measurement==SurveyMeasurement.LandSurfaceTemperature)){Assert.Equal(40000*.00341802+149,sample.Value,8);Assert.Equal(1,sample.UncertaintyKelvin);Assert.NotNull(sample.SourceUtc);}
    }
    [Fact]public void PreparationDefaultsToLocalAirAndPreservesExactSelectionCopiesAndRevisions(){var request=new ResearchStarterRequest(1,Guid.NewGuid(),SurveyResearch.Template,ResearchEntryIntent.FlyNow);var first=missions.PrepareResearchStarter(request);
        Assert.Equal([SurveyMeasurement.AirTemperature],first.Card.Survey!.Measurements);Assert.True(first.Plan.StartScienceArmed);Assert.Null(first.Plan.Collection);Assert.Empty(first.Plan.Waypoints??[]);Assert.Equal("ambient-temperature",Assert.Single(first.Plan.Instruments!,i=>i.Enabled).InstrumentId);Assert.Equal(first.Plan.Id,missions.PrepareResearchStarter(request).Plan.Id);
        var mixed=missions.PrepareResearchStarter(request with{RequestId=Guid.NewGuid(),Survey=new(1,[SurveyMeasurement.TerrainElevation,SurveyMeasurement.AirTemperature])});Assert.Equal(2,mixed.Plan.Instruments!.Count(i=>i.Enabled));Assert.Equal(4,mixed.Card.Version);
        var copy=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),SurveyResearch.Template,ResearchEntryIntent.Customize,new(mixed.Card.Id,1)));Assert.NotEqual(mixed.Card.Id,copy.Card.Id);Assert.Equal(PreflightReadiness.Hash(mixed.Card.Survey!),PreflightReadiness.Hash(copy.Card.Survey!));
        var revision=missions.SaveResearchCard(new("My objectives",null,copy.Card.Id,1,Survey:copy.Card.Survey! with{MinimumValidSamples=5}));Assert.Equal(2,revision.Revision);Assert.Equal(3,missions.ResearchCard(mixed.Card.Id,1).Survey!.MinimumValidSamples);
        Assert.Throws<ArgumentException>(()=>missions.SaveResearchCard(new("Wrong type",null,mixed.Card.Id,1,AtmosphericProfile:new())));
    }
    [Fact]public void MissingExternalDataCannotBlockLocalCollectionOrSubstituteForRequiredObjectives(){var card=Card(7);var flight=Flight(card);var result=Evaluate(flight,card);Assert.Equal(ResearchAttemptOutcome.PartialCollection,result.Outcome);
        Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Survey!.Objectives.Single(o=>o.Measurement==SurveyMeasurement.AirTemperature).Outcome);Assert.All(result.Survey.Objectives.Where(o=>o.Measurement!=SurveyMeasurement.AirTemperature),o=>Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,o.Outcome));
        var empty=Flight(card,false);Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,Evaluate(empty,card).Outcome);
    }
    [Theory][InlineData("unknown-datum")][InlineData("stale")][InlineData("missing-qa")][InlineData("cloud")][InlineData("nodata")][InlineData("bad-band")]
    public async Task UnqualifiedSourcePixelsNeverCompleteAnObjective(string issue){var thermal=issue is "stale" or "missing-qa" or "cloud";var card=Card(thermal?4:2,issue=="stale"?30:null);var flight=Flight(card);var source=await Source(thermal?RemoteKind.Thermal:RemoteKind.Terrain,issue=="unknown-datum"?"Unknown":"NAVD88",issue=="cloud");
        SurveyFixture.Publish(science,missions,flight,source,at.AddSeconds(5),change:d=>issue switch{
            "missing-qa"=>d with{Bands=d.Bands.Where(b=>b.Id!="QA_RADSAT").ToArray()},"nodata"=>d with{Bands=d.Bands.Select(b=>b with{Values=[null,null,null]}).ToArray()},
            "bad-band"=>d with{Source=d.Source with{Manifest=d.Source.Manifest with{Bands=d.Source.Manifest.Bands.Select(b=>b with{BadBand=true}).ToArray()}}},_=>d});
        var result=Evaluate(flight,card);Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,result.Outcome);Assert.Equal(0,Assert.Single(result.Survey!.Objectives).ValidSamples);
    }
    [Fact]public async Task RepeatFootprintsDeduplicateNativeCellsAndDoNotPoolIncompatibleTerrainDatums(){var card=Card(2);var flight=Flight(card);var source=await Source(RemoteKind.Terrain);SurveyFixture.Publish(science,missions,flight,source,at.AddSeconds(5));SurveyFixture.Publish(science,missions,flight,source,at.AddSeconds(6));
        var result=Evaluate(flight,card);Assert.Equal(3,Assert.Single(result.Survey!.Objectives).ValidSamples);Assert.Equal(2,result.Survey.Products);
        var other=await Source(RemoteKind.Terrain,"EGM96");SurveyFixture.Publish(science,missions,flight,other,at.AddSeconds(7));var current=Evaluate(flight,card);Assert.Equal(2,Assert.Single(current.Survey!.Objectives).Groups.Length);Assert.NotEqual(result.Evidence.Sha256,current.Evidence.Sha256);
    }
    [Fact]public async Task OfflineReopenComparisonAndOriginalDeletionKeepIndependentSamplesAndProvenance(){var card=Card(7);var terrain=await Source(RemoteKind.Terrain);var thermal=await Source(RemoteKind.Thermal);var results=new List<PersonalResearchAttempt>();
        for(var i=0;i<2;i++){var flight=Flight(card);SurveyFixture.Publish(science,missions,flight,terrain,at.AddSeconds(5));SurveyFixture.Publish(science,missions,flight,thermal,at.AddSeconds(5));results.Add(Evaluate(flight,card));}
        var reopened=new MissionStore(missions.DatabasePath);reopened.Initialize();Assert.Equal(results[0].Evidence.Sha256,reopened.ResearchAttempt(results[0].Id).Evidence.Sha256);
        var comparison=reopened.CompareResearch(card.Id,new(results.Select(r=>new ResearchAttemptSelection(r.Id,ResearchAttemptKind.PostflightInterval,r.Id)).ToArray()),science:science);Assert.All(comparison.Entries,e=>Assert.True(e.Survey!.MatchesSavedEvidence));
        var preview=science.PreviewSourceDeletion(new([terrain.Id,thermal.Id]),missions);Assert.Equal(0,preview.BlockedSources);var coordinator=new MissionCoordinator(missions,1000);var deletion=science.DeleteExternalSources(new(preview.Id,preview.SelectionHash),missions,coordinator.EnterMaintenance);Assert.Equal("Completed",deletion.State);
        Assert.Null(science.Source(terrain.Id));Assert.Null(science.Source(thermal.Id));Assert.Empty(Directory.EnumerateFiles(Path.Combine(science.DirectoryPath,terrain.RelativeDirectory),"*",SearchOption.AllDirectories));
        var review=reopened.ReviewSurveyEvidence(results[0],science);Assert.True(review.MatchesSavedEvidence);Assert.Contains(review.Preview,s=>s.Measurement==SurveyMeasurement.TerrainElevation&&s.Value==123.25);Assert.All(review.OriginalsRetained.Values,v=>Assert.False(v));
        var collected=science.PreviewCollectedDeletion(new([results[0].FlightId]),missions);Assert.Empty(collected.Blockers);Assert.Equal("Completed",science.DeleteCollectedScience(new(collected.Id,collected.SelectionHash),missions,coordinator.EnterMaintenance).State);
        Assert.Empty(missions.ResearchAttempts(flightId:results[0].FlightId));Assert.Throws<InvalidOperationException>(()=>Evaluate(results[0].FlightId,card));Assert.NotEmpty(missions.StreamSamples(results[0].FlightId));Assert.True(missions.ReviewSurveyEvidence(results[1],science).MatchesSavedEvidence);
    }
    [Fact]public async Task ChangedOrMissingSavedProductsInvalidatePlotsWithoutRereadingExternalOriginals(){var card=Card(2);var flight=Flight(card);var product=SurveyFixture.Publish(science,missions,flight,await Source(RemoteKind.Terrain),at.AddSeconds(5));var result=Evaluate(flight,card);
        File.AppendAllText(Path.Combine(science.DirectoryPath,product.RelativePath),"changed");var review=missions.ReviewSurveyEvidence(result,science);Assert.False(review.MatchesSavedEvidence);Assert.Empty(review.Preview);Assert.Equal(result.Evidence.Sha256,missions.ResearchAttempt(result.Id).Evidence.Sha256);
    }
    [Theory][InlineData(0,179.999)][InlineData(89.9,-170)]public void WorldwideAirOnlyUsesActualControllerAndSeparateExplicitScienceStart(double lat,double lon){var card=Card(1);var time=DateTimeOffset.UtcNow;using var research=new ResearchOperations(missions,science);var coordinator=new MissionCoordinator(missions,1000,research:research,utcNow:()=>time);
        void Observe(int n){time=time.AddSeconds(1);var p=Geodesy.Direct(new(lat,lon),90,n*150);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic local-only survey",time),Frame(n) with{TimestampUtc=time,Latitude=p.Latitude,Longitude=p.Longitude},new(time,15+n,null,null,null,null)));}
        Observe(0);var flight=coordinator.Start(new("Local-only survey",PlanId:missions.CreateResearchPlan(card.Id,1).Id));var state=Assert.IsType<CollectionState>(coordinator.ScienceCollection(flight.Id).State);Assert.Equal(CollectionPhase.Armed,state.Phase);Assert.Empty(science.StreamObservations(flight.Id));coordinator.ControlScience(new(1,flight.Id,Guid.NewGuid(),state.Revision,CollectionAction.StartData,state.CurrentSessionId));for(var n=1;n<=4;n++)Observe(n);coordinator.End(flight.Id);
        var result=missions.EvaluateResearchAttempt(new(Guid.NewGuid(),flight.Id,flight.StartedUtc,time,CardId:card.Id),science:science);Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Outcome);Assert.Empty(science.Sources());
    }
    [Fact]public void InvalidDefinitionsAndCancelledWorkFailClosed(){foreach(var rule in new[]{new SurveyResearchDefinition(2,[SurveyMeasurement.AirTemperature]),new(1,[]),new(1,[SurveyMeasurement.AirTemperature,SurveyMeasurement.AirTemperature]),new(1,[SurveyMeasurement.AirTemperature],LocationSpacingMetres:double.NaN)})Assert.Throws<ArgumentException>(()=>SurveyResearch.Validate(rule));
        var card=Card(1);var flight=Flight(card);using var cancellation=new CancellationTokenSource();cancellation.Cancel();Assert.Throws<OperationCanceledException>(()=>missions.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(20),CardId:card.Id),cancellation.Token,science));
    }
    [Fact]public async Task ActualRemoteAcquisitionReusesVerifiedRetainedTerrainAcrossFlightsWithoutAnotherImport(){
        var card=Card(2);var source=await Source(RemoteKind.Terrain);var original=source.Files.Select(f=>f.Sha256).ToArray();using var research=new ResearchOperations(missions,science);var clock=DateTimeOffset.UtcNow;var coordinator=new MissionCoordinator(missions,1000,research:research,utcNow:()=>clock);
        for(var attempt=0;attempt<2;attempt++){clock=DateTimeOffset.UtcNow;var frame=Frame(0) with{TimestampUtc=clock,Latitude=40.001,Longitude=-75.001,AltitudeMslFeet=4000,AltitudeAglFeet=3000};coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic remote source reuse",clock),frame));
            var flight=coordinator.Start(new("Actual retained terrain",PlanId:missions.CreateResearchPlan(card.Id,1).Id));var state=Assert.IsType<CollectionState>(coordinator.ScienceCollection(flight.Id).State);coordinator.ControlScience(new(1,flight.Id,Guid.NewGuid(),state.Revision,CollectionAction.StartData,state.CurrentSessionId));clock=clock.AddSeconds(1);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic remote source reuse",clock),frame with{TimestampUtc=clock}));
            var deadline=DateTimeOffset.UtcNow.AddSeconds(5);while(science.Products(flight.Id).Length==0&&DateTimeOffset.UtcNow<deadline)await Task.Delay(25);var product=Assert.Single(science.Products(flight.Id));Assert.Equal(source.Id,product.SourceId);coordinator.End(flight.Id);
            var result=missions.EvaluateResearchAttempt(new(Guid.NewGuid(),flight.Id,flight.StartedUtc,clock,CardId:card.Id),science:science);Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Outcome);Assert.NotNull(product.IntentId);Assert.True(missions.ReviewSurveyEvidence(result,science).MatchesSavedEvidence);
        }Assert.Single(science.Sources());Assert.Equal(original,science.Source(source.Id)!.Files.Select(f=>f.Sha256));
    }
    public void Dispose(){science.Dispose();SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
