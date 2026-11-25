using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Shared;
using AeroScience.Telemetry;
using AeroScience.XPlane;
using Xunit;

namespace AeroScience.Tests;

public sealed class XPlaneCompatibilityTests
{
    [Fact]
    public void OptionalMetadataPreservesHistoricalJsonAndConstructorShapes()
    {
        const string legacy = "{\"connection\":{\"state\":\"connected\",\"source\":\"simulator\",\"message\":\"legacy\",\"changedUtc\":\"2026-01-01T00:00:00+00:00\"},\"frame\":null,\"atmosphere\":null,\"guidance\":null,\"environment\":null}";
        var original = JsonSerializer.Deserialize<TelemetryUpdate>(legacy, WireJson.Options)!;
        Assert.Null(original.Connection.Origin); Assert.Null(original.SampleContext);
        Assert.Equal(legacy, JsonSerializer.Serialize(original, WireJson.Options));
        var (state, source, message, changedUtc) = original.Connection;
        var (connection, frame, atmosphere, guidance, environment) = original;
        Assert.Equal(ConnectionState.Connected, state); Assert.Equal(TelemetrySource.Simulator, source);
        Assert.Equal("legacy", message); Assert.Equal(changedUtc, connection.ChangedUtc);
        Assert.Null(frame); Assert.Null(atmosphere); Assert.Null(guidance); Assert.Null(environment);
        // Historical generic Simulator records are not retrospectively assigned MSFS or X-Plane identity.
        Assert.Equal(TelemetrySource.Simulator, connection.Source); Assert.Null(connection.Origin);
    }

    [Fact]
    public void CandidateContextRoundTripsWithoutMutatingTheOriginalScientificValues()
    {
        var f = new XPlaneAssemblerFixture(); f.Seed(); f.Confirm(); var update = f.Assembler.Snapshot()!;
        var json = JsonSerializer.Serialize(update, WireJson.Options);
        var restored = JsonSerializer.Deserialize<TelemetryUpdate>(json, WireJson.Options)!;
        Assert.Equal(update.Frame, restored.Frame);
        Assert.Equal(update.Connection, restored.Connection);
        Assert.Equal(update.SampleContext!.ConnectionEpoch, restored.SampleContext!.ConnectionEpoch);
        Assert.Equal(update.SampleContext.Fields, restored.SampleContext.Fields);
        Assert.False(restored.SampleContext.Capabilities.ProductionRecordingReady);
        Assert.False(restored.SampleContext.Capabilities.ScienceIntegrationReady);
    }

    [Fact]
    public void ProviderReusesExistingInterfaceAndMsfsRemainsTheProductionDefault()
    {
        ITelemetryProvider candidate = new XPlaneTelemetryProvider();
        Assert.IsType<XPlaneTelemetryProvider>(candidate); // Construction alone opens no connection.
        var options = new EngineOptions(); Assert.Equal("SimConnect", options.Telemetry);
        options.Telemetry = "XPlane";
        Assert.Throws<InvalidOperationException>(() => options.Validate());
        var valid = new TelemetryFrame(DateTimeOffset.UnixEpoch, TelemetrySource.Simulator, "legacy", 0, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.True(valid.IsValid);
        Assert.False((valid with { Latitude = double.NaN }).IsValid);
        Assert.False((valid with { Aircraft = "" }).IsValid);
        Assert.False((valid with { TimestampUtc = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1)) }).IsValid);
    }
}
