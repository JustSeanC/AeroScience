using AeroScience.Shared;
using AeroScience.Missions;
using AeroScience.Telemetry;
using Xunit;
using AeroScience.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
namespace AeroScience.Tests;
public sealed class MissionPerformanceIntegrationTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-performance-"+Guid.NewGuid());
    readonly MissionStore store;readonly ScienceStore science;readonly ResearchOperations research;readonly MissionCoordinator coordinator;
    DateTimeOffset now=DateTimeOffset.UtcNow;
    public MissionPerformanceIntegrationTests(){var s=new StorageManager(new PreferenceStore(root)).Initialize();store=new(Path.Combine(s.MissionDirectory,"missions.db"));store.Initialize();science=new(s.ScienceLibraryDirectory);science.Initialize();store.ScienceLibrary=science;research=new(store,science);coordinator=new(store,1000,research:research,utcNow:()=>now);}
    Mission Start(bool armed=false,ScienceMode mode=ScienceMode.Realistic)
    {var p=store.SavePlan(new(Guid.NewGuid(),"Synthetic three-set mission",Mode:mode,Objective:"Observe three seconds of original wind",EstimatedDurationMinutes:.05,Instruments:[new("ambient-wind",true)]));return coordinator.Start(new("Synthetic mission",PlanId:p.Id,ArmScience:armed));}
    void Tick(){now=now.AddSeconds(1);var frame=SimulatedTelemetryProvider.Generate(now,0);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Explicitly synthetic development evidence",now),frame,new(now,20,29,10,90,.002)));}
    void Action(Guid mission,CollectionAction action){var state=store.CollectionCheckpoint(mission)!;coordinator.ControlScience(new(1,mission,Guid.NewGuid(),state.Revision,action,state.CurrentSessionId));}
    [Fact] public async Task RealArchiveReportsSurviveRestartSettingsChangesLibraryMoveAndPortableExport()
    {
        var flight=Start();Tick();Tick();Action(flight.Id,CollectionAction.EndData);Assert.Equal(MissionStatus.Recording,store.Detail(flight.Id)!.Mission.Status);Assert.Empty(store.PerformanceReports(flight.Id));Action(flight.Id,CollectionAction.StartData);Tick();coordinator.End(flight.Id);
        var input=MissionEvaluationArchive.Build(store,science,flight.Id);var report=store.SavePerformanceReport(input,"Actual synthetic backend acquisition evidence");Assert.Equal(MissionGradeStatus.Final,report.Grade.Status);Assert.Equal(MissionGradeLetter.A,report.Grade.Letter);Assert.Equal(2,report.Input.Sessions.Length);Assert.Equal(3,report.Input.Yield.Single().Metrics[0].Numerator);
        var frozen=store.CollectionSnapshot<MissionAssessmentContext>(flight.Id)!;store.SetInstruments(flight.Id,[]);store.Initialize();var replay=MissionEvaluationArchive.Build(store,science,flight.Id);Assert.Equal(report.Grade.EvidenceSha256,PreflightReadiness.Hash(replay));Assert.Equal(EvidenceContracts.ContentHash(frozen),EvidenceContracts.ContentHash(store.CollectionSnapshot<MissionAssessmentContext>(flight.Id)!));
        Assert.Single(store.PerformanceReports(flight.Id));MissionGrading.Verify(store.PerformanceReports(flight.Id)[0]);
        var path=science.DirectoryPath;research.Dispose();science.Dispose();var moved=Path.Combine(root,"MovedLibrary");Directory.Move(path,moved);using var relocated=new ScienceStore(moved);relocated.Initialize();store.ScienceLibrary=relocated;var relocatedInput=MissionEvaluationArchive.Build(store,relocated,flight.Id);Assert.Equal(report.Grade.EvidenceSha256,PreflightReadiness.Hash(relocatedInput));
        using var export=new MemoryStream();await store.ExportJsonAsync(flight.Id,export);var text=System.Text.Encoding.UTF8.GetString(export.ToArray());Assert.Contains("performanceReports",text);Assert.Contains(report.Sha256,text);Assert.DoesNotContain(path,text);
    }
    [Fact] public void EarlyEndAndEasyExtraSessionCannotEraseMissingFrozenWork()
    {
        var flight=Start();Tick();Action(flight.Id,CollectionAction.EndData);Tick();Action(flight.Id,CollectionAction.StartData);Tick();coordinator.End(flight.Id);
        var input=MissionEvaluationArchive.Build(store,science,flight.Id);var report=MissionGrading.Report(input);var discipline=report.Categories.Single(c=>c.Category==RubricCategory.AcquisitionDiscipline);Assert.Equal(100d*2/3,discipline.ScoreLower,7);Assert.True(report.Grade.ScoreUpper<100);Assert.Equal(2,input.Sessions.Length);
        Assert.All(input.Context!.Obligations.Where(o=>o.CriterionId=="discipline"),o=>Assert.Equal(3,o.TargetAmount));
    }
    [Fact] public void EasyExtendedCollectionCannotDiluteAnEarlierMissedFrozenOpportunity()
    {
        var flight=Start();Tick();Action(flight.Id,CollectionAction.EndData);Tick();Action(flight.Id,CollectionAction.StartData);Tick();
        var before=MissionGrading.Report(MissionEvaluationArchive.Build(store,science,flight.Id));
        for(var i=0;i<10;i++)Tick();coordinator.End(flight.Id);var after=MissionGrading.Report(MissionEvaluationArchive.Build(store,science,flight.Id));
        var original=before.Categories.Single(c=>c.Category==RubricCategory.AcquisitionDiscipline);var extended=after.Categories.Single(c=>c.Category==RubricCategory.AcquisitionDiscipline);
        Assert.Equal(100d*2/3,original.ScoreLower,7);Assert.Equal(original.ScoreLower,extended.ScoreLower);Assert.Equal(12,after.Input.Yield.Single().Metrics[0].Denominator);Assert.Equal(12,after.Input.Yield.Single().Metrics[0].Numerator);
    }
    [Fact] public void UnquantifiedResearchCannotBecomeACompleteGradeByAddingSuccessfulSamples()
    {
        var flight=StartWithUnboundedPlan();for(var i=0;i<8;i++)Tick();coordinator.End(flight.Id);var report=MissionGrading.Report(MissionEvaluationArchive.Build(store,science,flight.Id));
        Assert.Equal(MissionGradeStatus.InsufficientEvidence,report.Grade.Status);Assert.Null(report.Grade.Letter);Assert.Equal(0,report.Categories.Single(c=>c.Category==RubricCategory.AcquisitionDiscipline).EvidenceFraction);Assert.Equal(8,report.Input.Yield.Single().Metrics[0].Numerator);
    }
    Mission StartWithUnboundedPlan(){var p=store.SavePlan(new(Guid.NewGuid(),"Unbounded synthetic research",Mode:ScienceMode.Realistic,Objective:"Observe wind",Instruments:[new("ambient-wind",true)]));return coordinator.Start(new("Unbounded synthetic flight",PlanId:p.Id));}
    [Fact] public void AllPausedOrNeverStartedFlightsDoNotEarnPerfectGradesAndUniversalIsUngraded()
    {
        var flight=Start(true);Tick();Tick();Tick();coordinator.End(flight.Id);var empty=MissionGrading.Report(MissionEvaluationArchive.Build(store,science,flight.Id));Assert.NotEqual(MissionGradeLetter.A,empty.Grade.Letter);Assert.All(empty.Input.Yield,y=>Assert.Null(y.Metrics[0].Numerator));
        var universal=Start(mode:ScienceMode.Universal);Tick();Tick();Tick();coordinator.End(universal.Id);var result=MissionGrading.Report(MissionEvaluationArchive.Build(store,science,universal.Id));Assert.Equal(MissionGradeStatus.NotGraded,result.Grade.Status);Assert.Equal(3,result.Input.Yield.Single().Metrics[0].Numerator);
    }
    [Fact] public void RevisionsAreAppendOnlyDeduplicatedAndRawValuesAreNotModified()
    {
        var flight=Start();Tick();Tick();Tick();coordinator.End(flight.Id);var raw=PreflightReadiness.Hash(store.StreamSamples(flight.Id).ToArray());var input=MissionEvaluationArchive.Build(store,science,flight.Id);var first=store.SavePerformanceReport(input,"Original evidence");Assert.Equal(first.Sha256,store.SavePerformanceReport(input,"Same inputs").Sha256);
        var second=store.SavePerformanceReport(input with{Limitations=[..input.Limitations,"Additional supported evidence explanation"]},"Explicit controlled revision");Assert.Equal(2,second.Revision);Assert.Equal(first.Sha256,second.PreviousSha256);Assert.Equal(MissionGradeStatus.Revised,second.Grade.Status);Assert.Equal(2,store.PerformanceReports(flight.Id).Length);Assert.Equal(raw,PreflightReadiness.Hash(store.StreamSamples(flight.Id).ToArray()));
    }
    [Fact] public void IncrementalLiveProjectionUsesEachArchivedPositionAndIntentOnce()
    {
        var flight=Start();var projection=new MissionLiveProjection(store,flight.Id);Tick();var first=projection.Update();Assert.Equal(MissionGradeStatus.Provisional,first.Grade.Status);Assert.Null(first.Grade.Letter);
        Tick();Tick();var complete=projection.Update();var replay=projection.Update();Assert.Equal(complete.Sha256,replay.Sha256);Assert.True(complete.SupportedScoreLower>first.SupportedScoreLower);Assert.Equal(100,complete.SupportedScoreLower);Assert.Null(complete.Grade.Letter);coordinator.End(flight.Id);
    }
    [Fact] public void DeclaredSafetyPauseDoesNotRemoveFrozenTargetsOrPenalizeItsObservedInterval()
    {
        var flight=Start();Tick();var state=store.CollectionCheckpoint(flight.Id)!;coordinator.ControlScience(new(1,flight.Id,Guid.NewGuid(),state.Revision,CollectionAction.PauseData,state.CurrentSessionId,Justification:"safety"));Tick();Action(flight.Id,CollectionAction.ResumeData);Tick();Tick();coordinator.End(flight.Id);
        var input=MissionEvaluationArchive.Build(store,science,flight.Id);var grade=MissionGrading.Report(input);Assert.Equal(100,grade.Categories.Single(c=>c.Category==RubricCategory.AcquisitionDiscipline).ScoreLower);Assert.Equal(3,input.Context!.Obligations.Single(o=>o.CriterionId=="discipline").TargetAmount);Assert.Contains(input.Sessions.Single().Events,e=>e.Justification=="safety");
    }
    [Fact] public void MissingRemoteSourceDoesNotBecomePilotFailureOrGoodScientificData()
    {
        now=DateTimeOffset.UtcNow.AddSeconds(-4);var plan=store.SavePlan(new(Guid.NewGuid(),"Missing source fixture",Mode:ScienceMode.Realistic,Objective:"Acquire remote source if available",EstimatedDurationMinutes:.05,Instruments:[new("thermal-imager",true)]));
        var flight=coordinator.Start(new("Synthetic no-source flight",PlanId:plan.Id));Tick();Tick();Tick();coordinator.End(flight.Id);
        var input=MissionEvaluationArchive.Build(store,science,flight.Id);var report=MissionGrading.Report(input);Assert.Equal(100,report.Categories.Single(c=>c.Category==RubricCategory.AcquisitionDiscipline).ScoreLower);Assert.Null(input.Yield.Single().Metrics.Single(m=>m.Id=="good-data-sets").Numerator);Assert.True(input.Yield.Single().Metrics.Single(m=>m.Id=="good-data-sets").Unassessed>0);
    }
    [Fact] public void InterruptedFlightCannotBecomeAFinalGradeFromAnApparentlyCompletedSession()
    {
        var flight=Start();Tick();Tick();Tick();coordinator.Interrupt();var input=MissionEvaluationArchive.Build(store,science,flight.Id);Assert.True(input.Interrupted);var report=store.SavePerformanceReport(input,"Interrupted evidence review");Assert.Equal(MissionGradeStatus.InsufficientEvidence,report.Grade.Status);Assert.Null(report.Grade.Letter);Assert.Contains(report.Grade.Limitations!,s=>s.Contains("final recording boundary"));
    }
    [Fact] public void PlannedAutomaticStartWithoutEndRetainsResearchUntilFlightEnd()
    {
        var frame=SimulatedTelemetryProvider.Generate(now,0);var waypoint=new ResearchWaypoint("Start only",frame.Latitude,frame.Longitude,Id:Guid.NewGuid());var id=Guid.NewGuid();var trigger=new CollectionTrigger(Guid.NewGuid(),Guid.NewGuid(),CollectionAction.StartData,CollectionTriggerKind.Waypoint,waypoint.Id!.Value,1,20,PlannedSessionKey:"A");
        var plan=store.SavePlan(new(id,"Optional End fixture",Mode:ScienceMode.Realistic,Objective:"Observe wind after explicit Start",Waypoints:[waypoint],Instruments:[new("ambient-wind",true)],Collection:new(1,id,InitialCollectionPolicy.Armed,[trigger],new(5,6))));
        var flight=coordinator.Start(new("Synthetic optional-end flight",PlanId:plan.Id));Tick();Tick();Tick();Assert.Equal(CollectionPhase.Collecting,store.CollectionCheckpoint(flight.Id)!.Phase);coordinator.End(flight.Id);
        var input=MissionEvaluationArchive.Build(store,science,flight.Id);var obligation=input.Context!.Obligations.Single(o=>o.CriterionId=="discipline");var evidence=input.Evidence.Single(e=>e.ObligationId==obligation.Id);Assert.Equal(0,evidence.ScoreLower);Assert.Equal(100,evidence.ScoreUpper);Assert.Equal(0,evidence.EvidenceFraction);Assert.Equal(MissionGradeStatus.InsufficientEvidence,MissionGrading.Report(input).Grade.Status);Assert.Equal(3,evidence.Metrics!.Single().Denominator);Assert.Equal(3,evidence.Metrics!.Single().Numerator);
    }
    [Fact] public void FrozenRoutePauseAndExplicitEndAreAuthorizedHalfOpenAcquisitionWindows()
    {
        var id=Guid.NewGuid();var route=Enumerable.Range(0,4).Select(i=>new ResearchWaypoint("Action "+i,40,-75+i*.002,Id:Guid.NewGuid())).ToArray();
        CollectionTrigger Trigger(int index,CollectionAction action)=>new(Guid.NewGuid(),Guid.NewGuid(),action,CollectionTriggerKind.Waypoint,route[index].Id!.Value,1,20,PlannedSessionKey:"A");
        var plan=store.SavePlan(new(id,"Frozen route pauses",Mode:ScienceMode.Realistic,Objective:"Collect on authorized research segments",Waypoints:route,Instruments:[new("ambient-wind",true)],Collection:new(1,id,InitialCollectionPolicy.Armed,[Trigger(0,CollectionAction.StartData),Trigger(1,CollectionAction.PauseData),Trigger(2,CollectionAction.ResumeData),Trigger(3,CollectionAction.EndData)],new(5,6))));var flight=coordinator.Start(new("Synthetic authorized-pause flight",PlanId:plan.Id));
        foreach(var point in route){now=now.AddSeconds(1);var f=SimulatedTelemetryProvider.Generate(now,0) with{Latitude=point.Latitude,Longitude=point.Longitude};coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic route evidence",now),f,new(now,20,29,10,90,.002)));}
        coordinator.End(flight.Id);var input=MissionEvaluationArchive.Build(store,science,flight.Id);var result=input.Evidence.Single(e=>input.Context!.Obligations.Single(o=>o.Id==e.ObligationId).CriterionId=="discipline");Assert.Equal(100,result.ScoreLower);Assert.Equal(2,result.Metrics!.Single().Denominator);Assert.Equal(2,result.Metrics!.Single().Numerator);Assert.Equal(2,input.Yield.Single().Metrics[0].Numerator);
    }
    [Fact] public void GeographicSurveyUsesExplicitFrozenWindowsAndRetainsBonusScienceSeparately()
    {
        var id=Guid.NewGuid();var route=Enumerable.Range(0,3).Select(i=>new ResearchWaypoint("Survey "+i,40,-75+i*.002,Id:Guid.NewGuid())).ToArray();
        CollectionTrigger Trigger(int index,CollectionAction action)=>new(Guid.NewGuid(),Guid.NewGuid(),action,CollectionTriggerKind.Waypoint,route[index].Id!.Value,1,20,PlannedSessionKey:"survey-A");
        var area=new SurveyArea("Frozen study region",[new(39.99,-75.01),new(39.99,-74.99),new(40.01,-74.99),new(40.01,-75.01)]);
        var plan=store.SavePlan(new(id,"Bounded synthetic geographic study",MissionTemplateKind.GeographicSurvey,ScienceMode.Realistic,Objective:"Collect wind on the declared research legs",Waypoints:route,SurveyAreas:[area],Instruments:[new("ambient-wind",true)],Collection:new(1,id,InitialCollectionPolicy.Armed,[Trigger(0,CollectionAction.StartData),Trigger(2,CollectionAction.EndData)],new(5,6))));
        var flight=coordinator.Start(new("Synthetic bounded survey",PlanId:plan.Id));
        foreach(var point in route){now=now.AddSeconds(1);var f=SimulatedTelemetryProvider.Generate(now,0) with{Latitude=point.Latitude,Longitude=point.Longitude};coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic bounded geographic window",now),f,new(now,20,29,10,90,.002)));}
        Action(flight.Id,CollectionAction.StartData);
        for(var i=0;i<4;i++){now=now.AddSeconds(1);var f=SimulatedTelemetryProvider.Generate(now,0) with{Latitude=40,Longitude=-74.996};coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic bonus session",now),f,new(now,20,29,10,90,.002)));}
        coordinator.End(flight.Id);var input=MissionEvaluationArchive.Build(store,science,flight.Id);var report=MissionGrading.Report(input);Assert.Equal(MissionGradeLetter.A,report.Grade.Letter);Assert.Equal(6,input.Yield.Single().Metrics[0].Numerator);
        Assert.Equal(2,input.Sessions.Length);Assert.All(input.Evidence.Where(e=>input.Context!.Obligations.Single(o=>o.Id==e.ObligationId).CriterionId=="discipline"),e=>{Assert.Equal(100,e.ScoreLower);Assert.Equal(2,e.Metrics!.Single().Numerator);Assert.Equal(2,e.Metrics!.Single().Denominator);});
    }
    [Fact] public void TelemetryJumpHoldIsUnassessedEvidenceAndCannotBecomePilotNoncompliance()
    {
        var id=Guid.NewGuid();var route=new[]{new ResearchWaypoint("Start",40,-75,Id:Guid.NewGuid()),new ResearchWaypoint("End",40,-74,Id:Guid.NewGuid())};
        CollectionTrigger Trigger(int index,CollectionAction action)=>new(Guid.NewGuid(),Guid.NewGuid(),action,CollectionTriggerKind.Waypoint,route[index].Id!.Value,1,20,PlannedSessionKey:"A");
        var plan=store.SavePlan(new(id,"Uncertain continuity",Mode:ScienceMode.Realistic,Objective:"Observe wind with verified continuity",Waypoints:route,Instruments:[new("ambient-wind",true)],Collection:new(1,id,InitialCollectionPolicy.Armed,[Trigger(0,CollectionAction.StartData),Trigger(1,CollectionAction.EndData)],new(5,6))));var flight=coordinator.Start(new("Synthetic jump flight",PlanId:plan.Id));
        foreach(var point in route){now=now.AddSeconds(1);var f=SimulatedTelemetryProvider.Generate(now,0) with{Latitude=point.Latitude,Longitude=point.Longitude};coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic discontinuous positions",now),f,new(now,20,29,10,90,.002)));}
        Assert.True(store.CollectionCheckpoint(flight.Id)!.ContinuityHold);coordinator.End(flight.Id);var input=MissionEvaluationArchive.Build(store,science,flight.Id);var report=MissionGrading.Report(input);Assert.Equal(MissionGradeStatus.InsufficientEvidence,report.Grade.Status);Assert.Null(report.Grade.Letter);var result=input.Evidence.Single(e=>input.Context!.Obligations.Single(o=>o.Id==e.ObligationId).CriterionId=="discipline");Assert.Equal(100,result.ScoreUpper);Assert.True(result.EvidenceFraction<1);Assert.True(result.Metrics!.Single().Unassessed>0);
    }
    [Fact] public void LaterEnabledInstrumentRetainsItsYieldWithoutReplacingFrozenResearchObligations()
    {
        var flight=Start();Tick();coordinator.ConfigureInstruments([new("ambient-temperature",true)]);Tick();Tick();coordinator.End(flight.Id);var input=MissionEvaluationArchive.Build(store,science,flight.Id);
        Assert.Single(input.Context!.Configurations);Assert.Equal("ambient-wind",input.Context.Configurations[0].Settings.InstrumentId);Assert.Equal(2,input.Yield.Length);Assert.Equal(1,input.Yield.Single(y=>y.InstrumentId=="ambient-wind").Metrics[0].Numerator);Assert.Equal(2,input.Yield.Single(y=>y.InstrumentId=="ambient-temperature").Metrics[0].Numerator);Assert.Equal(2,input.Sessions.Single().Yield.Length);var grade=MissionGrading.Report(input);Assert.Equal(100d/3,grade.Categories.Single(c=>c.Category==RubricCategory.AcquisitionDiscipline).ScoreLower,7);
    }
    [Fact] public void QueuedPreStartTelemetryCannotProduceNegativeLiveGrades()
    {
        var flight=Start();var early=now.AddMilliseconds(-50);var frame=SimulatedTelemetryProvider.Generate(early,0);store.Append(flight.Id,frame);var projection=new MissionLiveProjection(store,flight.Id).Update();Assert.Equal(MissionGradeStatus.Provisional,projection.Grade.Status);Assert.True(projection.SupportedScoreLower>=0);Assert.All(projection.Input.Evidence,e=>Assert.True(e.EvidenceFraction>=0));coordinator.End(flight.Id);
    }
    [Fact] public async Task UnsupportedGradingContextCannotStopTheHostOrRawFlightRecorder()
    {
        var flight=Start();using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=store.DatabasePath,Pooling=false}.ToString())){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE collection_snapshots SET payload_json=json_set(payload_json,'$.version',99) WHERE kind='assessment-context' AND mission_id=$id";cmd.Parameters.AddWithValue("$id",flight.Id.ToString());Assert.Equal(1,cmd.ExecuteNonQuery());}
        using var analysis=new ScientificAnalysisJobs(store,science);var worker=new MissionPerformanceService(store,analysis,NullLogger<MissionPerformanceService>.Instance,science);
        using var host=new HostBuilder().ConfigureServices((_,services)=>{services.AddSingleton(worker);services.AddHostedService(sp=>sp.GetRequiredService<MissionPerformanceService>());}).Build();
        await host.StartAsync();var deadline=System.Diagnostics.Stopwatch.StartNew();
        while(true){await Task.Delay(10);try{if(System.Text.Json.JsonSerializer.SerializeToElement(worker.Read(flight.Id),WireJson.Options).GetProperty("state").GetString()=="Unavailable")break;}catch(NotSupportedException){}if(deadline.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("Expected the independent grading worker to report its fault without stopping the host.");}
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);Tick();Assert.Equal(1,store.Detail(flight.Id)!.Mission.SampleCount);Assert.Empty(store.PerformanceReports(flight.Id));await host.StopAsync();coordinator.End(flight.Id);
    }
    public void Dispose(){research.Dispose();science.Dispose();if(Directory.Exists(root))Directory.Delete(root,true);}
}
