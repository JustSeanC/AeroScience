using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using Coordinates = AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

public sealed partial class AviationMetadataStore
{
    private sealed record IndexEntry(string Id, Geometry Geometry, string Kind);
    private sealed record CachedIndex(Guid Store, string Dataset, STRtree<IndexEntry> Tree);
    private static readonly object IndexGate = new();
    private static readonly Queue<CachedIndex> IndexCache = new();
    public AviationVectorDataset[] VectorDatasets()
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM vector_datasets ORDER BY rowid DESC LIMIT 50";
            using var rows = cmd.ExecuteReader(); var result = new List<AviationVectorDataset>();
            while (rows.Read()) result.Add(ParseDataset(rows.GetString(0))); return result.ToArray();
        }
    }
    private static AviationVectorDataset ParseDataset(string json)
    {
        if (json.Length > 16384) throw new InvalidDataException("Oversized FAA dataset metadata.");
        var d = JsonSerializer.Deserialize<AviationVectorDataset>(json, WireJson.Options) ?? throw new InvalidDataException("Invalid FAA dataset metadata.");
        if (d.Version != 1 || !Regex.IsMatch(d.Id, "^[a-f0-9]{64}$") || !Regex.IsMatch(d.OriginalSha256, "^[a-f0-9]{64}$") ||
            d.JobId == Guid.Empty || d.Features is < 1 or > AviationNasr.MaximumFeatures || d.OmittedRecords < 0 || d.Family is not ("APT" or "NAV" or "FIX") || d.SourceCrs != "EPSG:4269 (NAD83; realization/epoch unspecified by NASR CSV)")
            throw new NotSupportedException("Unsupported FAA vector dataset version or metadata. Preserve the original store.");
        return d;
    }
    public void SaveVectors(AviationVectorDataset dataset, AviationVectorFeature[] features, CancellationToken token = default, Func<bool>? canContinue = null)
    {
        token.ThrowIfCancellationRequested();
        _ = ParseDataset(JsonSerializer.Serialize(dataset, WireJson.Options));
        if (features.Length != dataset.Features || features.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != features.Length) throw new InvalidDataException("FAA dataset inventory mismatch.");
        foreach (var feature in features) AviationNasr.Validate(feature);
        lock (gate)
        {
            var original = Job(dataset.JobId);
            if (original.State != "Completed" || original.OriginalSha256 != dataset.OriginalSha256 || original.Preview.Item.Product != AviationProduct.Nasr || original.Preview.Item.Edition != dataset.Edition)
                throw new InvalidDataException("FAA vector provenance differs from its retained approved original.");
            using var db = Open(true); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO vector_datasets VALUES($id,$job,$json)";
            cmd.Parameters.AddWithValue("$id", dataset.Id); cmd.Parameters.AddWithValue("$job", dataset.JobId.ToString()); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(dataset, WireJson.Options)); cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO vector_features VALUES($dataset,$id,$west,$south,$east,$north,$json)"; cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$dataset", dataset.Id); cmd.Parameters.Add("$id", SqliteType.Text); cmd.Parameters.Add("$west", SqliteType.Real); cmd.Parameters.Add("$south", SqliteType.Real); cmd.Parameters.Add("$east", SqliteType.Real); cmd.Parameters.Add("$north", SqliteType.Real); cmd.Parameters.Add("$json", SqliteType.Text);
            var written = 0;
            foreach (var feature in features)
            {
                if ((++written & 255) == 0 && canContinue?.Invoke() == false) throw new InvalidOperationException("FAA reference publication yielded to flight recording. Retry after flight; originals are preserved.");
                token.ThrowIfCancellationRequested(); cmd.Parameters["$id"].Value = feature.Id;
                cmd.Parameters["$west"].Value = feature.Coordinates.Min(c => c.Longitude); cmd.Parameters["$east"].Value = feature.Coordinates.Max(c => c.Longitude);
                cmd.Parameters["$south"].Value = feature.Coordinates.Min(c => c.Latitude); cmd.Parameters["$north"].Value = feature.Coordinates.Max(c => c.Latitude);
                var json = JsonSerializer.Serialize(feature, WireJson.Options); if (json.Length > 16384) throw new InvalidDataException("Oversized FAA feature.");
                cmd.Parameters["$json"].Value = json; cmd.ExecuteNonQuery();
            }
            tx.Commit(); // Dataset and complete feature inventory become visible together.
        }
    }
    private STRtree<IndexEntry> VectorIndex(SqliteConnection db, AviationVectorDataset dataset)
    {
        lock (IndexGate)
        {
            var cached = IndexCache.FirstOrDefault(c => c.Store == StoreId && c.Dataset == dataset.Id); if (cached is not null) return cached.Tree;
            using var command = db.CreateCommand(); command.CommandText = "SELECT id,json_extract(payload,'$.kind'),json_extract(payload,'$.coordinates') FROM vector_features WHERE dataset_id=$id LIMIT 120001"; command.Parameters.AddWithValue("$id", dataset.Id);
            using var rows = command.ExecuteReader(); var tree = new STRtree<IndexEntry>(); var factory = new GeometryFactory(); var count = 0;
            while (rows.Read())
            {
                if (++count > AviationNasr.MaximumFeatures) throw new InvalidDataException("FAA index bound exceeded.");
                var coordinates = JsonSerializer.Deserialize<Coordinates[]>(rows.GetString(2), WireJson.Options) ?? throw new InvalidDataException("Missing reference geometry.");
                var kind = rows.GetString(1);
                var feature = new AviationVectorFeature(1, rows.GetString(0), kind, "Index", coordinates, []); AviationNasr.Validate(feature);
                Geometry geometry = coordinates.Length == 1 ? factory.CreatePoint(new Coordinate(coordinates[0].Longitude, coordinates[0].Latitude)) : factory.CreateLineString(coordinates.Select(c => new Coordinate(c.Longitude, c.Latitude)).ToArray());
                var entry = new IndexEntry(feature.Id, geometry, kind); tree.Insert(geometry.EnvelopeInternal, entry);
            }
            if (count != dataset.Features) throw new InvalidDataException("FAA index inventory differs from its manifest.");
            tree.Build(); while (IndexCache.Count >= 2) IndexCache.Dequeue(); IndexCache.Enqueue(new(StoreId, dataset.Id, tree)); return tree;
        }
    }
    public AviationVectorPage VectorFeatures(string id, GeographicBounds bounds, string? kind = null, int offset = 0)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{64}$") || new[] { bounds.West, bounds.South, bounds.East, bounds.North }.Any(v => !double.IsFinite(v)) ||
            bounds.West < -180 || bounds.East > 180 || bounds.South < -90 || bounds.North > 90 || bounds.West >= bounds.East || bounds.South >= bounds.North || bounds.East - bounds.West > 30 || bounds.North - bounds.South > 30 || offset is < 0 or > 10000 || offset % 250 != 0 || kind is not null && !AviationNasr.Kinds.Contains(kind))
            throw new ArgumentException("Use a supported kind, a viewport up to 30 degrees per side, and 250-feature pages. Split dateline/extreme regions.");
        lock (gate)
        {
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM vector_datasets WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteScalar() is not string json) throw new KeyNotFoundException("FAA dataset not imported.");
            var dataset = ParseDataset(json); var tree = VectorIndex(db, dataset);
            var envelope = new Envelope(bounds.West, bounds.East, bounds.South, bounds.North); var region = new GeometryFactory().ToGeometry(envelope);
            var matches = tree.Query(envelope).Where(e => (kind is null || e.Kind == kind) && e.Geometry.Intersects(region)).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
            var features = new List<AviationVectorFeature>(); command.CommandText = "SELECT payload FROM vector_features WHERE dataset_id=$dataset AND id=$id"; command.Parameters.AddWithValue("$dataset", id);
            foreach (var entry in matches.Skip(offset).Take(250))
            {
                command.Parameters["$id"].Value = entry.Id; var raw = command.ExecuteScalar() as string ?? throw new InvalidDataException("Missing indexed reference feature.");
                if (raw.Length > 16384) throw new InvalidDataException("Oversized FAA reference feature.");
                var feature = JsonSerializer.Deserialize<AviationVectorFeature>(raw, WireJson.Options) ?? throw new InvalidDataException("Invalid FAA reference feature."); AviationNasr.Validate(feature); features.Add(feature);
            }
            return new(1, dataset, "Retained reference; edition freshness must be reviewed", offset, matches.Length, features.ToArray(), dataset.Limitations);
        }
    }
}
