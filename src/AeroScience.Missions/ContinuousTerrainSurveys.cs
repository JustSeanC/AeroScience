using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public static class ContinuousTerrainSurveys
{
    public const string Algorithm = "continuous-terrain-corridor-2/sensor-plane-2";
    public const string ArtifactKind = "continuous-terrain-survey";
    public static void ValidateOptions(TerrainSurveyOptions options) => TerrainSurveyGeometryBuilder.Validate(options);
    public static readonly string[] Limitations = [
        "MODELED sensor-support geometry, not measured scientific coverage, DEM samples or aircraft LiDAR returns. No source was downloaded or substituted.",
        "Only exact archived collection-active pose receipts are used. Recording, armed/waiting states, sparse map tracks and optimistic commands are not collection evidence.",
        "WGS84 geodesic position interpolation; shortest heading rotation; linear AGL/pitch/bank between nearby poses. Between-packet motion and occlusion were not observed.",
        "Existing sensor-plane-2 local horizontal ground plane and frozen scanner mount/FOV remain authoritative. No terrain ray tracing, Earth-curvature model or external vertical datum transformation.",
        "Regional geodesic azimuthal-equidistant overlays; selected radius at most 80 km, default 20 km. Tangential scale distortion is approximately r²/(6R²) (<0.003% at 80 km); this is not positional accuracy.",
        "Default interpolation steps are at most 50 m, 2 degrees and 5% AGL; finite linear triangle lofts approximate within-packet movement. No sub-packet trajectory accuracy or maximum safe flying speed is claimed.",
        "Unknown/unsupported historical fields and boundaries remain excluded. The first/last supported poses need not coincide with session start/end; no unsupported boundary extension is made.",
        "Regional chunks can overlap; chunk areas are local modeled unions, not a sum of unique measured coverage. Search rectangles are prefilters; precise polygon/holes clipping is mandatory.",
        "Original acquisition intent references retain their original publication state; Pending/Cancelled/failed intents are not observations. Zero source links means no elevation/point-cloud evidence is attached.",
        "Acquisition references are archived publication metadata, not a verification of retained product bytes/QA. Geometry does not depend on those numeric products; use the original scientific product reader to inspect their current availability.",
        "The generic profile inferred for a legacy payload-null configuration is explicitly marked. Original session/configuration records are never backfilled or rewritten."
    ];

    public static ScientificArtifact Build(ScienceStore science, MissionStore missions, Guid mission,
        TerrainSurveyOptions? request = null, CancellationToken token = default, Action<long>? progress = null)
    {
        var options = request ?? new(); TerrainSurveyGeometryBuilder.Validate(options);
        using var lease = science.BeginAnalysis();
        using var archive = missions.OpenTerrainSurveyArchive(mission, options, token);
        var header = new TerrainSurveyHeader(mission, mission, Algorithm, archive.Fingerprint, options,
            archive.StartedUtc, archive.EndedUtc, [], Limitations);
        var builder = new TerrainSurveyGeometryBuilder(options, token);
        return science.PublishArtifact(mission, ArtifactKind, "json", archive.Fingerprint,
            new { algorithm = Algorithm, evidenceClass = EvidenceOrigin.ModeledGeometry, originalScienceCredit = false, archive.Fingerprint, options, sources = Array.Empty<TerrainSurveySource>() },
            options.MaximumOutputBytes, path =>
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartArray(); long count = 0;
                    void Write(TerrainSurveyRecord record)
                    {
                        token.ThrowIfCancellationRequested(); JsonSerializer.Serialize(writer, record, WireJson.Options); writer.Flush();
                        if (stream.Position > options.MaximumOutputBytes) throw new TerrainSurveyBudgetException("Derived output byte budget exceeded; originals were preserved.");
                        if (++count % 64 == 0) progress?.Invoke(count);
                    }
                    Write(new(1, "header", Header: header));
                    foreach (var record in builder.Build(archive.Inputs())) Write(record);
                    long acquisitions = 0;
                    foreach (var acquisition in archive.Acquisitions()) { acquisitions++; Write(new(1, "acquisition-reference", Acquisition: acquisition)); }
                    Write(new(1, "summary", Summary: builder.Summary(acquisitions)));
                    writer.WriteEndArray(); writer.Flush();
                    if (stream.Position > options.MaximumOutputBytes) throw new TerrainSurveyBudgetException("Derived output byte budget exceeded.");
                }
                // A changed/deleted live archive cannot publish a stale pinned snapshot.
                // Scientific/source deletion is also excluded by the original analysis lease.
                using var current = missions.OpenTerrainSurveyArchive(mission, options, token);
                if (current.Fingerprint != archive.Fingerprint) throw new IOException("Archive changed during corridor reconstruction. Retry using retained inputs.");
                progress?.Invoke(archive.Rows);
            }, token);
    }

    static IEnumerable<TerrainSurveyRecord> Read(string path, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<TerrainSurveyRecord>(file, WireJson.Options, token).ToBlockingEnumerable(token))
        {
            if (record is null || record.Version != 1) throw new InvalidDataException("Unsupported terrain survey record version.");
            yield return record;
        }
    }
    static TerrainSurveyHeader Header(ScienceStore science, ScientificArtifact artifact, CancellationToken token)
    {
        if (artifact.Kind != ArtifactKind || artifact.Format != "json" || artifact.Bytes > 134217728) throw new ArgumentException("Choose a bounded continuous terrain survey artifact.");
        var first = Read(science.VerifiedArtifactPath(artifact), token).FirstOrDefault();
        if (first is not { Kind: "header", Header: {} h } || h.MissionId != artifact.MissionId || h.FlightId != h.MissionId
            || h.InputFingerprint != artifact.InputIdentity || h.Algorithm != Algorithm || h.Origin != EvidenceOrigin.ModeledGeometry || h.GrantsOriginalScienceCredit || h.Sources.Length != 0)
            throw new InvalidDataException("Terrain survey identity, algorithm or evidence class mismatch.");
        TerrainSurveyGeometryBuilder.Validate(h.Options); return h;
    }
    public static TerrainSurveyStatus Status(ScienceStore science, MissionStore missions, ScientificArtifact artifact, CancellationToken token = default)
    {
        try
        {
            var header = Header(science, artifact, token);
            using var archive = missions.OpenTerrainSurveyArchive(artifact.MissionId, header.Options, token);
            return archive.Fingerprint == header.InputFingerprint ? new(artifact.Id, "Current", null)
                : new(artifact.Id, "Invalidated", "Original archive/configuration/publication references changed; rebuild explicitly from retained inputs.");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        { return new(artifact.Id, "Invalidated", e.Message); }
    }
    public static string RequireCurrent(ScienceStore science, MissionStore missions, ScientificArtifact artifact, CancellationToken token = default)
    {
        var status = Status(science, missions, artifact, token);
        if (status.State != "Current") throw new InvalidOperationException("Derived terrain survey invalidated: " + status.Reason);
        return science.VerifiedArtifactPath(artifact);
    }
    public static IEnumerable<TerrainSurveyChunk> StreamCurrentChunks(ScienceStore science,MissionStore missions,ScientificArtifact artifact,CancellationToken token=default)
    {
        var path=RequireCurrent(science,missions,artifact,token);
        foreach(var record in Read(path,token))if(record.Chunk is {} chunk)yield return chunk;
    }
    // Up to eight bounded regional chunks, each with <=128 search rectangles and
    // exact RFC 7946 MultiPolygon clipping geometry (including holes/dateline splits).
    // P05b/P05c must still apply original source consent, CRS/datum, QA and quotas.
    public static TerrainSurveyChunkPage Chunks(ScienceStore science, MissionStore missions,
        ScientificArtifact artifact, int after = 0, int limit = 1, CancellationToken token = default)
    {
        if (after < 0 || limit is < 1 or > 8) throw new ArgumentException("Choose a page of 1–8 regional chunks after a nonnegative chunk index.");
        var path = RequireCurrent(science, missions, artifact, token);
        var chunks = new List<TerrainSurveyChunk>(); int? next = null;
        foreach (var record in Read(path, token)) if (record.Chunk is {} c && c.Index > after)
        {
            if (chunks.Count == limit) { next = chunks[^1].Index; break; }
            chunks.Add(c);
        }
        return new(artifact.Id, artifact.InputIdentity, chunks.ToArray(), next);
    }
}
