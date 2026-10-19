using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using AeroScience.Shared;

namespace AeroScience.Missions;

// One bounded worker, separate from the telemetry/coordinator gate. No network downloads on this worker; AutomaticScientificData prepares retained sources independently.
public sealed class RemoteAcquisition : IDisposable
{
    public static readonly Dictionary<string, RemoteKind> Instruments = new()
    { ["thermal-imager"] = RemoteKind.Thermal, ["multispectral-imager"] = RemoteKind.Multispectral,
      ["hyperspectral-imager"] = RemoteKind.Hyperspectral, ["terrain-scanner"] = RemoteKind.Terrain };
    private readonly ScienceStore science;
    private readonly Func<ScientificAccessPreferences>? sourcePreferences;
    private readonly MissionStore missions;
    private readonly Dictionary<string,Channel<RemoteAcquisitionRequest>> queues = Instruments.Keys.ToDictionary(id=>id,_=>Channel.CreateBounded<RemoteAcquisitionRequest>(new BoundedChannelOptions(2) {SingleReader=true,FullMode=BoundedChannelFullMode.Wait}));
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly ConcurrentDictionary<string, RemoteInstrumentStatus> states = new();
    private readonly Dictionary<string, (DateTimeOffset Anchor, long Slot, DateTimeOffset Last)> schedule = [];
    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified)> verifiedFiles = new();
    private long generation;
    private bool paused;
    private int disposed;
    public RemoteAcquisition(MissionStore missions, ScienceStore science,Func<ScientificAccessPreferences>? sourcePreferences=null)
    { this.missions = missions; this.science = science;this.sourcePreferences=sourcePreferences; worker = Task.WhenAll(queues.Values.Select(q=>Task.Run(()=>Run(q)))); }
    public void Reset(bool pause = false)
    {
        Interlocked.Increment(ref generation); paused = pause; schedule.Clear();
        foreach (var id in states.Keys) states.AddOrUpdate(id, Status(id), (_, s) => s with { Processing = false });
    }
    public void Begin() { Reset(); states.Clear(); }
    public RemoteInstrumentStatus Status(string id) => states.GetValueOrDefault(id) ?? new(id, ObservationQuality.Waiting,
        "Scientific data unavailable for this location or acquisition. Check Scientific Data Readiness and automatic scientific access in Settings. Manual source import remains an advanced option.", null, 0, 0, 0);
    public void Offer(Mission mission, MissionResearch research, InstrumentConfiguration[] configs, TelemetryUpdate update)
    {
        if (paused || update.Connection.State != ConnectionState.Connected || update.Frame is not { IsValid: true, IsPaused: false } frame
            || (DateTimeOffset.UtcNow - frame.TimestampUtc).TotalSeconds is < -2 or > 6) return;
        foreach (var config in configs.Where(c => c.Enabled && Instruments.ContainsKey(c.InstrumentId)))
        {
            var existing = Status(config.InstrumentId);
            if (!schedule.TryGetValue(config.InstrumentId, out var run)) run = (frame.TimestampUtc, -1, DateTimeOffset.MinValue);
            if (frame.TimestampUtc <= run.Last) continue;
            var slot = (long)Math.Floor(((frame.TimestampUtc - run.Anchor).TotalMilliseconds + 100) / config.IntervalMs);
            if (slot <= run.Slot) continue;
            var missed = run.Slot < 0 ? 0 : Math.Max(0, slot - run.Slot - 1);
            schedule[config.InstrumentId] = (run.Anchor, slot, frame.TimestampUtc);
            states.AddOrUpdate(config.InstrumentId, existing with { MissingIntervals = missed }, (_, s) => s with { MissingIntervals = s.MissingIntervals + missed });
            if (!queues[config.InstrumentId].Writer.TryWrite(new(mission, research, config, frame, Volatile.Read(ref generation), update.Atmosphere)))
                states.AddOrUpdate(config.InstrumentId, existing, (_, s) => s with { Quality = ObservationQuality.AcquisitionGap, Message = "Processing delayed: this instrument queue is full; flight recording continues.", Status="Processing delayed", MissingIntervals = s.MissingIntervals + 1 });
        }
    }
    private async Task Run(Channel<RemoteAcquisitionRequest> queue)
    {
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(stop.Token))
            {
                if (request.Generation != Volatile.Read(ref generation)) continue;
                var id = request.Configuration.InstrumentId;
                states.AddOrUpdate(id, Status(id), (_, s) => s with { Processing = true });
                var began=System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var product = Process(request);
                    if (request.Generation != Volatile.Read(ref generation)) continue;
                    states.AddOrUpdate(id, Status(id), (_, s) => s with { Quality = ObservationQuality.Valid, Message = "Historical geographic source sampled along simulated flight; open View Data for source date, quality and units.", LastSuccessfulUtc = product.FlightPacketUtc, Products = s.Products + 1, Processing = false,Status="Ready",ProcessingSeconds=began.Elapsed.TotalSeconds,ValidMeasurements=s.ValidMeasurements+(product.ValidSampleCount??0) });
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    if (request.Generation != Volatile.Read(ref generation)) continue;
                    ScientificDiagnostics.Record("acquisition-unavailable",request.Mission.Id.ToString(),"flight",Instruments[id],result:e is ScientificObservationUnavailableException u?u.Status+": "+u.Message:e.GetType().Name+": "+e.Message);
                    states.AddOrUpdate(id, Status(id), (_, s) => s with { Quality = ObservationQuality.Unavailable, Message = e.Message,Status=e is ScientificObservationUnavailableException unavailable?unavailable.Status:"Error",ProcessingSeconds=began.Elapsed.TotalSeconds, Rejected = s.Rejected + 1, MissingIntervals=s.MissingIntervals+1, Processing = false });
                }
                if (request.Generation == Volatile.Read(ref generation))
                {
                    var state = Status(id);
                    try { missions.SaveInstrumentState(request.Mission.Id, new(id, true, request.Configuration.IntervalMs, state.Quality, state.LastSuccessfulUtc, state.Products, state.Rejected, state.MissingIntervals, null, [], state.Message, state.Products)); }
                    catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException) { /* Per-product diagnostics do not stop flight recording. */ }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private RemoteProduct Process(RemoteAcquisitionRequest request)
    {
        void Check() { stop.Token.ThrowIfCancellationRequested(); if (request.Generation != Volatile.Read(ref generation)) throw new OperationCanceledException("Science paused/stopped or configuration changed."); }
        Check();
        science.Management?.RequireScienceCapacity();
        var mount=Instruments[request.Configuration.InstrumentId] == RemoteKind.Terrain ? new SensorMount(30,3,Swath:true) : new();
        var footprint = SensorGeometry.Footprint(request.Frame, mount);
        var source = ScientificSourceSelection.Select(science.Availability(footprint.Center),Instruments[request.Configuration.InstrumentId],sourcePreferences?.Invoke())
            ?? throw new ScientificObservationUnavailableException(science.Sources().Any(s=>s.Manifest.Kind==Instruments[request.Configuration.InstrumentId])?"Outside coverage":"Preparing data","No compatible retained source covers this acquisition location. Check data preparation and source selection; this interval is not backfilled.");
        var root = StorageManager.ResolveDataset(science.DirectoryPath, science.LibraryId, source.RelativeDirectory);
        foreach (var file in source.Files)
        {
            Check(); var path = ScientificRaster.SafeFile(root, file.RelativePath); var info = new FileInfo(path);
            var fingerprint = (info.Length, info.LastWriteTimeUtc);
            if (!verifiedFiles.TryGetValue(path, out var previous) || previous != fingerprint)
            {
                using var stream = File.OpenRead(path); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[131072]; int read;
                while ((read = stream.Read(buffer)) > 0) { Check(); hash.AppendData(buffer.AsSpan(0, read)); }
                if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != file.Sha256) throw new IOException("A copied scientific source failed its checksum. Reimport the original collection.");
                verifiedFiles[path] = fingerprint;
            }
        }
        var locations = SensorGeometry.Samples(footprint); var bands = new List<RemoteBandValues>();
        foreach (var band in source.Manifest.Bands)
        {
            Check(); using var raster = new ScientificRaster(root, band);
            var values = locations.Select(p => { Check(); return raster.Read(p); }).ToArray();
            bands.Add(new(band.Id, band.Unit, band.WavelengthNm, values, "Nearest original source pixel at each WGS84 footprint sample; no interpolation or improved source resolution is claimed", raster.Grid));
        }
        var derived = RemoteCalculations.Apply(source.Manifest.Kind, bands.ToArray());
        if (bands.Where(b=>b.Id is not("QA_PIXEL" or "QA_RADSAT" or "SCL")).All(b => b.Values.All(v => v is null)))
            throw new ScientificObservationUnavailableException("No valid pixels","All sampled pixels were outside coverage, nodata, bad bands or quality masked. No observation was fabricated.");
        Check();
        var dataset = science.EnsureDataset(request.Mission, request.Configuration, request.Research);
        missions.AddDatasetReference(request.Mission.Id, new(dataset.Id, dataset.LibraryId, dataset.RelativePath, dataset.ProvenanceJson));
        var productId = Guid.NewGuid();
        var views = source.Manifest.Kind switch { RemoteKind.Thermal => new[] { "temperature" }, RemoteKind.Multispectral => ["natural-color", "false-color", "ndvi"], RemoteKind.Hyperspectral => ["spectrum"], _ => ["elevation"] };
        var warnings = footprint.Limitations.Concat(new[] { "Source observation date is not the simulator flight time. FlightPacketUtc is host UTC when the simulator packet was received, not the simulator world clock.",
            "A 24×24 sample grid represents this approximate footprint; dense samples do not improve the original source resolution. No survey coverage percentage is computed.",
            "Source identity/license/date are supplied with the imported collection; AeroScience validates files/geometry but does not authenticate a provider signature." })
            .Concat(source.Manifest.ObservationUtc is null ? ["Original observation date is unknown; see source publication metadata."] : Array.Empty<string>()).ToArray();
        var provenance = source.Manifest.Kind == RemoteKind.Terrain ? "DEM-derived terrain samples, not measured LiDAR returns; source vertical datum retained without conversion"
            : "Historical external geographic dataset sampled along simulated aircraft footprint; not a live airborne camera";
        var product = new RemoteProduct(productId, dataset.Id, science.LibraryId, request.Mission.Id, request.Configuration.InstrumentId, source.Id,
            request.Frame.TimestampUtc, DateTimeOffset.UtcNow, source.Manifest.ObservationUtc, footprint, $"Products/{request.Mission.Id:N}/{productId:N}.json", 0, "", 24, 24, views, provenance, "Valid pixels only; per-band nulls preserve nodata/quality exclusions", warnings);
        product=product with{ValidSampleCount=Enumerable.Range(0,locations.Length).LongCount(i=>bands.Where(b=>b.Id is not("QA_PIXEL" or "QA_RADSAT" or "SCL")).Any(b=>b.Values[i].HasValue))};
        Check(); return science.Publish(new(1, product, source, locations, bands.ToArray(), derived,
            ["AeroScience remote sampling algorithm 1.0", "Original scaled numeric values retained; no scientific values inferred from display imagery", "Nearest-source-pixel resampling at each sampled position; thermal cloud/fill/shadow/snow masks and Sentinel SCL exclusions applied where required"],
            new(1, request.Frame, request.Atmosphere, request.Configuration, request.Research.PlanSnapshot, request.Mission.Mode,
                "Host UTC at simulator packet receipt. Original source observation and processing dates are separate. No proof of flight authenticity or local objective pass/fail is asserted.",mount)), Check);
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        Interlocked.Increment(ref generation); foreach(var q in queues.Values)q.Writer.TryComplete(); stop.Cancel();
        try { worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        stop.Dispose();
    }
}

public sealed class ScientificObservationUnavailableException(string status,string message):InvalidOperationException(message)
{public string Status {get;}=status;}

public static class RemoteCalculations
{
    public static Dictionary<string, double?[]> Apply(RemoteKind kind, RemoteBandValues[] bands)
    {
        var output = new Dictionary<string, double?[]>();
        double?[] Band(string id) => bands.Single(b => b.Id == id).Values;
        if (kind == RemoteKind.Thermal)
        {
            var qa = Band("QA_PIXEL"); var values = Band("ST_B10");
            var saturated=bands.FirstOrDefault(b=>b.Id=="QA_RADSAT")?.Values;
            for (var i = 0; i < values.Length; i++) if (qa[i] is not { } q || q is <0 or >65535 || q != Math.Floor(q) || ((int)q & 63) != 0 || values[i] is < 100 or > 500 || saturated is not null && saturated[i] != 0) values[i] = null;
            output["temperature"] = values.ToArray();
        }
        if (kind == RemoteKind.Multispectral)
        {
            var quality = Band("SCL");
            for (var i = 0; i < quality.Length; i++)
                if (quality[i] is not { } q || q != Math.Floor(q) || q is <0 or >11 or 0 or 1 or 3 or 8 or 9 or 10 or 11)
                    foreach (var b in bands.Where(b => b.Id != "SCL")) b.Values[i] = null;
            var red = Band("B04"); var nir = Band("B08");
            output["ndvi"] = red.Zip(nir).Select(p => p.First is { } r && p.Second is { } n && Math.Abs(n + r) > 1e-12 && (n - r) / (n + r) is var index && index is >= -1 and <= 1 ? (double?)index : null).ToArray();
        }
        if (kind == RemoteKind.Terrain) output["elevation"] = bands[0].Values.ToArray();
        return output;
    }
}
