using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    static bool ProcedureCompatible(PersonalResearchCard first,PersonalResearchCard second)
    {
        IEnumerable<FlightRegimeDefinition> Rules(PersonalResearchCard card)=>card.Procedure?.Points.Select(p=>p.Conditions with{TestPoint=p.Conditions.TestPoint! with{Policy="all"}})??[card.FlightRegime!];
        // Conservative for all selectable points; changing point cannot bypass an active-card conflict.
        return Rules(first).All(a=>Rules(second).All(b=>CompatibleResearchCards(
            first with{Version=a.TestPoint is null?1:7,TemplateId=a.TestPoint is null?"flight-regime":FlightRegimeEvaluation.CustomTemplate,Procedure=null,FlightRegime=a},
            second with{Version=b.TestPoint is null?1:7,TemplateId=b.TestPoint is null?"flight-regime":FlightRegimeEvaluation.CustomTemplate,Procedure=null,FlightRegime=b})));
    }
    static IEnumerable<RecordedResearchAttempt> ProcedureAttempts(SqliteConnection db,SqliteTransaction? tx,Guid flight,Guid card)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT payload_json FROM personal_card_attempts WHERE mission_id=$flight AND card_id=$card ORDER BY started_utc,id";
        cmd.Parameters.AddWithValue("$flight",flight.ToString());cmd.Parameters.AddWithValue("$card",card.ToString());
        using var reader=cmd.ExecuteReader();while(reader.Read())yield return JsonSerializer.Deserialize<RecordedResearchAttempt>(reader.GetString(0),WireJson.Options)!;
    }
    static FlightTestProcedureSummary RetainedProcedureProgress(SqliteConnection db,SqliteTransaction? tx,Guid flight,PersonalResearchCard card)
    {
        var progress=new FlightProcedureProgress(card.Procedure!);
        foreach(var attempt in ProcedureAttempts(db,tx,flight,card.Id))progress.Add(attempt);
        return progress.Summary(); // provisional controls only; postflight review rechecks original bytes
    }
    FlightTestPointEvidence ReadPointEvidence(SqliteConnection db,SqliteTransaction tx,PersonalResearchCard card,RecordedResearchAttempt attempt,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var point=card.Procedure!.Points.SingleOrDefault(p=>p.Id==attempt.TestPointId)??throw new InvalidOperationException("The marked point is absent from the frozen card.");
        if(attempt.EndUtc is not {} end||end<attempt.StartUtc)
            throw new InvalidOperationException("A marked point needs an ended evidence interval of at most 24 hours. Recording remains independent.");
        if(end-attempt.StartUtc>TimeSpan.FromHours(2))return new(point.Id,ResearchAttemptOutcome.InsufficientEvidence,0,null,null,
            new(0,0,0,Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(),PreflightReadiness.Hash(Research(attempt.FlightId).PlanSnapshot!),null),[],
            "This marked interval exceeds the two-hour point-processing bound. It was closed without qualifying; start a shorter explicit attempt. Recording and science continue independently.");
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT message FROM events WHERE id=$event AND mission_id=$flight AND timestamp_utc=$at AND kind='research-card-BeginAttempt'";
        cmd.Parameters.AddWithValue("$event",attempt.BeginEventId);cmd.Parameters.AddWithValue("$flight",attempt.FlightId.ToString());cmd.Parameters.AddWithValue("$at",Stamp(attempt.StartUtc));
        var begin=cmd.ExecuteScalar() as string;
        if(begin is null||!begin.Contains(point.Id.ToString("D"),StringComparison.Ordinal))throw new InvalidOperationException("Original point-selection event does not match the marked attempt.");
        var evaluation=new FlightRegimeEvaluation.PointWindow(point.Conditions,attempt.StartUtc,end);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var pauses=PersonalPauseTimes(attempt.FlightId,attempt.StartUtc,end).GetEnumerator();var hasPause=pauses.MoveNext();
        long first=0,last=0,count=0,gaps=0,invalid=0,breaks=0;DateTimeOffset? previous=null;TelemetrySource? source=null;string? aircraft=null;
        cmd.CommandText="SELECT sequence,payload_json FROM telemetry WHERE mission_id=$flight AND timestamp_utc >= $start AND timestamp_utc <= $end ORDER BY sequence";
        cmd.Parameters.AddWithValue("$start",Stamp(attempt.StartUtc));cmd.Parameters.AddWithValue("$end",Stamp(end));
        using(var r=cmd.ExecuteReader())while(r.Read()){
            token.ThrowIfCancellationRequested();var sequence=r.GetInt64(0);var json=r.GetString(1);var frame=JsonSerializer.Deserialize<TelemetryFrame>(json,WireJson.Options)!;
            while(hasPause&&pauses.Current<=frame.TimestampUtc){evaluation.Break();breaks++;AppendResearchBreak(hash,pauses.Current);hasPause=pauses.MoveNext();}
            if(previous is {} prior){if((frame.TimestampUtc-prior).TotalSeconds is <=0 or >FlightRegimeEvaluation.MaximumGapSeconds)gaps++;}
            else if((frame.TimestampUtc-attempt.StartUtc).TotalSeconds>FlightRegimeEvaluation.MaximumGapSeconds)gaps++;
            previous=frame.TimestampUtc;if(!FlightRegimeEvaluation.SupportedSample(frame,point.Conditions))invalid++;
            evaluation.Add(frame);AppendResearchSample(hash,sequence,json);if(count++==0)first=sequence;last=sequence;source??=frame.Source;aircraft??=frame.Aircraft;
        }
        while(hasPause){token.ThrowIfCancellationRequested();evaluation.Break();breaks++;AppendResearchBreak(hash,pauses.Current);hasPause=pauses.MoveNext();}
        if(previous is null||(end-previous.Value).TotalSeconds>FlightRegimeEvaluation.MaximumGapSeconds)gaps++;
        var plan=Research(attempt.FlightId).PlanSnapshot;
        return evaluation.Finish(point.Id,new(first,last,count,Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),PreflightReadiness.Hash(plan!),source),attempt.Status!=ResearchAttemptStatus.Recorded) with{SampleGaps=gaps,UnsupportedReadings=invalid,RecordedBreaks=breaks,Aircraft=aircraft};
    }
    (FlightTestProcedureSummary Summary,ResearchEvidenceReference Evidence,FlightTestMarkedReview[] Preview,bool More) CalculateProcedure(
        PersonalResearchCard card,Guid flight,DateTimeOffset start,DateTimeOffset end,bool interrupted,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(start.Offset!=TimeSpan.Zero||end.Offset!=TimeSpan.Zero||end<start||end-start>TimeSpan.FromHours(24))throw new NotSupportedException("Use a UTC procedure review interval of at most 24 hours.");
        using var db=Open();using var tx=db.BeginTransaction(deferred:true);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var progress=new FlightProcedureProgress(card.Procedure!);var preview=new List<FlightTestMarkedReview>();long count=0;TelemetrySource? source=null;
        foreach(var original in ProcedureAttempts(db,tx,flight,card.Id)){
            token.ThrowIfCancellationRequested();if(original.StartUtc<start||original.StartUtc>end)continue;
            if(original.CardRevision!=card.Revision||original.EndUtc>end)throw new InvalidOperationException("Marked point crosses the selected interval or differs from the frozen revision. Review the whole flight.");
            var verified=original.EndUtc is null?original with{PointEvidence=null}:original with{PointEvidence=ReadPointEvidence(db,tx,card,original,token)};
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(verified,WireJson.Options)));var qualification=progress.Add(verified);
            if(verified.PointEvidence is {} e){count+=e.Evidence.SampleCount;source??=e.Evidence.Source;}
            if(preview.Count<20)preview.Add(new(verified,qualification.Qualified,qualification.Reason));
        }
        var summary=progress.Summary(interrupted);var plan=Research(flight).PlanSnapshot;
        return(summary,new(0,0,count,Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),PreflightReadiness.Hash(plan!),source),preview.ToArray(),summary.AttemptCount>preview.Count);
    }
    public FlightTestProcedureReview ReviewProcedureEvidence(PersonalResearchAttempt result,CancellationToken token=default)
    {
        SupportedCard(result.Card);if(result.Card.Procedure is null)throw new ArgumentException("Choose a multi-point result.");
        var mission=Detail(result.FlightId)!.Mission;
        var c=CalculateProcedure(result.Card,result.FlightId,result.StartUtc,result.EndUtc,mission.Status!=MissionStatus.Completed,token);
        var frozen=ResearchOperations(result.FlightId).Cards.Single(c=>c.Card.Id==result.Card.Id).Card;
        var matches=PreflightReadiness.Hash(frozen)==PreflightReadiness.Hash(result.Card)&&result.EvaluatorVersion==FlightRegimeEvaluation.ProcedureVersion
            &&result.Version==8&&c.Evidence==result.Evidence&&PreflightReadiness.Hash(c.Summary)==PreflightReadiness.Hash(result.Procedure!)&&result.Outcome==c.Summary.Outcome;
        return new(matches,c.Summary,c.Preview,c.More,matches?"Original marked intervals, point identities, frozen criteria and evidence match. No recordings were copied.":"Evidence or frozen requirements changed. Historical outcomes are retained but not verified.");
    }
}
