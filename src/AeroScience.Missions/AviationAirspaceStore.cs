using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;
using Coordinates = AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

public sealed partial class AviationMetadataStore
{
    private sealed record AirspaceEntry(string Id, string Kind, Envelope Bounds);
    private sealed record AirspaceCachedIndex(Guid Store, string Dataset, STRtree<AirspaceEntry> Tree);
    private static readonly Queue<AirspaceCachedIndex> AirspaceIndexes = new();
    private sealed record AirspaceAttributes(int Version, int SourceRecord, string Name, Dictionary<string, string> Attributes);
    public AviationVectorDataset SaveAirspace(AviationDownloadJob job, string folder, DateTimeOffset utc,
        CancellationToken token, Func<bool> canContinue, Action verifyOriginals)
    {
        if (!AviationAirspace.IsOriginal(job.Preview.Item)) throw new NotSupportedException("This airspace distribution is not implemented.");
        var saa = job.Preview.Item.OriginalFileName == "aixm5.0.zip";
        var sourceCrs = saa ? AviationAirspace.Crs84 : AviationAirspace.ClassCrs(folder, job);
        var id = AviationAirspace.DatasetId(job);
        var omitted = new Dictionary<string, int>(); var count = 0;
        lock (gate)
        {
            var retained = Job(job.Id);
            if (retained.State != "Completed" || retained.OriginalSha256 != job.OriginalSha256) throw new IOException("Airspace original identity changed.");
            using var db = Open(true); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO airspace_features VALUES($dataset,$id,$kind,$west,$south,$east,$north,$vertices,$tolerance,$geometry,$display,$json)";
            cmd.Parameters.AddWithValue("$dataset", id);
            foreach (var name in new[] { "$id", "$kind", "$json" }) cmd.Parameters.Add(name, SqliteType.Text);
            foreach (var name in new[] { "$west", "$south", "$east", "$north", "$tolerance" }) cmd.Parameters.Add(name, SqliteType.Real);
            cmd.Parameters.Add("$vertices", SqliteType.Integer); cmd.Parameters.Add("$geometry", SqliteType.Blob); cmd.Parameters.Add("$display", SqliteType.Blob);
            var writer = new WKBWriter();
            foreach (var record in saa ? AviationAirspace.ReadSaa(folder, job, token, canContinue) : AviationAirspace.ReadClass(folder, job, token, canContinue))
            {
                token.ThrowIfCancellationRequested(); if (!canContinue()) throw new InvalidOperationException("Airspace import yielded to recording; original files preserved.");
                if (record.UnsupportedReason is {} reason) { omitted[reason] = omitted.GetValueOrDefault(reason) + 1; continue; }
                var original = record.Original!; var display = record.Display!; var bounds = original.EnvelopeInternal;
                var payload = JsonSerializer.Serialize(new AirspaceAttributes(1, record.Number, record.Name, record.Attributes), WireJson.Options);
                if (payload.Length > 32768) throw new InvalidDataException("Oversized airspace attributes.");
                cmd.Parameters["$id"].Value = AviationDownloads.Hash(new { id, record.Number }); cmd.Parameters["$kind"].Value = record.Kind;
                cmd.Parameters["$west"].Value = bounds.MinX; cmd.Parameters["$east"].Value = bounds.MaxX; cmd.Parameters["$south"].Value = bounds.MinY; cmd.Parameters["$north"].Value = bounds.MaxY;
                cmd.Parameters["$vertices"].Value = original.NumPoints; cmd.Parameters["$tolerance"].Value = record.Tolerance;
                cmd.Parameters["$geometry"].Value = writer.Write(original); cmd.Parameters["$display"].Value = ReferenceEquals(original, display) ? DBNull.Value : writer.Write(display);
                cmd.Parameters["$json"].Value = payload; cmd.ExecuteNonQuery(); count++;
            }
            if (count == 0) throw new InvalidDataException("No supported valid airspace polygons; no empty dataset was published.");
            var dataset = new AviationVectorDataset(1, id, job.Id, job.Preview.Item.Edition, job.OriginalSha256!, saa ? "SUA" : "CLASS", sourceCrs, utc, count, omitted.Values.Sum(),
                [AviationCatalog.Attribution,
                 saa ? "Immutable original XML retains literal component geometry/attributes; interpolated and Boolean indexed geometries are explicitly derived interpretations." : "Original horizontal polygons and attributes retained. Shape Z/PRJ VERTCS do not establish restriction altitude datum.",
                 saa ? "SAA geometry adapter 4: bounded circles, explicit increasing minor arcs including unwrapped source angles and verified adjacent joins; ordered BASE/UNION/SUBTR only with identical verified vertical limits. Boolean/interpolated geometry is derived; immutable XML retains every original component/coordinate. Ambiguous arcs, contributors and unequal vertical volumes remain unsupported." : "Class polygon adapter 1; literal source coordinates retained.",
                 "Map display may be topology-preserving generalized; each feature reports tolerance and original vertex count. Full original geometry remains retained separately.",
                 "Source CRS is retained; NAD83 display is approximate. No positional accuracy, activation or unrestricted-airspace clearance is certified.",
                 "Negative/unknown altitude codes remain Unknown. Published hours, DST and offsets are not inferred UTC activation intervals.",
                 .. omitted.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"Not drawn: {x.Value} source records: {x.Key}.")]);
            _ = ParseDataset(JsonSerializer.Serialize(dataset, WireJson.Options));
            verifyOriginals(); token.ThrowIfCancellationRequested();
            if (!canContinue()) throw new InvalidOperationException("Recording began before airspace publication; retry after flight.");
            cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO vector_datasets VALUES($id,$job,$json)";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$job", job.Id.ToString()); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(dataset, WireJson.Options)); cmd.ExecuteNonQuery(); tx.Commit();
            return dataset;
        }
    }
    private STRtree<AirspaceEntry> AirspaceIndex(SqliteConnection db, AviationVectorDataset dataset)
    {
        lock (IndexGate)
        {
            var cached = AirspaceIndexes.FirstOrDefault(c => c.Store == StoreId && c.Dataset == dataset.Id); if (cached is not null) return cached.Tree;
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,kind,west,south,east,north FROM airspace_features WHERE dataset_id=$id LIMIT 100001"; cmd.Parameters.AddWithValue("$id", dataset.Id);
            using var rows = cmd.ExecuteReader(); var tree = new STRtree<AirspaceEntry>(); var count = 0;
            while (rows.Read())
            {
                if (++count > AviationAirspace.MaximumRecords) throw new InvalidDataException("Airspace index inventory exceeds its bound.");
                var entry = new AirspaceEntry(rows.GetString(0), rows.GetString(1), new Envelope(rows.GetDouble(2), rows.GetDouble(4), rows.GetDouble(3), rows.GetDouble(5)));
                if (!AviationAirspace.Kinds.Contains(entry.Kind)) throw new InvalidDataException("Unknown retained airspace category.");
                tree.Insert(entry.Bounds, entry);
            }
            if (count != dataset.Features) throw new IOException("Airspace inventory differs from its immutable manifest.");
            tree.Build(); while (AirspaceIndexes.Count >= 2) AirspaceIndexes.Dequeue(); AirspaceIndexes.Enqueue(new(StoreId, dataset.Id, tree)); return tree;
        }
    }
    public AviationAirspacePage AirspaceFeatures(string id, GeographicBounds bounds, string? kind = null, int offset = 0)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{64}$") || new[] { bounds.West, bounds.South, bounds.East, bounds.North }.Any(v => !double.IsFinite(v)) ||
            bounds.West < -180 || bounds.East > 180 || bounds.South < -85 || bounds.North > 85 || bounds.West >= bounds.East || bounds.South >= bounds.North ||
            bounds.East - bounds.West > 12 || bounds.North - bounds.South > 12 || offset is < 0 or > 100000 || kind is not null && !AviationAirspace.Kinds.Contains(kind))
            throw new ArgumentException("Use a supported airspace class and viewport up to 12 degrees per side; split dateline/polar regions. Cursor must be bounded.");
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM vector_datasets WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteScalar() is not string json) throw new KeyNotFoundException("Airspace dataset not imported.");
            var dataset = ParseDataset(json); if (dataset.Family is not ("CLASS" or "SUA")) throw new ArgumentException("Select an imported polygon dataset.");
            var envelope = new Envelope(bounds.West, bounds.East, bounds.South, bounds.North);
            var entries = AirspaceIndex(db, dataset).Query(envelope).Where(e => kind is null || e.Kind == kind).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
            var region = new GeometryFactory().ToGeometry(envelope); var reader = new WKBReader(); var result = new List<AviationAirspaceFeature>(); var vertices = 0; var cursor = offset;
            cmd.CommandText = "SELECT vertices,tolerance,CASE WHEN length(COALESCE(display,geometry))<=512000 THEN COALESCE(display,geometry) ELSE NULL END,CASE WHEN length(CAST(payload AS BLOB))<=32768 THEN payload ELSE NULL END FROM airspace_features WHERE dataset_id=$dataset AND id=$id"; cmd.Parameters.AddWithValue("$dataset", id);
            foreach (var entry in entries.Skip(offset).Take(100))
            {
                cmd.Parameters["$id"].Value = entry.Id; using var row = cmd.ExecuteReader(); if (!row.Read()) throw new IOException("Missing airspace index member.");
                if (row.IsDBNull(2) || row.IsDBNull(3)) throw new IOException("Airspace display/metadata exceeds its verified byte bound.");
                var bytes = row.GetFieldValue<byte[]>(2);
                var geometry = reader.Read(bytes); if (geometry.NumPoints > AviationAirspace.MaximumDisplayVertices) throw new IOException("Oversized retained display geometry.");
                if (vertices + geometry.NumPoints > 25000) break;
                cursor++; if (!geometry.Intersects(region)) continue;
                var raw = row.GetString(3); if (raw.Length > 32768) throw new IOException("Oversized retained airspace metadata.");
                var attrs = JsonSerializer.Deserialize<AirspaceAttributes>(raw, WireJson.Options) ?? throw new IOException("Missing airspace metadata.");
                if (attrs.Version != 1) throw new NotSupportedException("Unsupported retained airspace attributes.");
                attrs.Attributes["SOURCE_RECORD"] = attrs.SourceRecord.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result.Add(new(1, entry.Id, entry.Kind, attrs.Name, PolygonCoordinates(geometry), attrs.Attributes,
                    AviationAirspace.Altitude(attrs.Attributes, "LOWER"), AviationAirspace.Altitude(attrs.Attributes, "UPPER"), AviationAirspace.Schedule(attrs.Attributes), row.GetInt32(0), row.GetDouble(1)));
                vertices += geometry.NumPoints;
            }
            return new(1, dataset, "Retained FAA reference; source edition and activation must be reviewed. Bounding candidates are not a count of active restrictions.", offset,
                cursor < entries.Length ? cursor : null, entries.Length, vertices, result.ToArray(), dataset.Limitations);
        }
    }
    private static Coordinates[][][] PolygonCoordinates(Geometry geometry)
    {
        Coordinates[] Ring(LineString ring) => ring.Coordinates.Select(c => new Coordinates(c.Y, c.X)).ToArray();
        return Enumerable.Range(0, geometry.NumGeometries).Select(i => geometry.GetGeometryN(i)).Cast<Polygon>()
            .Select(p => new[] { Ring(p.ExteriorRing) }.Concat(Enumerable.Range(0, p.NumInteriorRings).Select(i => Ring(p.GetInteriorRingN(i)))).ToArray()).ToArray();
    }
}
