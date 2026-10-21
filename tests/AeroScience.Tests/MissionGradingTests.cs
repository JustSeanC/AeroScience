using AeroScience.Shared;
using AeroScience.Missions;
using Xunit;
namespace AeroScience.Tests;
public sealed class MissionGradingTests
{
    internal static MissionEvaluationInput Example(double score=90)
    {
        var p=PreflightReadinessTests.Plan();var at=PreflightReadinessTests.At;var context=MissionRubrics.Freeze(Guid.NewGuid(),at,ScienceMode.Realistic,PreflightReadiness.Hash(p),p,ScienceCatalog.Normalize(p.Instruments));
        return new(1,context.MissionId,at.AddMinutes(10),true,context,context.Obligations.Select(o=>new CriterionEvidence(o.Id,score,score,1,[o.CriterionId switch{"design"=>"frozen-plan","operating"=>"operating-assessment","execution"=>"flight-position",_=>"collection-intent"}],["Explicitly labeled, hand-calculated development fixture"],[],["fixture:"+o.Id])).ToArray(),[],[],[],[]);
    }
    [Theory][InlineData(100,MissionGradeLetter.A)][InlineData(90,MissionGradeLetter.A)][InlineData(89.99999,MissionGradeLetter.B)][InlineData(80,MissionGradeLetter.B)][InlineData(79.99999,MissionGradeLetter.C)][InlineData(70,MissionGradeLetter.C)][InlineData(69.99999,MissionGradeLetter.D)][InlineData(60,MissionGradeLetter.D)][InlineData(59.99999,MissionGradeLetter.F)][InlineData(0,MissionGradeLetter.F)]
    public void HandCalculatedThresholdsDoNotRoundUp(double score,MissionGradeLetter letter)
    {
        var input=Example(score);var result=MissionGrading.Evaluate(input);Assert.Equal(MissionGradeStatus.Final,result.Grade.Status);Assert.Equal(letter,result.Grade.Letter);Assert.Equal(score,result.Grade.ScoreLower!.Value,8);Assert.Equal(result.Grade.ScoreLower,result.Grade.ScoreUpper);
        MissionGrading.Verify(MissionGrading.Report(input));
    }
    [Fact] public void MissingRequiredEvidenceNeverBecomesAnAAndUncertaintyIsNotNormalizedAway()
    {
        var input=Example(100);var unknown=input.Evidence.Select(e=>e with{ScoreLower=null,ScoreUpper=null,EvidenceFraction=0,Classes=[]}).ToArray();
        var empty=MissionGrading.Evaluate(input with{Evidence=unknown});Assert.Equal(MissionGradeStatus.InsufficientEvidence,empty.Grade.Status);Assert.Null(empty.Grade.Letter);Assert.Equal(0,empty.Lower);Assert.Equal(100,empty.Upper);
        var uncertain=MissionGrading.Evaluate(input with{Evidence=input.Evidence.Select(e=>e with{ScoreLower=80,ScoreUpper=95,EvidenceFraction=.8}).ToArray()});Assert.Equal(MissionGradeStatus.InsufficientEvidence,uncertain.Grade.Status);Assert.Null(uncertain.Grade.Letter);Assert.Equal(.8,uncertain.Grade.EvidenceWeightFraction!.Value,8);
        var below=MissionGrading.Evaluate(input with{Evidence=input.Evidence.Select(e=>e with{ScoreLower=92,ScoreUpper=95,EvidenceFraction=.7999}).ToArray()});Assert.Equal(MissionGradeStatus.InsufficientEvidence,below.Grade.Status);
        var justBelow=MissionGrading.Evaluate(input with{Evidence=input.Evidence.Select(e=>e with{ScoreLower=92,ScoreUpper=95,EvidenceFraction=.7999999999}).ToArray()});Assert.Equal(MissionGradeStatus.InsufficientEvidence,justBelow.Grade.Status);
        var enough=MissionGrading.Evaluate(input with{Evidence=input.Evidence.Select(e=>e with{ScoreLower=92,ScoreUpper=95,EvidenceFraction=.8}).ToArray()});Assert.Equal(MissionGradeLetter.A,enough.Grade.Letter);
    }
    [Fact] public void SessionsCannotDiluteOrRemoveFrozenWorkAndProvisionalNeverHasLetter()
    {
        var input=Example();var obligation=input.Context!.Obligations.Last(o=>o.CriterionId=="discipline");
        var skipped=input with{Evidence=input.Evidence.Select(e=>e.ObligationId==obligation.Id?e with{ScoreLower=0,ScoreUpper=0}:e).ToArray()};
        var first=MissionGrading.Evaluate(skipped);var at=input.AsOfUtc;
        SessionEvaluation Session(long n)=>new(new(1,Guid.NewGuid(),input.MissionId,n,at.AddMinutes(-2),CollectionSessionStatus.Ended,[],[],EndedUtc:at,EndReason:CollectionEndReason.Manual),[],[],[],[]);
        var split=MissionGrading.Evaluate(skipped with{Sessions=[Session(1),Session(2),Session(3)]});Assert.Equal(first.Grade.ScoreLower,split.Grade.ScoreLower);Assert.Equal(first.Grade.Letter,split.Grade.Letter);Assert.NotEqual(MissionGradeLetter.A,first.Grade.Letter);
        var live=MissionGrading.Evaluate(input with{Completed=false});Assert.Equal(MissionGradeStatus.Provisional,live.Grade.Status);Assert.Null(live.Grade.Letter);
        Assert.Throws<InvalidDataException>(()=>MissionGrading.Evaluate(input with{Evidence=[..input.Evidence,input.Evidence[0]]}));
    }
    [Fact] public void OriginalPolicyAndModesAreRetainedAndRevisionsReproduce()
    {
        var input=Example();var original=MissionGrading.Report(input);MissionGrading.Verify(original);
        var revised=MissionGrading.Report(input with{Evidence=input.Evidence.Select(e=>e with{ScoreLower=95,ScoreUpper=95}).ToArray()},2,"New supported evidence",original.Sha256);Assert.Equal(MissionGradeStatus.Revised,revised.Grade.Status);MissionGrading.Verify(revised);
        Assert.Throws<InvalidDataException>(()=>MissionGrading.Verify(original with{Reason="changed"}));
        Assert.Equal(MissionGradeStatus.NotGraded,MissionGrading.Evaluate(input with{Context=null}).Grade.Status);
        Assert.Equal(MissionGradeStatus.NotGraded,MissionGrading.Evaluate(input with{Context=input.Context! with{Mode=ScienceMode.Universal}}).Grade.Status);
        foreach(var version in new[]{0,2})Assert.Equal(MissionGradeStatus.Unsupported,MissionGrading.Evaluate(input with{Context=input.Context! with{Rubric=input.Context.Rubric with{Revision=version}}}).Grade.Status);
    }
}
