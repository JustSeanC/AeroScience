using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    // A per-flight work bound, never a retained-card/library ownership ceiling.
    public const int MaximumFlightCards = 16;
    private void MigrateResearchOperations(SqliteConnection db)
    {
        using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS personal_flight_cards(
              mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,card_id TEXT NOT NULL,
              card_json TEXT NOT NULL,active INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(mission_id,card_id));
            CREATE TABLE IF NOT EXISTS personal_research_state(
              mission_id TEXT PRIMARY KEY REFERENCES missions(id) ON DELETE CASCADE,revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS personal_card_attempts(
              id TEXT PRIMARY KEY,mission_id TEXT NOT NULL,card_id TEXT NOT NULL,started_utc TEXT NOT NULL,
              status TEXT NOT NULL,payload_json TEXT NOT NULL,
              FOREIGN KEY(mission_id,card_id) REFERENCES personal_flight_cards(mission_id,card_id) ON DELETE CASCADE);
            CREATE UNIQUE INDEX IF NOT EXISTS one_open_personal_attempt ON personal_card_attempts(mission_id,card_id) WHERE status='Recording';
            CREATE INDEX IF NOT EXISTS personal_card_attempts_flight ON personal_card_attempts(mission_id,started_utc,id);
            PRAGMA user_version=10;
            """;
        cmd.ExecuteNonQuery(); EvidenceFault?.Invoke("research-operations-before-commit"); tx.Commit();
    }

    public static ResearchCardReference[] SelectedResearchCards(MissionPlan? plan)
    {
        var cards = plan?.ResearchCards ?? (plan?.ResearchCard is {} single ? [single] : []);
        if (cards.Length > MaximumFlightCards || cards.Any(c => c is null || c.Id == Guid.Empty || c.Revision < 1)
            || cards.Select(c => c.Id).Distinct().Count() != cards.Length)
            throw new ArgumentException("Choose distinct saved test-card revisions, up to 16 cards for one flight.");
        return cards;
    }

    private void FreezeResearchCards(SqliteConnection db, SqliteTransaction tx, Guid flight, MissionPlan? plan)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        foreach (var reference in SelectedResearchCards(plan))
        {
            cmd.Parameters.Clear(); cmd.CommandText = "SELECT payload_json FROM personal_research_cards WHERE id=$id AND revision=$revision";
            cmd.Parameters.AddWithValue("$id", reference.Id.ToString()); cmd.Parameters.AddWithValue("$revision", reference.Revision);
            var json = cmd.ExecuteScalar() as string ?? throw new ArgumentException("Selected personal card revision is unavailable. Review the plan.");
            var card = JsonSerializer.Deserialize<PersonalResearchCard>(json, WireJson.Options)!; SupportedCard(card);
            cmd.CommandText = "INSERT INTO personal_flight_cards VALUES($flight,$id,$json,0)";
            cmd.Parameters.AddWithValue("$flight", flight.ToString()); cmd.Parameters.AddWithValue("$json", json); cmd.ExecuteNonQuery();
        }
        cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO personal_research_state VALUES($flight,0)";
        cmd.Parameters.AddWithValue("$flight", flight.ToString()); cmd.ExecuteNonQuery();
    }

    private static void SupportedCard(PersonalResearchCard card)
    {
        if(card.Version==4&&card.TemplateId==SurveyResearch.Template&&card.FlightRegime is null&&card.AtmosphericProfile is null&&card.AtmosphericTransect is null){SurveyResearch.Validate(card.Survey);return;}
        if(card.Survey is not null)throw new NotSupportedException("Unsupported survey definition version.");
        if(card.Version==3&&card.TemplateId==AtmosphericTransectResearch.Template&&card.FlightRegime is null&&card.AtmosphericProfile is null){AtmosphericTransectResearch.Validate(card.AtmosphericTransect);return;}
        if(card.Version==2&&card.TemplateId==AtmosphericResearch.Template&&card.FlightRegime is null&&card.AtmosphericTransect is null){AtmosphericResearch.Validate(card.AtmosphericProfile);return;}
        if (card.Version != 1 || card.TemplateId != "flight-regime"||card.AtmosphericProfile is not null||card.AtmosphericTransect is not null) throw new NotSupportedException("Unsupported personal research definition version.");
        FlightRegimeEvaluation.Validate(card.FlightRegime);
    }

    public static bool CompatibleResearchCards(PersonalResearchCard first, PersonalResearchCard second)
    {
        SupportedCard(first); SupportedCard(second);
        // Atmospheric collection sets no aircraft target constraint. Science controls remain separate.
        if(first.Survey is not null||second.Survey is not null||AtmosphericResearch.IsCollectionCard(first)||AtmosphericResearch.IsCollectionCard(second))return true;
        var a = first.FlightRegime!; var b = second.FlightRegime!;
        return Math.Max(a.AirspeedKnots-a.AirspeedToleranceKnots,b.AirspeedKnots-b.AirspeedToleranceKnots)
                <= Math.Min(a.AirspeedKnots+a.AirspeedToleranceKnots,b.AirspeedKnots+b.AirspeedToleranceKnots)
            && Math.Max(a.AltitudeMslFeet-a.AltitudeToleranceFeet,b.AltitudeMslFeet-b.AltitudeToleranceFeet)
                <= Math.Min(a.AltitudeMslFeet+a.AltitudeToleranceFeet,b.AltitudeMslFeet+b.AltitudeToleranceFeet);
    }

    private ResearchFlightState ReadResearchOperations(SqliteConnection db, SqliteTransaction? tx, Guid flight)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT status FROM missions WHERE id=$id"; cmd.Parameters.AddWithValue("$id", flight.ToString());
        var status = cmd.ExecuteScalar() as string ?? throw new ArgumentException("Recorded flight not found.");
        cmd.CommandText = "SELECT revision FROM personal_research_state WHERE mission_id=$id";
        var revision = Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        cmd.CommandText = "SELECT payload_json FROM personal_card_attempts WHERE mission_id=$id AND status='Recording'";
        var opened = new Dictionary<Guid,RecordedResearchAttempt>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
        { var attempt = JsonSerializer.Deserialize<RecordedResearchAttempt>(reader.GetString(0),WireJson.Options)!; opened.Add(attempt.CardId,attempt); }
        cmd.CommandText = "SELECT card_json,active FROM personal_flight_cards WHERE mission_id=$id ORDER BY card_id";
        var cards = new List<ResearchFlightCard>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
        { var card = JsonSerializer.Deserialize<PersonalResearchCard>(reader.GetString(0),WireJson.Options)!; cards.Add(new(card,reader.GetBoolean(1),opened.GetValueOrDefault(card.Id),null)); }
        var selected = cards.Select(c => c with { ActivationBlocked = c.Active ? null : cards.FirstOrDefault(other => other.Active && !CompatibleResearchCards(c.Card,other.Card)) is {} conflict
            ? $"Deactivate {conflict.Card.Name} first: indicated-airspeed or MSL-altitude target ranges do not overlap." : null }).ToArray();
        return new(1,flight,revision,status=="Recording",selected,DateTimeOffset.UtcNow,false,
            "Card controls do not start/stop flight recording or science collection. No card activates automatically.",selected.Length);
    }
    public ResearchFlightState ResearchOperations(Guid flight)
    { using var db = Open(); return ReadResearchOperations(db,null,flight); }

    public RecordedResearchAttempt[] RecordedResearchAttempts(Guid flight, int offset = 0)
    {
        if (offset < 0) throw new ArgumentException("Invalid attempt page.");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM personal_card_attempts WHERE mission_id=$flight ORDER BY started_utc,id LIMIT 20 OFFSET $offset";
        cmd.Parameters.AddWithValue("$flight",flight.ToString()); cmd.Parameters.AddWithValue("$offset",offset);
        var rows = new List<RecordedResearchAttempt>(); using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(JsonSerializer.Deserialize<RecordedResearchAttempt>(r.GetString(0),WireJson.Options)!); return rows.ToArray();
    }
    public RecordedResearchAttempt RecordedResearchAttempt(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload_json FROM personal_card_attempts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id",id.ToString()); return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<RecordedResearchAttempt>(json,WireJson.Options)!
            : throw new ArgumentException("Recorded card attempt not found.");
    }
    private static long ResearchEvent(SqliteConnection db,SqliteTransaction tx,Guid flight,DateTimeOffset at,string kind,string message)
    {
        Event(db,tx,flight,at,"research-card-"+kind,message);
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT last_insert_rowid()";return Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
    }
    private static void SaveRecordedAttempt(SqliteConnection db,SqliteTransaction tx,RecordedResearchAttempt attempt)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO personal_card_attempts VALUES($id,$flight,$card,$at,$status,$json) ON CONFLICT(id) DO UPDATE SET status=$status,payload_json=$json";
        cmd.Parameters.AddWithValue("$id",attempt.Id.ToString());cmd.Parameters.AddWithValue("$flight",attempt.FlightId.ToString());cmd.Parameters.AddWithValue("$card",attempt.CardId.ToString());
        cmd.Parameters.AddWithValue("$at",Stamp(attempt.StartUtc));cmd.Parameters.AddWithValue("$status",attempt.Status.ToString());cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(attempt,WireJson.Options));cmd.ExecuteNonQuery();
    }
    internal ResearchCardReceipt ControlResearchCard(ResearchCardControl request, DateTimeOffset at)
    {
        if(request.Version!=1||request.RequestId==Guid.Empty||request.ExpectedRevision<0||!Enum.IsDefined(request.Action)||request.Action==ResearchCardAction.Unknown)
            throw new ArgumentException("Invalid flight-bound card command.");
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        var hash=PreflightReadiness.Hash(request);
        cmd.CommandText="SELECT input_sha256,result_json FROM collection_receipts WHERE mission_id=$flight AND kind='personal-card' AND receipt_id=$request";
        cmd.Parameters.AddWithValue("$flight",request.FlightId.ToString());cmd.Parameters.AddWithValue("$request",request.RequestId.ToString());
        using(var r=cmd.ExecuteReader())if(r.Read()){
            if(r.GetString(0)!=hash)throw new InvalidOperationException("This card request identity was already used for a different command.");
            return JsonSerializer.Deserialize<ResearchCardReceipt>(r.GetString(1),WireJson.Options)!;
        }
        var state=ReadResearchOperations(db,tx,request.FlightId);
        if(!state.Recording||state.Revision!=request.ExpectedRevision)throw new InvalidOperationException("Flight or test-card state changed. Synchronize before trying again.");
        var selected=state.Cards.SingleOrDefault(c=>c.Card.Id==request.CardId)??throw new ArgumentException("This card was not selected before this flight.");
        if(selected.OpenAttempt?.Id!=request.ExpectedAttemptId)throw new InvalidOperationException("The current card attempt changed. Synchronize before trying again.");
        var attempt=selected.OpenAttempt;var attemptId=attempt?.Id;string message;
        switch(request.Action){
            case ResearchCardAction.Activate:
                if(selected.Active)throw new InvalidOperationException("This card is already active.");
                if(selected.ActivationBlocked is {} conflict)throw new InvalidOperationException(conflict);
                message=$"Activated {selected.Card.Name} · revision {selected.Card.Revision}.";break;
            case ResearchCardAction.Deactivate:
                if(!selected.Active)throw new InvalidOperationException("This card is already inactive.");
                message=$"Deactivated {selected.Card.Name}. Flight and science controls are unchanged.";break;
            case ResearchCardAction.BeginAttempt:
                if(!selected.Active||attempt is not null)throw new InvalidOperationException("Activate this card and end its current attempt before beginning another.");
                attemptId=Guid.NewGuid();message=$"Begin Attempt · {selected.Card.Name} · revision {selected.Card.Revision}.";break;
            case ResearchCardAction.EndAttempt:
                if(attempt is null)throw new InvalidOperationException("This card has no open attempt.");
                message=$"End Attempt · {selected.Card.Name}. Recorded evidence awaits evaluation.";break;
            default:throw new ArgumentException("Unsupported card action.");
        }
        var eventId=ResearchEvent(db,tx,request.FlightId,at,request.Action.ToString(),message);
        if(request.Action==ResearchCardAction.BeginAttempt)SaveRecordedAttempt(db,tx,new(attemptId!.Value,request.FlightId,selected.Card.Id,selected.Card.Revision,at,null,ResearchAttemptStatus.Recording,eventId,null,null));
        else if(attempt is not null && request.Action is ResearchCardAction.EndAttempt or ResearchCardAction.Deactivate)
            SaveRecordedAttempt(db,tx,attempt with{EndUtc=at<attempt.StartUtc?attempt.StartUtc:at,Status=request.Action==ResearchCardAction.EndAttempt?ResearchAttemptStatus.Recorded:ResearchAttemptStatus.Interrupted,EndEventId=eventId,EndReason=request.Action==ResearchCardAction.EndAttempt?"Ended by pilot":"Card deactivated before End Attempt"});
        cmd.CommandText="UPDATE personal_flight_cards SET active=$active WHERE mission_id=$flight AND card_id=$card";
        cmd.Parameters.AddWithValue("$card",request.CardId.ToString());cmd.Parameters.AddWithValue("$active",request.Action==ResearchCardAction.Activate||selected.Active&&request.Action!=ResearchCardAction.Deactivate?1:0);cmd.ExecuteNonQuery();
        cmd.CommandText="UPDATE personal_research_state SET revision=revision+1 WHERE mission_id=$flight";cmd.ExecuteNonQuery();
        var receipt=new ResearchCardReceipt(request.RequestId,request.FlightId,state.Revision+1,eventId,request.CardId,attemptId,at,message);
        cmd.CommandText="INSERT INTO collection_receipts VALUES($flight,'personal-card',$request,$hash,$json)";
        cmd.Parameters.AddWithValue("$hash",hash);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(receipt,WireJson.Options));cmd.ExecuteNonQuery();
        EvidenceFault?.Invoke("research-control-before-commit");tx.Commit();return receipt;
    }
    internal ResearchCardReceipt? ResearchReceipt(ResearchCardControl request)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT input_sha256,result_json FROM collection_receipts WHERE mission_id=$flight AND kind='personal-card' AND receipt_id=$request";
        cmd.Parameters.AddWithValue("$flight",request.FlightId.ToString());cmd.Parameters.AddWithValue("$request",request.RequestId.ToString());
        using var r=cmd.ExecuteReader();if(!r.Read())return null;
        if(r.GetString(0)!=PreflightReadiness.Hash(request))throw new InvalidOperationException("This card request identity was already used for a different command.");
        return JsonSerializer.Deserialize<ResearchCardReceipt>(r.GetString(1),WireJson.Options)!;
    }
    private void CloseResearchOperations(SqliteConnection db,SqliteTransaction tx,Guid flight,DateTimeOffset at)
    {
        var state=ReadResearchOperations(db,tx,flight);
        foreach(var card in state.Cards){
            if(card.OpenAttempt is {} attempt){
                var end=at<attempt.StartUtc?attempt.StartUtc:at;
                var eventId=ResearchEvent(db,tx,flight,end,"Interrupted",$"Interrupted attempt · {card.Card.Name}. Flight ended or recording was recovered; no automatic pass.");
                SaveRecordedAttempt(db,tx,attempt with{EndUtc=end,Status=ResearchAttemptStatus.Interrupted,EndEventId=eventId,EndReason="Flight ended or recording interrupted before End Attempt"});
            }
        }
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="UPDATE personal_flight_cards SET active=0 WHERE mission_id=$flight; UPDATE personal_research_state SET revision=revision+1 WHERE mission_id=$flight";
        cmd.Parameters.AddWithValue("$flight",flight.ToString());cmd.ExecuteNonQuery();
    }
}
