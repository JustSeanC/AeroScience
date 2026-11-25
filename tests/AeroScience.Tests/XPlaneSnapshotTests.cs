using System.Text;
using AeroScience.Shared;
using AeroScience.XPlane;
using Xunit;

namespace AeroScience.Tests;

public sealed class XPlaneSnapshotTests
{
    [Fact]
    public void CompleteCompositeNormalizesUnitsWithoutChangingTheCoreValidityContract()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(); f.Confirm();
        var update = Assert.IsType<TelemetryUpdate>(f.Assembler.Snapshot());
        var frame = Assert.IsType<TelemetryFrame>(update.Frame);
        Assert.True(frame.IsValid); Assert.Equal(ConnectionState.Connected, update.Connection.State);
        Assert.Equal("Aéro Cessna 172", frame.Aircraft);
        Assert.Equal(34, frame.Latitude); Assert.Equal(-117, frame.Longitude);
        Assert.Equal(1000 / .3048, frame.AltitudeMslFeet, 9);
        Assert.Equal(100 / .3048, frame.AltitudeAglFeet!.Value, 9);
        Assert.Equal(120, frame.IndicatedAirspeedKnots); // This dataref already reports knots.
        Assert.Equal(100 * 3600d / 1852, frame.TrueAirspeedKnots, 9);
        Assert.Equal(90 * 3600d / 1852, frame.GroundspeedKnots, 9);
        Assert.Equal(350, frame.HeadingDegrees); Assert.Equal(10, frame.GroundTrackDegrees);
        Assert.Equal(5, frame.PitchDegrees); Assert.Equal(-15, frame.BankDegrees);
        Assert.Equal(2 * 60 / .3048, frame.VerticalSpeedFeetPerMinute, 9);
        Assert.Equal(.4, frame.MachNumber);
        var weather = Assert.IsType<AtmosphericFrame>(update.Atmosphere);
        Assert.Equal(15, weather.TemperatureCelsius);
        Assert.Equal(101325 / 3386.389, weather.PressureInHg!.Value, 9);
        Assert.Equal(10 * 3600d / 1852, weather.WindSpeedKnots!.Value, 9);
        Assert.Equal(270, weather.WindDirectionTrueDegrees);
        Assert.Equal(1.225 * .028316846592 / 14.5939029372064, weather.DensitySlugsPerCubicFoot!.Value, 12);
        var context = Assert.IsType<SimulatorSampleContext>(update.SampleContext);
        Assert.Equal(frame.TimestampUtc, context.HostReceivedUtc);
        Assert.Equal(TimeSpan.Zero, frame.TimestampUtc.Offset);
        Assert.Null(context.Clock!.Year); Assert.Equal(36000, context.Clock.ZuluSeconds);
        Assert.False(context.Capabilities.ProductionRecordingReady); Assert.False(context.Capabilities.ScienceIntegrationReady);
        Assert.Contains("humidity-at-aircraft", context.Capabilities.UnavailableChannels);
        Assert.Equal("xplane12", update.Connection.Origin!.SimulatorId);
        Assert.Equal(124001, update.Connection.Origin.ReportedBuild);
        Assert.DoesNotContain("Aircraft/Fixture", System.Text.Json.JsonSerializer.Serialize(update));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void InitialSeedAndMatchingAcknowledgementAreBothRequiredInEitherOrder(bool seedFirst)
    {
        var f = new XPlaneAssemblerFixture();
        f.Assembler.Ingest(f.Epoch, XPlaneFixture.Ack(999));
        Assert.False(f.Assembler.Ready.IsCompleted);
        f.Assembler.Ingest(f.Epoch, seedFirst ? f.Api.Seed() : XPlaneFixture.Ack(1));
        Assert.False(f.Assembler.Ready.IsCompleted);
        f.Assembler.Ingest(f.Epoch, seedFirst ? XPlaneFixture.Ack(1) : f.Api.Seed());
        Assert.True(f.Assembler.Ready.IsCompletedSuccessfully);
        Assert.Null(f.Assembler.Snapshot()!.Frame); // A subscription seed alone is not a confirmed measurement.
        f.Confirm(); Assert.NotNull(f.Assembler.Snapshot()!.Frame);
    }

    [Fact]
    public void MissingInitialCoreFieldDoesNotBecomeReadyFromLaterHttpConfirmation()
    {
        var f = new XPlaneAssemblerFixture(); f.Api.Missing.Add("latitude");
        f.Seed(); Assert.False(f.Assembler.Ready.IsCompleted);
        f.Api.Missing.Clear(); f.Confirm();
        Assert.Equal("WaitingForInitialSnapshot", f.Assembler.Snapshot()!.Connection.Message);
    }

    [Theory]
    [InlineData("latitude", "91")] [InlineData("longitude", "-181")]
    [InlineData("latitude", "1e999")] [InlineData("msl", "null")]
    [InlineData("groundspeed", "-1")] [InlineData("ias", "\"120\"")]
    [InlineData("pitch", "91")] [InlineData("bank", "181")]
    [InlineData("ias", "1e100")]
    [InlineData("paused", "2")] [InlineData("replay", "0.5")]
    [InlineData("running-seconds", "-1")] [InlineData("aircraft", "\"not base64\"")]
    [InlineData("aircraft", "\"wyg=\"")] [InlineData("latitude", "[34]")]
    public void InvalidMandatoryMeasurementsNeverBecomeZeroOrEligibleFrames(string key, string raw)
    {
        var f = new XPlaneAssemblerFixture(); f.Api.RawOverrides[key] = raw;
        f.Seed(); f.Confirm();
        var update = f.Assembler.Snapshot()!;
        Assert.Null(update.Frame); Assert.Equal(ConnectionState.Initializing, update.Connection.State);
    }

    [Theory]
    [InlineData("agl", "null")] [InlineData("agl", "-1")]
    [InlineData("mach", "1e999")] [InlineData("temperature", "-274")]
    [InlineData("pressure", "0")] [InlineData("density", "-1")]
    [InlineData("wind-direction", "361")] [InlineData("wind-speed", "[1,2]")]
    public void InvalidOptionalMeasurementsDoNotInvalidateSoundPosition(string key, string raw)
    {
        var f = new XPlaneAssemblerFixture(); f.Api.RawOverrides[key] = raw; f.Seed(); f.Confirm();
        var update = f.Assembler.Snapshot()!;
        Assert.True(update.Frame!.IsValid);
        Assert.Contains(key, update.SampleContext!.Capabilities.UnavailableChannels);
        if (key == "agl") Assert.Null(update.Frame.AltitudeAglFeet); // Never substitutes MSL.
    }

    [Fact]
    public void StationaryConstantValuesRemainValidOnlyWithRealConfirmationAndClockProgress()
    {
        var f = new XPlaneAssemblerFixture(); f.Api.Overrides["groundspeed"] = 0;
        f.Seed(); f.Confirm(); var first = f.Assembler.Snapshot()!;
        Assert.Equal(0, first.Frame!.GroundspeedKnots);
        var timing = first.SampleContext!.Fields.Single(x => x.Dataref.EndsWith("/latitude"));
        f.Time.Advance(TimeSpan.FromMilliseconds(500)); f.Confirm(2);
        var second = f.Assembler.Snapshot()!;
        var next = second.SampleContext!.Fields.Single(x => x.Dataref.EndsWith("/latitude"));
        Assert.Equal(timing.LastChangedUtc, next.LastChangedUtc);
        Assert.True(next.LastReceivedUtc > timing.LastReceivedUtc);
        Assert.True(next.LastConfirmedUtc > timing.LastConfirmedUtc);
        Assert.True(second.Frame!.TimestampUtc > first.Frame.TimestampUtc);
        Assert.Null(f.Assembler.Snapshot());
        f.Assembler.Ingest(f.Epoch, f.Api.Update("latitude", 34));
        Assert.Null(f.Assembler.Snapshot()); // Neither heartbeat nor repeated consumer reads mint a frame.
    }

    [Fact]
    public void FreshSubscriptionReceiptsDoNotReviveExpiredConfirmations()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(); f.Confirm(); Assert.NotNull(f.Assembler.Snapshot()!.Frame);
        f.Time.Advance(TimeSpan.FromSeconds(3)); f.Assembler.Ingest(f.Epoch, f.Api.Seed(2));
        Assert.Equal("ConfirmationStale", f.Assembler.Snapshot()!.Connection.Message);
        Assert.Throws<XPlaneProtocolException>(() => f.Confirm(3)); // Requalify through a new epoch.
    }

    [Fact]
    public void MixedAgeCoreConfirmationIsRejectedWithoutClaimingAnAtomicSnapshot()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(); f.Confirm(perField: TimeSpan.FromMilliseconds(40));
        Assert.Equal("SnapshotSpanExceeded", f.Assembler.Snapshot()!.Connection.Message);
    }

    [Fact]
    public void OptionalAtmosphereOutsideThePositionWindowIsUnavailableAndCannotRedateThePose()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed();
        var start = f.Time.GetTimestamp(); var utc = f.Time.GetUtcNow();
        var values = f.Api.Discovery.Found.OrderByDescending(d => d.Definition.Required).Select(d =>
        {
            f.Time.Advance(TimeSpan.FromMilliseconds(d.Definition.Required ? 1 : 50));
            return new XPlaneValueConfirmation(d.Id, f.Api.Value(d.Definition.Key, 1), f.Time.GetUtcNow(), f.Time.GetTimestamp());
        }).ToArray();
        f.Assembler.Confirm(f.Epoch, new(utc, start, f.Time.GetUtcNow(), f.Time.GetTimestamp(), values));
        var update = f.Assembler.Snapshot()!; Assert.NotNull(update.Frame);
        Assert.Null(update.Atmosphere!.DensitySlugsPerCubicFoot);
        Assert.Contains("density", update.SampleContext!.Capabilities.UnavailableChannels);
        Assert.InRange((update.Atmosphere.TimestampUtc - update.Frame.TimestampUtc).TotalMilliseconds, 0, 500);
        Assert.Equal(update.SampleContext.HostReceivedUtc, update.Frame.TimestampUtc);
        Assert.True(update.SampleContext.ConfirmationCompletedUtc > update.Frame.TimestampUtc);
    }

    [Fact]
    public void UnknownOptionalChannelsCannotBeInventedByDerivationOrArrayExtraction()
    {
        var f = new XPlaneAssemblerFixture();
        foreach (var key in new[] { "temperature", "pressure", "wind-speed", "wind-direction", "density" }) f.Api.RawOverrides[key] = "null";
        f.Seed(); f.Confirm(); var update = f.Assembler.Snapshot()!;
        Assert.Equal(ObservationQuality.Unavailable, update.Atmosphere!.Quality);
        Assert.Null(update.Environment); Assert.Null(update.Guidance);
        Assert.Null(update.Frame!.Environment); Assert.Null(update.Frame.Guidance);
        Assert.All(new[] { "humidity-at-aircraft", "in-cloud", "cloud-density", "precipitation", "wind-vectors", "ambient-visibility", "detailed-guidance" },
            key => Assert.Contains(key, update.SampleContext!.Capabilities.UnavailableChannels));
    }

    [Theory]
    [InlineData("\"\"")] [InlineData("\"AA==\"")] [InlineData("\"QQBC\"")]
    public void EmptyOrEmbeddedControlAircraftIdentitiesRemainInvalid(string raw)
    {
        var f = new XPlaneAssemblerFixture(); f.Api.RawOverrides["aircraft"] = raw; f.Seed(); f.Confirm();
        Assert.Null(f.Assembler.Snapshot()!.Frame);
    }

    [Fact]
    public void OutdatedAndFailedAcknowledgementsCannotUnlockTheInitialSnapshot()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(acknowledge: false);
        Assert.False(f.Assembler.Ingest(f.Epoch, XPlaneFixture.Ack(2)));
        Assert.False(f.Assembler.Ready.IsCompleted);
        Assert.Equal("SubscriptionRejected", Assert.Throws<XPlaneProtocolException>(() => f.Assembler.Ingest(f.Epoch, XPlaneFixture.Ack(1, false))).Code);
        Assert.False(f.Assembler.Ready.IsCompleted);
    }

    [Fact]
    public void UnpausedFrozenClockNeverProvesLoadedLiveFlight()
    {
        var f = new XPlaneAssemblerFixture(); f.Api.FreezeClocks = true; f.Seed(); f.Confirm();
        Assert.Equal("FlightProgressUnverified", f.Assembler.Snapshot()!.Connection.Message);
    }

    [Fact]
    public void PauseIsExplicitAndResumeNeedsClockProgress()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(); f.Confirm(); Assert.False(f.Assembler.Snapshot()!.Frame!.IsPaused);
        f.Api.Overrides["paused"] = 1; f.Confirm(1); var paused = f.Assembler.Snapshot()!;
        Assert.True(paused.Frame!.IsPaused); Assert.Equal(ObservationQuality.Paused, paused.Atmosphere!.Quality);
        f.Api.Overrides["paused"] = 0; f.Confirm(1);
        Assert.Equal("FlightProgressUnverified", f.Assembler.Snapshot()!.Connection.Message);
        f.Confirm(2); Assert.False(f.Assembler.Snapshot()!.Frame!.IsPaused);
    }

    [Fact]
    public void NewerPauseDeltaPreventsPublishingAnOlderUnpausedConfirmation()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(); f.Confirm();
        f.Time.Advance(TimeSpan.FromMilliseconds(1));
        f.Assembler.Ingest(f.Epoch, f.Api.Update("paused", 1));
        var update = f.Assembler.Snapshot()!;
        Assert.Null(update.Frame); Assert.Equal("PauseChangeAwaitingConfirmation", update.Connection.Message);
        f.Api.Overrides["paused"] = 1; f.Confirm(1);
        Assert.True(f.Assembler.Snapshot()!.Frame!.IsPaused);
    }

    [Theory]
    [InlineData("running-seconds", 0, "SimulatorClockReset")]
    [InlineData("replay", 1, "ReplayTransition")]
    [InlineData("distance-rate", 2, "AccelerationTransition")]
    public void LiveDiscontinuitiesAreRejectedImmediatelyBeforeAnOlderGetCanPublish(string key, int value, string code)
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(); f.Confirm();
        f.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(code, Assert.Throws<XPlaneProtocolException>(() => f.Assembler.Ingest(f.Epoch, f.Api.Update(key, value))).Code);
    }

    [Fact]
    public void LateAssembledReadDoesNotMoveTheLatestDeltaReceiptOrChangeTimeBackwards()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed();
        var start = f.Time.GetTimestamp(); var utc = f.Time.GetUtcNow();
        var values = f.Api.Discovery.Found.Select(d =>
        {
            f.Time.Advance(TimeSpan.FromMilliseconds(1));
            return new XPlaneValueConfirmation(d.Id, f.Api.Value(d.Definition.Key, 1), f.Time.GetUtcNow(), f.Time.GetTimestamp());
        }).ToArray();
        f.Time.Advance(TimeSpan.FromMilliseconds(10)); var deltaTime = f.Time.GetUtcNow();
        f.Assembler.Ingest(f.Epoch, f.Api.Update("latitude", 34.001));
        f.Assembler.Confirm(f.Epoch, new(utc, start, f.Time.GetUtcNow(), f.Time.GetTimestamp(), values));
        var update = f.Assembler.Snapshot()!;
        Assert.Equal(34, update.Frame!.Latitude); // Explicitly confirmed composite, not a claimed atomic latest packet.
        var timing = update.SampleContext!.Fields.Single(d => d.Dataref.EndsWith("/latitude"));
        Assert.Equal(deltaTime, timing.LastChangedUtc); Assert.Equal(deltaTime, timing.LastReceivedUtc);
        Assert.True(timing.LastConfirmedUtc < timing.LastReceivedUtc);
    }

    [Theory]
    [InlineData("running-seconds", 1, "SimulatorClockReset")]
    [InlineData("flight-seconds", 0, "SimulatorClockReset")]
    [InlineData("replay", 1, "ReplayTransition")]
    [InlineData("requested-rate", 2, "AccelerationTransition")]
    [InlineData("distance-rate", 2, "AccelerationTransition")]
    public void DiscontinuousClockOrModeStartsNewEpochRatherThanBridgingObservations(string key, int value, string code)
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(); f.Confirm(); Assert.NotNull(f.Assembler.Snapshot()!.Frame);
        f.Api.Overrides[key] = value;
        Assert.Equal(code, Assert.Throws<XPlaneProtocolException>(() => f.Confirm(2)).Code);
    }

    [Theory]
    [InlineData("aircraft", "Other aircraft", "AircraftChanged")]
    [InlineData("aircraft-token", "Aircraft/Other.acf", "AircraftReloadCandidate")]
    public void AircraftIdentityChangesInvalidateTheCurrentEpoch(string key, string value, string code)
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(); f.Confirm(); Assert.NotNull(f.Assembler.Snapshot()!.Frame);
        Assert.Equal(code, Assert.Throws<XPlaneProtocolException>(() => f.Assembler.Ingest(f.Epoch,
            f.Api.Update(key, Convert.ToBase64String(Encoding.UTF8.GetBytes(value))))).Code);
    }

    [Fact]
    public void PositionJumpIsACandidateDiscontinuityNotFabricatedContinuousFlight()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(); f.Confirm(); f.Assembler.Snapshot();
        f.Api.Overrides["latitude"] = 50; f.Confirm(2);
        Assert.Equal("PositionDiscontinuityCandidate", Assert.Throws<XPlaneProtocolException>(() => f.Assembler.Snapshot()).Code);
    }

    [Fact]
    public void OldEpochTrafficIsIgnoredAndUnknownCurrentIdsAreRejectedWithoutCacheGrowth()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed();
        Assert.False(f.Assembler.Ingest(Guid.NewGuid(), "invalid"u8.ToArray()));
        Assert.False(f.Assembler.Confirm(Guid.NewGuid(), new(default, 0, default, 0, [])));
        var count = f.Assembler.CachedFieldCount;
        Assert.Equal("UnexpectedUpdateId", Assert.Throws<XPlaneProtocolException>(() => f.Assembler.Ingest(f.Epoch,
            XPlaneFixture.Bytes(new { type = "dataref_update_values", data = new Dictionary<string, int> { ["1"] = 10 } }))).Code);
        Assert.Equal(count, f.Assembler.CachedFieldCount); Assert.Equal(f.Api.Refs.Count, count);
    }
}
