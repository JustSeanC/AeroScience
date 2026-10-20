using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

// Synthetic positions/configurations exercise persistence, never scientific/live acceptance.
public sealed class MissionEvidenceStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-evidence", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset utc = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly MissionStore store;
    private readonly Guid flight;
    private readonly EffectiveInstrumentConfiguration configuration;
    private CollectionState Initial => new(1, flight, 0, CollectionPhase.Armed, CollectionAuthority.Automatic);

    public MissionEvidenceStoreTests()
    {
        store = new(Path.Combine(root, "missions.db")); store.Initialize();
        flight = store.Create("Synthetic persistence flight", null, utc, ScienceMode.Realistic);
        configuration = new(Guid.NewGuid(), 1, utc, new("ambient-temperature", true));
    }

    private CollectionInputReceipt Input(int second, string? id = null) => new(id ?? "packet-" + second,
        new(new(ConnectionState.Connected, TelemetrySource.Simulated, "Synthetic fixture", utc.AddSeconds(second)),
            SimulatedTelemetryProvider.Generate(utc.AddSeconds(second), second)));

    private CollectionEvidenceCommit Start(CollectionState current, int second = 0, bool automatic = false)
    {
        var packet = Input(second); var link = MissionStore.DescribeCollectionInput(packet);
        var id = Guid.NewGuid(); var sequence = store.CollectionSessions(flight).Length + 1;
        var session = new ScienceCollectionSession(1, id, flight, sequence, link.ObservedUtc,
            CollectionSessionStatus.Collecting, [configuration], [link], "survey-" + sequence);
        var request = automatic ? null : new CollectionControlRequest(1, flight, Guid.NewGuid(), current.Revision, CollectionAction.StartData, null);
        var occurrence = automatic ? Guid.NewGuid() : (Guid?)null;
        var next = current with { Revision = current.Revision + 1, CurrentSessionId = id, Phase = CollectionPhase.Collecting,
            Authority = automatic ? CollectionAuthority.Automatic : CollectionAuthority.Manual,
            FiredOccurrenceIds = occurrence is { } key ? [..current.FiredOccurrenceIds ?? [], key] : current.FiredOccurrenceIds };
        var transition = new CollectionEvent(1, flight, next.Revision, utc.AddSeconds(second), link.ObservedUtc, id,
            CollectionAction.StartData, automatic ? CollectionEventOrigin.RouteTrigger : CollectionEventOrigin.Manual,
            CollectionEventOutcome.Applied, current.Phase, next.Phase, next.Authority, "Synthetic start", [link],
            RequestId: request?.RequestId, OccurrenceId: occurrence, ExpectedSessionId: null);
        return new(next, transition, session, request, [packet], FlightSample: packet.Packet.Frame);
    }

    private CollectionEvidenceCommit End(CollectionState current, int second, bool automatic = false)
    {
        var session = store.CollectionSessions(flight).Single(s => s.Id == current.CurrentSessionId);
        var request = automatic ? null : new CollectionControlRequest(1, flight, Guid.NewGuid(), current.Revision, CollectionAction.EndData, session.Id);
        var occurrence = automatic ? Guid.NewGuid() : (Guid?)null;
        var packet = Input(second); var link = MissionStore.DescribeCollectionInput(packet);
        var next = current with { Revision = current.Revision + 1, CurrentSessionId = null, LastSessionId = session.Id,
            Phase = CollectionPhase.BetweenSessions, ManualAutomationHold = !automatic,
            FiredOccurrenceIds = occurrence is { } key ? [..current.FiredOccurrenceIds ?? [], key] : current.FiredOccurrenceIds };
        var transition = new CollectionEvent(1, flight, next.Revision, link.ObservedUtc, link.ObservedUtc, session.Id,
            CollectionAction.EndData, automatic ? CollectionEventOrigin.RouteTrigger : CollectionEventOrigin.Manual,
            CollectionEventOutcome.Applied, current.Phase, next.Phase, next.Authority, "Synthetic explicit End", [link],
            RequestId: request?.RequestId, OccurrenceId: occurrence, ExpectedSessionId: session.Id);
        return new(next, transition, session with { Status = CollectionSessionStatus.Ended, EndedUtc = link.ObservedUtc,
            EndReason = automatic ? CollectionEndReason.Trigger : CollectionEndReason.Manual }, request, [packet]);
    }

    private ScienceAcquisitionIntent Intent(CollectionEvidenceCommit start, long slot = 0) => new(Guid.NewGuid(), flight,
        start.Session!.Id, "ambient-temperature", slot, configuration.Id, 1, start.Session.StartedUtc, start.Inputs![0].Id);

    private static string Json(object value) => JsonSerializer.Serialize(value, WireJson.Options);
    private static void Sql(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
    private static long Scalar(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void SchemaSevenUpgradeRetainsOriginalRowsModesJsonAndVerifiedBackupWithoutInventingEvidence()
    {
        var frame = Input(0).Packet.Frame!; store.Append(flight, frame); store.Finish(flight, MissionStatus.Completed, utc.AddSeconds(2), "Fixture end");
        EvidenceTestDatabase.RemoveSchemaEight(store.DatabasePath);
        var original = StorageManager.DatabaseFingerprint(store.DatabasePath); store.Initialize();
        Assert.Equal(8L, Scalar(store.DatabasePath, "PRAGMA user_version"));
        Assert.Equal(frame, Assert.Single(store.StreamSamples(flight))); Assert.Equal(ScienceMode.Realistic, store.Detail(flight)!.Mission.Mode);
        Assert.Null(store.CollectionCheckpoint(flight)); Assert.Empty(store.CollectionSessions(flight)); Assert.Empty(store.CollectionEvents(flight));
        Assert.Null(store.CollectionSnapshot<MissionAssessmentContext>(flight));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(root, "Backups"), "missions-schema7-*.db"));
        Assert.Equal(original, StorageManager.DatabaseFingerprint(backup)); Assert.Equal(7L, Scalar(backup, "PRAGMA user_version"));
        Assert.Equal(8L, Scalar(store.DatabasePath, "PRAGMA user_version")); store.Initialize();
        Assert.Single(Directory.GetFiles(Path.Combine(root, "Backups"), "*.db"));
    }

    [Theory]
    [InlineData("disk")][InlineData("migration-before-commit")]
    public void MigrationFailurePreservesUsableOriginalAndCanRetry(string fault)
    {
        EvidenceTestDatabase.RemoveSchemaEight(store.DatabasePath);
        var original = StorageManager.DatabaseFingerprint(store.DatabasePath);
        if (fault == "disk") store.EvidenceFreeBytes = () => 0;
        else store.EvidenceFault = point => { if (point == fault) throw new IOException("Injected migration failure"); };
        Assert.Throws<IOException>(store.Initialize);
        Assert.Equal(7L, Scalar(store.DatabasePath, "PRAGMA user_version"));
        Assert.Equal(original, StorageManager.DatabaseFingerprint(store.DatabasePath));
        Assert.Equal(0, Scalar(store.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE name='science_sessions'"));
        Assert.Equal("Synthetic persistence flight", store.Detail(flight)!.Mission.Name);
        store.EvidenceFreeBytes = null; store.EvidenceFault = null; store.Initialize(); Assert.Equal(8L, Scalar(store.DatabasePath, "PRAGMA user_version"));
    }

    [Fact]
    public void NewerDatabaseIsRefusedWithoutBackupOrMutation()
    {
        Sql(store.DatabasePath, "PRAGMA user_version=9"); var hash = StorageManager.DatabaseFingerprint(store.DatabasePath);
        Assert.Throws<InvalidOperationException>(store.Initialize);
        Assert.Equal(hash, StorageManager.DatabaseFingerprint(store.DatabasePath)); Assert.False(Directory.Exists(Path.Combine(root, "Backups")));
    }

    [Fact]
    public void RawInputAndFlightSampleNeedNotHaveTheSameCadenceAndKeepTheirOriginalValues()
    {
        store.InitializeCollectionEvidence(Initial); var start = Start(Initial); store.CommitCollectionEvidence(start);
        var end = End(start.State, 1) with { FlightSample = null }; store.CommitCollectionEvidence(end);
        Assert.Single(store.StreamSamples(flight)); Assert.Equal(Json(Input(1)), Json(store.CollectionInput(flight, "packet-1")!));
        Assert.Equal(EvidenceOrigin.SyntheticFixture, store.CollectionEvents(flight)[1].Evidence.Single().Origin);
        var update = Input(1) with { Packet = Input(1).Packet with { Connection = Input(1).Packet.Connection with { Source = TelemetrySource.Simulator }, Frame = Input(1).Packet.Frame! with { Source = TelemetrySource.Simulator } } };
        Assert.Equal(EvidenceOrigin.SimulatorReported, MissionStore.DescribeCollectionInput(update).Origin);
    }

    [Theory]
    [InlineData("after-session")][InlineData("after-event")][InlineData("after-checkpoint")][InlineData("before-commit")]
    public void FailureAtEveryPrecommitBoundaryRollsBackSessionPacketEventCheckpointAndIntent(string point)
    {
        store.InitializeCollectionEvidence(Initial); var start = Start(Initial); start = start with { Intents = [Intent(start)] };
        store.EvidenceFault = at => { if (at == point) throw new IOException("Injected transaction failure"); };
        Assert.Throws<IOException>(() => store.CommitCollectionEvidence(start));
        Assert.Equal(Json(Initial), Json(store.CollectionCheckpoint(flight)!)); Assert.Empty(store.CollectionSessions(flight));
        Assert.Empty(store.CollectionEvents(flight)); Assert.Empty(store.ScienceIntents(flight)); Assert.Null(store.CollectionInput(flight, "packet-0")); Assert.Empty(store.StreamSamples(flight));
        store.EvidenceFault = null; Assert.True(store.CommitCollectionEvidence(start).Applied); Assert.Single(store.ScienceIntents(flight));
    }

    [Fact]
    public void LostReplyAfterCommitReturnsOriginalOutcomeWithoutDuplicateSampleOrSession()
    {
        store.InitializeCollectionEvidence(Initial); var start = Start(Initial);
        store.EvidenceFault = point => { if (point == "after-commit") throw new IOException("Injected lost reply"); };
        Assert.Throws<IOException>(() => store.CommitCollectionEvidence(start)); store.EvidenceFault = null;
        var result = store.CommitCollectionEvidence(start);
        Assert.False(result.Applied); Assert.Equal(start.Session!.Id, result.Session!.Id);
        Assert.Single(store.CollectionSessions(flight)); Assert.Single(store.CollectionEvents(flight)); Assert.Single(store.StreamSamples(flight));
    }

    [Fact]
    public void DelayedEndRetryCannotCloseNewSessionAndReceiptReuseCannotRewriteHistory()
    {
        store.InitializeCollectionEvidence(Initial); var a = Start(Initial); store.CommitCollectionEvidence(a);
        var end = End(a.State, 5); store.CommitCollectionEvidence(end);
        var b = Start(end.State, 10); store.CommitCollectionEvidence(b);
        var result = store.CommitCollectionEvidence(end);
        Assert.False(result.Applied); Assert.Equal(end.Session!.Id, result.Session!.Id); Assert.Equal(b.Session!.Id, result.CurrentState.CurrentSessionId);
        var stale = end with { Request = end.Request! with { RequestId = Guid.NewGuid() }, Event = end.Event with { RequestId = Guid.NewGuid() } };
        stale = stale with { Event = stale.Event with { RequestId = stale.Request!.RequestId } };
        Assert.Throws<ArgumentException>(() => store.CommitCollectionEvidence(stale));
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(end with { Request = end.Request! with { ExpectedRevision = 20 } }));
        Assert.Equal(CollectionSessionStatus.Collecting, store.CollectionSessions(flight)[1].Status);
        Assert.Equal(MissionStatus.Recording, store.Detail(flight)!.Mission.Status);
        Assert.Equal(new long[] { 1, 2, 3 }, store.CollectionEvents(flight).Select(e => e.Sequence));
    }

    [Fact]
    public void OccurrenceReceiptsAreFlightWideAndCannotBeErasedWhenSessionChanges()
    {
        store.InitializeCollectionEvidence(Initial); var a = Start(Initial, automatic: true); store.CommitCollectionEvidence(a);
        var end = End(a.State, 5, true); store.CommitCollectionEvidence(end);
        var b = Start(end.State, 10, true); store.CommitCollectionEvidence(b);
        Assert.False(store.CommitCollectionEvidence(end).Applied);
        Assert.Equal(3, store.CollectionCheckpoint(flight)!.FiredOccurrenceIds!.Length);
        var next = End(b.State, 15, true);
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(next with { State = next.State with { FiredOccurrenceIds = [next.Event.OccurrenceId!.Value] } }));
        Assert.Equal(b.Session!.Id, store.CollectionCheckpoint(flight)!.CurrentSessionId);
    }

    [Fact]
    public void StaleControlConflictCanBeAuditedDurablyButCannotAlterTheNewSessionOrAuthority()
    {
        store.InitializeCollectionEvidence(Initial); var a = Start(Initial); store.CommitCollectionEvidence(a);
        var end = End(a.State, 5); store.CommitCollectionEvidence(end); var b = Start(end.State, 10); store.CommitCollectionEvidence(b);
        var stale = new CollectionControlRequest(1, flight, Guid.NewGuid(), 1, CollectionAction.EndData, a.Session!.Id);
        var state = b.State with { Revision = b.State.Revision + 1 };
        var audit = end.Event with { Sequence = state.Revision, RequestId = stale.RequestId, Outcome = CollectionEventOutcome.Conflict,
            From = b.State.Phase, To = b.State.Phase, Authority = b.State.Authority, Reason = "Stale Session A End refused" };
        var conflict = new CollectionEvidenceCommit(state, audit, Request: stale);
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(conflict with { State = state with { ManualAutomationHold = !state.ManualAutomationHold } }));
        store.CommitCollectionEvidence(conflict); Assert.False(store.CommitCollectionEvidence(conflict).Applied);
        Assert.Equal(b.Session!.Id, store.CollectionCheckpoint(flight)!.CurrentSessionId);
        Assert.Equal(CollectionEventOutcome.Conflict, store.CollectionEvents(flight).Last().Outcome);
        Assert.Equal(4, store.CollectionEvents(flight).Length);
    }

    [Fact]
    public async Task ConcurrentSameRevisionDecisionsCommitOnlyOneOpenSession()
    {
        store.InitializeCollectionEvidence(Initial); var a = Start(Initial); var b = Start(Initial, 1);
        async Task<bool> Try(CollectionEvidenceCommit decision) => await Task.Run(() =>
        { try { store.CommitCollectionEvidence(decision); return true; } catch (ArgumentException) { return false; } });
        var results = await Task.WhenAll(Try(a), Try(b)); Assert.Single(results, v => v);
        Assert.Single(store.CollectionSessions(flight)); Assert.Single(store.CollectionEvents(flight)); Assert.Single(store.StreamSamples(flight));
    }

    [Fact]
    public void InvalidAttributionAndPacketSubstitutionRollBackWithoutChangingOriginals()
    {
        store.InitializeCollectionEvidence(Initial); var start = Start(Initial);
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(start with { FlightSample = start.FlightSample! with { AltitudeMslFeet = 1 } }));
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(start with { Event = start.Event with { Evidence = [start.Event.Evidence[0] with { Sha256 = new('b', 64) }] } }));
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(start with { Intents = [Intent(start) with { SessionId = Guid.NewGuid() }] }));
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(start with { Event = start.Event with { Outcome = CollectionEventOutcome.Suppressed } }));
        Assert.Empty(store.CollectionSessions(flight)); Assert.Empty(store.StreamSamples(flight));
        store.CommitCollectionEvidence(start); var end = End(start.State, 5);
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(end with { Session = end.Session! with { Configurations = [] } }));
        var changed = Input(0) with { Packet = Input(0).Packet with { Frame = Input(0).Packet.Frame! with { Latitude = 20 } } };
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(end with { Inputs = [changed] }));
        Assert.Equal(Json(Input(0)), Json(store.CollectionInput(flight, "packet-0")!));
    }

    [Fact]
    public void PublishedWorkStaysWithItsOriginalSessionAndClosingCancelsUnpublishedWork()
    {
        store.InitializeCollectionEvidence(Initial); var a = Start(Initial); var published = Intent(a); var pending = Intent(a, 1);
        a = a with { Intents = [published, pending] }; store.CommitCollectionEvidence(a);
        var product = Guid.NewGuid(); var hash = new string('c', 64);
        store.AcknowledgeScienceIntent(published.Id, a.Session!.Id, product, hash);
        store.AcknowledgeScienceIntent(published.Id, a.Session.Id, product, hash); // idempotent
        Assert.Throws<InvalidOperationException>(() => store.AcknowledgeScienceIntent(published.Id, a.Session.Id, Guid.NewGuid(), hash));
        var end = End(a.State, 5); store.CommitCollectionEvidence(end); var b = Start(end.State, 10); store.CommitCollectionEvidence(b);
        Assert.Throws<InvalidOperationException>(() => store.AcknowledgeScienceIntent(pending.Id, a.Session.Id, Guid.NewGuid(), hash));
        Assert.Throws<InvalidOperationException>(() => store.AcknowledgeScienceIntent(published.Id, b.Session!.Id, product, hash));
        var rows = store.ScienceIntents(flight); Assert.Equal("Published", rows[0].State); Assert.Equal(product, rows[0].ProductId);
        Assert.Equal(a.Session.Id, rows[0].Intent.SessionId); Assert.Equal("Cancelled", rows[1].State); Assert.Empty(store.ScienceIntents(flight, true));
    }

    [Fact]
    public void AcquisitionIntentCannotUseFutureOrSupersededInstrumentSettings()
    {
        store.InitializeCollectionEvidence(Initial); var start = Start(Initial);
        var future = configuration with { Id = Guid.NewGuid(), Revision = 2, EffectiveUtc = utc.AddMinutes(1) };
        start = start with { Session = start.Session! with { Configurations = [configuration, future] } };
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(start with { Intents = [Intent(start) with { ConfigurationId = future.Id }] }));
        var superseding = future with { EffectiveUtc = utc };
        start = start with { Session = start.Session! with { Configurations = [configuration, superseding] } };
        Assert.Throws<InvalidOperationException>(() => store.CommitCollectionEvidence(start with { Intents = [Intent(start)] }));
        store.CommitCollectionEvidence(start with { Intents = [Intent(start) with { ConfigurationId = superseding.Id }] });
        Assert.Equal(superseding.Id, Assert.Single(store.ScienceIntents(flight)).Intent.ConfigurationId);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void EndFlightAndRestartCloseOnlyOpenSessionAndKeepUnknownInterruptionBoundary(bool interrupted)
    {
        store.InitializeCollectionEvidence(Initial); var a = Start(Initial); store.CommitCollectionEvidence(a);
        var end = End(a.State, 5); store.CommitCollectionEvidence(end);
        var b = Start(end.State, 10); store.CommitCollectionEvidence(b);
        var closedA = Json(store.CollectionSessions(flight)[0]);
        var reopened = new MissionStore(store.DatabasePath); reopened.Initialize();
        if (interrupted) reopened.RecoverInterrupted(); else reopened.Finish(flight, MissionStatus.Completed, utc.AddSeconds(20), "End flight");
        var sessions = reopened.CollectionSessions(flight); Assert.Equal(closedA, Json(sessions[0]));
        Assert.Equal(interrupted ? CollectionSessionStatus.Interrupted : CollectionSessionStatus.Ended, sessions[1].Status);
        Assert.Equal(interrupted ? (DateTimeOffset?)null : utc.AddSeconds(20), sessions[1].EndedUtc);
        Assert.Equal(interrupted ? CollectionEndReason.Interrupted : CollectionEndReason.FlightEnded, sessions[1].EndReason);
        Assert.Equal(CollectionPhase.Stopped, reopened.CollectionCheckpoint(flight)!.Phase); Assert.Null(reopened.CollectionCheckpoint(flight)!.CurrentSessionId);
        Assert.Equal(4, reopened.CollectionEvents(flight).Length); Assert.Equal(2, reopened.StreamSamples(flight).Count());
    }

    [Fact]
    public void FrozenPlanAndFlightWideObligationsSurviveCallerEditsAndRestart()
    {
        // This is a synthetic rubric/storage test, not production grading implementation.
        var rubric = new GradingRubric(1, "fixture", 1, MissionTemplateKind.GeographicSurvey,
            [new(RubricCategory.MissionDesign, 20), new(RubricCategory.SensorOperatingCompliance, 30), new(RubricCategory.FlightPlanExecution, 25), new(RubricCategory.AcquisitionDiscipline, 25)],
            new[] { RubricCategory.MissionDesign, RubricCategory.SensorOperatingCompliance, RubricCategory.FlightPlanExecution, RubricCategory.AcquisitionDiscipline }
                .Select(c => new RubricCriterion(c.ToString(), c, 1, true, ["fixture"], "fixture")).ToArray(), new(90, 80, 70, 60), .8, true, []);
        var context = new MissionAssessmentContext(1, flight, utc, ScienceMode.Realistic, store.RecordedPlanHash(flight), rubric,
            [new(Guid.NewGuid(), RubricCategory.MissionDesign.ToString(), 1, "difficult-area")], [configuration]);
        store.InitializeCollectionEvidence(Initial, assessment: context);
        var frozen = EvidenceContracts.ContentHash(store.CollectionSnapshot<MissionAssessmentContext>(flight)!);
        context.Obligations[0] = context.Obligations[0] with { PlannedSessionKey = "easy-area" };
        Assert.Throws<InvalidOperationException>(() => store.InitializeCollectionEvidence(Initial, assessment: context));
        Assert.Equal(frozen, EvidenceContracts.ContentHash(new MissionStore(store.DatabasePath).CollectionSnapshot<MissionAssessmentContext>(flight)!));
        Assert.Throws<InvalidOperationException>(() => store.InitializeCollectionEvidence(Initial));
        Assert.Equal("difficult-area", store.CollectionSnapshot<MissionAssessmentContext>(flight)!.Obligations[0].PlannedSessionKey);
    }

    [Fact]
    public async Task CollectionPlanSnapshotBindsRecordedWaypointsAndDoesNotFollowLaterPlanEdits()
    {
        store.Finish(flight, MissionStatus.Completed, utc, "Fixture complete");
        var waypoint = Guid.NewGuid(); var original = new MissionPlan(Guid.NewGuid(), "Original planned geometry",
            Waypoints: [new("Explicit start", 40, -75, Id: waypoint)]);
        var mission = store.Create("Planned fixture", null, utc, plan: original);
        var plan = new CollectionPlan(1, original.Id, InitialCollectionPolicy.Armed,
            [new(Guid.NewGuid(), Guid.NewGuid(), CollectionAction.StartData, CollectionTriggerKind.Waypoint, waypoint, 1, 100)], new(5, 5));
        var initial = Initial with { MissionId = mission }; store.InitializeCollectionEvidence(initial, plan);
        var frozen = EvidenceContracts.ContentHash(store.CollectionSnapshot<CollectionPlan>(mission)!);
        plan.Triggers[0] = plan.Triggers[0] with { Action = CollectionAction.EndData };
        Assert.Throws<InvalidOperationException>(() => store.InitializeCollectionEvidence(initial, plan));
        store.SavePlan(original with { Name = "Edited later", Waypoints = [original.Waypoints![0] with { Latitude = 20 }] });
        Assert.Equal(frozen, EvidenceContracts.ContentHash(store.CollectionSnapshot<CollectionPlan>(mission)!));
        Assert.Equal("Original planned geometry", store.Research(mission).PlanSnapshot!.Name);
        Assert.Equal(CollectionAction.StartData, store.CollectionSnapshot<CollectionPlan>(mission)!.Triggers[0].Action);
        store.Finish(mission, MissionStatus.Completed, utc.AddSeconds(10), "Done");
        using var output = new MemoryStream(); await store.ExportJsonAsync(mission, output);
        using var parsed = JsonDocument.Parse(output.ToArray());
        var snapshot = parsed.RootElement.GetProperty("collectionEvidence").GetProperty("snapshots")[0];
        Assert.Equal("collection-plan", snapshot.GetProperty("kind").GetString()); Assert.Equal(frozen, snapshot.GetProperty("sha256").GetString());
        Sql(store.DatabasePath, "UPDATE collection_snapshots SET sha256='incorrect'");
        Assert.Throws<InvalidOperationException>(() => store.CollectionSnapshot<CollectionPlan>(mission));
        using var corrupted = new MemoryStream(); await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExportJsonAsync(mission, corrupted));
    }

    [Fact]
    public void InputReceiptIdentifiersAndCorruptedFrozenHashesCannotBecomeTrustedEvidence()
    {
        Assert.Throws<ArgumentException>(() => MissionStore.DescribeCollectionInput(Input(0, new string('a', 201))));
        Assert.Throws<ArgumentException>(() => MissionStore.DescribeCollectionInput(Input(0, "packet\nforged")));
        store.InitializeCollectionEvidence(Initial); var start = Start(Initial); store.CommitCollectionEvidence(start);
        Sql(store.DatabasePath, "UPDATE collection_inputs SET sha256='incorrect'");
        Assert.Throws<InvalidOperationException>(() => store.CollectionInput(flight, "packet-0"));
        Sql(store.DatabasePath, "UPDATE collection_states SET revision=99");
        Assert.Throws<InvalidOperationException>(() => store.CollectionCheckpoint(flight));
    }

    [Fact]
    public void LateInitializationCannotInventHistoricalEvidenceAndClearingFlightDoesNotTouchUnrelatedFiles()
    {
        store.Append(flight, Input(0).Packet.Frame!);
        Assert.Throws<InvalidOperationException>(() => store.InitializeCollectionEvidence(Initial));
        store.Finish(flight, MissionStatus.Completed, utc, "Fixture end"); store.ClearFlightData("DELETE FLIGHT DATA");
        var newFlight = store.Create("New flight", null, utc); var initial = Initial with { MissionId = newFlight };
        store.InitializeCollectionEvidence(initial); store.Finish(newFlight, MissionStatus.Completed, utc, "Done");
        var backup = Path.Combine(root, "preserved-backup.db"); File.WriteAllText(backup, "Unrelated file");
        store.ClearFlightData("DELETE FLIGHT DATA"); Assert.Null(store.CollectionCheckpoint(newFlight)); Assert.Empty(store.CollectionSessions(newFlight));
        Assert.Equal("Unrelated file", File.ReadAllText(backup)); Assert.Equal(0L, Scalar(store.DatabasePath, "SELECT COUNT(*) FROM collection_states"));
    }

    [Fact]
    public async Task OfflineExportIncludesAllSessionEvidenceAndLegacyExportsRemainUnsegmented()
    {
        store.InitializeCollectionEvidence(Initial); var start = Start(Initial); start = start with { Intents = [Intent(start)] };
        store.CommitCollectionEvidence(start); store.CommitCollectionEvidence(End(start.State, 5)); store.Finish(flight, MissionStatus.Completed, utc.AddSeconds(10), "Done");
        var reopened = new MissionStore(store.DatabasePath); using var output = new MemoryStream(); await reopened.ExportJsonAsync(flight, output);
        using var parsed = JsonDocument.Parse(output.ToArray()); var evidence = parsed.RootElement.GetProperty("collectionEvidence");
        Assert.Single(evidence.GetProperty("sessions").EnumerateArray()); Assert.Equal(3, evidence.GetProperty("events").GetArrayLength());
        Assert.Equal(2, evidence.GetProperty("inputs").GetArrayLength()); Assert.Single(evidence.GetProperty("acquisitionIntents").EnumerateArray());
        Assert.Equal("syntheticFixture", evidence.GetProperty("events")[0].GetProperty("evidence")[0].GetProperty("origin").GetString());
        var legacy = store.Create("Legacy unsegmented", null, utc); store.Finish(legacy, MissionStatus.Completed, utc, "Done");
        using var oldOutput = new MemoryStream(); await reopened.ExportJsonAsync(legacy, oldOutput); using var oldParsed = JsonDocument.Parse(oldOutput.ToArray());
        Assert.False(oldParsed.RootElement.TryGetProperty("collectionEvidence", out _)); Assert.Equal(2, oldParsed.RootElement.GetProperty("formatVersion").GetInt32());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
