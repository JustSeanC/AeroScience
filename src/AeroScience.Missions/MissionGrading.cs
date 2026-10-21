using AeroScience.Shared;
namespace AeroScience.Missions;

public static class MissionGrading
{
    public static MissionGradeLetter Letter(double score,GradeThresholds thresholds)=>score>=thresholds.A?MissionGradeLetter.A:score>=thresholds.B?MissionGradeLetter.B:score>=thresholds.C?MissionGradeLetter.C:score>=thresholds.D?MissionGradeLetter.D:MissionGradeLetter.F;
    public static (MissionGrade Grade,CategoryPerformance[] Categories,double? Lower,double? Upper) Evaluate(MissionEvaluationInput input)
    {
        if(input.Version!=1)throw new NotSupportedException("Unsupported performance evidence version; retained inputs were not changed.");
        var context=input.Context;var hash=PreflightReadiness.Hash(input);
        MissionGrade Ungraded(MissionGradeStatus state,string reason)=>new(1,input.MissionId,input.AsOfUtc,state,EvidenceSha256:hash,Limitations:[reason,..input.Limitations]);
        if(context is null)return(Ungraded(MissionGradeStatus.NotGraded,"Legacy mission: no frozen grading contract. No sessions, evidence or obligations were invented."),[],null,null);
        if(context.MissionId!=input.MissionId)throw new InvalidDataException("Frozen context belongs to another flight.");
        if(context.Mode==ScienceMode.Universal)return(Ungraded(MissionGradeStatus.NotGraded,"Universal mode exposes factual scientific yield without imposing Realistic pilot grades."),[],null,null);
        var rubric=context.Rubric;
        if(context.Version!=1||rubric.Version!=1||rubric.Id!=MissionRubrics.Policy||rubric.Revision!=1)
            return(Ungraded(MissionGradeStatus.Unsupported,"Unsupported frozen rubric/context version; no current policy substituted."),[],null,null);
        EvidenceContracts.Validate(context);
        var expected=MissionRubrics.Define(rubric.Template,rubric.Criteria.Single(c=>c.Id=="operating").Applicable??true);
        if(EvidenceContracts.ContentHash(expected)!=EvidenceContracts.ContentHash(rubric))return(Ungraded(MissionGradeStatus.Unsupported,"Frozen rubric contents do not match the supported policy revision."),[],null,null);
        if(input.Evidence.Select(e=>e.ObligationId).Distinct().Count()!=input.Evidence.Length||input.Evidence.Any(e=>!context.Obligations.Any(o=>o.Id==e.ObligationId)))throw new InvalidDataException("Duplicate or unbound criterion evidence.");
        foreach(var e in input.Evidence)
            if(!double.IsFinite(e.EvidenceFraction)||e.EvidenceFraction is <0 or >1||e.ScoreLower.HasValue!=e.ScoreUpper.HasValue||e.ScoreLower is {} low&&(!double.IsFinite(low)||low<0||low>100)||e.ScoreUpper is {} high&&(!double.IsFinite(high)||high<0||high>100)||e.ScoreLower>e.ScoreUpper||e.EvidenceFraction>0&&e.ScoreLower is null)throw new InvalidDataException("Invalid criterion score bounds/evidence fraction.");
        var categories=new List<CategoryPerformance>();var results=new List<GradingCriterionResult>();bool required=true;
        var activeWeight=rubric.Categories.Where(c=>rubric.Criteria.Any(k=>k.Category==c.Category&&(k.Applicable??true))).Sum(c=>c.Weight);
        foreach(var category in rubric.Categories)
        {
            var applicable=rubric.Criteria.Where(c=>c.Category==category.Category&&(c.Applicable??true)).ToArray();decimal lower=0,upper=0,known=0;
            foreach(var criterion in applicable)
            {
                var obligations=context.Obligations.Where(o=>o.CriterionId==criterion.Id).ToArray();var total=obligations.Sum(o=>(decimal)o.Weight);
                if(total==0){upper+=100*(decimal)criterion.Weight;required=false;continue;}
                decimal criterionLower=0,criterionUpper=0,criterionKnown=0;
                foreach(var obligation in obligations)
                {
                    var evidence=input.Evidence.FirstOrDefault(e=>e.ObligationId==obligation.Id);var weight=(decimal)obligation.Weight;
                    criterionLower+=weight*(decimal)(evidence?.ScoreLower??0);criterionUpper+=weight*(decimal)(evidence?.ScoreUpper??100);criterionKnown+=weight*(decimal)(evidence?.EvidenceFraction??0);
                    if(criterion.Required&&criterion.RequiredEvidenceClasses.Any(c=>!input.Evidence.Where(e=>obligations.Any(o=>o.Id==e.ObligationId)&&e.EvidenceFraction>0).SelectMany(e=>e.Classes).Contains(c)))required=false;
                    results.Add(new(obligation.Id,criterion.Id,evidence?.ScoreLower is null?AssessmentStatus.NotAssessed:evidence.ScoreLower==evidence.ScoreUpper?AssessmentStatus.Supported:AssessmentStatus.Caution,
                        evidence?.ScoreLower==evidence?.ScoreUpper?evidence?.ScoreLower:null,evidence?.Reasons??["No supporting evidence for this frozen obligation."],evidence?.Recommendations??["Review the missing planned research work."]));
                }
                lower+=(decimal)criterion.Weight*criterionLower/total;upper+=(decimal)criterion.Weight*criterionUpper/total;known+=(decimal)criterion.Weight*criterionKnown/total;
            }
            var isApplicable=applicable.Length>0;
            categories.Add(new(category.Category,category.Weight,isApplicable?100*category.Weight/activeWeight:0,isApplicable,(double)lower,(double)upper,(double)known,
                isApplicable?"Frozen obligation-weighted evidence; unknown weight remains in the 0–100 interval. No session averages.":"Declared inapplicable in the frozen rubric before departure; no undocumented sensor envelope invented."));
        }
        var lo=Math.Clamp((double)(categories.Where(c=>c.Applicable).Sum(c=>(decimal)c.ScoreLower*(decimal)c.OriginalWeight)/(decimal)activeWeight),0,100);var hi=Math.Clamp((double)(categories.Where(c=>c.Applicable).Sum(c=>(decimal)c.ScoreUpper*(decimal)c.OriginalWeight)/(decimal)activeWeight),lo,100);
        var fraction=(double)(categories.Where(c=>c.Applicable).Sum(c=>(decimal)c.EvidenceFraction*(decimal)c.OriginalWeight)/(decimal)activeWeight);
        var eligible=fraction>=rubric.MinimumEvidenceWeightFraction&&required&&!input.Interrupted;
        var same=Letter(lo,rubric.Thresholds)==Letter(hi,rubric.Thresholds);
        var status=!input.Completed?MissionGradeStatus.Provisional:eligible&&same?MissionGradeStatus.Final:MissionGradeStatus.InsufficientEvidence;
        var grade=new MissionGrade(1,input.MissionId,input.AsOfUtc,status,EvidenceContracts.ContentHash(rubric),hash,
            status==MissionGradeStatus.InsufficientEvidence?null:lo,status==MissionGradeStatus.InsufficientEvidence?null:hi,fraction,
            status==MissionGradeStatus.Final?Letter(lo,rubric.Thresholds):null,results.ToArray(),
            input.Sessions.Select(s=>new SessionPerformanceBreakdown(s.Session.Id,s.Criteria,s.Yield.SelectMany(y=>y.Metrics.Select(m=>m with{Id=y.InstrumentId+":"+m.Id})).ToArray(),s.Limitations)).ToArray(),
            [..rubric.Limitations,..input.Limitations,..(input.Interrupted?new[]{"Flight interrupted: the final recording boundary is unknown; no final letter grade was inferred."}:Array.Empty<string>()),$"Evidence eligibility: {fraction.ToString("P1",System.Globalization.CultureInfo.InvariantCulture)}, required classes {(required?"present":"missing")}; score range {(same?"within one letter":"spans letter bands")}. Provisional scores have no final letter."]);
        EvidenceContracts.Validate(grade);return(grade,categories.ToArray(),lo,hi);
    }
    public static MissionPerformanceReport Report(MissionEvaluationInput input,int revision=1,string reason="End-flight archived evidence",string? previous=null,DateTimeOffset? created=null)
    {
        var evaluated=Evaluate(input);var grade=evaluated.Grade;
        if(revision>1&&grade.Status==MissionGradeStatus.Final)grade=grade with{Status=MissionGradeStatus.Revised};
        var report=new MissionPerformanceReport(1,input.MissionId,revision,created??input.AsOfUtc,reason,previous,input,grade,evaluated.Categories,evaluated.Lower,evaluated.Upper,"");
        return report with{Sha256=PreflightReadiness.Hash(report)};
    }
    public static void Verify(MissionPerformanceReport report)
    {
        if(report.Version!=1||report.MissionId!=report.Input.MissionId||report.Sha256!=PreflightReadiness.Hash(report with{Sha256=""}))throw new InvalidDataException("Performance report identity/hash verification failed.");
        var replay=Report(report.Input,report.Revision,report.Reason,report.PreviousSha256,report.CreatedUtc);
        if(PreflightReadiness.Hash(replay)!=PreflightReadiness.Hash(report))throw new InvalidDataException("Archived report does not reproduce from frozen evidence and rules.");
    }
}
