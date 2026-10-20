using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

// All definitions, hashes, scores and positions here are synthetic contract fixtures, not scientific acceptance.
public sealed class EvidenceContractsTests
{
    private static readonly DateTimeOffset Utc = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid MissionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SessionB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly string Hash = new('a', 64);
    private static EffectiveInstrumentConfiguration Configuration() => new(Guid.NewGuid(), 1, Utc,
        new("ambient-temperature", true), new(1, "fixture-calibration", 1, "ambient-temperature",
            CalibrationInformationStatus.UserDefined, [new("offset", .25, "degC", "Synthetic fixture only")], [], ["Not a real calibration"], "Test author", Utc));
    private static EvidenceLink Link() => new("fixture-receipt", Hash, Utc, EvidenceOrigin.SyntheticFixture);
    private static CollectionTrigger Trigger(CollectionAction action = CollectionAction.StartData,
        CollectionTriggerKind kind = CollectionTriggerKind.Waypoint) =>
        new(Guid.NewGuid(), Guid.NewGuid(), action, kind, Guid.NewGuid(), 1, 100,
            kind == CollectionTriggerKind.DirectionalGate ? 90 : null, "survey-a");
    private static CollectionPlan Plan() => new(1, Guid.NewGuid(), InitialCollectionPolicy.Armed,
        [Trigger()], new(5, 5));
    private static ScienceCollectionSession Session(Guid id) => new(1, id, MissionId,
        id == SessionA ? 1 : 2, Utc, CollectionSessionStatus.Collecting, [Configuration()], [Link()]);
    private static GradingRubric Rubric()
    {
        var categories = new[] { RubricCategory.MissionDesign, RubricCategory.SensorOperatingCompliance,
            RubricCategory.FlightPlanExecution, RubricCategory.AcquisitionDiscipline };
        return new(1, "fixture-performance", 1, MissionTemplateKind.GeographicSurvey,
            categories.Zip(new[] { 20d, 30d, 25d, 25d }, (c, w) => new RubricCategoryWeight(c, w)).ToArray(),
            categories.Select(c => new RubricCriterion(c.ToString(), c, 1, true, ["fixture-evidence"], "Fixture method")).ToArray(),
            new(90, 80, 70, 60), .8, true, ["Fixture, not a production rubric"]);
    }
    private static MissionAssessmentContext Context() => new(1, MissionId, Utc, ScienceMode.Realistic,
        Hash, Rubric(), [new(Guid.NewGuid(), RubricCategory.MissionDesign.ToString(), 1, "survey-a")], [Configuration()]);

    [Fact]
    public void VersionedContractsRoundTripWithoutInventingRuntimeResults()
    {
        RoundTrip(Plan()); RoundTrip(Session(SessionA)); RoundTrip(Context()); RoundTrip(Rubric());
        RoundTrip(Configuration().Calibration!);
        var specification = new InstrumentSpecification(1, "fixture-specification", 1,
            ScienceCatalog.Definitions.Single(d => d.Id == "ambient-temperature"), [], ["Contract fixture only"]);
        RoundTrip(specification);
        RoundTrip(Session(SessionA) with { Configurations = [Configuration() with { Specification = specification }] });
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(Session(SessionA) with
        { Configurations = [Configuration() with { Specification = specification with { Definition = specification.Definition with { Id = "ambient-pressure" } } }] }));
        RoundTrip(new CollectionState(1, MissionId, 3, CollectionPhase.BetweenSessions,
            CollectionAuthority.Manual, LastSessionId: SessionA, ManualAutomationHold: true));
        RoundTrip(new CollectionControlRequest(1, MissionId, Guid.NewGuid(), 3, CollectionAction.StartData, null));
        RoundTrip(new CollectionEvent(1, MissionId, 1, Utc, Utc, SessionA, CollectionAction.StartData,
            CollectionEventOrigin.Manual, CollectionEventOutcome.Applied, CollectionPhase.Armed,
            CollectionPhase.Collecting, CollectionAuthority.Manual, "Fixture manual start", [Link()], RequestId: Guid.NewGuid()));
        RoundTrip(new ReadinessAssessment(1, Hash, Utc, [new("fixture", ReadinessStatus.NotYetAssessed, ["No assessment yet"], [])], []));
        RoundTrip(new OperatingAssessment(1, MissionId, SessionA, Guid.NewGuid(), Utc,
            [new(new("fixture-rule", 1, Hash), "temperature", "degC", null, AssessmentStatus.Unknown,
                AssessmentStatus.NotAssessed, AssessmentStatus.Unavailable, ["Missing input"], [])], [Link()]));
        RoundTrip(new ScienceYield(1, MissionId, SessionA, "ambient-temperature", Utc,
            [new("good-data", AssessmentStatus.NotAssessed, null, null, "count", "Not calculated", 0, 0, EvidenceOrigin.Unknown, [])], []));
        var grade = new MissionGrade(1, MissionId, Utc);
        RoundTrip(grade); Assert.Equal(MissionGradeStatus.NotGraded, grade.Status); Assert.Null(grade.Letter); Assert.Null(grade.ScoreLower);
        IVersionedEvidence polymorphic = grade;
        Assert.Equal(EvidenceContracts.Write(grade), EvidenceContracts.Write(polymorphic));
        Assert.Equal(EvidenceContracts.ContentHash(grade), EvidenceContracts.ContentHash(polymorphic));
    }

    private static void RoundTrip<T>(T value) where T : class, IVersionedEvidence
    {
        var json = EvidenceContracts.Write(value);
        var copy = EvidenceContracts.Read<T>(json);
        Assert.Equal(json, EvidenceContracts.Write(copy));
        Assert.Equal(EvidenceContracts.ContentHash(value), EvidenceContracts.ContentHash(copy));
    }

    [Theory]
    [InlineData(0)][InlineData(-1)][InlineData(2)][InlineData(int.MaxValue)]
    public void UnsupportedVersionsAreRefusedWithoutReinterpretation(int version)
    {
        var node = JsonSerializer.SerializeToNode(Plan(), WireJson.Options)!;
        node["version"] = version;
        Assert.Throws<NotSupportedException>(() => EvidenceContracts.Read<CollectionPlan>(node.ToJsonString()));
        Assert.Throws<NotSupportedException>(() => EvidenceContracts.Snapshot(Plan() with { Version = version }));
    }

    [Theory]
    [InlineData("version")][InlineData("expectedRevision")][InlineData("expectedSessionId")]
    public void ControlRequiresExplicitVersionRevisionAndNoSessionExpectation(string field)
    {
        var node = JsonSerializer.SerializeToNode(new CollectionControlRequest(1, MissionId,
            Guid.NewGuid(), 0, CollectionAction.StartData, null), WireJson.Options)!.AsObject();
        node.Remove(field);
        Assert.Throws<JsonException>(() => EvidenceContracts.Read<CollectionControlRequest>(node.ToJsonString()));
    }

    [Theory]
    [InlineData("999")][InlineData("\"finishScienceForThisFlight\"")][InlineData("\"endFlight\"")][InlineData("\"unrecognizedFutureAction\"")]
    public void UnknownNumericAndFlightWideActionsCannotBecomeEndCurrentSession(string action)
    {
        var node = JsonSerializer.SerializeToNode(new CollectionControlRequest(1, MissionId,
            Guid.NewGuid(), 0, CollectionAction.EndData, SessionA), WireJson.Options)!;
        node["action"] = JsonNode.Parse(action);
        Assert.Throws<JsonException>(() => EvidenceContracts.Read<CollectionControlRequest>(node.ToJsonString()));
    }

    [Fact]
    public void AmbiguousFutureAndOversizedJsonAreRejected()
    {
        var json = EvidenceContracts.Write(Plan());
        Assert.Throws<JsonException>(() => EvidenceContracts.Read<CollectionPlan>(json.Replace("\"version\":1", "\"version\":1,\"Version\":1")));
        Assert.Throws<JsonException>(() => EvidenceContracts.Read<CollectionPlan>(json.Insert(1, "\"futureControl\":true,")));
        Assert.Throws<JsonException>(() => EvidenceContracts.Read<CollectionPlan>(json.Replace("\"version\":1", "\"version\":\"1\"")));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Read<CollectionPlan>(new string(' ', EvidenceContracts.MaximumJsonBytes + 1)));
        Assert.ThrowsAny<JsonException>(() => EvidenceContracts.Read<CollectionPlan>("{bad"));
    }

    [Fact]
    public void SessionBoundRequestsCannotRetargetAfterAnotherSessionStarts()
    {
        var state = new CollectionState(1, MissionId, 8, CollectionPhase.Collecting, CollectionAuthority.Automatic, SessionB, SessionA);
        var endA = new CollectionControlRequest(1, MissionId, Guid.NewGuid(), 7, CollectionAction.EndData, SessionA);
        Assert.Throws<ArgumentException>(() => EvidenceContracts.ValidateControl(endA, state));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.ValidateControl(endA with { ExpectedRevision = 8 }, state));
        EvidenceContracts.ValidateControl(endA with { ExpectedRevision = 8, ExpectedSessionId = SessionB }, state);
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(endA with { Action = CollectionAction.StartData }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(endA with { Action = CollectionAction.ResumeData, ExpectedSessionId = null }));
        Assert.Equal(SessionB, state.CurrentSessionId); // validation does not transition or close either session
    }

    [Fact]
    public void ManualHoldAndFlightWideReceiptsSurviveContractRoundTripsBetweenSessions()
    {
        var receipt = Guid.NewGuid();
        var state = new CollectionState(1, MissionId, 9, CollectionPhase.BetweenSessions,
            CollectionAuthority.Manual, LastSessionId: SessionA, ManualAutomationHold: true,
            FiredOccurrenceIds: [receipt], SuppressedOccurrenceIds: [Guid.NewGuid()]);
        var copy = EvidenceContracts.Snapshot(state);
        Assert.True(copy.ManualAutomationHold); Assert.Equal(SessionA, copy.LastSessionId);
        Assert.Equal(receipt, copy.FiredOccurrenceIds!.Single());
        EvidenceContracts.ValidateControl(new(1, MissionId, Guid.NewGuid(), 9, CollectionAction.StartData, null), copy);
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(state with { CurrentSessionId = SessionB }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(state with { SuppressedOccurrenceIds = [receipt] }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(state with { Holds = [new(CollectionHoldKind.Boundary, "Old session boundary", SessionA)] }));
        RoundTrip(state with { Holds = [new(CollectionHoldKind.Manual, "Pilot override")] });
        RoundTrip(state with { Phase = CollectionPhase.Paused, CurrentSessionId = SessionB,
            Holds = [new(CollectionHoldKind.Boundary, "Outside selected area", SessionB, Guid.NewGuid())] });
    }

    [Fact]
    public void DistinctSessionsRetainConfigurationProvenanceAndKnownEndTimes()
    {
        var a = Session(SessionA) with { Status = CollectionSessionStatus.Ended, EndedUtc = Utc.AddSeconds(10), EndReason = CollectionEndReason.Trigger };
        var b = Session(SessionB) with { StartedUtc = Utc.AddSeconds(20) };
        RoundTrip(a); RoundTrip(b);
        Assert.Equal(a.Configurations[0].Calibration!.Parameters, EvidenceContracts.Snapshot(a).Configurations[0].Calibration!.Parameters);
        Assert.Equal(EvidenceOrigin.SyntheticFixture, EvidenceContracts.Snapshot(a).Evidence[0].Origin);
        Assert.NotEqual(a.Id, b.Id); Assert.Equal(a.MissionId, b.MissionId);
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(a with { EndedUtc = null }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(a with { EndedUtc = Utc.AddSeconds(-1) }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(b with { EndReason = CollectionEndReason.Manual }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(b with { Configurations = [b.Configurations[0], b.Configurations[0] with { Id = Guid.NewGuid() }] }));
        RoundTrip(b with { Status = CollectionSessionStatus.Interrupted, EndReason = CollectionEndReason.Interrupted }); // unknown interruption time stays absent
    }

    [Fact]
    public void TriggersRequireStableTargetsAndNeverInferEndFromAnAreaOrLastWaypoint()
    {
        var waypoint = Guid.NewGuid(); var area = Guid.NewGuid(); var plan = Plan();
        var polygon = new SurveyArea("Survey", [new(0, 0), new(0, 1), new(1, 1)]);
        var mission = new MissionPlan(plan.MissionPlanId, "Fixture", Waypoints: [new("Last", 0, 0, Id: waypoint)], SurveyAreas: [polygon]);
        plan = plan with { SurveyAreas = [new(area, 0, EvidenceContracts.SurveyGeometryHash(polygon))] };
        var explicitEnd = Trigger(CollectionAction.EndData) with { TargetId = waypoint };
        EvidenceContracts.ValidatePlan(plan with { Triggers = [explicitEnd] }, mission);
        EvidenceContracts.ValidatePlan(plan with { Triggers = [] }, mission); // no implicit final-waypoint End
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(plan with { Triggers = [explicitEnd with { Kind = CollectionTriggerKind.AreaExit, TargetId = area }] }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.ValidatePlan(plan, mission));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(plan with { Triggers = [explicitEnd, explicitEnd with { Id = Guid.NewGuid() }] }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(plan with { Triggers = Enumerable.Range(0, 129).Select(_ => Trigger()).ToArray() }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(plan with { Triggers = null! }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(plan with { Boundary = new([area], false, true, 5, 1) }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(plan with { Triggers = [Trigger(kind: CollectionTriggerKind.DirectionalGate) with { DirectionTrueDegrees = null }] }));
        EvidenceContracts.ValidatePlan(plan with { Triggers = [Trigger(kind: CollectionTriggerKind.AreaEntry) with { TargetId = area }] }, mission);
        Assert.Throws<ArgumentException>(() => EvidenceContracts.ValidatePlan(plan with { Triggers = [], SurveyAreas = [new(area, 0, Hash)] }, mission));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(plan with { SurveyAreas = [plan.SurveyAreas![0], plan.SurveyAreas[0]] }));
    }

    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(-1)][InlineData(10001)]
    public void NonFiniteAndUnboundedGeometryIsRejected(double tolerance)
    {
        var plan = Plan(); Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(plan with { Triggers = [plan.Triggers[0] with { ToleranceMetres = tolerance }] }));
    }

    [Fact]
    public void EventContractsPreserveManualAutomaticAndSuppressedEvidence()
    {
        var applied = new CollectionEvent(1, MissionId, 4, Utc, Utc, SessionA, CollectionAction.EndData,
            CollectionEventOrigin.RouteTrigger, CollectionEventOutcome.Applied, CollectionPhase.Collecting,
            CollectionPhase.BetweenSessions, CollectionAuthority.Automatic, "Explicit fixture End gate", [Link()], OccurrenceId: Guid.NewGuid(), ExpectedSessionId: SessionA);
        RoundTrip(applied);
        RoundTrip(applied with { CollectionPolicy = new("fixture-collection-policy", 1, Hash), ConfigurationIds = [Guid.NewGuid()] });
        RoundTrip(applied with { SessionId = null, Action = CollectionAction.StartData, Outcome = CollectionEventOutcome.Suppressed, Reason = "Manual hold" });
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(applied with { OccurrenceId = null }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(applied with { Origin = CollectionEventOrigin.Boundary }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(applied with { SessionId = null }));
    }

    [Fact]
    public void DetachedSnapshotsAndHashesPreserveFrozenFlightWideObligations()
    {
        var context = Context(); var frozen = EvidenceContracts.Snapshot(context); var hash = EvidenceContracts.ContentHash(frozen);
        context.Obligations[0] = context.Obligations[0] with { PlannedSessionKey = "edited-easy-session", Weight = .5 };
        context.Configurations[0].Calibration!.Parameters[0] = new("offset", 99, "degC", "Edited fixture");
        Assert.Equal("survey-a", frozen.Obligations[0].PlannedSessionKey);
        Assert.Equal(.25, frozen.Configurations[0].Calibration!.Parameters[0].Value);
        Assert.Equal(hash, EvidenceContracts.ContentHash(frozen)); Assert.NotEqual(hash, EvidenceContracts.ContentHash(context));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(frozen with { Obligations = [frozen.Obligations[0], frozen.Obligations[0]] }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(frozen with { Obligations = [frozen.Obligations[0] with { CriterionId = "nonexistent" }] }));
    }

    [Fact]
    public void CalibrationAndAssessmentUnknownsCannotBecomeScientificSuccess()
    {
        var calibration = Configuration().Calibration!;
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(calibration with { Status = CalibrationInformationStatus.Unknown }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(calibration with { Status = CalibrationInformationStatus.Documented }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(calibration with { Author = null }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(calibration with { Parameters = [new("offset", double.NaN, "degC", "fixture")] }));
        var rule = new OperatingRuleAssessment(new("fixture", 1, Hash), "AGL", "m", null,
            AssessmentStatus.Unknown, AssessmentStatus.Invalid, AssessmentStatus.Unavailable, ["Unknown input"], ["footprint"]);
        var assessment = new OperatingAssessment(1, MissionId, SessionA, Guid.NewGuid(), Utc, [rule], []);
        RoundTrip(assessment); Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(assessment with { Rules = [rule with { Compliance = AssessmentStatus.Supported }] }));
        Assert.Throws<ArgumentNullException>(() => EvidenceContracts.Validate(assessment with { Rules = [rule with { Rule = null! }] }));
    }

    [Fact]
    public void RubricsAndResultsRetainUncertaintyAndNeverAverageSessionGrades()
    {
        var rubric = Rubric(); RoundTrip(rubric);
        Assert.Equal(new[] { 20d, 30d, 25d, 25d }, rubric.Categories.Select(c => c.Weight));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(rubric with { Categories = [rubric.Categories[0], rubric.Categories[0]] }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(rubric with { Thresholds = new(60, 80, 70, 90) }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(rubric with { MinimumEvidenceWeightFraction = .79 }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(rubric with { RequireSingleLetterScoreRange = false }));
        var grade = new MissionGrade(1, MissionId, Utc, Sessions:
            [new(SessionA, [], [], ["No calculation yet"]), new(SessionB, [], [], ["No calculation yet"])]);
        RoundTrip(grade); Assert.Null(grade.Letter); Assert.Null(grade.ScoreLower);
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(grade with { Status = MissionGradeStatus.Final, Letter = MissionGradeLetter.A }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(grade with { Letter = MissionGradeLetter.A }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(grade with { Sessions = [grade.Sessions![0], grade.Sessions[0]] }));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(grade with { Criteria = [new(Guid.NewGuid(), "missing", AssessmentStatus.Missing, 100, [], [])] }));
        var metric = new ScienceMetric("coverage", AssessmentStatus.Unknown, 1, 1, "m2", "Unknown", 0, 0, EvidenceOrigin.Unknown, []);
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(new ScienceYield(1, MissionId, null, "thermal-imager", Utc, [metric], [])));
        Assert.Throws<ArgumentException>(() => EvidenceContracts.Validate(new ScienceYield(1, MissionId, null, "thermal-imager", Utc,
            [metric with { Status = AssessmentStatus.Supported, Numerator = null, Denominator = null }], [])));
    }

    [Fact]
    public void OldPlanAndWireJsonStayUnsegmentedWithoutSchemaMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), "aeroscience-p01", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "missions.db");
        try
        {
            var store = new MissionStore(path); store.Initialize(); var id = Guid.NewGuid();
            var legacy = "{\"id\":\"" + id + "\",\"name\":\"Legacy\",\"surveyAreas\":[{\"name\":\"Area\",\"vertices\":[{\"latitude\":0,\"longitude\":0},{\"latitude\":0,\"longitude\":1},{\"latitude\":1,\"longitude\":1}]}]}";
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO mission_plans VALUES($id,'Legacy',$utc,$json)";
                cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$utc", Utc.ToString("O")); cmd.Parameters.AddWithValue("$json", legacy); cmd.ExecuteNonQuery();
            }
            var before = store.Plan(id)!;
            Assert.DoesNotContain("\"id\"", JsonSerializer.Serialize(before.SurveyAreas![0], WireJson.Options));
            Assert.DoesNotContain("collection", JsonSerializer.Serialize(before, WireJson.Options));
            var saved = store.SavePlan(before);
            Assert.DoesNotContain("\"id\"", JsonSerializer.Serialize(saved.SurveyAreas![0], WireJson.Options));
            var binding = new CollectionSurveyArea(Guid.NewGuid(), 0, EvidenceContracts.SurveyGeometryHash(saved.SurveyAreas[0]));
            var collection = new CollectionPlan(1, saved.Id, InitialCollectionPolicy.Armed, [], new(5, 5), SurveyAreas: [binding]);
            Assert.Equal(binding.Id, EvidenceContracts.Snapshot(collection).SurveyAreas!.Single().Id);
            EvidenceContracts.ValidatePlan(collection, saved);
            using var verify = new SqliteConnection($"Data Source={path};Pooling=False"); verify.Open(); using var check = verify.CreateCommand();
            check.CommandText = "PRAGMA user_version"; Assert.Equal(7L, check.ExecuteScalar());
            var handler = new ToolbarCommandHandler(new MissionCoordinator(store, 1000));
            var reply = handler.Handle(JsonSerializer.Serialize(new ToolbarCommand(1, Guid.NewGuid(), "state"), WireJson.Options));
            Assert.InRange(Encoding.UTF8.GetByteCount(reply), 1, 8000);
            Assert.DoesNotContain("currentSessionId", reply); Assert.DoesNotContain("scoreLower", reply);
            Assert.Equal("\"collecting\"", JsonSerializer.Serialize(ScienceCollectionStatus.Collecting, WireJson.Options));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
