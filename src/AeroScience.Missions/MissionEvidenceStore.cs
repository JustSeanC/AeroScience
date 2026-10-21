using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public enum CollectionReceiptKind { Request, Occurrence, Engine }
public sealed record CollectionInputReceipt(string Id, TelemetryUpdate Packet);
public sealed record ScienceAcquisitionIntent(Guid Id, Guid MissionId, Guid SessionId, string InstrumentId,
    long Slot, Guid ConfigurationId, long CollectionEpoch, DateTimeOffset ExposureUtc, string InputReceiptId);
public sealed record StoredScienceIntent(ScienceAcquisitionIntent Intent, string State, Guid? ProductId, string? ProductSha256);
public sealed record CollectionEvidenceCommit(CollectionState State, CollectionEvent Event,
    ScienceCollectionSession? Session = null, CollectionControlRequest? Request = null,
    CollectionInputReceipt[]? Inputs = null, ScienceAcquisitionIntent[]? Intents = null,
    TelemetryFrame? FlightSample = null, Guid? EngineDecisionId = null);
public sealed record CollectionCommitResult(bool Applied, CollectionState ResultState, CollectionEvent Event,
    ScienceCollectionSession? Session, CollectionState CurrentState);

public sealed partial class MissionStore
{
    public CollectionCommitResult? CollectionControlReceipt(CollectionControlRequest request)
    {
        EvidenceContracts.Validate(request);
        using var db=Open();using var tx=db.BeginTransaction();
        var receipt=EvidenceReceipt(db,tx,request.MissionId,CollectionReceiptKind.Request,request.RequestId);
        if(receipt is null)return null;
        EvidenceRequire(receipt.Value.Hash==EvidenceContracts.ContentHash(request),"Request ID reused for another command.");
        var result=JsonSerializer.Deserialize<CollectionCommitResult>(receipt.Value.Json,WireJson.Options)!;
        return result with{Applied=false,CurrentState=EvidenceState(db,tx,request.MissionId)!};
    }
    public StoredScienceIntent? ScienceIntent(Guid id)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json,state,product_id,product_sha256 FROM science_intents WHERE id=$id";
        cmd.Parameters.AddWithValue("$id",id.ToString());using var row=cmd.ExecuteReader();
        return row.Read()?new(JsonSerializer.Deserialize<ScienceAcquisitionIntent>(row.GetString(0),WireJson.Options)!,row.GetString(1),row.IsDBNull(2)?null:Guid.Parse(row.GetString(2)),row.IsDBNull(3)?null:row.GetString(3)):null;
    }
    public CollectionState? CollectionCheckpoint(Guid missionId)
    {
        using var db = Open(); return EvidenceState(db, null, missionId);
    }

    public string RecordedPlanHash(Guid missionId)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT plan_snapshot_json FROM mission_research WHERE mission_id=$id";
        cmd.Parameters.AddWithValue("$id", missionId.ToString());
        return EvidenceDigest(cmd.ExecuteScalar() as string ?? "null");
    }

    // Called explicitly at a new flight's start. Reading an old flight never invents these records.
    public CollectionState InitializeCollectionEvidence(CollectionState initial, CollectionPlan? plan = null,
        MissionAssessmentContext? assessment = null)
    {
        initial = EvidenceContracts.Snapshot(initial);
        if (plan is not null) plan = EvidenceContracts.Snapshot(plan);
        if (assessment is not null) assessment = EvidenceContracts.Snapshot(assessment);
        EvidenceContracts.Validate(initial);
        EvidenceRequire(initial.Revision == 0 && initial.CurrentSessionId is null && initial.LastSessionId is null
            && initial.Phase == CollectionPhase.Armed, "Initialize an armed controller without fabricated sessions.");
        if (plan is not null) EvidenceContracts.Validate(plan);
        if (assessment is not null) EvidenceContracts.Validate(assessment);
        using var db = Open(); using var tx = db.BeginTransaction();
        var started = EvidenceRecording(db, tx, initial.MissionId);
        if (EvidenceState(db, tx, initial.MissionId) is { } existing)
        {
            EvidenceMatchSnapshot(db, tx, initial.MissionId, "collection-plan", plan);
            EvidenceMatchSnapshot(db, tx, initial.MissionId, "assessment-context", assessment);
            return existing;
        }
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM telemetry WHERE mission_id=$id"; cmd.Parameters.AddWithValue("$id", initial.MissionId.ToString());
        EvidenceRequire(Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0,
            "Freeze collection and grading context before the first recorded sample; legacy/partial flights remain unsegmented.");
        if (plan is not null)
        {
            cmd.CommandText = "SELECT plan_snapshot_json FROM mission_research WHERE mission_id=$id";
            var archived = cmd.ExecuteScalar() as string;
            EvidenceRequire(archived is not null, "A collection plan needs the recorded mission-plan snapshot.");
            EvidenceContracts.ValidatePlan(plan, JsonSerializer.Deserialize<MissionPlan>(archived!, WireJson.Options)!);
        }
        if (assessment is not null)
        {
            EvidenceRequire(assessment.MissionId == initial.MissionId && assessment.FrozenUtc <= started,
                "Freeze assessment evidence at flight start, not retroactively.");
            cmd.CommandText = "SELECT mode FROM missions WHERE id=$id";
            EvidenceRequire(cmd.ExecuteScalar() as string == assessment.Mode.ToString(), "Assessment mode differs from the flight.");
            cmd.CommandText = "SELECT plan_snapshot_json FROM mission_research WHERE mission_id=$id";
            EvidenceRequire(assessment.PlanSha256.Equals(EvidenceDigest(cmd.ExecuteScalar() as string ?? "null"), StringComparison.OrdinalIgnoreCase),
                "Frozen obligations reference a different plan snapshot.");
        }
        EvidenceWriteSnapshot(db, tx, initial.MissionId, "collection-plan", plan);
        EvidenceWriteSnapshot(db, tx, initial.MissionId, "assessment-context", assessment);
        cmd.CommandText = "INSERT INTO collection_states VALUES($id,0,$json)";
        cmd.Parameters.AddWithValue("$json", EvidenceContracts.Write(initial)); cmd.ExecuteNonQuery();
        EvidenceFault?.Invoke("initialize-before-commit"); tx.Commit(); return initial;
    }

    public T? CollectionSnapshot<T>(Guid missionId) where T : class, IVersionedEvidence
    {
        var kind = typeof(T) == typeof(CollectionPlan) ? "collection-plan" : typeof(T) == typeof(MissionAssessmentContext)
            ? "assessment-context" : throw new ArgumentException("Unknown frozen collection snapshot.");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload_json,sha256 FROM collection_snapshots WHERE mission_id=$id AND kind=$kind";
        cmd.Parameters.AddWithValue("$id", missionId.ToString()); cmd.Parameters.AddWithValue("$kind", kind);
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null;
        var value = EvidenceContracts.Read<T>(reader.GetString(0));
        EvidenceRequire(EvidenceContracts.ContentHash(value) == reader.GetString(1), "Frozen snapshot hash verification failed."); return value;
    }

    public ScienceCollectionSession[] CollectionSessions(Guid missionId, int limit = 100, int offset = 0)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM science_sessions WHERE mission_id=$id ORDER BY sequence LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$id", missionId.ToString()); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500)); cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        using var reader = cmd.ExecuteReader(); var results = new List<ScienceCollectionSession>();
        while (reader.Read()) results.Add(EvidenceContracts.Read<ScienceCollectionSession>(reader.GetString(0))); return results.ToArray();
    }

    public CollectionEvent[] CollectionEvents(Guid missionId, long after = 0, int limit = 100)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM collection_events WHERE mission_id=$id AND sequence>$after ORDER BY sequence LIMIT $limit";
        cmd.Parameters.AddWithValue("$id", missionId.ToString()); cmd.Parameters.AddWithValue("$after", Math.Max(0, after)); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = cmd.ExecuteReader(); var results = new List<CollectionEvent>();
        while (reader.Read()) results.Add(EvidenceContracts.Read<CollectionEvent>(reader.GetString(0))); return results.ToArray();
    }

    public static EvidenceLink DescribeCollectionInput(CollectionInputReceipt input)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(input.Packet);
        if (string.IsNullOrWhiteSpace(input.Id) || input.Id.Length > 200 || input.Id != input.Id.Trim() || input.Id.Any(char.IsControl))
            throw new ArgumentException("Name the exact bounded input receipt without control characters.");
        var packet = input.Packet;
        EvidenceRequire(Enum.IsDefined(packet.Connection.Source), "Unknown input source.");
        if (packet.Frame is { } frame) EvidenceRequire(frame.IsValid && frame.Source == packet.Connection.Source, "Invalid or inconsistently labeled flight input.");
        var at = packet.Frame?.TimestampUtc ?? packet.Atmosphere?.TimestampUtc ?? packet.Environment?.TimestampUtc ?? packet.Guidance?.TimestampUtc ?? packet.Connection.ChangedUtc;
        EvidenceRequire(at != default && at.Offset == TimeSpan.Zero, "Retain the original UTC input time.");
        var json = JsonSerializer.Serialize(packet, WireJson.Options);
        EvidenceRequire(Encoding.UTF8.GetByteCount(json) <= EvidenceContracts.MaximumJsonBytes, "Input receipt is too large.");
        return new(input.Id, EvidenceDigest(json), at, packet.Connection.Source == TelemetrySource.Simulated ? EvidenceOrigin.SyntheticFixture : EvidenceOrigin.SimulatorReported);
    }

    public CollectionInputReceipt? CollectionInput(Guid missionId, string receiptId)
    {
        using var db = Open(); return EvidenceInput(db, null, missionId, receiptId);
    }

    // P05 owns decisions. This method atomically commits their evidence, expected revision and outbox.
    public CollectionCommitResult CommitCollectionEvidence(CollectionEvidenceCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        EvidenceContracts.Validate(commit.State); EvidenceContracts.Validate(commit.Event);
        if (commit.Session is not null) EvidenceContracts.Validate(commit.Session);
        if (commit.Request is not null) EvidenceContracts.Validate(commit.Request);
        EvidenceRequire((commit.Inputs?.Length ?? 0) <= 64 && (commit.Intents?.Length ?? 0) <= 64,
            "A commit has bounded input/intent work.");
        // Detach arrays/nested packet payloads before validation and the transaction. Caller
        // changes must not alter frozen evidence between validation, hashing and persistence.
        var batch = JsonSerializer.Serialize(commit, WireJson.Options);
        EvidenceRequire(Encoding.UTF8.GetByteCount(batch) <= 8 * EvidenceContracts.MaximumJsonBytes, "Collection evidence batch exceeds the bounded transaction budget.");
        commit = JsonSerializer.Deserialize<CollectionEvidenceCommit>(batch, WireJson.Options)!;
        EvidenceContracts.Validate(commit.State); EvidenceContracts.Validate(commit.Event);
        if (commit.Session is not null) EvidenceContracts.Validate(commit.Session);
        if (commit.Request is not null) EvidenceContracts.Validate(commit.Request);
        var mission = commit.State.MissionId; var transition = commit.Event;
        EvidenceRequire(transition.MissionId == mission && (commit.Session is null || commit.Session.MissionId == mission), "Evidence belongs to another flight.");
        var identity = EvidenceReceiptIdentity(commit);
        var inputs = commit.Inputs ?? []; var intents = commit.Intents ?? [];
        EvidenceRequire(inputs.Length <= 64 && intents.Length <= 64 && inputs.All(i => i is not null) && intents.All(i => i is not null), "A commit has bounded input/intent work.");
        EvidenceRequire(inputs.Select(i => i.Id).Distinct().Count() == inputs.Length, "Duplicate input IDs in one commit.");
        foreach (var input in inputs) _ = DescribeCollectionInput(input);
        using var db = Open(); using var tx = db.BeginTransaction();
        // Retry lookup MUST precede current-state validation: an old successful End cannot close a new session.
        var current = EvidenceState(db, tx, mission) ?? throw new InvalidOperationException("Collection evidence has not been initialized.");
        if (EvidenceReceipt(db, tx, mission, identity.Kind, identity.Id) is { } receipt)
        {
            EvidenceRequire(receipt.Hash == identity.Hash, "Receipt identity was reused for a different command/occurrence.");
            var original = JsonSerializer.Deserialize<CollectionCommitResult>(receipt.Json, WireJson.Options)!;
            EvidenceContracts.Validate(original.ResultState); EvidenceContracts.Validate(original.Event);
            if (original.Session is not null) EvidenceContracts.Validate(original.Session);
            return original with { Applied = false, CurrentState = current };
        }
        _ = EvidenceRecording(db, tx, mission);
        if (commit.Request is { } request)
        {
            if (transition.Outcome == CollectionEventOutcome.Conflict)
            {
                EvidenceRequire(request.ExpectedRevision != current.Revision || request.ExpectedSessionId != current.CurrentSessionId,
                    "A stale-control conflict needs the actual mismatched request.");
                EvidenceRequire(EvidenceContracts.Write(commit.State with { Revision = current.Revision }) == EvidenceContracts.Write(current)
                    && commit.Session is null && intents.Length == 0 && commit.FlightSample is null,
                    "A conflicted command may append an audit/receipt but cannot change collection authority or data.");
            }
            else EvidenceContracts.ValidateControl(request, current);
        }
        EvidenceRequire(commit.State.Revision == current.Revision + 1 && transition.Sequence == commit.State.Revision
            && transition.From == current.Phase && transition.To == commit.State.Phase && transition.Authority == commit.State.Authority,
            "Collection checkpoint and event ordering conflict.");
        EvidenceRequire(transition.Outcome == CollectionEventOutcome.Conflict || transition.ExpectedSessionId == current.CurrentSessionId,
            "An old session decision cannot target the current session.");
        EvidenceRequire((current.FiredOccurrenceIds ?? []).All(id => (commit.State.FiredOccurrenceIds ?? []).Contains(id))
            && (current.SuppressedOccurrenceIds ?? []).All(id => (commit.State.SuppressedOccurrenceIds ?? []).Contains(id)), "A new session cannot erase flight-wide occurrence receipts.");
        if (transition.OccurrenceId is { } occurrence)
        {
            EvidenceRequire(!(current.FiredOccurrenceIds ?? []).Contains(occurrence) && !(current.SuppressedOccurrenceIds ?? []).Contains(occurrence), "This flight-wide occurrence is already consumed.");
            var keys = transition.Outcome == CollectionEventOutcome.Applied ? commit.State.FiredOccurrenceIds : commit.State.SuppressedOccurrenceIds;
            EvidenceRequire((keys ?? []).Contains(occurrence), "Persist the occurrence key with its decision.");
        }
        foreach (var input in inputs) EvidenceWriteInput(db, tx, mission, input);
        foreach (var link in transition.Evidence)
        {
            var packet = EvidenceInput(db, tx, mission, link.Id) ?? throw new ArgumentException("Event input evidence is missing.");
            EvidenceRequire(DescribeCollectionInput(packet) == link, "Event evidence does not match its exact retained packet.");
        }
        if (transition.Origin is CollectionEventOrigin.RouteTrigger or CollectionEventOrigin.Boundary)
            EvidenceRequire(transition.Evidence.Length > 0 && transition.Evidence.Any(l => l.ObservedUtc == transition.InputUtc
                && EvidenceInput(db, tx, mission, l.Id)?.Packet.Frame is not null), "Geographic decisions need actual retained position evidence.");
        if (commit.FlightSample is { } sample)
        {
            EvidenceRequire(inputs.Any(i => i.Packet.Frame is { } frame && JsonSerializer.Serialize(frame, WireJson.Options) == JsonSerializer.Serialize(sample, WireJson.Options)), "Flight sample must be one of the exact input packets.");
            Append(db, tx, mission, sample);
        }
        EvidenceWriteSession(db, tx, commit, current);
        EvidenceFault?.Invoke("after-session");
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO collection_events VALUES($mission,$sequence,$session,$json)";
        cmd.Parameters.AddWithValue("$mission", mission.ToString()); cmd.Parameters.AddWithValue("$sequence", transition.Sequence);
        cmd.Parameters.AddWithValue("$session", (object?)transition.SessionId?.ToString() ?? DBNull.Value); cmd.Parameters.AddWithValue("$json", EvidenceContracts.Write(transition)); cmd.ExecuteNonQuery();
        EvidenceFault?.Invoke("after-event");
        EvidenceUpdateState(db, tx, commit.State, current.Revision);
        foreach (var intent in intents) EvidenceWriteIntent(db, tx, commit, intent);
        EvidenceFault?.Invoke("after-checkpoint");
        var result = new CollectionCommitResult(true, commit.State, transition, commit.Session, commit.State);
        cmd.CommandText = "INSERT INTO collection_receipts VALUES($mission,$kind,$id,$hash,$result)";
        cmd.Parameters.AddWithValue("$kind", identity.Kind.ToString()); cmd.Parameters.AddWithValue("$id", identity.Id.ToString()); cmd.Parameters.AddWithValue("$hash", identity.Hash);
        cmd.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result, WireJson.Options)); cmd.ExecuteNonQuery();
        if(transition.Outcome==CollectionEventOutcome.Applied)
        {
            if(current.Phase==CollectionPhase.Collecting&&commit.State.Phase==CollectionPhase.Paused)
                Event(db,tx,mission,transition.InputUtc,"science-paused",transition.Reason);
            if(current.Phase==CollectionPhase.Paused&&commit.State.Phase==CollectionPhase.Collecting)
                Event(db,tx,mission,transition.InputUtc,"science-resumed",transition.Reason);
            if(commit.State.Phase==CollectionPhase.BetweenSessions&&current.CurrentSessionId is not null)
                Event(db,tx,mission,transition.InputUtc,"science-session-ended",transition.Reason);
            if(transition.Action==CollectionAction.StartData)
                Event(db,tx,mission,transition.InputUtc,"science-session-started",transition.Reason);
        }
        EvidenceFault?.Invoke("before-commit"); tx.Commit(); EvidenceFault?.Invoke("after-commit"); return result;
    }

    public StoredScienceIntent[] ScienceIntents(Guid missionId, bool pendingOnly = false, int limit = 100, int offset = 0)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload_json,state,product_id,product_sha256 FROM science_intents WHERE mission_id=$mission AND ($pending=0 OR state='Pending') ORDER BY rowid LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$mission", missionId.ToString()); cmd.Parameters.AddWithValue("$pending", pendingOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500)); cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        using var reader = cmd.ExecuteReader(); var rows = new List<StoredScienceIntent>();
        while (reader.Read()) rows.Add(new(JsonSerializer.Deserialize<ScienceAcquisitionIntent>(reader.GetString(0), WireJson.Options)!, reader.GetString(1),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3))); return rows.ToArray();
    }

    public void AcknowledgeScienceIntent(Guid intentId, Guid sessionId, Guid productId, string productSha256)
    {
        EvidenceRequire(intentId != Guid.Empty && sessionId != Guid.Empty && productId != Guid.Empty && productSha256 is { Length: 64 } && productSha256.All(Uri.IsHexDigit), "Identify the verified original session/product.");
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT i.state,i.product_id,i.product_sha256,s.status FROM science_intents i JOIN science_sessions s ON s.id=i.session_id WHERE i.id=$id AND i.session_id=$session";
        cmd.Parameters.AddWithValue("$id", intentId.ToString()); cmd.Parameters.AddWithValue("$session", sessionId.ToString());
        using (var reader = cmd.ExecuteReader())
        {
            EvidenceRequire(reader.Read(), "Intent belongs to another session or is missing.");
            if (reader.GetString(0) == "Published")
            {
                EvidenceRequire(reader.GetString(1) == productId.ToString() && reader.GetString(2) == productSha256.ToLowerInvariant(), "A published intent cannot be assigned another product."); return;
            }
            EvidenceRequire(reader.GetString(0) == "Pending" && reader.GetString(3) is "Collecting" or "Paused", "Closed/cancelled session work cannot become a new live product.");
        }
        cmd.CommandText = "UPDATE science_intents SET state='Published',product_id=$product,product_sha256=$hash WHERE id=$id AND state='Pending'";
        cmd.Parameters.AddWithValue("$product", productId.ToString()); cmd.Parameters.AddWithValue("$hash", productSha256.ToLowerInvariant());
        EvidenceRequire(cmd.ExecuteNonQuery() == 1, "Intent acknowledgement conflicted."); tx.Commit();
    }

    private static void EvidenceWriteSession(SqliteConnection db, SqliteTransaction tx, CollectionEvidenceCommit commit, CollectionState current)
    {
        var next = commit.State; var session = commit.Session;
        EvidenceRequire(current.CurrentSessionId is null || next.CurrentSessionId is null || current.CurrentSessionId == next.CurrentSessionId,
            "Close the old session before creating a new one.");
        if (session is null)
        {
            EvidenceRequire(current.CurrentSessionId == next.CurrentSessionId && current.LastSessionId == next.LastSessionId && current.Phase == next.Phase,
                "A session phase/identity change needs its persisted session record."); return;
        }
        EvidenceRequire(commit.Event.Outcome == CollectionEventOutcome.Applied, "A suppressed/conflicting event cannot modify scientific session data.");
        EvidenceRequire(commit.Event.SessionId == session.Id, "Session update and collection event disagree.");
        var original = EvidenceSession(db, tx, next.MissionId, session.Id);
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.Parameters.AddWithValue("$mission", next.MissionId.ToString());
        if (original is null)
        {
            cmd.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM science_sessions WHERE mission_id=$mission";
            EvidenceRequire(session.Sequence == Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)
                && current.CurrentSessionId is null && next.CurrentSessionId == session.Id && session.Status == CollectionSessionStatus.Collecting
                && commit.Event.Action == CollectionAction.StartData && next.LastSessionId == current.LastSessionId,
                "A Start creates exactly one new independently identified session.");
            cmd.CommandText = "INSERT INTO science_sessions VALUES($id,$mission,$sequence,$status,$json)";
        }
        else
        {
            EvidenceRequire(current.CurrentSessionId == session.Id && original.Status is CollectionSessionStatus.Collecting or CollectionSessionStatus.Paused,
                "Closed session records are immutable.");
            EvidenceRequire(original.MissionId == session.MissionId && original.Sequence == session.Sequence && original.StartedUtc == session.StartedUtc
                && original.PlannedSessionKey == session.PlannedSessionKey, "Session identity/start/provenance cannot be rewritten.");
            EvidenceRequire(session.Configurations.Length >= original.Configurations.Length && original.Configurations.Select((c, i) => JsonSerializer.Serialize(c, WireJson.Options) == JsonSerializer.Serialize(session.Configurations[i], WireJson.Options)).All(v => v),
                "Effective configuration history is append-only.");
            EvidenceRequire(session.Evidence.Length >= original.Evidence.Length && original.Evidence.Select((e, i) => e == session.Evidence[i]).All(v => v), "Session provenance history is append-only.");
            cmd.CommandText = "UPDATE science_sessions SET status=$status,payload_json=$json WHERE id=$id AND mission_id=$mission";
        }
        if (session.Status is CollectionSessionStatus.Ended or CollectionSessionStatus.Interrupted)
            EvidenceRequire(next.CurrentSessionId is null && next.LastSessionId == session.Id, "Closing a session leaves no open session and retains its identity.");
        else EvidenceRequire(next.CurrentSessionId == session.Id && ((session.Status == CollectionSessionStatus.Collecting && next.Phase == CollectionPhase.Collecting)
            || (session.Status == CollectionSessionStatus.Paused && next.Phase == CollectionPhase.Paused)), "Session status and controller phase differ.");
        cmd.Parameters.AddWithValue("$id", session.Id.ToString()); cmd.Parameters.AddWithValue("$sequence", session.Sequence);
        cmd.Parameters.AddWithValue("$status", session.Status.ToString()); cmd.Parameters.AddWithValue("$json", EvidenceContracts.Write(session)); cmd.ExecuteNonQuery();
        if (session.Status is CollectionSessionStatus.Ended or CollectionSessionStatus.Interrupted)
        {
            cmd.CommandText = "UPDATE science_intents SET state='Cancelled' WHERE session_id=$id AND state='Pending'"; cmd.ExecuteNonQuery();
        }
    }

    private static void EvidenceWriteIntent(SqliteConnection db, SqliteTransaction tx, CollectionEvidenceCommit commit, ScienceAcquisitionIntent intent)
    {
        var session = intent.SessionId == commit.Session?.Id ? commit.Session : EvidenceSession(db, tx, commit.State.MissionId, intent.SessionId);
        EvidenceRequire(commit.Event.Outcome == CollectionEventOutcome.Applied && intent.Id != Guid.Empty && intent.MissionId == commit.State.MissionId && commit.State.CurrentSessionId == intent.SessionId
            && commit.State.Phase == CollectionPhase.Collecting && session is { Status: CollectionSessionStatus.Collecting }, "Only the current collecting session may create new acquisition intents.");
        EvidenceRequire(intent.Slot >= 0 && intent.CollectionEpoch >= 0 && intent.ExposureUtc != default && intent.ExposureUtc.Offset == TimeSpan.Zero,
            "Invalid acquisition slot/epoch/time.");
        var effective = session!.Configurations.Where(c => c.Settings.InstrumentId == intent.InstrumentId && c.EffectiveUtc <= intent.ExposureUtc)
            .OrderByDescending(c => c.Revision).FirstOrDefault();
        EvidenceRequire(effective is not null && effective.Id == intent.ConfigurationId && effective.Settings.Enabled,
            "Acquisition must use the enabled configuration actually effective at its exposure time.");
        var input = EvidenceInput(db, tx, intent.MissionId, intent.InputReceiptId) ?? throw new ArgumentException("Intent exposure evidence is missing.");
        EvidenceRequire(DescribeCollectionInput(input).ObservedUtc == intent.ExposureUtc && intent.ExposureUtc >= session.StartedUtc, "Intent must retain its actual session exposure evidence.");
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO science_intents(id,mission_id,session_id,instrument_id,slot,configuration_id,epoch,state,payload_json) VALUES($id,$mission,$session,$instrument,$slot,$config,$epoch,'Pending',$json)";
        cmd.Parameters.AddWithValue("$id", intent.Id.ToString()); cmd.Parameters.AddWithValue("$mission", intent.MissionId.ToString()); cmd.Parameters.AddWithValue("$session", intent.SessionId.ToString());
        cmd.Parameters.AddWithValue("$instrument", intent.InstrumentId); cmd.Parameters.AddWithValue("$slot", intent.Slot); cmd.Parameters.AddWithValue("$config", intent.ConfigurationId.ToString());
        cmd.Parameters.AddWithValue("$epoch", intent.CollectionEpoch); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(intent, WireJson.Options)); cmd.ExecuteNonQuery();
    }

    private static void EvidenceWriteInput(SqliteConnection db, SqliteTransaction tx, Guid mission, CollectionInputReceipt input)
    {
        var link = DescribeCollectionInput(input); var json = JsonSerializer.Serialize(input.Packet, WireJson.Options);
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT sha256 FROM collection_inputs WHERE mission_id=$mission AND receipt_id=$id";
        cmd.Parameters.AddWithValue("$mission", mission.ToString()); cmd.Parameters.AddWithValue("$id", input.Id);
        if (cmd.ExecuteScalar() is string previous) { EvidenceRequire(previous == link.Sha256, "Input receipt content is immutable."); return; }
        cmd.CommandText = "INSERT INTO collection_inputs VALUES($mission,$id,$hash,$json)";
        cmd.Parameters.AddWithValue("$hash", link.Sha256); cmd.Parameters.AddWithValue("$json", json); cmd.ExecuteNonQuery();
    }

    private static CollectionInputReceipt? EvidenceInput(SqliteConnection db, SqliteTransaction? tx, Guid mission, string id)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT payload_json,sha256 FROM collection_inputs WHERE mission_id=$mission AND receipt_id=$id";
        cmd.Parameters.AddWithValue("$mission", mission.ToString()); cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null;
        var json = reader.GetString(0); EvidenceRequire(EvidenceDigest(json) == reader.GetString(1), "Retained input hash verification failed.");
        return new(id, JsonSerializer.Deserialize<TelemetryUpdate>(json, WireJson.Options)!);
    }

    private static CollectionState? EvidenceState(SqliteConnection db, SqliteTransaction? tx, Guid mission)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT payload_json,revision FROM collection_states WHERE mission_id=$id";
        cmd.Parameters.AddWithValue("$id", mission.ToString()); using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null;
        var state = EvidenceContracts.Read<CollectionState>(reader.GetString(0));
        EvidenceRequire(state.MissionId == mission && state.Revision == reader.GetInt64(1), "Collection checkpoint identity/revision verification failed."); return state;
    }
    private static ScienceCollectionSession? EvidenceSession(SqliteConnection db, SqliteTransaction tx, Guid mission, Guid id)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT payload_json FROM science_sessions WHERE mission_id=$mission AND id=$id";
        cmd.Parameters.AddWithValue("$mission", mission.ToString()); cmd.Parameters.AddWithValue("$id", id.ToString());
        return cmd.ExecuteScalar() is string json ? EvidenceContracts.Read<ScienceCollectionSession>(json) : null;
    }
    private static (string Hash, string Json)? EvidenceReceipt(SqliteConnection db, SqliteTransaction tx, Guid mission, CollectionReceiptKind kind, Guid id)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT input_sha256,result_json FROM collection_receipts WHERE mission_id=$mission AND kind=$kind AND receipt_id=$id";
        cmd.Parameters.AddWithValue("$mission", mission.ToString()); cmd.Parameters.AddWithValue("$kind", kind.ToString()); cmd.Parameters.AddWithValue("$id", id.ToString());
        using var reader = cmd.ExecuteReader(); return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }
    private static (CollectionReceiptKind Kind, Guid Id, string Hash) EvidenceReceiptIdentity(CollectionEvidenceCommit commit)
    {
        var e = commit.Event;
        if (commit.Request is { } request)
        {
            EvidenceRequire(request.MissionId == commit.State.MissionId && e.RequestId == request.RequestId && e.Action == request.Action && e.ExpectedSessionId == request.ExpectedSessionId
                && e.OccurrenceId is null && commit.EngineDecisionId is null && e.Origin == CollectionEventOrigin.Manual, "Manual request and event receipt differ.");
            return (CollectionReceiptKind.Request, request.RequestId, EvidenceContracts.ContentHash(request));
        }
        if (e.OccurrenceId is { } occurrence)
        {
            EvidenceRequire(e.Origin is CollectionEventOrigin.RouteTrigger or CollectionEventOrigin.Boundary && commit.EngineDecisionId is null, "Invalid automatic receipt.");
            return (CollectionReceiptKind.Occurrence, occurrence, EvidenceDigest(JsonSerializer.Serialize(new { e.MissionId, e.OccurrenceId, e.Action, e.ExpectedSessionId, e.CollectionPolicy }, WireJson.Options)));
        }
        EvidenceRequire(e.Origin == CollectionEventOrigin.Engine && commit.EngineDecisionId is { } id && id != Guid.Empty, "A decision needs a stable request, occurrence or engine receipt.");
        return (CollectionReceiptKind.Engine, commit.EngineDecisionId!.Value, EvidenceDigest(EvidenceContracts.Write(e)));
    }
    private static void EvidenceUpdateState(SqliteConnection db, SqliteTransaction tx, CollectionState state, long expected)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE collection_states SET revision=$revision,payload_json=$json WHERE mission_id=$id AND revision=$expected";
        cmd.Parameters.AddWithValue("$id", state.MissionId.ToString()); cmd.Parameters.AddWithValue("$revision", state.Revision);
        cmd.Parameters.AddWithValue("$expected", expected); cmd.Parameters.AddWithValue("$json", EvidenceContracts.Write(state));
        EvidenceRequire(cmd.ExecuteNonQuery() == 1, "Collection state changed concurrently.");
    }
    private static DateTimeOffset EvidenceRecording(SqliteConnection db, SqliteTransaction tx, Guid mission)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT started_utc FROM missions WHERE id=$id AND status='Recording'";
        cmd.Parameters.AddWithValue("$id", mission.ToString()); return cmd.ExecuteScalar() is string utc ? Parse(utc) : throw new InvalidOperationException("Flight is not recording.");
    }
    private static void EvidenceWriteSnapshot<T>(SqliteConnection db, SqliteTransaction tx, Guid mission, string kind, T? value) where T : class, IVersionedEvidence
    {
        if (value is null) return;
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO collection_snapshots VALUES($mission,$kind,$hash,$json)";
        cmd.Parameters.AddWithValue("$mission", mission.ToString()); cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$hash", EvidenceContracts.ContentHash(value)); cmd.Parameters.AddWithValue("$json", EvidenceContracts.Write(value)); cmd.ExecuteNonQuery();
    }
    private static void EvidenceMatchSnapshot<T>(SqliteConnection db, SqliteTransaction tx, Guid mission, string kind, T? value) where T : class, IVersionedEvidence
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT sha256 FROM collection_snapshots WHERE mission_id=$mission AND kind=$kind";
        cmd.Parameters.AddWithValue("$mission", mission.ToString()); cmd.Parameters.AddWithValue("$kind", kind);
        EvidenceRequire(cmd.ExecuteScalar() as string == (value is null ? null : EvidenceContracts.ContentHash(value)), "Frozen mission obligations/collection plan cannot be replaced.");
    }

    private void CloseEvidenceForFlight(SqliteConnection db, SqliteTransaction tx, Guid mission, MissionStatus status, DateTimeOffset at)
    {
        var current = EvidenceState(db, tx, mission); if (current is null || current.Phase == CollectionPhase.Stopped) return;
        var session = current.CurrentSessionId is { } id ? EvidenceSession(db, tx, mission, id) : null;
        var next = current with { Revision = current.Revision + 1, Phase = CollectionPhase.Stopped,
            CurrentSessionId = null, LastSessionId = session?.Id ?? current.LastSessionId,
            ManualAutomationHold = true, ContinuityHold = status == MissionStatus.Interrupted, Holds = [] };
        var transition = new CollectionEvent(1, mission, next.Revision, DateTimeOffset.UtcNow, at, session?.Id,
            session is null ? CollectionAction.Unknown : CollectionAction.EndData, CollectionEventOrigin.Engine,
            CollectionEventOutcome.Applied, current.Phase, next.Phase, next.Authority,
            status == MissionStatus.Interrupted ? "Engine interruption; exact stop boundary unknown. Collection does not auto-resume." : "End Flight closes the open science session.", [], ExpectedSessionId: session?.Id);
        var closed = session is null ? null : session with { Status = status == MissionStatus.Interrupted ? CollectionSessionStatus.Interrupted : CollectionSessionStatus.Ended,
            EndReason = status == MissionStatus.Interrupted ? CollectionEndReason.Interrupted : CollectionEndReason.FlightEnded,
            EndedUtc = status == MissionStatus.Interrupted ? null : at };
        if (closed is not null) EvidenceWriteSession(db, tx, new(next, transition, closed), current);
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO collection_events VALUES($mission,$sequence,$session,$json)";
        cmd.Parameters.AddWithValue("$mission", mission.ToString()); cmd.Parameters.AddWithValue("$sequence", transition.Sequence);
        cmd.Parameters.AddWithValue("$session", (object?)session?.Id.ToString() ?? DBNull.Value); cmd.Parameters.AddWithValue("$json", EvidenceContracts.Write(transition)); cmd.ExecuteNonQuery();
        EvidenceUpdateState(db, tx, next, current.Revision);
    }

    private static string EvidenceDigest(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    private static void EvidenceRequire(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
