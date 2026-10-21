using AeroScience.Shared;
namespace AeroScience.Missions;

public static class ToolbarIntelligenceProjection
{
    public static ToolbarIntelligence Create(EngineSnapshot flight,CollectionOverview? collection,
        MissionPerformanceReport? report=null,InstrumentOperatingReport[]? operating=null,string triggerStatus="Next crossing not assessed; review configured actions in the dashboard.")
    {
        var state=collection?.State;var warnings=new List<string>();
        if(collection?.Fault==true||flight.Research?.ScienceStorageError is not null)warnings.Add("Scientific storage unavailable; resolve storage and rearm. Raw flight recording is independent.");
        if(state?.ContinuityHold==true)warnings.Add("Continuity hold: establish fresh position evidence and explicitly rearm.");
        var rules=(operating??[]).Where(o=>o.Assessment.MissionId==flight.ActiveMission?.Id&&o.Assessment.SessionId==state?.CurrentSessionId).SelectMany(o=>o.Assessment.Rules).ToArray();
        var concerns=rules.Count(r=>r.Compliance is AssessmentStatus.Caution or AssessmentStatus.Invalid||r.DerivationValidity is AssessmentStatus.Invalid or AssessmentStatus.Unsupported);
        if(concerns>0)warnings.Add($"{concerns} operating/derived-validity concerns; review instrument evidence on the dashboard.");
        var unknown=rules.Count(r=>r.Compliance is AssessmentStatus.Unknown or AssessmentStatus.NotAssessed or AssessmentStatus.Unsupported);
        if(unknown>0)warnings.Add($"{unknown} operating constraints unknown/unassessed; no successful compliance is inferred.");
        if(report?.Input.Limitations.Any(l=>l.Contains("projection is partial",StringComparison.Ordinal))==true)warnings.Insert(0,"Bounded live projection is partial; full archived reconciliation is required.");
        var metrics=(report?.Input.Yield??[]).Where(y=>y.SessionId is null).SelectMany(y=>y.Metrics.Where(m=>m.Id=="good-data-sets").Select(m=>new ToolbarScienceMetric(y.InstrumentId,m.Id,m.Status,m.Numerator,m.Denominator,m.Unit,m.Missing,m.Unassessed))).ToArray();
        return new(1,DateTimeOffset.UtcNow,flight.ActiveMission?.Id,state?.Revision,state?.ControlRevision,
            state?.CurrentSessionId,collection?.CurrentSession?.Sequence,state?.Phase??CollectionPhase.Unknown,
            state?.Authority??CollectionAuthority.Unknown,state?.ManualAutomationHold??false,state?.ContinuityHold??false,
            collection?.Actions.Where(a=>a.Available).Select(a=>a.Action).ToArray()??[],
            report?.Grade.Status??MissionGradeStatus.NotGraded,report?.SupportedScoreLower,report?.SupportedScoreUpper,
            report?.Grade.EvidenceWeightFraction,report?.Input.AsOfUtc,metrics.Take(4).ToArray(),warnings.Take(3).ToArray(),triggerStatus,
            metrics.Length>4||warnings.Count>3);
    }
}
