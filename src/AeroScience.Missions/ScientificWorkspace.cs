using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed record ScienceMapQuery(Guid[]? Missions = null, string? Instrument = null, DateTimeOffset? From = null, DateTimeOffset? To = null, GeographicBounds? Area = null, long After = 0, int Limit = 40, Guid? Source = null, DateTimeOffset? SourceFrom = null, DateTimeOffset? SourceTo = null,bool? HasGaps=null,string? Measurement=null,int Offset=0,string? Search=null);
public sealed record ScienceMapPage(RemoteProduct[] Products, long Next, bool HasMore, string Method);
public sealed record ScientificComparisonRequest(Guid[] Products, string Measurement);
public sealed record ScientificDifferenceCell(Coordinates[] Boundary, double Baseline, double Compared, double Delta, Guid BaselineProductId, Guid ProductId);
public sealed record ScientificComparison(bool Comparable, string[] Reasons, string Measurement, string[] Units, long MatchedNativeCells, double? MinimumDelta, double? MaximumDelta, double? MeanDelta, object[] Sources, ScientificDifferenceCell[]? DifferenceCells = null, long TotalDifferenceValues = 0);
public sealed record InstrumentReview(string InstrumentId, string Name, long MeasurementValues, long AcquisitionTimestamps, long RemoteProducts, long ValidGeographicSamples, MeasurementSummary[] Measurements, InstrumentState? Diagnostics, string Explanation,object[]? Sources=null,object[]? RemoteStatistics=null);
public sealed record MissionScientificSummary(Mission Mission, string? Objective, ScientificReviewStatus Review, InstrumentReview[] Instruments, FlightStatistics Flight);
public sealed partial class ScienceStore
{
    private void InitializeSpatialIndex(SqliteConnection db)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT name FROM sqlite_master WHERE name='scientific_spatial'"; if (cmd.ExecuteScalar() is not null) return;
        // Rebuildable index; the original product payloads and files are never rewritten.
        using var tx = db.BeginTransaction(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE VIRTUAL TABLE scientific_spatial USING rtree(id,west,east,south,north);
            INSERT INTO scientific_spatial
            SELECT p.rowid,MIN(json_extract(v.value,'$.longitude')),MAX(json_extract(v.value,'$.longitude')),
                MIN(json_extract(v.value,'$.latitude')),MAX(json_extract(v.value,'$.latitude'))
            FROM remote_products p,json_each(p.payload_json,'$.footprint.boundary') v GROUP BY p.rowid;
            CREATE TRIGGER spatial_product_insert AFTER INSERT ON remote_products BEGIN
              INSERT INTO scientific_spatial SELECT new.rowid,MIN(json_extract(value,'$.longitude')),MAX(json_extract(value,'$.longitude')),
                MIN(json_extract(value,'$.latitude')),MAX(json_extract(value,'$.latitude')) FROM json_each(new.payload_json,'$.footprint.boundary');
            END;
            CREATE TRIGGER spatial_product_delete AFTER DELETE ON remote_products BEGIN DELETE FROM scientific_spatial WHERE id=old.rowid; END;
            CREATE INDEX remote_products_instrument ON remote_products(instrument_id,flight_utc);
            """; cmd.ExecuteNonQuery(); tx.Commit();
    }
    public ScienceMapPage MapPage(ScienceMapQuery query)
    {
        if(query.Search?.Length>128)throw new ArgumentException("Use at most 128 characters to search saved metadata.");
        if (query.Missions?.Length > 100) throw new ArgumentException("Select at most 100 specific missions, or use All Missions.");
        if (query.Area is { } bounds && (bounds.West < -180 || bounds.East > 180 || bounds.South < -90 || bounds.North > 90 || bounds.South >= bounds.North || new[] { bounds.West, bounds.East, bounds.South, bounds.North }.Any(v => !double.IsFinite(v)))) throw new ArgumentException("Invalid map bounds.");
        if (!File.Exists(DatabasePath)) return new([], query.After, false, "No recorded scientific products.");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.rowid,p.payload_json FROM remote_products p JOIN scientific_spatial geo ON geo.id=p.rowid
            WHERE p.rowid>$after AND ($instrument='' OR instrument_id=$instrument)
              AND ($from='' OR flight_utc>=$from) AND ($to='' OR flight_utc<=$to)
              AND ($source='' OR json_extract(p.payload_json,'$.sourceId')=$source)
              AND ($sourceFrom='' OR json_extract(p.payload_json,'$.sourceObservationUtc')>=$sourceFrom)
              AND ($sourceTo='' OR json_extract(p.payload_json,'$.sourceObservationUtc')<=$sourceTo)
              AND ($gaps=-1 OR (json_extract(p.payload_json,'$.validSampleCount') < json_extract(p.payload_json,'$.width') * json_extract(p.payload_json,'$.height'))=$gaps)
              AND ($search='' OR instr(lower(p.payload_json),lower($search))>0)
              AND ($area=0 OR (geo.north>=$south AND geo.south<=$north AND
                   (($west<=$east AND geo.east>=$west AND geo.west<=$east) OR ($west>$east AND (geo.east>=$west OR geo.west<=$east)))))
            """;
        if (query.Missions is { Length: > 0 }) { var names = query.Missions.Select((id, i) => { var name = "$mission" + i; cmd.Parameters.AddWithValue(name, id.ToString()); return name; }); cmd.CommandText += " AND mission_id IN (" + string.Join(',', names) + ")"; }
        var limit = Math.Clamp(query.Limit, 1, 100); cmd.CommandText += " ORDER BY p.rowid LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$offset",Math.Max(0,query.Offset));
        cmd.Parameters.AddWithValue("$search",query.Search?.Trim()??"");
        cmd.Parameters.AddWithValue("$after", Math.Max(0, query.After)); cmd.Parameters.AddWithValue("$limit", limit + 1); cmd.Parameters.AddWithValue("$instrument", query.Instrument ?? "");
        cmd.Parameters.AddWithValue("$from", query.From?.ToUniversalTime().ToString("O") ?? ""); cmd.Parameters.AddWithValue("$to", query.To?.ToUniversalTime().ToString("O") ?? "");
        cmd.Parameters.AddWithValue("$gaps",query.HasGaps is null?-1:query.HasGaps.Value?1:0);
        cmd.Parameters.AddWithValue("$source", query.Source?.ToString() ?? ""); cmd.Parameters.AddWithValue("$sourceFrom", query.SourceFrom?.ToUniversalTime().ToString("O") ?? ""); cmd.Parameters.AddWithValue("$sourceTo", query.SourceTo?.ToUniversalTime().ToString("O") ?? "");
        cmd.Parameters.AddWithValue("$area", query.Area is null ? 0 : 1); cmd.Parameters.AddWithValue("$west", query.Area?.West ?? -180); cmd.Parameters.AddWithValue("$east", query.Area?.East ?? 180); cmd.Parameters.AddWithValue("$south", query.Area?.South ?? -90); cmd.Parameters.AddWithValue("$north", query.Area?.North ?? 90);
        using var reader = cmd.ExecuteReader(); var rows = new List<RemoteProduct>(); long next = query.After; bool more = false;
        while (reader.Read()) { if (rows.Count == limit) { more = true; break; } next = reader.GetInt64(0); rows.Add(ReadCatalog<RemoteProduct>(reader.GetString(1))); }
        return new(rows.ToArray(), next, more, "R-tree geographic candidate index, keyset pagination. Footprints are envelopes; actual numeric samples and QA remain authoritative. No source-only pixels imply mission acquisition.");
    }
    public object[] InstrumentCounts(Guid mission)
    {
        if (!File.Exists(DatabasePath)) return [];
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT instrument_id,COUNT(*),COUNT(DISTINCT timestamp_utc) FROM observations WHERE mission_id=$id GROUP BY instrument_id"; cmd.Parameters.AddWithValue("$id", mission.ToString());
        using var reader = cmd.ExecuteReader(); var rows = new List<object>(); while (reader.Read()) rows.Add(new { instrumentId = reader.GetString(0), values = reader.GetInt64(1), timestamps = reader.GetInt64(2) }); return rows.ToArray();
    }
    public (long Values, long Times) ObservationCounts(Guid mission, string instrument)
    {
        if (!File.Exists(DatabasePath)) return (0, 0); using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*),COUNT(DISTINCT timestamp_utc) FROM observations WHERE mission_id=$id AND json_extract(payload_json,'$.instrumentId')=$instrument"; cmd.Parameters.AddWithValue("$id", mission.ToString()); cmd.Parameters.AddWithValue("$instrument", instrument); using var reader = cmd.ExecuteReader(); reader.Read(); return (reader.GetInt64(0), reader.GetInt64(1));
    }
    public ScientificComparison Compare(ScientificComparisonRequest request)
    {
        if (request.Products.Length is < 2 or > 8) throw new ArgumentException("Choose 2–8 actual acquisitions.");
        var data = request.Products.Distinct().Select(ReadProduct).ToArray(); if (data.Length < 2) throw new ArgumentException("Choose distinct acquisitions.");
        (string Id, string Unit, double?[] Values, RasterGrid[] Grids)[] bands;
        try { bands = data.Select(d => ScientificSupport.Band(d, request.Measurement)).ToArray(); }
        catch (ArgumentException) { return new(false, ["The selected acquisitions do not share this physical measurement. Inspect their individual layers side-by-side; subtraction is not meaningful."], request.Measurement, [], 0, null, null, null, data.Select(d => (object)new { d.Product.Id, d.Source.Manifest.Kind, d.Source.Manifest.ObservationUtc, d.Source.Manifest.SceneId }).ToArray()); }
        var reasons = new List<string>();
        if (bands.Select(b => b.Unit).Distinct().Count() != 1) reasons.Add("Physical units differ; subtraction is not meaningful.");
        if (data.Select(d => d.Source.Manifest.Kind).Distinct().Count() != 1) reasons.Add("Different sensor quantities cannot be subtracted.");
        if (data.Any(d => d.Source.Manifest.ObservationUtc is null)) reasons.Add("Original observation date is unknown; temporal equivalence is not established.");
        if(data.Select(d=>string.Join(',',ScientificCapabilities.Describe(d.Source).Capabilities.Order())).Distinct().Count()!=1)reasons.Add("Source capabilities differ (for example bare-earth elevation versus surface model).");
        if(data[0].Source.Manifest.Kind==RemoteKind.Hyperspectral&&data.Select(d=>d.Source.Manifest.Bands.FirstOrDefault(b=>b.Id==request.Measurement)?.WavelengthNm).Distinct().Count()!=1)reasons.Add("Spectral band centers differ; no response-function correction was performed.");
        if (data.Select(d => d.Source.Manifest.VerticalDatum).Distinct().Count() != 1) reasons.Add("Vertical references differ; no datum conversion has been applied.");
        var grid = bands[0].Grids[0];
        if (bands.Any(b => b.Grids.Any(g => !ScientificComposites.Aligned(grid,g)))) reasons.Add("Native resolution, CRS or pixel orientation differ; no implicit scientific resampling.");
        if (data.Any(d => d.Evidence is null) || data.Select(d => ScientificComposites.Identity(d.Evidence!.AcquisitionSettings)).Distinct().Count() != 1) reasons.Add("Equivalent archived sensor configuration is not established.");
        var sources = data.Select(d => (object)new { d.Product.Id, d.Product.MissionId, d.Source.Manifest.Provider, d.Source.Manifest.SceneId, d.Source.Manifest.ObservationUtc, d.Product.FlightPacketUtc, d.Product.Quality, grids = d.Source.Grids, warning = "A simulator revisit of one historical scene is not independent satellite change." }).ToArray();
        if (reasons.Count > 0) return new(false, reasons.ToArray(), request.Measurement, bands.Select(b => b.Unit).ToArray(), 0, null, null, null, sources);
        (long X, long Y) Key(Coordinates p)
        {
            var projected = ScientificRaster.Project(p, grid.Crs); var x = projected.X - grid.OriginX; var y = projected.Y - grid.OriginY; var det = grid.PixelWidth * grid.PixelHeight - grid.RowX * grid.ColumnY;
            return ((long)Math.Floor((x * grid.PixelHeight - y * grid.RowX) / det + 1e-8), (long)Math.Floor((y * grid.PixelWidth - x * grid.ColumnY) / det + 1e-8));
        }
        var lookups = data.Select((d, i) => d.SampleLocations.Select((p, n) => (Key: Key(p), Value: bands[i].Values[n])).Where(v => v.Value is { } x && double.IsFinite(x)).GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Last().Value!.Value)).ToArray();
        var common = lookups[0].Where(p => lookups.Skip(1).All(l => l.ContainsKey(p.Key))).ToArray();
        var deltas = common.SelectMany(p => lookups.Skip(1).Select(l => l[p.Key] - p.Value)).ToArray();
        if (deltas.Length == 0) return new(false, ["No common valid native cells; cloud/nodata gaps are not filled."], request.Measurement, [bands[0].Unit], 0, null, null, null, sources);
        var sameScene = data.Select(d => (d.Source.Manifest.Provider, d.Source.Manifest.SceneId, d.Source.Manifest.ObservationUtc)).Distinct().Count() == 1;
        // Display exact common native cells. This is a bounded view of the existing
        // comparison, not a new resampling, interpolation or scientific calculation.
        var display = new List<ScientificDifferenceCell>();
        foreach (var cell in common)
        {
            var (x, y) = cell.Key;
            var boundary = new[] { ScientificRaster.GridPoint(grid,x,y), ScientificRaster.GridPoint(grid,x+1,y), ScientificRaster.GridPoint(grid,x+1,y+1), ScientificRaster.GridPoint(grid,x,y+1), ScientificRaster.GridPoint(grid,x,y) };
            if (boundary.Max(p=>p.Longitude)-boundary.Min(p=>p.Longitude)>180) continue;
            for (var index=1;index<lookups.Length && display.Count<2048;index++)
            {
                var compared=lookups[index][cell.Key];
                display.Add(new(boundary,cell.Value,compared,compared-cell.Value,data[0].Product.Id,data[index].Product.Id));
            }
            if(display.Count>=2048)break;
        }
        return new(true, [sameScene ? "Same historical scene: these differences describe sampled support/reprocessing, not independently observed Earth change." : "Different source dates: differences are descriptive, not causal or simultaneous airborne measurements.", "Each common native pixel counted once per acquisition; no averaging of incompatible quantities."], request.Measurement, [bands[0].Unit], common.Length, deltas.Min(), deltas.Max(), deltas.Average(), sources, display.ToArray(), deltas.LongLength);
    }
}
public static class ScientificResearchSummary
{
    public static MissionScientificSummary Build(MissionStore missions, ScienceStore science, Guid id)
    {
        var mission = missions.Detail(id)?.Mission ?? throw new ArgumentException("Mission not found."); var research = missions.Research(id); var stats = science.Summary(id); var datasets = science.Datasets(id, 200);
        var allSources=science.Sources().ToDictionary(s=>s.Id);
        var instrumentIds = datasets.Select(d => d.InstrumentId).Concat(research.Instruments.Where(c => c.Enabled).Select(c => c.InstrumentId)).Distinct();
        var reviews = instrumentIds.Select(instrument =>
        {
            var count = science.ObservationCounts(id, instrument); long products = 0, samples = 0;var used=new HashSet<Guid>();var ranges=new Dictionary<(Guid,string,string),(long Count,double Min,double Max,double Sum)>();
            foreach(var p in science.AllProducts(id).Where(p=>p.InstrumentId==instrument))
            {
                products++;samples+=p.ValidSampleCount??0;used.Add(p.SourceId);
                // Terrain's derived elevation aliases its native elevation band. Count one
                // physical quantity per acquisition, including legacy cached statistics.
                foreach(var statistic in science.ProductStatistics(p).DistinctBy(s=>(s.Id,s.Unit))){var key=(p.SourceId,statistic.Id,statistic.Unit);var value=ranges.GetValueOrDefault(key,(0,double.PositiveInfinity,double.NegativeInfinity,0));ranges[key]=(value.Item1+statistic.Count,Math.Min(value.Item2,statistic.Minimum),Math.Max(value.Item3,statistic.Maximum),value.Item4+statistic.Mean*statistic.Count);}
            }
            var ids = ScienceCatalog.Definitions.FirstOrDefault(d => d.Id == instrument); var measurements = stats.Where(s => ids?.Measurements.Any(m => m.Id == s.MeasurementId) == true).ToArray();
            return new InstrumentReview(instrument, ids?.Name ?? instrument, count.Values, count.Times, products, samples, measurements, research.Diagnostics?.FirstOrDefault(d => d.InstrumentId == instrument), RemoteAcquisition.Instruments.ContainsKey(instrument) ? "Geographic samples from independently observed historical source data. Source dates and masks remain distinct from aircraft time." : "Simulator-reported environmental measurement values. Several fields can share one timestamp; stored values are not additional sampling instants.",
                used.Where(allSources.ContainsKey).Select(sourceId=>(object)new{sourceId,name=allSources[sourceId].Manifest.Name,provider=allSources[sourceId].Manifest.Provider,observedUtc=allSources[sourceId].Manifest.ObservationUtc,verticalDatum=allSources[sourceId].Manifest.VerticalDatum,quality=allSources[sourceId].Manifest.Acquisition?.Quality}).ToArray(),
                ranges.Select(k=>(object)new{sourceId=k.Key.Item1,measurement=k.Key.Item2,unit=k.Key.Item3,count=k.Value.Count,minimum=k.Value.Min,maximum=k.Value.Max,mean=k.Value.Sum/k.Value.Count,basis="Saved acquisition samples, including repeated positions at different packets. Separate original sources; not an area mean."}).ToArray());
        }).ToArray(); return new(mission, research.Objective, missions.ScientificReview(id), reviews, missions.Statistics(id));
    }
}

public static class ScientificMapLayer
{
    public static object Create(RemoteProductData data, string measurement)
    {
        var rgbIds = measurement == "natural-color" ? new[] { "B04", "B03", "B02" } : measurement == "false-color" ? new[] { "B08", "B04", "B03" } : null;
        var rgb = rgbIds?.Select(id => ScientificSupport.Band(data, id)).ToArray();
        var band = ScientificSupport.Band(data, rgbIds?[0] ?? measurement); var grid = band.Grids[0];
        if (rgb is not null && rgb.SelectMany(b => b.Grids).Any(g => g != grid)) throw new ArgumentException("RGB bands use different native grids; select individual bands instead of implicit resampling."); var seen = new HashSet<(long, long)>();
        var features = new List<object>(); var values = new List<double>();
        for (var i = 0; i < data.SampleLocations.Length; i++)
        {
            if (band.Values[i] is not { } value || !double.IsFinite(value) || rgb?.Any(b => b.Values[i] is null) == true) continue;
            var q = ScientificRaster.Project(data.SampleLocations[i], grid.Crs); var dx = q.X - grid.OriginX; var dy = q.Y - grid.OriginY; var det = grid.PixelWidth * grid.PixelHeight - grid.RowX * grid.ColumnY;
            var x = (long)Math.Floor((dx * grid.PixelHeight - dy * grid.RowX) / det + 1e-8); var y = (long)Math.Floor((dy * grid.PixelWidth - dx * grid.ColumnY) / det + 1e-8); if (!seen.Add((x, y))) continue;
            var corners = new[] { ScientificRaster.GridPoint(grid, x, y), ScientificRaster.GridPoint(grid, x + 1, y), ScientificRaster.GridPoint(grid, x + 1, y + 1), ScientificRaster.GridPoint(grid, x, y + 1), ScientificRaster.GridPoint(grid, x, y) };
            // Preserve numeric export even when a single display polygon spans the date line.
            if (corners.Max(p => p.Longitude) - corners.Min(p => p.Longitude) > 180) continue;
            values.Add(value); features.Add(new { type = "Feature", geometry = new { type = "Polygon", coordinates = new[] { corners.Select(p => new[] { p.Longitude, p.Latitude }).ToArray() } }, properties = new { value, rgb = rgb?.Select(b => b.Values[i]).ToArray(), unit = band.Unit, productId = data.Product.Id, missionId = data.Product.MissionId, sourceId = data.Source.Id, flightUtc = data.Product.FlightPacketUtc, sourceUtc = data.Source.Manifest.ObservationUtc } });
        }
        return new
        {
            type = "FeatureCollection",
            features,
            minimum = values.Count > 0 ? (double?)values.Min() : null,
            maximum = values.Count > 0 ? (double?)values.Max() : null,
            unit = band.Unit,wavelengthNm=data.Source.Manifest.Bands.FirstOrDefault(b=>b.Id==band.Id)?.WavelengthNm,
            method = "Native cell containing a saved QA-valid sample; display of the full cell does not prove the whole cell was sensed. Duplicate positions in one native pixel counted once. Gaps are transparent. No retained-only source pixels or improved resolution are implied.",
            source = data.Source.Manifest,
            provenance = data.Product.Provenance
        };
    }
}
