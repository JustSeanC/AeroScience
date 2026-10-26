using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

public sealed class PersonalResearchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-personal-" + Guid.NewGuid());
    private readonly DateTimeOffset at = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
    private static readonly FlightRegimeDefinition Rule = new(120, 5, 3000, 100, 3);
    private readonly MissionStore store;
    public PersonalResearchTests() { store = new(Path.Combine(root, "missions.db")); store.Initialize(); }
    private TelemetryFrame Frame(double second, double speed = 120) => new(at.AddSeconds(second), TelemetrySource.Simulator,
        "Fixture aircraft", 40, -75, 3000, null, speed, 160, 150, 0, 0, 0, 0, 0);
    private Guid Flight(PersonalResearchCard card, int seconds = 5, double speed = 120)
    {
        var plan = store.CreateResearchPlan(card.Id, card.Revision);
        var flight = store.Create("Recorded test fixture", Frame(0), at, plan:plan);
        for (var i = 0; i <= seconds; i++) store.Append(flight, Frame(i, speed));
        store.Finish(flight, MissionStatus.Completed, at.AddSeconds(seconds), "Fixture end"); return flight;
    }
    private ResearchAttemptRequest Request(Guid flight, double end = 5) => new(Guid.NewGuid(), flight, at, at.AddSeconds(end));

    [Theory]
    [InlineData("valid", ResearchAttemptOutcome.Pass)]
    [InlineData("boundary", ResearchAttemptOutcome.Pass)]
    [InlineData("speed", ResearchAttemptOutcome.Fail)]
    [InlineData("altitude", ResearchAttemptOutcome.Fail)]
    [InlineData("short", ResearchAttemptOutcome.InsufficientEvidence)]
    [InlineData("gap", ResearchAttemptOutcome.InsufficientEvidence)]
    [InlineData("paused", ResearchAttemptOutcome.InsufficientEvidence)]
    [InlineData("invalid", ResearchAttemptOutcome.InsufficientEvidence)]
    [InlineData("backwards", ResearchAttemptOutcome.InsufficientEvidence)]
    [InlineData("source", ResearchAttemptOutcome.InsufficientEvidence)]
    [InlineData("aircraft", ResearchAttemptOutcome.InsufficientEvidence)]
    [InlineData("disjoint", ResearchAttemptOutcome.Fail)]
    public void ContinuousHoldUsesActualIasMslAndCadenceWithoutInventingEvidence(string scenario, ResearchAttemptOutcome expected)
    {
        var window = new FlightRegimeEvaluation.Window(Rule, at, at.AddSeconds(4));
        for (var i = 0; i <= 4; i++)
        {
            if (scenario == "short" && i > 1 || scenario == "gap" && i == 2) continue;
            var f = Frame(i, scenario == "speed" ? 130 : scenario == "boundary" ? 125 : 120);
            f = f with { AltitudeMslFeet = scenario == "altitude" ? 3150 : scenario == "boundary" ? 3100 : 3000 };
            if (i == 2) f = scenario switch {
                "paused" => f with { IsPaused = true }, "invalid" => f with { IndicatedAirspeedKnots = double.NaN },
                "backwards" => f with { TimestampUtc = at }, "source" => f with { Source = TelemetrySource.Simulated },
                "aircraft" => f with { Aircraft = "Changed aircraft" }, "disjoint" => f with { IndicatedAirspeedKnots = 130 }, _ => f };
            window.Add(f);
        }
        Assert.Equal(expected, window.Finish(false).Outcome);
    }

    [Theory]
    [InlineData(double.NaN, 3)][InlineData(0, 3)][InlineData(120, 0)][InlineData(120, double.PositiveInfinity)]
    public void UnsupportedRulesAreRejected(double speed, double duration) =>
        Assert.Throws<ArgumentException>(() => FlightRegimeEvaluation.Validate(Rule with { AirspeedKnots = speed, HoldSeconds = duration }));

    [Fact]
    public void CardRevisionsPlanSnapshotAttemptHashAndRestartRetainOriginalEvidenceWithoutCopyingRawTelemetry()
    {
        var card = store.SaveResearchCard(new("My first experiment", Rule)); var flight = Flight(card);
        var raw = store.StreamSamples(flight).ToArray(); var research = store.Research(flight);
        var changed = store.SaveResearchCard(new(card.Name, Rule with { AirspeedKnots = 150 }, card.Id, card.Revision));
        Assert.Equal(2, changed.Revision); Assert.Equal(card, store.ResearchCard(card.Id, 1));
        Assert.Throws<InvalidOperationException>(() => store.SaveResearchCard(new(card.Name, Rule, card.Id, 1)));
        var request = Request(flight); var result = store.EvaluateResearchAttempt(request);
        Assert.Equal(ResearchAttemptOutcome.Pass, result.Outcome); Assert.Equal(card, result.Card);
        Assert.Equal(6, result.Evidence.SampleCount); Assert.Equal(64, result.Evidence.Sha256.Length);
        Assert.Equal(PreflightReadiness.Hash(research.PlanSnapshot!), result.Evidence.PlanSha256);
        Assert.Equal(result, store.EvaluateResearchAttempt(request)); Assert.Single(store.ResearchAttempts(card.Id));
        Assert.Throws<InvalidOperationException>(() => store.EvaluateResearchAttempt(request with { EndUtc = at.AddSeconds(4) }));
        var reopened = new MissionStore(store.DatabasePath); reopened.Initialize();
        Assert.Equal(result, Assert.Single(reopened.ResearchAttempts(flightId:flight))); Assert.Equal(raw, reopened.StreamSamples(flight));
        Assert.Equal(flight, Assert.Single(reopened.ResearchFlights(card.Id)).Id);
        var repeated = reopened.EvaluateResearchAttempt(request with { RequestId = Guid.NewGuid() });
        Assert.Equal(result.Evidence, repeated.Evidence);
    }

    [Fact]
    public void PausedSimulatorFramesSkippedByRecorderStillBreakASubsecondPause()
    {
        var card = store.SaveResearchCard(new("Pause test", Rule with { HoldSeconds = 4 }));
        var plan = store.CreateResearchPlan(card.Id, card.Revision); var coordinator = new MissionCoordinator(store, 250, utcNow:() => at);
        var flight = coordinator.Start(new("Pause flight", PlanId:plan.Id));
        for (var i = 0; i <= 16; i++)
        {
            var f = Frame(i / 4d) with { IsPaused = i == 2 };
            coordinator.Apply(new(new(ConnectionState.Connected, f.Source, "Fixture", f.TimestampUtc), f));
        }
        coordinator.End(flight.Id);
        Assert.DoesNotContain(store.StreamSamples(flight.Id), f => f.IsPaused);
        Assert.Contains(store.Detail(flight.Id)!.Events, e => e.Kind == "simulator-paused");
        var end = store.Detail(flight.Id)!.Mission.EndedUtc!.Value;
        var result = store.EvaluateResearchAttempt(new(Guid.NewGuid(), flight.Id, at, end));
        Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence, result.Outcome); Assert.True(result.LongestSupportedHoldSeconds < 4);
    }

    [Fact]
    public void RecordingUnboundFlightsInvalidIntervalsAndMissingTargetsDoNotProduceResults()
    {
        var flight = store.Create("Unbound flight", Frame(0), at);
        Assert.Throws<InvalidOperationException>(() => store.EvaluateResearchAttempt(Request(flight)));
        store.Finish(flight, MissionStatus.Completed, at.AddSeconds(5), "End");
        Assert.Throws<ArgumentException>(() => store.EvaluateResearchAttempt(Request(flight)));
        Assert.Throws<ArgumentException>(() => store.EvaluateResearchAttempt(Request(flight) with { StartUtc = at.AddDays(-1) }));
        Assert.Throws<ArgumentException>(() => store.SavePlan(new(Guid.NewGuid(), "Bad reference", ResearchCard:new(Guid.NewGuid(),1))));
        Assert.Empty(store.ResearchAttempts());
    }

    [Fact]
    public void CancelledEvaluationPublishesNothingAndClearingFlightHistoryRemovesDependentAttempts()
    {
        var card = store.SaveResearchCard(new("Cancellation", Rule)); var flight = Flight(card);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.EvaluateResearchAttempt(Request(flight), cancellation.Token));
        Assert.Empty(store.ResearchAttempts()); Assert.Equal(6, store.StreamSamples(flight).Count());
        store.EvaluateResearchAttempt(Request(flight)); store.ClearFlightData("DELETE FLIGHT DATA");
        Assert.Empty(store.ResearchAttempts()); Assert.Equal(card, store.ResearchCard(card.Id));
    }

    [Fact]
    public void MissingEvidenceAndMarkedRecordingGapCannotCombineShortHolds()
    {
        var card = store.SaveResearchCard(new("Gap event", Rule with { HoldSeconds = 5 })); var flight = Flight(card);
        store.AddEvent(flight, at.AddSeconds(2), "telemetry-gap", "Missing configured recording interval");
        Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence, store.EvaluateResearchAttempt(Request(flight)).Outcome);
        var empty = store.Create("No frames", null, at, plan:store.CreateResearchPlan(card.Id,1));
        store.Finish(empty, MissionStatus.Interrupted, at.AddSeconds(5), "Interrupted without samples");
        Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence, store.EvaluateResearchAttempt(Request(empty)).Outcome);
    }

    [Fact]
    public void CardAndAttemptPagesAreBoundedWithoutALifetimeOwnershipLimit()
    {
        for (var i = 0; i < 25; i++) store.SaveResearchCard(new($"Card {i:00}", Rule));
        Assert.Equal(20, store.ResearchCards().Length); Assert.Equal(5, store.ResearchCards(20).Length);
        var card = store.ResearchCards()[0]; var flight = Flight(card);
        for (var i = 0; i < 25; i++) store.EvaluateResearchAttempt(Request(flight));
        Assert.Equal(20, store.ResearchAttempts(card.Id).Length); Assert.Equal(5, store.ResearchAttempts(card.Id, offset:20).Length);
    }

    [Fact]
    public void SchemaEightUpgradeIsAdditiveWithVerifiedBackupAndTransactionalFailureRecovery()
    {
        var flight = store.Create("Old flight", Frame(0), at); store.Append(flight, Frame(0));
        store.Finish(flight, MissionStatus.Completed, at.AddSeconds(1), "End");
        using (var db = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE personal_card_attempts; DROP TABLE personal_research_state; DROP TABLE personal_flight_cards; DROP TABLE personal_research_attempts; DROP TABLE personal_research_cards; PRAGMA user_version=8"; cmd.ExecuteNonQuery(); }
        var before = StorageManager.DatabaseFingerprint(store.DatabasePath);
        store.EvidenceFault = point => { if (point == "personal-research-before-commit") throw new IOException("Fixture crash"); };
        Assert.Throws<IOException>(store.Initialize); Assert.Equal(before, StorageManager.DatabaseFingerprint(store.DatabasePath));
        store.EvidenceFault = null; store.Initialize(); Assert.Equal(Frame(0), Assert.Single(store.StreamSamples(flight)));
        Assert.Empty(store.ResearchCards()); Assert.Empty(store.ResearchAttempts());
        foreach (var backup in Directory.GetFiles(Path.Combine(root, "Backups"), "missions-schema8-*.db")) Assert.Equal(before, StorageManager.DatabaseFingerprint(backup));
        store.ClearFlightData("DELETE FLIGHT DATA"); Assert.Null(store.Detail(flight));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
