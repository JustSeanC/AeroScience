using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Xunit;

namespace AeroScience.Tests;

// Synthetic arithmetic/store fixtures; not certificates or genuine-source acceptance.
public sealed class CalibrationTests
{
    internal static CalibrationDefinition Definition(double offset = .25, int revision = 1) => new(1,
        "user.synthetic-temperature", revision, "ambient-temperature", CalibrationInformationStatus.UserDefined,
        [new("gain", 1, "1", "Synthetic arithmetic test"), new("offset", offset, "degC", "Synthetic arithmetic test")],
        [], ["Test only; no measurement accuracy established"], "Synthetic test author", DateTimeOffset.UtcNow,
        new(InstrumentCalibration.AffineMethod, "temperature", "degC"));

    [Fact]
    public void LegacyConfigurationsAndCalibrationEvidenceKeepTheirSerializedShape()
    {
        const string json = "{\"instrumentId\":\"ambient-temperature\",\"enabled\":true,\"intervalMs\":1000,\"payload\":null}";
        var configuration = JsonSerializer.Deserialize<InstrumentConfiguration>(json, WireJson.Options)!;
        Assert.Null(configuration.Calibration); Assert.Equal(json, JsonSerializer.Serialize(configuration, WireJson.Options));
        var definition = Definition() with { Adjustment = null };
        var frozen = EvidenceContracts.Snapshot(definition);
        Assert.Null(frozen.Adjustment); Assert.DoesNotContain("adjustment", EvidenceContracts.Write(frozen));
        Assert.Equal(EvidenceContracts.ContentHash(definition), EvidenceContracts.ContentHash(frozen));
    }

    [Fact]
    public void EveryReferenceStatesItsActualLimitationsAndDetachedIdentity()
    {
        foreach (var instrument in ScienceCatalog.Definitions)
        {
            var info = InstrumentCalibration.Describe(instrument.Id);
            Assert.Empty(info.Reference.Parameters); Assert.Null(info.Reference.Adjustment);
            Assert.Contains(info.Reference.Status, new[] { CalibrationInformationStatus.Unknown, CalibrationInformationStatus.Unavailable });
            Assert.NotEmpty(info.Reference.Limitations); Assert.Equal(64, info.ReferenceSha256.Length);
            Assert.Equal(info.ReferenceSha256, EvidenceContracts.ContentHash(info.Reference));
            Assert.Equal(info.SpecificationSha256, EvidenceContracts.ContentHash(info.Specification));
        }
        var first = InstrumentCalibration.Describe("ambient-temperature");
        first.Specification.Definition.Measurements[0] = new("forged", "forged", "K", "forged");
        Assert.Equal("temperature", InstrumentCalibration.Describe("ambient-temperature").Specification.Definition.Measurements[0].Id);
    }

    [Fact]
    public void CalibrationSelectionRejectsForgedReferencesWrongInstrumentsAndUnknownVersions()
    {
        var reference = InstrumentCalibration.Describe("ambient-temperature").Reference;
        Assert.Throws<ArgumentException>(() => InstrumentCalibration.ValidateSelection(reference with { Status = CalibrationInformationStatus.Documented }, reference.InstrumentId));
        Assert.Throws<ArgumentException>(() => InstrumentCalibration.ValidateSelection(Definition(), "ambient-pressure"));
        Assert.Throws<NotSupportedException>(() => InstrumentCalibration.ValidateSelection(Definition() with { Version = 99 }, "ambient-temperature"));
        Assert.Throws<NotSupportedException>(() => InstrumentCalibration.ValidateSelection(Definition() with { Adjustment = new("unknown-method", "temperature", "degC") }, "ambient-temperature"));
        Assert.Throws<ArgumentException>(() => InstrumentCalibration.ValidateSelection(Definition() with { References = ["javascript:alert(1)"] }, "ambient-temperature"));
    }

    [Theory]
    [InlineData("gain", "degC")]
    [InlineData("offset", "K")]
    [InlineData("offset", "1")]
    public void IncompatibleParameterUnitsAreRejected(string id, string unit)
    {
        var definition = Definition();
        definition.Parameters[Array.FindIndex(definition.Parameters, p => p.Id == id)] = new(id, 1, unit, "Synthetic invalid units");
        Assert.Throws<ArgumentException>(() => InstrumentCalibration.ValidateSelection(definition, definition.InstrumentId));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonfiniteParametersNeverEnterSettingsOrEvidence(double value)
    {
        var definition = Definition(value);
        Assert.Throws<ArgumentException>(() => InstrumentCalibration.ValidateSelection(definition, definition.InstrumentId));
    }

    [Fact]
    public void UnsupportedExternalPixelsAndCircularDirectionsCannotBeAdjusted()
    {
        foreach (var instrument in RemoteAcquisition.Instruments.Keys.Append("ambient-wind"))
        {
            var definition = Definition() with { InstrumentId = instrument,
                Adjustment = new(InstrumentCalibration.AffineMethod, instrument == "ambient-wind" ? "wind-direction" : "temperature", "degC") };
            Assert.Throws<ArgumentException>(() => InstrumentCalibration.ValidateSelection(definition, instrument));
        }
        var settings = new InstrumentConfiguration("ambient-temperature", true, Calibration: Definition());
        var external = InstrumentCalibration.Derive(settings, new("temperature", 12, "degC", ScienceProvenance.ExternalDataset), DateTimeOffset.UtcNow);
        Assert.Null(external!.Value); Assert.Equal(ObservationQuality.Invalid, external.Quality);
    }

    [Theory]
    [InlineData(12, 1, .25, 12.25)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(12, 2, -1, 23)]
    public void DerivedValuesAreSeparateAndRetainTheOriginalExactly(double value, double gain, double offset, double expected)
    {
        var definition = Definition(offset); definition.Parameters[0] = definition.Parameters[0] with { Value = gain };
        var configuration = InstrumentCalibration.Freeze(new("ambient-temperature", true, Calibration: definition));
        var original = new ScienceObservation(0, Guid.NewGuid(), Guid.NewGuid(), "ambient-temperature", DateTimeOffset.UtcNow,
            "temperature", value, "degC", ScienceProvenance.SimulatorReported, "Synthetic fixture", 40, -75, 1000, ObservationQuality.Valid);
        var raw = JsonSerializer.Serialize(original, WireJson.Options);
        var result = InstrumentCalibration.Derive(configuration, original)!;
        Assert.Equal(expected, result.Value); Assert.Equal("temperature.user-adjusted", result.MeasurementId);
        Assert.Equal(ScienceProvenance.Derived, result.Provenance); Assert.Equal(value, result.Calibration!.OriginalValue);
        Assert.Equal(EvidenceContracts.ContentHash(configuration.Calibration!), result.Calibration.DefinitionSha256);
        Assert.Equal(original.TimestampUtc, result.TimestampUtc); Assert.Equal(original.DatasetId, result.DatasetId);
        Assert.Equal(raw, JsonSerializer.Serialize(original, WireJson.Options));
        Assert.Null(InstrumentCalibration.Derive(configuration with { Calibration = null }, original));
    }

    [Fact]
    public void InvalidDerivedResultsNeverInvalidateOtherwiseValidRawMeasurements()
    {
        var original = new MeasurementReading("temperature", 12, "degC");
        var definition = Definition(-500);
        var configuration = new InstrumentConfiguration("ambient-temperature", true, Calibration: definition);
        Assert.Equal(ObservationQuality.Invalid, InstrumentCalibration.Derive(configuration, original, DateTimeOffset.UtcNow)!.Quality);
        Assert.Equal(12, original.Value); Assert.Equal(ObservationQuality.Valid, original.Quality);
        definition = Definition() with { Adjustment = new(InstrumentCalibration.AffineMethod, "temperature", "degC", MinimumInput: 20) };
        Assert.Null(InstrumentCalibration.Derive(configuration with { Calibration = definition }, original, DateTimeOffset.UtcNow)!.Value);
        definition = Definition() with { Adjustment = new(InstrumentCalibration.AffineMethod, "temperature", "degC", ValidUntilUtc: DateTimeOffset.UtcNow.AddDays(-1)) };
        Assert.Null(InstrumentCalibration.Derive(configuration with { Calibration = definition }, original, DateTimeOffset.UtcNow)!.Value);
        Assert.Equal(ObservationQuality.Missing, InstrumentCalibration.Derive(configuration, original with { Value = null, Quality = ObservationQuality.Missing }, DateTimeOffset.UtcNow)!.Quality);
    }

    [Theory]
    [InlineData("ambient-temperature", "temperature", "degC")]
    [InlineData("ambient-pressure", "pressure", "inHg")]
    [InlineData("ambient-wind", "wind-speed", "kt")]
    [InlineData("ambient-density", "density", "slug/ft3")]
    public void SupportedScalarChannelsKeepTheirNativeUnits(string instrument, string channel, string unit)
    {
        var definition = Definition() with { InstrumentId = instrument, Parameters = [new("gain", 1, "1", "Synthetic fixture"), new("offset", .25, unit, "Synthetic fixture")],
            Adjustment = new(InstrumentCalibration.AffineMethod, channel, unit) };
        var config = InstrumentCalibration.Freeze(new(instrument, true, Calibration: definition));
        var result = InstrumentCalibration.Derive(config, new(channel, 12, unit), DateTimeOffset.UtcNow)!;
        Assert.Equal(12.25, result.Value); Assert.Equal(unit, result.Unit);
        var displayed = ScienceUnits.Display(result, ScienceUnits.Aviation);
        Assert.NotNull(displayed.DisplayValue); Assert.NotNull(displayed.DisplayUnit);
    }

    [Fact]
    public void CustomRevisionsAreImmutableDetachedAndSurviveRestart()
    {
        using var files = new TemporaryFiles();
        var settings = new InstrumentCalibrationSettings(files.Root);
        var definition = Definition(); var saved = settings.Save(definition);
        Assert.Equal(saved.Sha256, settings.Save(definition).Sha256);
        Assert.Throws<InvalidOperationException>(() => settings.Save(definition with { Limitations = ["Overwrite attempt"] }));
        definition.Parameters[1] = definition.Parameters[1] with { Value = 99 };
        Assert.Equal(.25, settings.Get().Single().Definition.Parameters[1].Value);
        settings.Save(Definition(1, 2));
        var after = new InstrumentCalibrationSettings(files.Root).Get(); Assert.Equal(2, after.Length);
        Assert.Equal(.25, after[0].Definition.Parameters[1].Value); Assert.Equal(1, after[1].Definition.Parameters[1].Value);
        Assert.Throws<InvalidOperationException>(() => settings.Save(Definition(1, 4)));
    }

    [Fact]
    public void UnreadableOrFutureSettingsArePreservedAndCannotBeOverwritten()
    {
        using var files = new TemporaryFiles(); var settings = new InstrumentCalibrationSettings(files.Root);
        foreach (var content in new[] { "{broken", "{\"version\":99,\"definitions\":[]}",
            "{\"version\":1,\"definitions\":[],\"futureField\":true}", "{\"version\":1,\"definitions\":[null]}" })
        {
            File.WriteAllText(settings.PathName, content);
            Assert.Empty(settings.Get()); Assert.Contains("preserved", settings.Warning);
            Assert.ThrowsAny<Exception>(() => settings.Save(Definition()));
            Assert.Equal(content, File.ReadAllText(settings.PathName));
        }
    }

    [Fact]
    public void StorageEstimatesIncludeOptionalDerivedRowsAndTheirRetainedDefinitions()
    {
        using var files = new TemporaryFiles();
        var paths = new StorageManager(new PreferenceStore(files.Root)).Initialize();
        using var science = new ScienceStore(paths.ScienceLibraryDirectory);
        var raw = science.Estimate([new("ambient-temperature", true)], 1);
        var adjusted = science.Estimate([new("ambient-temperature", true, Calibration: Definition())], 1);
        Assert.Equal(60, raw.EstimatedObservationCounts["ambient-temperature"]);
        Assert.Equal(120, adjusted.EstimatedObservationCounts["ambient-temperature"]);
        Assert.True(adjusted.EstimatedBytes > raw.EstimatedBytes * 2);
    }

    [Theory]
    [InlineData(ScienceMode.Universal)]
    [InlineData(ScienceMode.Realistic)]
    public async Task StoredOriginalsDerivedSnapshotsReplayAndExportRemainIndependent(ScienceMode mode)
    {
        using var files = new TemporaryFiles();
        var preferences = new PreferenceStore(files.Root); var paths = new StorageManager(preferences).Initialize();
        var store = new MissionStore(Path.Combine(paths.MissionDirectory, "missions.db")); store.Initialize();
        using var science = new ScienceStore(paths.ScienceLibraryDirectory); store.ScienceLibrary = science;
        using var operations = new ResearchOperations(store, science);
        var definition = Definition(); var originalHash = EvidenceContracts.ContentHash(definition);
        var clock=DateTimeOffset.UtcNow;var coordinator = new MissionCoordinator(store, 1000, research: operations,utcNow:()=>clock);
        var flight = coordinator.Start(new StartMissionRequest("Synthetic calibration archive", mode,
            Instruments: [new("ambient-temperature", true, Calibration: definition)]));
        var at = clock;
        definition.Parameters[1] = definition.Parameters[1] with { Value = 99 }; // caller mutation after acceptance
        void Apply(DateTimeOffset utc)
        {
            var frame = SimulatedTelemetryProvider.Generate(utc, 0);
            coordinator.Apply(new(new(ConnectionState.Connected, TelemetrySource.Simulated, "Synthetic test", utc), frame,
                new(utc, 12, 29.3, 9, 270, .0023)));
        }
        Apply(at);
        Assert.Equal(.25, store.ReplayInstruments(flight.Id, at).Instruments!.Single(c => c.Enabled).Calibration!.Parameters[1].Value);
        clock=at.AddSeconds(1);
        coordinator.ConfigureInstruments([new("ambient-temperature", true, Calibration: Definition(1, 2))]);
        Apply(at.AddSeconds(1)); coordinator.End(flight.Id);
        var rows = science.StreamObservations(flight.Id).ToArray(); Assert.Equal(4, rows.Length);
        var originals = rows.Where(r => r.MeasurementId == "temperature").ToArray();
        Assert.Equal(2, originals.Length); Assert.All(originals, r => { Assert.Equal(12, r.Value); Assert.Null(r.Calibration); Assert.Equal(ScienceProvenance.SimulatorReported, r.Provenance); });
        var derived = rows.Where(r => r.Calibration is not null).ToArray();
        Assert.Equal(new[] { 12.25, 13 }, derived.Select(r => r.Value));
        Assert.Equal(originalHash, derived[0].Calibration!.DefinitionSha256); Assert.Equal(2, derived[1].Calibration!.Definition.Revision);
        Assert.All(derived, r => Assert.Contains("Synthetic", r.Source));
        using var json = new MemoryStream(); await store.ExportJsonAsync(flight.Id, json);
        Assert.Contains("definitionSha256", System.Text.Encoding.UTF8.GetString(json.ToArray()));
        using var csv = new MemoryStream(); await ResearchExports.CsvAsync(store, flight.Id, "science", csv, CancellationToken.None);
        Assert.Contains(originalHash, System.Text.Encoding.UTF8.GetString(csv.ToArray()));
        var reopened = new MissionStore(store.DatabasePath); reopened.Initialize();
        Assert.Equal(originalHash, EvidenceContracts.ContentHash(reopened.ReplayInstruments(flight.Id, at).Instruments!.Single(c => c.Enabled).Calibration!));
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
        db.Open(); using var query = db.CreateCommand(); query.CommandText = "PRAGMA user_version";
        Assert.Equal(MissionStore.CurrentSchemaVersion, Convert.ToInt32(query.ExecuteScalar()));
    }

    private sealed class TemporaryFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "aeroscience-calibration", Guid.NewGuid().ToString("N"));
        public TemporaryFiles() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
