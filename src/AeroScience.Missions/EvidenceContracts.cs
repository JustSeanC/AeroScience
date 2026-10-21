using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AeroScience.Shared;

namespace AeroScience.Missions;

/// <summary>Pure contract validation/copying. No collection, persistence, calibration or grading execution.</summary>
public static class EvidenceContracts
{
    public const int CurrentVersion = 1;
    public const int MaximumTriggers = 128;
    // Saved triggers stay bounded at 128; repeated boundary decisions need their own
    // bounded, flight-wide receipts and must never evict earlier session history.
    public const int MaximumOccurrences = 4096;
    public const int MaximumJsonBytes = 1_048_576;
    private static readonly JsonSerializerOptions StrictJson = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(WireJson.Options)
        {
            MaxDepth = 32,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Insert(0, new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    public static T Read<T>(string json) where T : class, IVersionedEvidence
    {
        ArgumentNullException.ThrowIfNull(json);
        Require(Encoding.UTF8.GetByteCount(json) <= MaximumJsonBytes, "Evidence JSON exceeds its bounded size.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        CheckDuplicateProperties(document.RootElement);
        var result = JsonSerializer.Deserialize<T>(json, StrictJson) ?? throw new ArgumentException("Evidence cannot be null.");
        Validate(result);
        return result;
    }

    public static string Write<T>(T value) where T : class, IVersionedEvidence
    {
        Validate(value);
        var json = JsonSerializer.Serialize(value, value.GetType(), StrictJson);
        Require(Encoding.UTF8.GetByteCount(json) <= MaximumJsonBytes, "Evidence JSON exceeds its bounded size.");
        return json;
    }

    // Detached arrays/settings: editing a draft must not mutate a previously taken snapshot.
    // P02 owns durable, append-only storage of these snapshots.
    public static T Snapshot<T>(T value) where T : class, IVersionedEvidence => Read<T>(Write(value));

    public static string ContentHash<T>(T value) where T : class, IVersionedEvidence
    {
        using var document = JsonDocument.Parse(Write(value));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static void Validate(IVersionedEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Version != CurrentVersion) throw new NotSupportedException($"Unsupported evidence contract version {value.Version}; preserve the original record.");
        switch (value)
        {
            case CollectionPlan plan: ValidatePlan(plan); break;
            case CollectionControlRequest request: ValidateRequest(request); break;
            case CollectionState state: ValidateState(state); break;
            case ScienceCollectionSession session: ValidateSession(session); break;
            case CollectionEvent transition: ValidateEvent(transition); break;
            case CalibrationDefinition calibration: ValidateCalibration(calibration); break;
            case InstrumentSpecification specification:
                Text(specification.Id); Require(specification.Revision > 0, "Instrument specification revision must be positive.");
                ArgumentNullException.ThrowIfNull(specification.Definition); Instrument(specification.Definition.Id);
                Text(specification.Definition.Name); Text(specification.Definition.Description); Text(specification.Definition.DataSource);
                foreach (var measurement in Items(specification.Definition.Measurements, 128))
                { Text(measurement.Id); Text(measurement.Name); Text(measurement.Unit); Text(measurement.SimulatorVariable); }
                Unique(specification.Definition.Measurements.Select(m => m.Id));
                Require(specification.Definition.MinimumIntervalMs is >= 1000 and <= 60000, "Instrument source cadence must remain supported.");
                Texts(specification.References); Texts(specification.Limitations); break;
            case OperatingAssessment assessment: ValidateOperating(assessment); break;
            case ReadinessAssessment readiness:
                Hash(readiness.PlanSha256); Utc(readiness.AssessedUtc); Texts(readiness.Limitations);
                foreach (var check in Items(readiness.Checks, 256))
                { Text(check.Id); Known(check.Status); Texts(check.Reasons); Texts(check.Actions); Text(check.Category); if(check.Inputs is not null)Texts(check.Inputs); }
                Unique(readiness.Checks.Select(c => c.Id)); if(readiness.ContextSha256 is not null)Hash(readiness.ContextSha256); OptionalUtc(readiness.ExpiresUtc); Require(readiness.ExpiresUtc is null||readiness.ExpiresUtc>readiness.AssessedUtc,"Readiness expiry must follow assessment."); break;
            case GradingRubric rubric: ValidateRubric(rubric); break;
            case MissionAssessmentContext context: ValidateContext(context); break;
            case ScienceYield yield:
                Id(yield.MissionId); OptionalId(yield.SessionId); Instrument(yield.InstrumentId); Utc(yield.AsOfUtc);
                Metrics(yield.Metrics); Texts(yield.Limitations); break;
            case MissionGrade grade: ValidateGrade(grade); break;
            default: throw new NotSupportedException("Unknown evidence contract type.");
        }
    }

    public static void ValidatePlan(CollectionPlan collection, MissionPlan mission)
    {
        Validate(collection); ArgumentNullException.ThrowIfNull(mission);
        Require(collection.MissionPlanId == mission.Id, "Collection plan belongs to a different mission plan.");
        var waypoints = (mission.Waypoints ?? []).Where(w => w.Id.HasValue).Select(w => w.Id!.Value).ToHashSet();
        var identities = collection.SurveyAreas ?? [];
        var areas = identities.Select(a => a.Id).ToHashSet();
        foreach (var identity in identities)
        {
            Require(identity.PlanAreaIndex < (mission.SurveyAreas?.Length ?? 0), "Survey identity references a missing plan polygon.");
            Require(string.Equals(identity.GeometrySha256, SurveyGeometryHash(mission.SurveyAreas![identity.PlanAreaIndex]), StringComparison.OrdinalIgnoreCase),
                "Survey geometry changed; explicitly update its identity binding before saving collection automation.");
        }
        foreach (var trigger in collection.Triggers)
            Require((trigger.Kind is CollectionTriggerKind.Waypoint or CollectionTriggerKind.DirectionalGate ? waypoints : areas).Contains(trigger.TargetId),
                "A trigger must reference a saved, stable waypoint or survey-area ID.");
        if (collection.Boundary is { } boundary)
            Require(boundary.SurveyAreaIds.All(areas.Contains), "Boundary automation references a missing survey area.");
    }

    public static string SurveyGeometryHash(SurveyArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        var vertices = Items(area.Vertices, 1000);
        Require(vertices.Length >= 3 && vertices.All(p => MissionPlanning.Position(p.Latitude, p.Longitude)), "Survey geometry requires bounded WGS84 vertices.");
        SurveyGeometry.Validate(vertices);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(vertices, WireJson.Options)));
    }

    public static void ValidateControl(CollectionControlRequest request, CollectionState current)
    {
        Validate(request); Validate(current);
        Require(ControlMatches(request,current), "Stale mission, controller revision or session target; resynchronize before issuing a new command.");
        // Receipt lookup/idempotent retry and transitions belong to P02/P05, not this validator.
    }
    public static bool ControlMatches(CollectionControlRequest request,CollectionState current)=>
        request.MissionId==current.MissionId&&request.ExpectedSessionId==current.CurrentSessionId&&
        (request.ExpectedRevision==current.Revision&&(!request.ExpectedControlRevision.HasValue||request.ExpectedControlRevision==current.ControlRevision)||
         request.ExpectedRevision<current.Revision&&request.ExpectedControlRevision.HasValue&&request.ExpectedControlRevision==current.ControlRevision);

    private static void ValidatePlan(CollectionPlan plan)
    {
        Id(plan.MissionPlanId); Known(plan.InitialPolicy);
        Require(plan.InitialPolicy != InitialCollectionPolicy.Unknown, "Select an initial collection policy.");
        var triggers = Items(plan.Triggers, MaximumTriggers);
        var areas = Items(plan.SurveyAreas ?? [], 20);
        Unique(areas.Select(a => a.Id)); Unique(areas.Select(a => a.PlanAreaIndex));
        foreach (var area in areas) { Id(area.Id); Require(area.PlanAreaIndex is >= 0 and < 20, "Survey index is out of bounds."); Hash(area.GeometrySha256); }
        Unique(triggers.Select(t => t.Id)); Unique(triggers.Select(t => t.OccurrenceId));
        foreach (var trigger in triggers)
        {
            Id(trigger.Id); Id(trigger.OccurrenceId); Id(trigger.TargetId); Known(trigger.Action); Known(trigger.Kind); Known(trigger.InitialInside);
            Require(trigger.Action is CollectionAction.StartData or CollectionAction.PauseData or CollectionAction.ResumeData or CollectionAction.EndData,
                "Triggers support only explicitly configured Start/Pause/Resume/End Data.");
            Require(trigger.Kind != CollectionTriggerKind.Unknown && trigger.InitialInside != InitialInsidePolicy.Unknown, "Trigger geometry/entry policy must be explicit.");
            Require(trigger.Pass is >= 1 and <= 1000, "Route occurrence/pass is out of bounds.");
            Range(trigger.ToleranceMetres, .01, 10000);
            Require(trigger.Action != CollectionAction.EndData || trigger.Kind is CollectionTriggerKind.Waypoint or CollectionTriggerKind.DirectionalGate,
                "Automatic End is optional and only valid at an explicitly configured route point or directional gate.");
            if (trigger.Kind == CollectionTriggerKind.DirectionalGate)
            { Require(trigger.DirectionTrueDegrees.HasValue, "A directional gate requires a true direction."); Range(trigger.DirectionTrueDegrees!.Value, 0, 359.999999999); }
            else Require(trigger.DirectionTrueDegrees is null, "Only a directional gate has a crossing direction.");
            OptionalText(trigger.PlannedSessionKey);
        }
        ArgumentNullException.ThrowIfNull(plan.Continuity);
        Range(plan.Continuity.MaximumGapSeconds, .1, 60); Range(plan.Continuity.MaximumPositionAgeSeconds, .1, 60);
        if (plan.Boundary is { } boundary)
        {
            var ids = Items(boundary.SurveyAreaIds, 20); Require(ids.Length > 0, "Choose a boundary area.");
            Unique(ids); foreach (var id in ids) Id(id);
            Require(!boundary.ResumeOnEntry || boundary.PauseOnExit, "Boundary resume requires boundary pause; it never starts a new session.");
            Range(boundary.HysteresisMetres, 0, 1000); Range(boundary.DwellSeconds, 0, 60);
        }
    }

    private static void ValidateRequest(CollectionControlRequest request)
    {
        Require(request.Justification is null || request.Action==CollectionAction.PauseData && request.Justification=="safety", "Only an explicit safety pause justification is supported; frozen research work is never erased.");
        Id(request.MissionId); Id(request.RequestId); OptionalId(request.ExpectedSessionId); Known(request.Action);
        Require(request.ExpectedRevision >= 0 && request.Action != CollectionAction.Unknown, "A control requires an explicit action and revision.");
        Require(request.ExpectedControlRevision is null||request.ExpectedControlRevision>=0&&request.ExpectedControlRevision<=request.ExpectedRevision,"Invalid control revision.");
        if (request.Action == CollectionAction.StartData) Require(request.ExpectedSessionId is null, "Start creates a new session only when no session is open.");
        if (request.Action is CollectionAction.PauseData or CollectionAction.ResumeData)
            Require(request.ExpectedSessionId.HasValue, "Pause/Resume must target an open session.");
    }

    private static void ValidateState(CollectionState state)
    {
        Id(state.MissionId); Require(state.Revision >= 0, "Controller revision cannot be negative.");
        Require(state.ControlRevision is null||state.ControlRevision>=0&&state.ControlRevision<=state.Revision,"Invalid control checkpoint revision.");
        Known(state.Phase); Known(state.Authority); OptionalId(state.CurrentSessionId); OptionalId(state.LastSessionId);
        Require(state.CurrentSessionId.HasValue == (state.Phase is CollectionPhase.Collecting or CollectionPhase.Paused), "Controller phase and current session disagree.");
        foreach (var ids in new[] { state.FiredOccurrenceIds, state.SuppressedOccurrenceIds })
            if (ids is not null) { Items(ids, MaximumOccurrences); Unique(ids); foreach (var id in ids) Id(id); }
        Require(!(state.FiredOccurrenceIds ?? []).Intersect(state.SuppressedOccurrenceIds ?? []).Any(), "An occurrence cannot be both fired and suppressed.");
        foreach (var hold in Items(state.Holds ?? [], 256))
        {
            Known(hold.Kind); Require(hold.Kind != CollectionHoldKind.Unknown, "A hold needs a known reason category."); Text(hold.Reason); OptionalId(hold.SessionId); OptionalId(hold.CauseId);
            if (hold.Kind is CollectionHoldKind.Route or CollectionHoldKind.Boundary)
                Require(hold.SessionId.HasValue && hold.SessionId == state.CurrentSessionId && hold.CauseId.HasValue,
                    "Route/boundary holds identify their open session and distinct area/occurrence cause.");
        }
    }

    private static void ValidateSession(ScienceCollectionSession session)
    {
        Id(session.Id); Id(session.MissionId); Require(session.Sequence > 0, "Session sequence starts at one.");
        Utc(session.StartedUtc); OptionalUtc(session.EndedUtc); Known(session.Status); OptionalText(session.PlannedSessionKey);
        Require(session.Status != CollectionSessionStatus.Unknown, "A session requires an explicit status.");
        if (session.Status is CollectionSessionStatus.Ended or CollectionSessionStatus.Interrupted)
        {
            Require(session.EndReason.HasValue && session.EndReason != CollectionEndReason.Unknown, "Closed sessions require an end reason.");
            Known(session.EndReason!.Value);
            Require((session.Status == CollectionSessionStatus.Interrupted) == (session.EndReason == CollectionEndReason.Interrupted), "Interruption status/reason disagree.");
            if (session.Status == CollectionSessionStatus.Ended) Require(session.EndedUtc.HasValue, "An ended session requires its known end time.");
        }
        else Require(session.EndedUtc is null && session.EndReason is null, "An open session cannot have an end time/reason.");
        Require(session.EndedUtc is null || session.EndedUtc >= session.StartedUtc, "Session end precedes start.");
        Configurations(session.Configurations); Links(session.Evidence);
    }

    private static void ValidateEvent(CollectionEvent transition)
    {
        Id(transition.MissionId); Require(transition.Sequence > 0 && transition.SessionSequence is not <= 0, "Event sequences must be positive.");
        Utc(transition.RecordedUtc); Utc(transition.InputUtc); OptionalId(transition.SessionId); OptionalId(transition.ExpectedSessionId);
        OptionalId(transition.RequestId); OptionalId(transition.OccurrenceId); Known(transition.Action); Known(transition.Origin);
        Known(transition.Outcome); Known(transition.From); Known(transition.To); Known(transition.Authority);
        Require(transition.Origin != CollectionEventOrigin.Unknown && transition.Outcome != CollectionEventOutcome.Unknown, "Event origin/outcome must be explicit.");
        Require(transition.Origin == CollectionEventOrigin.Engine || transition.Action != CollectionAction.Unknown, "Manual/automatic events require a known action.");
        if (transition.Origin == CollectionEventOrigin.Manual) Require(transition.RequestId.HasValue && transition.OccurrenceId is null, "Manual events require their request receipt.");
        if (transition.Origin is CollectionEventOrigin.RouteTrigger or CollectionEventOrigin.Boundary)
            Require(transition.OccurrenceId.HasValue && transition.RequestId is null, "Automatic events require a flight-wide occurrence receipt.");
        if (transition.Origin == CollectionEventOrigin.Boundary)
            Require(transition.Action is CollectionAction.PauseData or CollectionAction.ResumeData, "Boundary automation only pauses/resumes an existing session.");
        if (transition.Outcome == CollectionEventOutcome.Applied && transition.Action is CollectionAction.StartData or CollectionAction.PauseData or CollectionAction.ResumeData or CollectionAction.EndData)
            Require(transition.SessionId.HasValue, "An applied collection action must identify its session.");
        Text(transition.Reason); OptionalText(transition.PlannedSessionKey); Links(transition.Evidence);
        if (transition.CollectionPolicy is { } policy) Reference(policy);
        var configurations = Items(transition.ConfigurationIds ?? [], 256); Unique(configurations);
        foreach (var id in configurations) Id(id);
    }

    private static void ValidateCalibration(CalibrationDefinition calibration)
    {
        Text(calibration.Id); Require(calibration.Revision > 0, "Definition revision must be positive."); Instrument(calibration.InstrumentId);
        Known(calibration.Status); OptionalText(calibration.Author); OptionalUtc(calibration.DefinedUtc);
        Texts(calibration.References); Texts(calibration.Limitations);
        var parameters = Items(calibration.Parameters, 128); Unique(parameters.Select(p => p.Id));
        foreach (var parameter in parameters) { Text(parameter.Id); Finite(parameter.Value); Text(parameter.Unit); Text(parameter.Basis); }
        if (calibration.Status == CalibrationInformationStatus.Documented) Require(calibration.References.Length > 0, "Documented calibration needs references, not a certification claim.");
        if (calibration.Status == CalibrationInformationStatus.UserDefined) Require(!string.IsNullOrWhiteSpace(calibration.Author), "User-defined calibration needs attribution.");
        if (calibration.Status is CalibrationInformationStatus.Unknown or CalibrationInformationStatus.Unavailable)
            Require(parameters.Length == 0, "Unknown/unavailable calibration cannot supply invented parameters.");
        InstrumentCalibration.ValidateAdjustment(calibration);
    }

    private static void Configurations(EffectiveInstrumentConfiguration[] configurations)
    {
        Items(configurations, 256); Unique(configurations.Select(c => c.Id));
        foreach (var configuration in configurations)
        {
            Id(configuration.Id); Require(configuration.Revision > 0, "Configuration revision must be positive."); Utc(configuration.EffectiveUtc);
            ArgumentNullException.ThrowIfNull(configuration.Settings); _ = ScienceCatalog.Normalize([configuration.Settings]);
            if (configuration.Calibration is { } calibration)
            {
                Validate(calibration); Require(calibration.InstrumentId == configuration.Settings.InstrumentId, "Calibration belongs to another instrument.");
                if (configuration.Settings.Calibration is {} selected)
                    Require(ContentHash(calibration) == ContentHash(selected), "Effective calibration differs from the complete selected settings.");
            }
            if (configuration.OperatingPolicy is { } policy) Reference(policy);
            if (configuration.Specification is { } specification)
            { Validate(specification); Require(specification.Definition.Id == configuration.Settings.InstrumentId, "Instrument specification belongs to another instrument."); }
        }
        Unique(configurations.Select(c => (c.Settings.InstrumentId, c.Revision)));
        foreach (var group in configurations.GroupBy(c => c.Settings.InstrumentId))
        {
            var ordered = group.OrderBy(c => c.Revision).ToArray();
            for (var i = 1; i < ordered.Length; i++) Require(ordered[i].EffectiveUtc >= ordered[i - 1].EffectiveUtc, "Configuration revisions cannot reverse their effective timeline.");
        }
    }

    private static void ValidateOperating(OperatingAssessment assessment)
    {
        Id(assessment.MissionId); OptionalId(assessment.SessionId); Id(assessment.ConfigurationId); Utc(assessment.AssessedUtc); Links(assessment.Evidence);
        foreach (var rule in Items(assessment.Rules, 128))
        {
            Reference(rule.Rule); Text(rule.Parameter); Text(rule.Unit); if (rule.ObservedValue is { } value) Finite(value);
            Known(rule.Compliance); Known(rule.DerivationValidity); Known(rule.SourceQuality); Texts(rule.Reasons); Texts(rule.AffectedDerivations);
            Require(rule.ObservedValue.HasValue || rule.Compliance is not AssessmentStatus.Supported, "Absent observations cannot establish supported operating compliance.");
        }
        Unique(assessment.Rules.Select(r => r.Rule.Id));
    }

    private static void ValidateRubric(GradingRubric rubric)
    {
        Text(rubric.Id); Require(rubric.Revision > 0, "Rubric revision must be positive."); Known(rubric.Template); Texts(rubric.Limitations);
        var categories = Items(rubric.Categories, 4); Require(categories.Length == 4, "Rubrics require all four mission-performance categories.");
        Unique(categories.Select(c => c.Category));
        foreach (var category in categories) { Known(category.Category); Require(category.Category != RubricCategory.Unknown, "Unknown grading category."); Range(category.Weight, double.Epsilon, 100); }
        Require(Math.Abs(categories.Sum(c => c.Weight) - 100) < 1e-9, "Category weights must total 100 percent.");
        var criteria = Items(rubric.Criteria, 128); Require(criteria.Length > 0, "A rubric requires explicit criteria."); Unique(criteria.Select(c => c.Id));
        foreach (var criterion in criteria)
        {
            Text(criterion.Id); Require(categories.Any(c => c.Category == criterion.Category), "Criterion category is not in the rubric.");
            Range(criterion.Weight, double.Epsilon, 1); Texts(criterion.RequiredEvidenceClasses); Text(criterion.Method);
            Require(criterion.RequiredEvidenceClasses.Length > 0, "Declare required evidence classes explicitly.");
        }
        foreach (var category in categories)
            Require(Math.Abs(criteria.Where(c => c.Category == category.Category).Sum(c => c.Weight) - 1) < 1e-9, "Criterion weights within each category must total one.");
        ArgumentNullException.ThrowIfNull(rubric.Thresholds);
        foreach (var threshold in new[] { rubric.Thresholds.A, rubric.Thresholds.B, rubric.Thresholds.C, rubric.Thresholds.D }) Range(threshold, 0, 100);
        Require(rubric.Thresholds.A > rubric.Thresholds.B && rubric.Thresholds.B > rubric.Thresholds.C && rubric.Thresholds.C > rubric.Thresholds.D, "Grade thresholds must be strictly ordered.");
        Range(rubric.MinimumEvidenceWeightFraction, .8, 1);
        Require(rubric.RequireSingleLetterScoreRange, "Do not hide uncertainty spanning multiple grade bands.");
    }

    private static void ValidateContext(MissionAssessmentContext context)
    {
        Id(context.MissionId); Utc(context.FrozenUtc); Known(context.Mode); Hash(context.PlanSha256); Validate(context.Rubric); Configurations(context.Configurations);
        if(context.Readiness is not null){Validate(context.Readiness);Require(context.Readiness.AssessedUtc<=context.FrozenUtc,"Preflight evidence must precede flight start.");}
        var obligations = Items(context.Obligations, 4096); Unique(obligations.Select(o => o.Id));
        foreach (var obligation in obligations)
        {
            Id(obligation.Id); Require(context.Rubric.Criteria.Any(c => c.Id == obligation.CriterionId), "Obligation references an unknown criterion.");
            Range(obligation.Weight, double.Epsilon, 1); OptionalText(obligation.PlannedSessionKey); OptionalId(obligation.WaypointId); OptionalId(obligation.SurveyAreaId);
            OptionalUtc(obligation.WindowStartUtc); OptionalUtc(obligation.WindowEndUtc);
            Require(obligation.WindowStartUtc is null || obligation.WindowEndUtc is null || obligation.WindowEndUtc >= obligation.WindowStartUtc, "Obligation window is reversed.");
            if (obligation.TargetAmount is { } target) { Range(target, double.Epsilon, double.MaxValue); Text(obligation.Unit); }
            else Require(obligation.Unit is null, "A target unit needs a target amount.");
            OptionalText(obligation.RepeatRuleId); OptionalText(obligation.Scope); if(obligation.InstrumentId is not null)Instrument(obligation.InstrumentId); Require(obligation.RegionIndex is null||obligation.RegionIndex>=0,"Invalid region index."); if(obligation.TargetAltitudeFeet is {} altitude)Require(double.IsFinite(altitude),"Finite MSL target required.");
        }
        // Applicability and evidence sufficiency are decided by P08/P09, never guessed here.
    }

    private static void ValidateGrade(MissionGrade grade)
    {
        Id(grade.MissionId); Utc(grade.AsOfUtc); Known(grade.Status);
        if (grade.RubricSha256 is not null) Hash(grade.RubricSha256);
        if (grade.EvidenceSha256 is not null) Hash(grade.EvidenceSha256);
        if (grade.ScoreLower is { } low) Range(low, 0, 100); if (grade.ScoreUpper is { } high) Range(high, 0, 100);
        Require(grade.ScoreLower.HasValue == grade.ScoreUpper.HasValue && !(grade.ScoreLower > grade.ScoreUpper), "Score bounds must be paired and ordered.");
        if (grade.EvidenceWeightFraction is { } fraction) Range(fraction, 0, 1);
        if (grade.Letter is { } letter) Known(letter);
        if (grade.Status is MissionGradeStatus.NotGraded or MissionGradeStatus.InsufficientEvidence or MissionGradeStatus.Unsupported)
            Require(grade.Letter is null && grade.ScoreLower is null, "Ungraded/unsupported/insufficient records cannot claim a score or grade.");
        if (grade.Status == MissionGradeStatus.Provisional) Require(grade.Letter is null, "A provisional score is not a final letter grade.");
        if (grade.Status is MissionGradeStatus.Final or MissionGradeStatus.Revised)
            Require(grade.RubricSha256 is not null && grade.EvidenceSha256 is not null && grade.ScoreLower.HasValue
                && grade.Letter.HasValue && grade.EvidenceWeightFraction is >= .8, "A final grade needs versioned evidence identity, score bounds, letter and evidence sufficiency.");
        Results(grade.Criteria ?? []);
        var sessions = Items(grade.Sessions ?? [], 4096); Unique(sessions.Select(s => s.SessionId));
        foreach (var session in sessions) { Id(session.SessionId); Results(session.Criteria); Metrics(session.Metrics); Texts(session.Limitations); }
        Texts(grade.Limitations ?? []);
        // Structural validation cannot prove archived evidence or calculate eligibility/letters (P09).
    }

    private static void Results(GradingCriterionResult[] results)
    {
        Items(results, 4096); Unique(results.Select(r => r.ObligationId));
        foreach (var result in results)
        {
            Id(result.ObligationId); Text(result.CriterionId); Known(result.Status);
            if (result.Score is { } score)
            {
                Range(score, 0, 100);
                Require(result.Status is AssessmentStatus.Supported or AssessmentStatus.Caution or AssessmentStatus.Invalid, "Unassessed criteria cannot claim a score.");
            }
            Texts(result.Reasons); Texts(result.Recommendations);
        }
    }

    private static void Metrics(ScienceMetric[] metrics)
    {
        Items(metrics, 128); Unique(metrics.Select(m => m.Id));
        foreach (var metric in metrics)
        {
            Text(metric.Id); Known(metric.Status); Known(metric.Origin); Text(metric.Unit); Text(metric.Method); Texts(metric.Limitations);
            Require(metric.Missing >= 0 && metric.Unassessed >= 0, "Evidence counts cannot be negative.");
            if (metric.Numerator is { } numerator) Range(numerator, 0, double.MaxValue);
            if (metric.Denominator is { } denominator) Range(denominator, double.Epsilon, double.MaxValue);
            Require(metric.Denominator is null || metric.Numerator.HasValue, "A denominator needs an assessed numerator.");
            Require(metric.Denominator is null || metric.Numerator <= metric.Denominator, "A scoped fraction cannot exceed its denominator.");
            if (metric.Status == AssessmentStatus.Supported) Require(metric.Numerator.HasValue, "An assessed supported metric requires a numeric result.");
            if (metric.Status is AssessmentStatus.Unknown or AssessmentStatus.NotAssessed or AssessmentStatus.Unavailable or AssessmentStatus.Missing or AssessmentStatus.Unsupported)
                Require(metric.Numerator is null && metric.Denominator is null, "Unknown/unassessed metrics cannot claim numeric results.");
        }
    }

    private static void Links(EvidenceLink[] links)
    {
        Items(links, 4096); Unique(links.Select(l => l.Id));
        foreach (var link in links) { Text(link.Id); Hash(link.Sha256); Utc(link.ObservedUtc); Known(link.Origin); OptionalId(link.SourceId); OptionalUtc(link.SourceObservationUtc); }
    }
    private static void Reference(EvidenceReference reference) { ArgumentNullException.ThrowIfNull(reference); Text(reference.Id); Require(reference.Revision > 0, "Reference revision must be positive."); Hash(reference.Sha256); }
    private static void Instrument(string id) { Text(id); Require(ScienceCatalog.Definitions.Any(d => d.Id == id), "Unknown instrument; preserve unsupported evidence without applying it."); }
    private static void Hash(string? hash) => Require(hash is { Length: 64 } && hash.All(Uri.IsHexDigit), "A SHA-256 identity must contain 64 hex digits.");
    private static void Id(Guid id) => Require(id != Guid.Empty, "An explicit nonempty stable identifier is required.");
    private static void OptionalId(Guid? id) { if (id.HasValue) Id(id.Value); }
    private static void Utc(DateTimeOffset utc) => Require(utc != default && utc.Offset == TimeSpan.Zero, "An explicit UTC timestamp is required.");
    private static void OptionalUtc(DateTimeOffset? utc) { if (utc.HasValue) Utc(utc.Value); }
    private static void Text(string? text) => Require(!string.IsNullOrWhiteSpace(MissionPlanning.Text(text, 4000)), "Nonempty bounded text is required.");
    private static void OptionalText(string? text) { if (text is not null) Text(text); }
    private static void Texts(string[] texts) { foreach (var text in Items(texts, 256)) Text(text); }
    private static void Finite(double value) => Require(double.IsFinite(value), "Numbers must be finite.");
    private static void Range(double value, double minimum, double maximum) { Finite(value); Require(value >= minimum && value <= maximum, "Number is outside contract bounds."); }
    private static void Known<T>(T value) where T : struct, Enum => Require(Enum.IsDefined(value), "Unsupported enum value; do not reinterpret it as success.");
    private static T[] Items<T>(T[]? items, int maximum)
    { Require(items is not null && items.Length <= maximum && items.All(i => i is not null), "A bounded non-null collection is required."); return items!; }
    private static void Unique<T>(IEnumerable<T> values) => Require(values.Distinct().Count() == values.Count(), "Stable identifiers must be unique within their scope.");
    private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }

    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException("Duplicate evidence property."); CheckDuplicateProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckDuplicateProperties(item);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else element.WriteTo(writer);
    }
}
