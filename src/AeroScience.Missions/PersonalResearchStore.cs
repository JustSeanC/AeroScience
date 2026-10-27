using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    private void MigratePersonalResearch(SqliteConnection db)
    {
        using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS personal_research_cards(
              id TEXT NOT NULL, revision INTEGER NOT NULL, name TEXT NOT NULL, payload_json TEXT NOT NULL,
              PRIMARY KEY(id,revision));
            CREATE TABLE IF NOT EXISTS personal_research_attempts(
              id TEXT PRIMARY KEY, mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
              card_id TEXT NOT NULL, card_revision INTEGER NOT NULL, created_utc TEXT NOT NULL,
              request_sha256 TEXT NOT NULL,payload_json TEXT NOT NULL,
              FOREIGN KEY(card_id,card_revision) REFERENCES personal_research_cards(id,revision));
            CREATE INDEX IF NOT EXISTS personal_attempts_card ON personal_research_attempts(card_id,created_utc DESC,id);
            CREATE INDEX IF NOT EXISTS personal_attempts_flight ON personal_research_attempts(mission_id);
            PRAGMA user_version=9;
            """;
        cmd.ExecuteNonQuery(); EvidenceFault?.Invoke("personal-research-before-commit"); tx.Commit();
    }

    public PersonalResearchCard SaveResearchCard(SaveResearchCardRequest input)
    {
        if(input.Extensions is {Count:>0})throw new ArgumentException("Unsupported authoring fields cannot be discarded. Keep the original definition and use a compatible editor.");
        if(new object?[]{input.FlightRegime,input.AtmosphericProfile,input.AtmosphericTransect,input.Survey,input.ObservationFlight,input.ExperimentalSensor,input.Procedure}.Count(x=>x is not null)!=1)throw new ArgumentException("Choose exactly one experiment type.");
        if(input.Procedure is not null)FlightRegimeEvaluation.ValidateProcedure(input.Procedure);
        else if(input.ExperimentalSensor is not null)ExperimentalSensorResearch.Validate(input.ExperimentalSensor);
        else if(input.ObservationFlight is not null)ObservationResearch.Validate(input.ObservationFlight);
        else if(input.Survey is not null)SurveyResearch.Validate(input.Survey);
        else if(input.AtmosphericTransect is not null)AtmosphericTransectResearch.Validate(input.AtmosphericTransect);
        else if(input.AtmosphericProfile is not null)AtmosphericResearch.Validate(input.AtmosphericProfile);
        else FlightRegimeEvaluation.Validate(input.FlightRegime);
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Give the test card a name of 1–100 characters.");
        if (input.Id == Guid.Empty || input.Id.HasValue != input.ExpectedRevision.HasValue)
            throw new ArgumentException("Editing requires the card identity and its current revision.");
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        var id = input.Id ?? Guid.NewGuid();
        cmd.CommandText = "SELECT MAX(revision) FROM personal_research_cards WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var revision = cmd.ExecuteScalar() is long value ? checked((int)value) : 0;
        if (input.Id.HasValue && (revision == 0 || input.ExpectedRevision != revision))
            throw new InvalidOperationException("This card changed. Reload it before saving a new revision.");
        var template=input.Procedure is not null?FlightRegimeEvaluation.ProcedureTemplate:input.FlightRegime?.TestPoint is not null?FlightRegimeEvaluation.CustomTemplate:input.ExperimentalSensor is not null?ExperimentalSensorResearch.Template:input.ObservationFlight is not null?ObservationResearch.Template:input.Survey is not null?SurveyResearch.Template:input.AtmosphericTransect is not null?AtmosphericTransectResearch.Template:input.AtmosphericProfile is null?"flight-regime":AtmosphericResearch.Template;
        if(revision>0){var current=ResearchCard(id,revision);SupportedCard(current);if(current.OrganizationAssignment is not null)throw new ArgumentException("Issued organization definitions are immutable. Create an independent personal experiment to change requirements.");if(current.TemplateId!=template)throw new ArgumentException("Keep an experiment's research type when revising it; prepare a separate experiment instead.");}
        var card = new PersonalResearchCard(input.Procedure is not null?8:input.FlightRegime?.TestPoint is not null?7:input.ExperimentalSensor is not null?6:input.ObservationFlight is not null?5:input.Survey is not null?4:input.AtmosphericTransect is not null?3:input.AtmosphericProfile is null?1:2, id, revision + 1, name, template, input.FlightRegime, DateTimeOffset.UtcNow,input.AtmosphericProfile,input.AtmosphericTransect,input.Survey,input.ObservationFlight,input.ExperimentalSensor,input.Procedure);
        InsertResearchCard(db,tx,card);tx.Commit();return card;
    }

    static void InsertResearchCard(SqliteConnection db,SqliteTransaction tx,PersonalResearchCard card)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO personal_research_cards VALUES($id,$revision,$name,$json)";
        cmd.Parameters.AddWithValue("$id",card.Id.ToString());cmd.Parameters.AddWithValue("$revision",card.Revision);cmd.Parameters.AddWithValue("$name",card.Name);
        cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(card,WireJson.Options));cmd.ExecuteNonQuery();
    }

    public PersonalResearchCard ResearchCard(Guid id, int? revision = null)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM personal_research_cards WHERE id=$id AND ($revision IS NULL OR revision=$revision) ORDER BY revision DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<PersonalResearchCard>(json, WireJson.Options)!
            : throw new ArgumentException("The requested personal test-card revision was not found.");
    }

    public PersonalResearchCard[] ResearchCards(int offset = 0)
    {
        if (offset < 0) throw new ArgumentException("Invalid test-card page.");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM personal_research_cards c WHERE revision=(SELECT MAX(revision) FROM personal_research_cards WHERE id=c.id) ORDER BY name,id LIMIT 20 OFFSET $offset";
        cmd.Parameters.AddWithValue("$offset", offset); using var r = cmd.ExecuteReader(); var cards = new List<PersonalResearchCard>();
        while (r.Read()) cards.Add(JsonSerializer.Deserialize<PersonalResearchCard>(r.GetString(0), WireJson.Options)!);
        return cards.ToArray();
    }

    public MissionPlan CreateResearchPlan(Guid cardId, int revision)
    {
        var card = ResearchCard(cardId, revision);SupportedCard(card);
        if(card.OrganizationAssignment is not null)throw new ArgumentException("Use Prepare Mission under My Assignments, or open an already prepared plan. Organization definitions cannot silently create a new authorization.");
        if(card.ExperimentalSensor is not null||AtmosphericResearch.IsCollectionCard(card)||card.Survey is not null||card.ObservationFlight is not null)return PrepareResearchStarter(new(1,Guid.NewGuid(),card.TemplateId,ResearchEntryIntent.FlyNow,new(cardId,revision))).Plan;
        return SavePlan(new(Guid.NewGuid(), card.Name, MissionTemplateKind.ObservationFlight,
            Objective:card.Procedure is not null?"Complete the explicitly marked named points and repetitions; review each point in My Research.":card.FlightRegime?.TestPoint?.Description??"Hold the card's indicated airspeed and MSL altitude within its tolerances.",
            Instruments: [], ResearchCard: new(card.Id, card.Revision)));
    }

    public PersonalResearchAttempt[] ResearchAttempts(Guid? cardId = null, Guid? flightId = null, int offset = 0)
    {
        if (offset < 0) throw new ArgumentException("Invalid results page.");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM personal_research_attempts WHERE ($card IS NULL OR card_id=$card) AND ($flight IS NULL OR mission_id=$flight) ORDER BY created_utc DESC,id LIMIT 20 OFFSET $offset";
        cmd.Parameters.AddWithValue("$card", (object?)cardId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$flight", (object?)flightId?.ToString() ?? DBNull.Value); cmd.Parameters.AddWithValue("$offset", offset);
        using var r = cmd.ExecuteReader(); var items = new List<PersonalResearchAttempt>();
        while (r.Read()) items.Add(JsonSerializer.Deserialize<PersonalResearchAttempt>(r.GetString(0), WireJson.Options)!);
        return items.ToArray();
    }

    public Mission[] ResearchFlights(Guid cardId, int offset = 0)
    {
        if (offset < 0) throw new ArgumentException("Invalid recorded-flight page.");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT m.id FROM missions m JOIN mission_research r ON r.mission_id=m.id
            WHERE m.status <> 'Recording' AND (json_extract(r.plan_snapshot_json,'$.researchCard.id')=$id
              OR EXISTS(SELECT 1 FROM personal_flight_cards f WHERE f.mission_id=m.id AND f.card_id=$id))
            ORDER BY m.started_utc DESC,m.id LIMIT 20 OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$id", cardId.ToString()); cmd.Parameters.AddWithValue("$offset", offset);
        var ids = new List<Guid>(); using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(Guid.Parse(r.GetString(0)));
        return ids.Select(id => Detail(id)!.Mission).ToArray();
    }

    public PersonalResearchAttempt EvaluateResearchAttempt(ResearchAttemptRequest request, CancellationToken token = default,ScienceStore? science=null)
    {
        var marked = request.RecordedAttemptId is {} marker ? RecordedResearchAttempt(marker) : null;
        if (marked is not null && (marked.FlightId != request.FlightId || marked.Status == ResearchAttemptStatus.Recording
            || marked.StartUtc != request.StartUtc || marked.EndUtc != request.EndUtc || marked.CardId != request.CardId))
            throw new InvalidOperationException("The recorded attempt identity, boundaries or flight do not match. Open its retained timeline.");
        if (request.RequestId == Guid.Empty || request.StartUtc.Offset != TimeSpan.Zero || request.EndUtc.Offset != TimeSpan.Zero
            || (marked is null ? request.EndUtc <= request.StartUtc : request.EndUtc < request.StartUtc) || request.EndUtc - request.StartUtc > TimeSpan.FromHours(24))
            throw new ArgumentException("Select a UTC evidence interval of up to 24 hours.");
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        var requestHash = PreflightReadiness.Hash(request);
        cmd.CommandText = "SELECT request_sha256,payload_json FROM personal_research_attempts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", request.RequestId.ToString());
        using (var existing = cmd.ExecuteReader())
            if (existing.Read())
            {
                if (existing.GetString(0) != requestHash) throw new InvalidOperationException("This attempt command was already used for different evidence.");
                var saved=JsonSerializer.Deserialize<PersonalResearchAttempt>(existing.GetString(1), WireJson.Options)!;
                if((saved.AtmosphericProfile is not null||saved.Survey is not null||saved.ExperimentalSensor is not null)&&ScienceDeleted(saved.FlightId))throw new InvalidOperationException("Collected science was permanently deleted; this investigation cannot be recreated from flight context.");
                return saved;
            }
        var mission = Detail(request.FlightId)?.Mission ?? throw new ArgumentException("Recorded flight not found.");
        if (mission.Status == MissionStatus.Recording) throw new InvalidOperationException("End the flight before reviewing its recorded attempt.");
        if ((request.StartUtc < mission.StartedUtc || request.EndUtc > mission.EndedUtc) && marked?.Status != ResearchAttemptStatus.Interrupted)
            throw new ArgumentException("The evidence interval must stay inside this recorded flight.");
        var plan = Research(mission.Id).PlanSnapshot;
        var frozen = ResearchOperations(mission.Id).Cards;
        var reference = plan?.ResearchCard ?? SelectedResearchCards(plan).FirstOrDefault();
        var cardId = request.CardId ?? reference?.Id;
        var card = frozen.FirstOrDefault(c => c.Card.Id == cardId)?.Card
            ?? (reference is not null && reference.Id == cardId ? ResearchCard(reference.Id, reference.Revision)
                : throw new ArgumentException("This flight has no matching frozen personal test card. Select the card before recording."));
        SupportedCard(card);
        cmd.Parameters.AddWithValue("$flight", mission.Id.ToString());
        using var scienceLease=card.ExperimentalSensor is not null||AtmosphericResearch.IsCollectionCard(card)||card.Survey is not null||card.ObservationFlight is not null&&science?.AvailabilityError() is null?science?.BeginAnalysis():null;
        PersonalResearchAttempt attempt;
        if(card.Procedure is not null){
            if(marked is not null){var point=ReadPointEvidence(db,tx,card,marked,token);attempt=new(8,request.RequestId,mission.Id,card,request.StartUtc,request.EndUtc,DateTimeOffset.UtcNow,"flight-procedure-point-1",point.Outcome,point.LongestHoldSeconds,point.HoldStartUtc,point.HoldEndUtc,point.Evidence,"This is one point attempt; whole-card completion requires all required points and repetitions. "+point.Explanation,marked.Id,QuantitativePoint:point);}
            else{var c=CalculateProcedure(card,mission.Id,request.StartUtc,request.EndUtc,mission.Status!=MissionStatus.Completed,token);
            attempt=new(8,request.RequestId,mission.Id,card,request.StartUtc,request.EndUtc,DateTimeOffset.UtcNow,FlightRegimeEvaluation.ProcedureVersion,c.Summary.Outcome,0,null,null,c.Evidence,c.Summary.Explanation,marked?.Id,Procedure:c.Summary);}
        }else if(card.ExperimentalSensor is not null){
            if(ScienceDeleted(mission.Id))throw new InvalidOperationException("Collected science was permanently deleted; modeled inputs cannot be recreated from flight context.");
            if(science is null)throw new InvalidOperationException("Reconnect the Scientific Data Library to evaluate original inputs.");
            var c=ExperimentalSensorResearch.Calculate(science.ResearchObservationWindow(mission.Id,request.StartUtc,request.EndUtc),card,science.LibraryId,PersonalPauseTimes(mission.Id,request.StartUtc,request.EndUtc,true),request,token);
            var outcome=ExperimentalSensorResearch.Outcome(c.Summary,card.ExperimentalSensor,mission.Status!=MissionStatus.Completed||marked?.Status==ResearchAttemptStatus.Interrupted);
            attempt=new(6,request.RequestId,mission.Id,card,request.StartUtc,request.EndUtc,DateTimeOffset.UtcNow,ExperimentalSensorResearch.Evaluator,outcome.Outcome,0,null,null,
                new(0,0,c.Summary.OriginalInputs,c.Hash,PreflightReadiness.Hash(plan!),mission.Source),outcome.Explanation,marked?.Id,ExperimentalSensor:c.Summary);
        }else if(card.ObservationFlight is not null){
            if(ScienceDeleted(mission.Id))throw new InvalidOperationException("Collected pilot research was permanently deleted; flight context cannot recreate it.");
            var c=ObservationResearch.Calculate(this,science,card,request,token);var outcome=ObservationResearch.Outcome(c.Summary,card.ObservationFlight,mission.Status!=MissionStatus.Completed||marked?.Status==ResearchAttemptStatus.Interrupted);
            attempt=new(5,request.RequestId,mission.Id,card,request.StartUtc,request.EndUtc,DateTimeOffset.UtcNow,ObservationResearch.Evaluator,outcome.Outcome,0,null,null,
                new(0,0,c.Summary.Documented,c.Summary.DocumentationSha256,PreflightReadiness.Hash(plan!),mission.Source),outcome.Explanation,marked?.Id,ObservationFlight:c.Summary);
        }else if(card.Survey is not null){
            if(ScienceDeleted(mission.Id))throw new InvalidOperationException("Collected survey science was permanently deleted; raw flight context cannot recreate it.");
            if(science is null)throw new InvalidOperationException("Reconnect the Scientific Data Library to evaluate retained survey evidence.");
            var calculated=SurveyResearch.Calculate(this,science,card,request,PersonalPauseTimes(mission.Id,request.StartUtc,request.EndUtc,true),token);
            var outcome=SurveyResearch.Outcome(calculated.Summary,mission.Status!=MissionStatus.Completed||marked?.Status==ResearchAttemptStatus.Interrupted);
            attempt=new(4,request.RequestId,mission.Id,card,request.StartUtc,request.EndUtc,DateTimeOffset.UtcNow,SurveyResearch.Evaluator,outcome.Outcome,0,null,null,
                new(0,0,calculated.Summary.OriginalObservations+calculated.Summary.Products,calculated.Hash,PreflightReadiness.Hash(plan!),mission.Source),outcome.Explanation,marked?.Id,Survey:calculated.Summary);
        }else if(AtmosphericResearch.IsCollectionCard(card)){
            if(ScienceDeleted(mission.Id))throw new InvalidOperationException("Collected science was permanently deleted; it cannot be recreated from raw flight context.");
            if(science is null)throw new InvalidOperationException("Reconnect the Scientific Data Library to evaluate retained atmospheric observations.");
            var calculated=AtmosphericResearch.Calculate(science.ResearchObservationWindow(mission.Id,request.StartUtc,request.EndUtc),card,science.LibraryId,PersonalPauseTimes(mission.Id,request.StartUtc,request.EndUtc,true),token,request);
            var outcome=AtmosphericResearch.Outcome(calculated.Summary,card,mission.Status!=MissionStatus.Completed||marked?.Status==ResearchAttemptStatus.Interrupted);
            attempt=new(card.Version,request.RequestId,mission.Id,card,request.StartUtc,request.EndUtc,DateTimeOffset.UtcNow,AtmosphericResearch.EvaluatorFor(card),outcome.Outcome,0,null,null,
                new(0,0,calculated.Summary.OriginalObservations,calculated.Hash,PreflightReadiness.Hash(plan!),mission.Source),outcome.Explanation,marked?.Id,calculated.Summary);
        }else{
        if(marked?.Automatic is not null||marked?.ManualSource is not null)_=ReadPointEvidence(db,tx,card,marked,token);
        var evaluation = new FlightRegimeEvaluation.Window(card.FlightRegime!, request.StartUtc, request.EndUtc);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long first = 0, last = 0, count = 0; TelemetrySource? source = null;
        cmd.CommandText = marked?.Automatic is null&&marked?.ManualSource is null?"SELECT sequence,payload_json FROM telemetry WHERE mission_id=$flight AND timestamp_utc >= $start AND timestamp_utc <= $end ORDER BY sequence":"SELECT sequence,payload_json FROM telemetry WHERE mission_id=$flight AND sequence >= $autoFirst AND sequence <= $autoLast ORDER BY sequence";
        if(marked?.Automatic is not null||marked?.ManualSource is not null){cmd.Parameters.AddWithValue("$autoFirst",marked.Automatic?.FirstSequence??marked.ManualSource!.AfterSequence+1);cmd.Parameters.AddWithValue("$autoLast",marked.Automatic?.LastSequence??marked.ManualSource!.ThroughSequence!.Value);}
        cmd.Parameters.AddWithValue("$start", Stamp(request.StartUtc)); cmd.Parameters.AddWithValue("$end", Stamp(request.EndUtc));
        // Pause edges are retained by the existing event recorder, not copied telemetry.
        using var pauses = PersonalPauseTimes(mission.Id, request.StartUtc, request.EndUtc).GetEnumerator();
        var hasPause = pauses.MoveNext();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var sequence = reader.GetInt64(0); var json = reader.GetString(1);
                var sample = JsonSerializer.Deserialize<TelemetryFrame>(json, WireJson.Options)!;
                while (hasPause && pauses.Current <= sample.TimestampUtc)
                { token.ThrowIfCancellationRequested(); evaluation.Break(); AppendResearchBreak(hash,pauses.Current); hasPause = pauses.MoveNext(); }
                evaluation.Add(sample); source ??= sample.Source;
                if (count++ == 0) first = sequence; last = sequence;
                AppendResearchSample(hash,sequence,json);
            }
        while (hasPause)
        {
            token.ThrowIfCancellationRequested(); evaluation.Break();
            AppendResearchBreak(hash,pauses.Current); hasPause = pauses.MoveNext();
        }
        var result = evaluation.Finish(marked?.Automatic is not null?marked.Status!=ResearchAttemptStatus.Recorded:mission.Status != MissionStatus.Completed);
        if (marked?.Status == ResearchAttemptStatus.Interrupted)
            result = (ResearchAttemptOutcome.InsufficientEvidence,"Attempt interrupted: " + marked.EndReason + ". Retained samples remain available; no automatic success was inferred.");
        attempt = new PersonalResearchAttempt(card.Version, request.RequestId, mission.Id, card, request.StartUtc, request.EndUtc,
            DateTimeOffset.UtcNow, FlightRegimeEvaluation.VersionFor(card.FlightRegime!), result.Outcome, evaluation.LongestHold,
            evaluation.HoldStart, evaluation.HoldEnd,
            new(first, last, count, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), PreflightReadiness.Hash(plan!), source), result.Explanation, marked?.Id);
        }
        attempt=attempt with{Automatic=marked?.Automatic};
        tx.Commit(); // Release the read snapshot before acquiring a short publication write lock.
        using var publication = db.BeginTransaction(); cmd.Transaction = publication;
        cmd.CommandText = "SELECT request_sha256,payload_json FROM personal_research_attempts WHERE id=$id";
        using (var existing = cmd.ExecuteReader())
            if (existing.Read())
            {
                if (existing.GetString(0) != requestHash) throw new InvalidOperationException("This attempt command was already used for different evidence.");
                return JsonSerializer.Deserialize<PersonalResearchAttempt>(existing.GetString(1), WireJson.Options)!;
            }
        cmd.CommandText = "INSERT INTO personal_research_attempts VALUES($id,$flight,$card,$revision,$at,$request,$json)";
        cmd.Parameters.AddWithValue("$card", card.Id.ToString()); cmd.Parameters.AddWithValue("$revision", card.Revision);
        cmd.Parameters.AddWithValue("$at", Stamp(attempt.EvaluatedUtc)); cmd.Parameters.AddWithValue("$request", requestHash);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(attempt, WireJson.Options)); cmd.ExecuteNonQuery(); publication.Commit(); return attempt;
    }

    public PersonalResearchAttempt EvaluateRecordedResearchAttempt(Guid id, Guid requestId, CancellationToken token=default,ScienceStore? science=null)
    {
        var attempt=RecordedResearchAttempt(id);
        if(attempt.EndUtc is not {} end)throw new InvalidOperationException("End this attempt before evaluating its retained evidence.");
        return EvaluateResearchAttempt(new(requestId,attempt.FlightId,attempt.StartUtc,end,attempt.Id,attempt.CardId),token,science);
    }

    private IEnumerable<DateTimeOffset> PersonalPauseTimes(Guid flight, DateTimeOffset start, DateTimeOffset end,bool includeScience=false)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT timestamp_utc FROM events WHERE mission_id=$id AND kind IN ('simulator-paused','telemetry-gap','connection') AND timestamp_utc >= $start AND timestamp_utc <= $end ORDER BY timestamp_utc,id";
        if(includeScience)cmd.CommandText=cmd.CommandText.Replace("'connection')","'connection','science-paused','science-stopped','science-session-started','science-session-ended','science-armed')",StringComparison.Ordinal);
        cmd.Parameters.AddWithValue("$id", flight.ToString()); cmd.Parameters.AddWithValue("$start", Stamp(start)); cmd.Parameters.AddWithValue("$end", Stamp(end));
        using var r = cmd.ExecuteReader(); while (r.Read()) yield return Parse(r.GetString(0));
    }
}
