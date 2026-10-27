using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    IEnumerable<RecordedResearchAttempt> DetectionManualWindows(SqliteConnection db,SqliteTransaction tx,DetectionSession session,DateTimeOffset from,DateTimeOffset to)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT payload_json FROM personal_card_attempts WHERE mission_id=$flight AND card_id=$card AND started_utc <= $to AND (json_extract(payload_json,'$.endUtc') IS NULL OR json_extract(payload_json,'$.endUtc') >= $from) AND json_extract(payload_json,'$.automatic') IS NULL ORDER BY started_utc,id";
        cmd.Parameters.AddWithValue("$flight",session.Consent.FlightId.ToString());cmd.Parameters.AddWithValue("$card",session.Consent.CardId.ToString());cmd.Parameters.AddWithValue("$from",Stamp(from));cmd.Parameters.AddWithValue("$to",Stamp(to));using var r=cmd.ExecuteReader();while(r.Read())yield return JsonSerializer.Deserialize<RecordedResearchAttempt>(r.GetString(0),WireJson.Options)!;
    }
    void DetectionManualAt(SqliteConnection db,SqliteTransaction tx,DetectionSession session,FlightAttemptDetection detector,DateTimeOffset from,DateTimeOffset to,long sequence,bool afterRows=false)
    {
        if(to<from)return;var blocked=false;
        foreach(var m in DetectionManualWindows(db,tx,session,from,to)){
            if(!afterRows&&m.ManualSource is {} bounds&&sequence<=bounds.AfterSequence)continue;
            detector.Manual(true,m.StartUtc);
            blocked=afterRows?m.Status==ResearchAttemptStatus.Recording:m.ManualSource is {} source?source.ThroughSequence is null||sequence<=source.ThroughSequence:m.EndUtc is null||m.EndUtc>=to;
            if(!blocked)detector.Manual(false,m.EndUtc??to);
        }
        detector.Manual(blocked,to);
    }
    bool DetectionBreaks(SqliteConnection db,SqliteTransaction tx,DetectionSession session,FlightAttemptDetection detector,DateTimeOffset through)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT id,timestamp_utc FROM events WHERE mission_id=$flight AND id>$after AND timestamp_utc<=$through AND kind IN ('simulator-paused','connection','telemetry-gap') ORDER BY id LIMIT 257";
        cmd.Parameters.AddWithValue("$flight",session.Consent.FlightId.ToString());cmd.Parameters.AddWithValue("$after",Math.Max(session.Consent.EventId,detector.State.BreakEventId));cmd.Parameters.AddWithValue("$through",Stamp(through));
        using var r=cmd.ExecuteReader();var count=0;while(r.Read()){if(count++==256)return false;detector.Interrupt(Parse(r.GetString(1)),"Recorded pause, connection or telemetry-gap evidence interrupted detection.");detector.AcknowledgeBreak(r.GetInt64(0));}return true;
    }
    static void AdvanceResearchRevision(SqliteConnection db,SqliteTransaction tx,Guid flight)
    {using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE personal_research_state SET revision=revision+1 WHERE mission_id=$id";cmd.Parameters.AddWithValue("$id",flight.ToString());cmd.ExecuteNonQuery();}
    void PublishDetectionEmission(SqliteConnection db,SqliteTransaction tx,PersonalResearchCard card,DetectionSession session,DetectionEmission emission)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT payload_json FROM personal_card_attempts WHERE id=$id";cmd.Parameters.AddWithValue("$id",emission.Id.ToString());
        var existing=cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<RecordedResearchAttempt>(json,WireJson.Options):null;
        if(existing is not null&&(existing.FlightId!=session.Consent.FlightId||existing.CardId!=card.Id||existing.CardRevision!=card.Revision||existing.TestPointId!=session.Consent.PointId||existing.StartUtc!=emission.StartUtc||existing.Automatic?.SessionId!=session.Consent.Id||existing.Automatic.FirstSequence!=emission.FirstSequence||existing.Automatic.PolicyVersion!=FlightAttemptDetection.Version))throw new InvalidOperationException("A deterministic automatic identity already belongs to different original evidence.");
        if(existing is null){
            if(session.FinishedLedger is not null)throw new InvalidOperationException("Replay found a different automatic candidate in changed originals; historic results were not replaced.");
            var begin=ResearchEvent(db,tx,session.Consent.FlightId,emission.StartUtc,"AutomaticBegin",$"Automatic attempt detected · {card.Name} · {FlightAttemptDetection.Version} · session {session.Consent.Id:D}"+ProcedureEventIdentity(session.Consent.PointId,emission.Id));
            existing=new(emission.Id,session.Consent.FlightId,card.Id,card.Revision,emission.StartUtc,null,ResearchAttemptStatus.Recording,begin,null,null,session.Consent.PointId,Automatic:new(1,session.Consent.Id,FlightAttemptDetection.Version,emission.FirstSequence));
            // Publication follows a complete snapshot/CAS. A manual open interval always wins.
            cmd.CommandText="SELECT COUNT(*) FROM personal_card_attempts WHERE mission_id=$flight AND card_id=$card AND status='Recording' AND json_extract(payload_json,'$.automatic') IS NULL";cmd.Parameters.AddWithValue("$flight",existing.FlightId.ToString());cmd.Parameters.AddWithValue("$card",card.Id.ToString());
            if(Convert.ToInt64(cmd.ExecuteScalar())>0)existing=existing with{Status=ResearchAttemptStatus.Finalizing};
            SaveRecordedAttempt(db,tx,existing);
        }
        if(!emission.Closed)return;
        if(existing.Automatic!.CompletionStatus is not null){if(existing.EndUtc!=emission.EndUtc||existing.Automatic.LastSequence!=emission.LastSequence||existing.Automatic.CompletionStatus!=emission.CompletionStatus)throw new InvalidOperationException("Replay disagrees with a finished automatic interval. Historic attempts were not replaced.");return;}
        var end=ResearchEvent(db,tx,existing.FlightId,emission.EndUtc!.Value,"AutomaticEnd",$"Automatic interval ended · {FlightAttemptDetection.Version} · session {session.Consent.Id:D} · {emission.Reason}"+ProcedureEventIdentity(session.Consent.PointId,emission.Id));
        SaveRecordedAttempt(db,tx,existing with{EndUtc=emission.EndUtc,EndEventId=end,EndReason=emission.Reason,Status=ResearchAttemptStatus.Finalizing,
            Automatic=existing.Automatic with{LastSequence=emission.LastSequence,CompletionStatus=emission.CompletionStatus}});
    }
    public int ProcessAutomaticAttempts(Guid flight,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();DetectionSession[] sessions;
        using(var db=Open())sessions=DetectionSessions(db,null,flight).Where(s=>!s.Complete&&s.Failure is null).Take(16).ToArray();
        var published=0;var replayed=new HashSet<Guid>();
        foreach(var session in sessions){
            try{token.ThrowIfCancellationRequested();PersonalResearchCard card;long revision,maximum;DetectionCursor cursor;DetectionEmission[] emissions;bool complete;
            using(var db=Open())using(var tx=db.BeginTransaction(deferred:true)){
                card=DetectionCard(db,tx,session);using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT revision FROM personal_research_state WHERE mission_id=$flight";cmd.Parameters.AddWithValue("$flight",flight.ToString());revision=Convert.ToInt64(cmd.ExecuteScalar());
                maximum=session.StopSequence??LastDetectionSequence(db,tx,flight);cmd.CommandText="SELECT sequence,payload_json FROM telemetry WHERE mission_id=$flight AND sequence>$after AND sequence<=$maximum ORDER BY sequence LIMIT 128";cmd.Parameters.AddWithValue("$after",session.Cursor.Sequence);cmd.Parameters.AddWithValue("$maximum",maximum);
                var rows=new List<(long Sequence,TelemetryFrame Frame)>();using(var r=cmd.ExecuteReader())while(r.Read())rows.Add((r.GetInt64(0),JsonSerializer.Deserialize<TelemetryFrame>(r.GetString(1),WireJson.Options)!));
                var detector=new FlightAttemptDetection(session.Consent.Id,QuantitativePoint(card,session.Consent.PointId),session.Cursor);
                foreach(var row in rows){
                    token.ThrowIfCancellationRequested();var from=detector.State.LastUtc??session.Consent.StartUtc;var to=row.Frame.TimestampUtc;
                    if(to>=from){if(!DetectionBreaks(db,tx,session,detector,to))break;DetectionManualAt(db,tx,session,detector,from,to,row.Sequence);}
                    if(to<session.Consent.StartUtc||session.StopUtc is {} stop&&to>stop)detector.Skip(row.Sequence,"The original timestamp is outside the explicitly armed window.");else detector.Add(row.Sequence,row.Frame);
                }
                // Drain source data before applying a later manual boundary; never cut off unseen valid samples.
                if(detector.State.Sequence>=maximum){
                    cmd.CommandText="SELECT MAX(started_utc) FROM personal_card_attempts WHERE mission_id=$flight AND card_id=$card AND json_extract(payload_json,'$.automatic') IS NULL";cmd.Parameters.AddWithValue("$card",card.Id.ToString());
                    if(cmd.ExecuteScalar() is string latest&&Parse(latest)>=(detector.State.LastUtc??session.Consent.StartUtc))DetectionManualAt(db,tx,session,detector,detector.State.LastUtc??session.Consent.StartUtc,Parse(latest),maximum,true);
                }
                var sourceEventsDrained=true;
                if(detector.State.Sequence>=maximum){cmd.CommandText="SELECT MAX(timestamp_utc) FROM events WHERE mission_id=$flight AND kind IN ('simulator-paused','connection','telemetry-gap')";if(cmd.ExecuteScalar() is string lastEvent){var through=session.StopUtc is {} stopped&&Parse(lastEvent)>stopped?stopped:Parse(lastEvent);sourceEventsDrained=DetectionBreaks(db,tx,session,detector,through);}}
                complete=session.StopUtc is not null&&detector.State.Sequence>=maximum&&sourceEventsDrained;
                if(complete)detector.Stop(session.StopUtc!.Value,session.StopReason??"The armed recording window ended.");
                cursor=detector.State;emissions=detector.Drain();
            }
            using(var db=Open())using(var tx=db.BeginTransaction()){
                using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT revision FROM personal_research_state WHERE mission_id=$flight";cmd.Parameters.AddWithValue("$flight",flight.ToString());if(Convert.ToInt64(cmd.ExecuteScalar())!=revision)continue;
                var current=DetectionSessions(db,tx,flight,card.Id).Single(s=>s.Consent.Id==session.Consent.Id);if(PreflightReadiness.Hash(current)!=PreflightReadiness.Hash(session))continue;
                foreach(var emission in emissions){PublishDetectionEmission(db,tx,card,session,emission);if(emission.Closed)replayed.Add(emission.Id);published++;}
                if(complete&&session.FinishedLedger is not null&&session.FinishedLedger!=cursor.ClosureDigest)throw new InvalidOperationException("Replay omitted or changed a finished automatic interval; retained originals no longer match.");
                SaveDetectionSession(db,tx,session with{Cursor=cursor,Complete=complete,FinishedLedger=complete?session.FinishedLedger??cursor.ClosureDigest:session.FinishedLedger});if(emissions.Length>0)AdvanceResearchRevision(db,tx,flight);
                EvidenceFault?.Invoke("automatic-detection-before-commit");tx.Commit();
            }
            }catch(Exception ex) when(ex is InvalidOperationException or ArgumentException){FailAutomaticSession(flight,session.Consent.Id);}
        }
        FinalizeAutomaticAttempts(flight,token);VerifyAutomaticReplay(flight,replayed,token);return published;
    }
    void FinalizeAutomaticAttempts(Guid flight,CancellationToken token)
    {
        // Numeric reads happen outside the coordinator and writer transaction. Publication is a short CAS.
        RecordedResearchAttempt[] pending;
        using(var db=Open()){using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM personal_card_attempts WHERE mission_id=$flight AND status='Finalizing' AND json_extract(payload_json,'$.automatic.completionStatus') IS NOT NULL ORDER BY started_utc,id LIMIT 16";cmd.Parameters.AddWithValue("$flight",flight.ToString());var list=new List<RecordedResearchAttempt>();using var r=cmd.ExecuteReader();while(r.Read())list.Add(JsonSerializer.Deserialize<RecordedResearchAttempt>(r.GetString(0),WireJson.Options)!);pending=list.ToArray();}
        foreach(var attempt in pending){
            try{token.ThrowIfCancellationRequested();FlightTestPointEvidence evidence;
            var ended=attempt with{Status=attempt.Automatic!.CompletionStatus!.Value};
            using(var db=Open())using(var tx=db.BeginTransaction(deferred:true)){
                var session=DetectionSessions(db,tx,flight,attempt.CardId).Single(s=>s.Consent.Id==attempt.Automatic.SessionId);var card=DetectionCard(db,tx,session);evidence=ReadPointEvidence(db,tx,card,ended,token);
            }
            using(var db=Open())using(var tx=db.BeginTransaction()){
                using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT payload_json FROM personal_card_attempts WHERE id=$id";cmd.Parameters.AddWithValue("$id",attempt.Id.ToString());if(cmd.ExecuteScalar() as string!=JsonSerializer.Serialize(attempt,WireJson.Options))continue;
                SaveRecordedAttempt(db,tx,ended with{PointEvidence=evidence});AdvanceResearchRevision(db,tx,flight);EvidenceFault?.Invoke("automatic-finalization-before-commit");tx.Commit();
            }
            }catch(Exception ex) when(ex is InvalidOperationException or ArgumentException){FailAutomaticSession(flight,attempt.Automatic!.SessionId);}
        }
    }
    void VerifyAutomaticReplay(Guid flight,IEnumerable<Guid> replayed,CancellationToken token)
    {
        foreach(var id in replayed){token.ThrowIfCancellationRequested();var attempt=RecordedResearchAttempt(id);if(attempt.PointEvidence is null||attempt.Automatic is null)continue;
            try{using var db=Open();using var tx=db.BeginTransaction(deferred:true);var session=DetectionSessions(db,tx,flight,attempt.CardId).Single(s=>s.Consent.Id==attempt.Automatic.SessionId);var card=DetectionCard(db,tx,session);var actual=ReadPointEvidence(db,tx,card,attempt,token);if(PreflightReadiness.Hash(actual)!=PreflightReadiness.Hash(attempt.PointEvidence))throw new InvalidOperationException("Original automatic numeric evidence differs from its retained result.");}
            catch(Exception ex) when(ex is InvalidOperationException or ArgumentException){FailAutomaticSession(flight,attempt.Automatic.SessionId);}
        }
    }
    public void EvaluateAutomaticResults(Guid flight,CancellationToken token=default)
    {
        var mission=Detail(flight)?.Mission;if(mission is null||mission.Status==MissionStatus.Recording)return;
        DetectionSession[] sessions;using(var db=Open())sessions=DetectionSessions(db,null,flight).ToArray();if(!sessions.Any(s=>s.Complete&&s.Failure is null))return;
        using(var db=Open()){using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM personal_card_attempts a WHERE a.mission_id=$flight AND a.status IN ('Recording','Finalizing') AND json_extract(a.payload_json,'$.automatic') IS NOT NULL AND EXISTS(SELECT 1 FROM collection_receipts d WHERE d.mission_id=a.mission_id AND d.kind='personal-detection' AND d.receipt_id=json_extract(a.payload_json,'$.automatic.sessionId') AND json_extract(d.result_json,'$.failure') IS NULL)";cmd.Parameters.AddWithValue("$flight",flight.ToString());if(Convert.ToInt64(cmd.ExecuteScalar())>0)return;}
        const string unevaluated="SELECT a.payload_json FROM personal_card_attempts a WHERE a.mission_id=$flight AND a.status IN ('Recorded','Interrupted') AND json_extract(a.payload_json,'$.automatic') IS NOT NULL AND EXISTS(SELECT 1 FROM collection_receipts d WHERE d.mission_id=a.mission_id AND d.kind='personal-detection' AND d.receipt_id=json_extract(a.payload_json,'$.automatic.sessionId') AND json_extract(d.result_json,'$.complete')=1 AND json_extract(d.result_json,'$.failure') IS NULL) AND NOT EXISTS(SELECT 1 FROM personal_research_attempts r WHERE json_extract(r.payload_json,'$.recordedAttemptId')=a.id) ORDER BY a.started_utc,a.id LIMIT 16";
        var work=new List<RecordedResearchAttempt>();using(var db=Open()){using var cmd=db.CreateCommand();cmd.CommandText=unevaluated;cmd.Parameters.AddWithValue("$flight",flight.ToString());using var r=cmd.ExecuteReader();while(r.Read())work.Add(JsonSerializer.Deserialize<RecordedResearchAttempt>(r.GetString(0),WireJson.Options)!);}
        foreach(var attempt in work){token.ThrowIfCancellationRequested();EvaluateRecordedResearchAttempt(attempt.Id,FlightAttemptDetection.Identity($"{attempt.Id}/automatic-evaluation-1"),token);}
        using(var db=Open()){using var cmd=db.CreateCommand();cmd.CommandText=unevaluated;cmd.Parameters.AddWithValue("$flight",flight.ToString());using var r=cmd.ExecuteReader();if(r.Read())return;}
        foreach(var session in sessions.Where(s=>!s.Evaluated&&s.Complete&&s.Failure is null).Take(16)){
            var card=ResearchOperations(flight).Cards.Single(c=>c.Card.Id==session.Consent.CardId).Card;
            if(card.Procedure is not null&&sessions.Where(s=>s.Consent.CardId==card.Id).All(s=>s.Complete&&s.Failure is null))EvaluateResearchAttempt(new(FlightAttemptDetection.Identity($"{flight}/{card.Id}/automatic-card-evaluation-1"),flight,mission.StartedUtc,mission.EndedUtc!.Value,CardId:card.Id),token);
            using var db=Open();using var tx=db.BeginTransaction();var current=DetectionSessions(db,tx,flight,card.Id).Single(s=>s.Consent.Id==session.Consent.Id);SaveDetectionSession(db,tx,current with{Evaluated=true});tx.Commit();
        }
    }
}
