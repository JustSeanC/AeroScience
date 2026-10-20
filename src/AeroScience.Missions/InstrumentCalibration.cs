using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

/// <summary>Calibration metadata and explicitly opted-in scalar derivations; never edits a source value.</summary>
public static class InstrumentCalibration
{
    public const string AffineMethod = "user-affine-1";
    public const string Formula = "derived = original * gain + offset";
    private const string Sdk = "https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimVars/Simulation_Variables.htm";
    private static readonly HashSet<string> Units = ["1", "degC", "inHg", "kt", "degT", "slug/ft3", "deg", "m", "nm", "Hz"];

    public static InstrumentCalibrationInfo Describe(string instrument)
    {
        var definition = ScienceCatalog.Definitions.SingleOrDefault(d => d.Id == instrument)
            ?? throw new ArgumentException("Select a known scientific instrument.");
        var profiles = InstrumentProfiles.Catalog.Where(p => p.InstrumentId == instrument).ToArray();
        var references = definition.Measurements.Length > 0 ? new[] { Sdk } : profiles.SelectMany(p => p.References).Distinct().ToArray();
        var limitations = new[]
        {
            "Calibration accuracy, certificates, drift and internal sensor temperatures are not established.",
            definition.Measurements.Length > 0
                ? "Simulator values remain authoritative. Optional user affine adjustments are separately labeled mathematical derivations, not calibrated real-world observations."
                : "Camera/scanner reference geometry does not recalibrate external datasets. Provider scale, offset, QA, wavelengths and source dates remain authoritative; user radiometric adjustments are unsupported."
        };
        var specification = new InstrumentSpecification(1, "aeroscience.instrument." + instrument, 1, definition, references, limitations);
        var reference = new CalibrationDefinition(1, "aeroscience.calibration." + instrument, 1, instrument,
            definition.Measurements.Length > 0 ? CalibrationInformationStatus.Unknown : CalibrationInformationStatus.Unavailable,
            [], references, limitations);
        // Detach catalog arrays: a client must not edit the process-wide reference catalog.
        specification = EvidenceContracts.Snapshot(specification);
        reference = EvidenceContracts.Snapshot(reference);
        return new(specification, EvidenceContracts.ContentHash(specification), reference, EvidenceContracts.ContentHash(reference),
            definition.Measurements.Select(m => new CalibrationChannel(m.Id, m.Unit, m.Id != "wind-direction",
                m.Id == "wind-direction" ? "Circular direction adjustments are unsupported; degrees true remain unchanged." : "Optional user-defined affine scalar adjustment; no certified accuracy claim.")).ToArray());
    }

    // Called by the generic evidence validator too. Existing metadata-only P01 records
    // remain readable; only the explicitly versioned executable operation is constrained.
    public static void ValidateAdjustment(CalibrationDefinition definition)
    {
        if (definition.Adjustment is not { } operation) return;
        if (operation.Method != AffineMethod) throw new NotSupportedException("Unsupported calibration method; preserve the original definition.");
        var channel = ScienceCatalog.Definitions.Single(d => d.Id == definition.InstrumentId).Measurements
            .SingleOrDefault(m => m.Id == operation.MeasurementId);
        if (definition.Status != CalibrationInformationStatus.UserDefined || channel is null || channel.Id == "wind-direction"
            || channel.Unit != operation.Unit || definition.Parameters.Length != 2
            || !definition.Parameters.Any(p => p.Id == "gain" && p.Unit == "1")
            || !definition.Parameters.Any(p => p.Id == "offset" && p.Unit == channel.Unit))
            throw new ArgumentException("Affine adjustment requires a supported simulator scalar channel, its original unit, dimensionless gain and same-unit offset; external scientific pixels cannot be adjusted.");
        if (new[] { operation.MinimumInput, operation.MaximumInput }.Any(v => v is {} n && !double.IsFinite(n))
            || operation.MinimumInput > operation.MaximumInput
            || new[] { operation.ValidFromUtc, operation.ValidUntilUtc }.Any(t => t is {} utc && utc.Offset != TimeSpan.Zero)
            || operation.ValidFromUtc > operation.ValidUntilUtc)
            throw new ArgumentException("Calibration applicability needs finite ordered input bounds and ordered UTC validity dates.");
    }

    public static CalibrationDefinition ValidateSelection(CalibrationDefinition definition, string instrument)
    {
        EvidenceContracts.Validate(definition);
        if (System.Text.Encoding.UTF8.GetByteCount(EvidenceContracts.Write(definition)) > 16384)
            throw new ArgumentException("A selected calibration definition must fit within 16 KiB; keep supporting files outside recorded sample metadata.");
        if (definition.InstrumentId != instrument) throw new ArgumentException("Calibration belongs to a different instrument.");
        if (definition.Status == CalibrationInformationStatus.UserDefined)
        {
            if (!definition.Id.StartsWith("user.", StringComparison.Ordinal) || definition.DefinedUtc is null
                || definition.Parameters.Any(p => !Units.Contains(p.Unit)))
                throw new ArgumentException("Custom calibration needs a user.* identity, author/date and supported explicit parameter units.");
        }
        else
        {
            var reference = Describe(instrument).Reference;
            if (EvidenceContracts.ContentHash(definition) != EvidenceContracts.ContentHash(reference))
                throw new ArgumentException("Reference calibration metadata cannot be modified or asserted as a certificate. Identify edits as user-defined.");
        }
        foreach (var reference in definition.References)
            if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Calibration references must be public HTTP(S) references without embedded credentials.");
        return EvidenceContracts.Snapshot(definition);
    }

    public static InstrumentConfiguration Freeze(InstrumentConfiguration settings) => settings.Calibration is {} calibration
        ? settings with { Calibration = ValidateSelection(calibration, settings.InstrumentId) } : settings;

    public static EffectiveInstrumentConfiguration Effective(InstrumentConfiguration settings, Guid id, int revision, DateTimeOffset at)
    {
        settings = InstrumentProfiles.Freeze([settings]).Single();
        var info = Describe(settings.InstrumentId);
        return new(id, revision, at, settings, settings.Calibration ?? info.Reference, Specification: info.Specification);
    }

    public static MeasurementReading? Derive(InstrumentConfiguration configuration, MeasurementReading original, DateTimeOffset at)
    {
        if (configuration.Calibration?.Adjustment is not { } operation || operation.MeasurementId != original.MeasurementId) return null;
        var definition = configuration.Calibration;
        ValidateAdjustment(definition);
        // Definitions are validated/frozen when a configuration is accepted, outside
        // the 1 Hz loop. Never substitute an adjustment for the original reading.
        var quality = original.Quality;
        double? value = null;
        if (original.Unit != operation.Unit || original.Provenance != ScienceProvenance.SimulatorReported)
            quality = ObservationQuality.Invalid;
        if (original.Value is {} input && double.IsFinite(input) && quality == ObservationQuality.Valid)
        {
            if (input < operation.MinimumInput || input > operation.MaximumInput || at < operation.ValidFromUtc || at > operation.ValidUntilUtc)
                quality = ObservationQuality.Invalid;
            else
            {
                var adjusted = input * definition.Parameters.Single(p => p.Id == "gain").Value + definition.Parameters.Single(p => p.Id == "offset").Value;
                var physical = double.IsFinite(adjusted) && (original.MeasurementId switch
                {
                    "temperature" => adjusted >= -273.15,
                    "pressure" or "density" => adjusted > 0,
                    "wind-speed" => adjusted >= 0,
                    _ => false
                });
                if (physical) value = adjusted; else quality = ObservationQuality.Invalid;
            }
        }
        else if (quality == ObservationQuality.Valid) quality = ObservationQuality.Unavailable;
        return new(original.MeasurementId + ".user-adjusted", value, original.Unit, ScienceProvenance.Derived, quality);
    }

    public static ScienceObservation? Derive(InstrumentConfiguration configuration, ScienceObservation original)
    {
        var reading = Derive(configuration, new(original.MeasurementId, original.Value, original.Unit,
            original.Provenance, original.Quality == ObservationQuality.AcquisitionGap ? ObservationQuality.Valid : original.Quality), original.TimestampUtc);
        if (reading?.Value is not {} value || reading.Quality != ObservationQuality.Valid) return null;
        var definition = configuration.Calibration!;
        return original with { MeasurementId = reading.MeasurementId, Value = value, Provenance = ScienceProvenance.Derived,
            Source = $"User-defined affine derivation {definition.Id} r{definition.Revision} sha256:{EvidenceContracts.ContentHash(definition)} of " + original.Source,
            Calibration = new(EvidenceContracts.Snapshot(definition), EvidenceContracts.ContentHash(definition), original.MeasurementId, original.Value, original.Unit, Formula) };
    }
}

/// <summary>Append-only calibration revisions in the existing atomic configuration directory.</summary>
public sealed class InstrumentCalibrationSettings(string directory)
{
    private sealed record Saved(int Version, CalibrationDefinition[] Definitions);
    private static readonly JsonSerializerOptions SettingsJson = new(WireJson.Options)
    { MaxDepth = 32, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private readonly object gate = new();
    public string PathName { get; } = Path.Combine(Path.GetFullPath(directory), "instrument-calibrations.json");
    public string? Warning { get; private set; }
    private CalibrationDefinition[] Read()
    {
        if (!File.Exists(PathName)) return [];
        if (new FileInfo(PathName).Length > EvidenceContracts.MaximumJsonBytes) throw new IOException("Calibration settings exceed the supported bound.");
        var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(PathName), SettingsJson) ?? throw new JsonException("Calibration settings are missing.");
        if (saved.Version != 1) throw new NotSupportedException("Unsupported calibration settings version; preserve the original file.");
        if (saved.Definitions is null || saved.Definitions.Length > 256 || saved.Definitions.Any(d => d is null)
            || saved.Definitions.Select(d => (d.Id, d.Revision)).Distinct().Count() != saved.Definitions.Length)
            throw new JsonException("Calibration revisions are invalid.");
        return saved.Definitions.Select(d => InstrumentCalibration.ValidateSelection(d, d.InstrumentId)).ToArray();
    }
    public CalibrationDefinitionRevision[] Get()
    {
        lock (gate)
        {
            try { var result = Read(); Warning = null; return result.Select(d => new CalibrationDefinitionRevision(d, EvidenceContracts.ContentHash(d))).ToArray(); }
            catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            { Warning = "Saved calibration definitions could not be read. The original file is preserved; existing mission snapshots are unchanged. " + error.Message; return []; }
        }
    }
    public CalibrationDefinitionRevision Save(CalibrationDefinition input)
    {
        var definition = InstrumentCalibration.ValidateSelection(input, input.InstrumentId);
        if (definition.Status != CalibrationInformationStatus.UserDefined) throw new ArgumentException("Only user-defined revisions may be saved.");
        lock (gate)
        {
            var existing = Read(); // Do not replace a damaged/unreadable catalog with an empty one.
            if (existing.SingleOrDefault(d => d.Id == definition.Id && d.Revision == definition.Revision) is {} same)
            {
                if (EvidenceContracts.ContentHash(same) != EvidenceContracts.ContentHash(definition)) throw new InvalidOperationException("That revision already exists. Save changes as the next revision; prior definitions remain immutable.");
                return new(same, EvidenceContracts.ContentHash(same));
            }
            var previous = existing.Where(d => d.Id == definition.Id).OrderByDescending(d => d.Revision).FirstOrDefault();
            if (definition.Revision != (previous?.Revision ?? 0) + 1 || previous is not null && previous.InstrumentId != definition.InstrumentId)
                throw new InvalidOperationException("Save the next sequential revision of the same instrument identity.");
            if (existing.Length >= 256) throw new InvalidOperationException("The calibration catalog is full. Existing mission evidence remains unchanged.");
            var saved = new Saved(1, [.. existing, definition]);
            if (JsonSerializer.SerializeToUtf8Bytes(saved, WireJson.Options).Length > EvidenceContracts.MaximumJsonBytes) throw new ArgumentException("Calibration settings exceed the supported size.");
            PreferenceStore.WriteAtomic(PathName, saved); Warning = null;
            return new(definition, EvidenceContracts.ContentHash(definition));
        }
    }
}
