using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed record DetectionConsent(int Version,string Policy,Guid Id,Guid FlightId,Guid CardId,int CardRevision,Guid PointId,
    string DefinitionSha256,long AfterSequence,DateTimeOffset StartUtc,long EventId,bool WaitForExit=false);
public sealed record DetectionSession(DetectionConsent Consent,DetectionCursor Cursor,long? StopSequence=null,DateTimeOffset? StopUtc=null,
    string? StopReason=null,long? StopEventId=null,bool Complete=false,bool Evaluated=false,string? Failure=null,string? FinishedLedger=null);
public sealed record DetectionRecoveryResult(Guid FlightId,int Sessions,string Message);

public sealed partial class MissionStore
{
    static FlightTestProcedurePoint QuantitativePoint(PersonalResearchCard card,Guid? point)
    {
        SupportedCard(card);
        if(card.Procedure is not null)return card.Procedure.Points.SingleOrDefault(p=>p.Id==point)??throw new ArgumentException("Choose a point from this frozen quantitative procedure.");
        if(card.Version==7&&card.FlightRegime?.TestPoint is not null&&(point is null||point==card.Id))return new(card.Id,card.FlightRegime);
        throw new ArgumentException("Automatic detection supports P10 custom IAS/MSL cards and P11 quantitative points only.");
    }
    static long LastDetectionSequence(SqliteConnection db,SqliteTransaction? tx,Guid flight)
    {using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT COALESCE(MAX(sequence),0) FROM telemetry WHERE mission_id=$flight";cmd.Parameters.AddWithValue("$flight",flight.ToString());return Convert.ToInt64(cmd.ExecuteScalar());}
    static IEnumerable<DetectionSession> DetectionSessions(SqliteConnection db,SqliteTransaction? tx,Guid flight,Guid? card=null,bool latest=false)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT input_sha256,result_json FROM collection_receipts WHERE mission_id=$flight AND kind='personal-detection' AND ($card IS NULL OR json_extract(result_json,'$.consent.cardId')=$card) ORDER BY json_extract(result_json,'$.consent.eventId')"+(latest?" DESC LIMIT 1":"");
        cmd.Parameters.AddWithValue("$flight",flight.ToString());cmd.Parameters.AddWithValue("$card",(object?)card?.ToString()??DBNull.Value);
        using var r=cmd.ExecuteReader();while(r.Read()){var s=JsonSerializer.Deserialize<DetectionSession>(r.GetString(1),WireJson.Options)!;if(PreflightReadiness.Hash(s.Consent)!=r.GetString(0)||s.Consent.Version!=1||s.Consent.Policy!=FlightAttemptDetection.Version)throw new InvalidOperationException("Automatic detection consent or policy is unsupported or changed.");yield return s;}
    }
    static void SaveDetectionSession(SqliteConnection db,SqliteTransaction tx,DetectionSession session)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO collection_receipts VALUES($flight,'personal-detection',$id,$hash,$json) ON CONFLICT(mission_id,kind,receipt_id) DO UPDATE SET result_json=$json";
        cmd.Parameters.AddWithValue("$flight",session.Consent.FlightId.ToString());cmd.Parameters.AddWithValue("$id",session.Consent.Id.ToString());cmd.Parameters.AddWithValue("$hash",PreflightReadiness.Hash(session.Consent));cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(session,WireJson.Options));cmd.ExecuteNonQuery();
    }
    static string DetectionStopMessage(Guid id,long sequence,string reason)=>$"Automatic detection stopped [detection:{id:D}; after:{sequence}] · {reason}";
    static string DetectionConsentMessage(PersonalResearchCard card,Guid id,Guid point,long after,bool wait=false)=>$"Automatic detection enabled for {card.Name}. [detection:{id:D}; point:{point:D}; after:{after}; revision:{card.Revision}; reentry:{wait}]";
    AutomaticDetectionStatus? DetectionStatus(SqliteConnection db,SqliteTransaction? tx,Guid flight,PersonalResearchCard card)
    {
        var s=DetectionSessions(db,tx,flight,card.Id,latest:true).SingleOrDefault();if(s is null)return null;
        FlightTestPointEvidence? evidence=null;if(s.Cursor.LastAttempt is {} id){using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT payload_json FROM personal_card_attempts WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());if(cmd.ExecuteScalar() is string json)evidence=JsonSerializer.Deserialize<RecordedResearchAttempt>(json,WireJson.Options)?.PointEvidence;}
        var enabled=s.StopUtc is null&&!s.Complete&&s.Failure is null;
        var message=s.Failure??s.Cursor.Message;var status=s.Failure is not null?"Insufficient Evidence":s.Cursor.Candidate is {Recognized:true}?"Attempt Detected":evidence?.Outcome==ResearchAttemptOutcome.InsufficientEvidence?"Insufficient Evidence":s.Cursor.LastAttempt is not null?"Attempt Complete":enabled?"Monitoring":"Detection Off";
        if(enabled&&card.Procedure is not null&&s.Cursor.Candidate is null){var progress=RetainedProcedureProgress(db,tx,flight,card);if(progress.Points.Single(p=>p.PointId==s.Consent.PointId).CompletedRepetitions>=card.Procedure.Points.Single(p=>p.Id==s.Consent.PointId).Repetitions){status="Waiting for Next Test Point";message="This point's repetitions are complete. Select the next point and Enable Automatic Detection; separated extra attempts on the current point remain retained until you switch or turn detection off.";}}
        return new(1,s.Consent.Id,s.Consent.PointId,enabled,status,s.Cursor.LastAttempt,s.Failure is null?evidence?.Outcome:null,message);
    }
    static void DeferAutomaticOpen(SqliteConnection db,SqliteTransaction tx,RecordedResearchAttempt? attempt)
    {if(attempt?.Automatic is not null&&attempt.Status==ResearchAttemptStatus.Recording)SaveRecordedAttempt(db,tx,attempt with{Status=ResearchAttemptStatus.Finalizing,EndReason="Awaiting deterministic automatic finalization; manual controls retain priority."});}
    void StopAutomaticDetection(SqliteConnection db,SqliteTransaction tx,Guid flight,Guid? card,DateTimeOffset at,string reason)
    {
        var sessions=DetectionSessions(db,tx,flight,card).Where(s=>s.StopUtc is null).ToArray();var sequence=LastDetectionSequence(db,tx,flight);
        foreach(var s in sessions){var stop=at<s.Consent.StartUtc?s.Consent.StartUtc:at;var id=ResearchEvent(db,tx,flight,stop,"AutomaticStopped",DetectionStopMessage(s.Consent.Id,sequence,reason));SaveDetectionSession(db,tx,s with{StopSequence=sequence,StopUtc=stop,StopReason=reason,StopEventId=id});}
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT payload_json FROM personal_card_attempts WHERE mission_id=$flight AND status='Recording' AND ($card IS NULL OR card_id=$card) AND json_extract(payload_json,'$.automatic') IS NOT NULL";
        cmd.Parameters.AddWithValue("$flight",flight.ToString());cmd.Parameters.AddWithValue("$card",(object?)card?.ToString()??DBNull.Value);var rows=new List<RecordedResearchAttempt>();using(var r=cmd.ExecuteReader())while(r.Read())rows.Add(JsonSerializer.Deserialize<RecordedResearchAttempt>(r.GetString(0),WireJson.Options)!);foreach(var a in rows)DeferAutomaticOpen(db,tx,a);
    }
    void EnableAutomaticDetection(SqliteConnection db,SqliteTransaction tx,Guid flight,PersonalResearchCard card,Guid pointId,Guid id,DateTimeOffset at,long eventId,long after)
    {
        var point=QuantitativePoint(card,pointId);
        if(card.Procedure is {Ordered:true} procedure){var progress=RetainedProcedureProgress(db,tx,flight,card);var index=Array.FindIndex(procedure.Points,p=>p.Id==point.Id);if(progress.Points.Take(index).Any(p=>p.CompletedRepetitions<p.RequiredRepetitions))throw new InvalidOperationException("Complete the required earlier point repetitions before arming this ordered point.");}
        StopAutomaticDetection(db,tx,flight,card.Id,at,"The pilot changed the monitored test point.");
        var wait=DetectionSessions(db,tx,flight,card.Id).Any(s=>s.Consent.PointId==point.Id);
        var consent=new DetectionConsent(1,FlightAttemptDetection.Version,id,flight,card.Id,card.Revision,point.Id,PreflightReadiness.Hash(card),after,at,eventId,wait);
        SaveDetectionSession(db,tx,new(consent,new(Sequence:after,WaitingExit:wait,BreakEventId:eventId)));
    }
    PersonalResearchCard DetectionCard(SqliteConnection db,SqliteTransaction? tx,DetectionSession session)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT card_json FROM personal_flight_cards WHERE mission_id=$flight AND card_id=$card";cmd.Parameters.AddWithValue("$flight",session.Consent.FlightId.ToString());cmd.Parameters.AddWithValue("$card",session.Consent.CardId.ToString());
        var card=JsonSerializer.Deserialize<PersonalResearchCard>(cmd.ExecuteScalar() as string??throw new InvalidOperationException("Frozen automatic card is unavailable."),WireJson.Options)!;
        if(card.Revision!=session.Consent.CardRevision||PreflightReadiness.Hash(card)!=session.Consent.DefinitionSha256)throw new InvalidOperationException("The frozen automatic card or revision changed.");
        _=QuantitativePoint(card,session.Consent.PointId);
        cmd.CommandText="SELECT message FROM events WHERE mission_id=$flight AND id=$event AND timestamp_utc=$at AND kind='research-card-EnableAutomatic'";cmd.Parameters.AddWithValue("$event",session.Consent.EventId);cmd.Parameters.AddWithValue("$at",Stamp(session.Consent.StartUtc));
        if(cmd.ExecuteScalar() as string!=DetectionConsentMessage(card,session.Consent.Id,session.Consent.PointId,session.Consent.AfterSequence,session.Consent.WaitForExit))throw new InvalidOperationException("Original automatic detection consent is unavailable or changed.");
        if(session.StopUtc is {} stop){cmd.CommandText="SELECT message FROM events WHERE mission_id=$flight AND id=$stopEvent AND timestamp_utc=$stop AND kind='research-card-AutomaticStopped'";cmd.Parameters.AddWithValue("$stopEvent",(object?)session.StopEventId??DBNull.Value);cmd.Parameters.AddWithValue("$stop",Stamp(stop));if(cmd.ExecuteScalar() as string!=DetectionStopMessage(session.Consent.Id,session.StopSequence??-1,session.StopReason??""))throw new InvalidOperationException("Original automatic stop boundary changed.");}
        return card;
    }
    public Guid[] PendingAutomaticFlights(int offset=0)
    {
        if(offset<0)throw new ArgumentException("Invalid detection work page.");using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT DISTINCT mission_id FROM collection_receipts WHERE kind='personal-detection' AND (json_extract(result_json,'$.complete')=0 OR json_extract(result_json,'$.evaluated')=0) AND json_extract(result_json,'$.failure') IS NULL ORDER BY mission_id LIMIT 16 OFFSET $offset";cmd.Parameters.AddWithValue("$offset",offset);var rows=new List<Guid>();using var r=cmd.ExecuteReader();while(r.Read())rows.Add(Guid.Parse(r.GetString(0)));return rows.ToArray();
    }
    public DetectionRecoveryResult RecoverAutomaticAttempts(Guid flight)
    {
        if(Detail(flight)?.Mission.Status==MissionStatus.Recording)throw new InvalidOperationException("End the flight before replaying its automatic detection windows.");
        using var db=Open();using var tx=db.BeginTransaction();var sessions=DetectionSessions(db,tx,flight).ToArray();
        foreach(var session in sessions){_=DetectionCard(db,tx,session);SaveDetectionSession(db,tx,session with{Cursor=new(Sequence:session.Consent.AfterSequence,WaitingExit:session.Consent.WaitForExit,BreakEventId:session.Consent.EventId),Complete=false,Evaluated=false,Failure=null});}
        tx.Commit();return new(flight,sessions.Length,"Queued bounded replay of originally armed windows only. Original finished attempts and frozen requirements are never replaced.");
    }
    void FailAutomaticSession(Guid flight,Guid id)
    {
        using var db=Open();using var tx=db.BeginTransaction();var s=DetectionSessions(db,tx,flight).Single(s=>s.Consent.Id==id);
        SaveDetectionSession(db,tx,s with{Failure="Automatic evidence could not be verified. Manual controls remain available; recover originally armed windows after flight to retry safely."});AdvanceResearchRevision(db,tx,flight);tx.Commit();
    }

}
