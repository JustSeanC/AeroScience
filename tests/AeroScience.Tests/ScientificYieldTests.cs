using AeroScience.Shared;
using AeroScience.Missions;
using Xunit;
using AeroScience.Telemetry;
namespace AeroScience.Tests;
public sealed class ScientificYieldTests
{
    static DateTimeOffset At=>PreflightReadinessTests.At;
    static AcquisitionResult Result(Guid session,int valid=2,int missing=0,bool assessed=true,bool acquired=true)=>new(Guid.NewGuid(),session,"ambient-wind",At,2,valid,missing,assessed,acquired,false,[],[]);
    [Fact] public void HandCalculatedWindCountsSetsAndFieldsWithoutCalibrationOrDuplicateInflation()
    {
        var a=Guid.NewGuid();var good=Result(a);var rows=new[]{good,good,Result(a,1,1),Result(a,0,2,false,false)};
        var y=ScienceYieldEvaluation.Calculate(Guid.NewGuid(),null,"ambient-wind",At,rows);
        var sets=y.Metrics.Single(m=>m.Id=="good-data-sets");Assert.Equal(1,sets.Numerator);Assert.Equal(2,sets.Denominator);Assert.Equal(1,sets.Unassessed);
        var values=y.Metrics.Single(m=>m.Id=="good-data-values");Assert.Equal(3,values.Numerator);Assert.Equal(4,values.Denominator);Assert.Equal(1,values.Missing);
    }
    [Fact] public void SessionSplittingDoesNotChangeFlightDenominatorsAndReconstructionIsSeparate()
    {
        var mission=Guid.NewGuid();var a=Guid.NewGuid();var b=Guid.NewGuid();var rows=new[]{Result(a),Result(a,1,1),Result(a,0,2,false,false)};
        var first=ScienceYieldEvaluation.Calculate(mission,null,"ambient-wind",At,rows);
        var split=ScienceYieldEvaluation.Calculate(mission,null,"ambient-wind",At,rows.Select((r,i)=>r with{SessionId=i%2==0?a:b}));
        Assert.Equal(first.Metrics,split.Metrics);
        var reconstructed=ScienceYieldEvaluation.Calculate(mission,null,"ambient-wind",At,[..rows,Result(b) with{Reconstructed=true}]);Assert.Equal(first.Metrics[0],reconstructed.Metrics[0]);Assert.Equal(1,reconstructed.Metrics.Single(m=>m.Id=="reconstructed-sets").Numerator);
    }
    [Fact] public void FrozenObligationsDoNotChangeWhenSessionsOrSettingsChange()
    {
        var p=PreflightReadinessTests.Plan();var c=MissionRubrics.Freeze(Guid.NewGuid(),At,ScienceMode.Realistic,PreflightReadiness.Hash(p),p,ScienceCatalog.Normalize(p.Instruments));var hash=EvidenceContracts.ContentHash(c);
        Assert.Equal(new double[]{20,30,25,25},c.Rubric.Categories.Select(w=>w.Weight));Assert.Equal(.8,c.Rubric.MinimumEvidenceWeightFraction);Assert.All(c.Obligations.GroupBy(o=>o.CriterionId),g=>Assert.Equal(1,g.Sum(o=>o.Weight),9));
        p=p with{Instruments=[],SurveyAreas=[]};Assert.Equal(hash,EvidenceContracts.ContentHash(c));Assert.NotEmpty(c.Configurations);Assert.Contains(c.Obligations,o=>o.Scope=="region");
        Assert.Equal(hash,EvidenceContracts.ContentHash(EvidenceContracts.Read<MissionAssessmentContext>(EvidenceContracts.Write(c))));
        var atmospheric=MissionRubrics.Define(MissionTemplateKind.AtmosphericProfile,false);Assert.False(atmospheric.Criteria.Single(c=>c.Id=="operating").Applicable);Assert.DoesNotContain(atmospheric.Criteria,c=>c.Method.Contains("require polygon"));
    }
    [Fact] public void MissingAndEmptyEvidenceNeverBecomePerfectScience()
    {
        var y=ScienceYieldEvaluation.Calculate(Guid.NewGuid(),null,"ambient-wind",At,[]);Assert.Null(y.Metrics[0].Numerator);Assert.Null(y.Metrics[0].Denominator);Assert.Equal(AssessmentStatus.NotAssessed,y.Metrics[0].Status);
    }
    [Fact] public void ActualWindAcquisitionAndFrozenContextSurviveArchiveReopen()
    {
        var root=Path.Combine(Path.GetTempPath(),"aeroscience-yield-"+Guid.NewGuid());
        try
        {
            var settings=new StorageManager(new PreferenceStore(root)).Initialize();var store=new MissionStore(Path.Combine(settings.MissionDirectory,"missions.db"));store.Initialize();using var science=new ScienceStore(settings.ScienceLibraryDirectory);science.Initialize();store.ScienceLibrary=science;
            using var research=new ResearchOperations(store,science);var now=At;var coordinator=new MissionCoordinator(store,1000,research:research,utcNow:()=>now);
            var p=store.SavePlan(new(Guid.NewGuid(),"Synthetic wind study",MissionTemplateKind.AtmosphericProfile,ScienceMode.Realistic,Objective:"Observe wind",MinimumAltitudeFeet:1000,MaximumAltitudeFeet:2000,Instruments:[new("ambient-wind",true)]));
            var flight=coordinator.Start(new("Synthetic flight",PlanId:p.Id));var context=store.CollectionSnapshot<MissionAssessmentContext>(flight.Id)!;Assert.NotNull(context);var hash=EvidenceContracts.ContentHash(context);
            for(var i=0;i<3;i++){now=now.AddSeconds(1);var frame=SimulatedTelemetryProvider.Generate(now,0);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Explicitly synthetic development fixture",now),frame,new(now,20,29,12,90,.002)));}
            coordinator.End(flight.Id);var evidence=ScientificEvaluationEvidence.Read(store,science,flight.Id);Assert.Equal(3,evidence.Length);Assert.All(evidence,e=>{Assert.Equal(2,e.ExpectedMeasurements);Assert.Equal(2,e.ValidMeasurements);});
            var y=ScienceYieldEvaluation.Calculate(flight.Id,null,"ambient-wind",now,evidence);Assert.Equal(3,y.Metrics[0].Numerator);Assert.Equal(6,y.Metrics[1].Numerator);
            store.Initialize();Assert.Equal(hash,EvidenceContracts.ContentHash(store.CollectionSnapshot<MissionAssessmentContext>(flight.Id)!));Assert.Equal(2,context.Rubric.Criteria.Count(c=>c.Id is "execution" or "discipline"));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
