using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed partial class AviationReference
{
    private readonly SemaphoreSlim vectorImport = new(1);
    private async Task<byte[]> NasrMetadata(string url, CancellationToken token)
    {
        if (!url.StartsWith(AviationCatalog.NasrUrl, StringComparison.Ordinal) || !url.EndsWith('/'))
            throw new InvalidDataException("Unsupported NASR metadata endpoint.");
        lock (gate) { if (!Preferences().OnlineEnabled) throw new InvalidOperationException("FAA consent was revoked. No additional NASR metadata request was started."); }
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.UserAgent.ParseAdd("AeroScience/0.6.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("Unsupported FAA metadata redirect.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > AviationCatalog.MaximumMetadataBytes) throw new InvalidDataException("NASR metadata exceeds 4 MiB.");
        using var output = new MemoryStream(); await using var input = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[32768]; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > AviationCatalog.MaximumMetadataBytes) throw new InvalidDataException("NASR metadata exceeds 4 MiB.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }
    public AviationVectorDataset[] VectorDatasets()
    {
        lock (gate) { if (Preferences().StoreId is null) return []; using var store = Open(Preferences()); return store.VectorDatasets(); }
    }
    private bool CanIndexVectors()
    {
        if (disk.FreeBytes(directory) < StorageManager.MinimumFreeBytes)
            throw new IOException("Insufficient physical aviation storage for reference indexing. Originals were preserved; free space and retry.");
        return !FlightActive;
    }
    public async Task<AviationVectorDataset> ImportVectors(Guid jobId, CancellationToken token = default)
    {
        if (!await vectorImport.WaitAsync(0, token)) throw new InvalidOperationException("An aviation reference import is already running.");
        try
        {
            AviationDownloadJob job;
            lock (gate)
            {
                if (!CanIndexVectors()) throw new InvalidOperationException("Reference indexing yields to active flight recording. Retained originals are preserved; import after flight.");
                using var store = Open(Preferences()); job = store.Job(jobId);
                if (job.State != "Completed" || job.Preview.Item.Product != AviationProduct.Nasr) throw new ArgumentException("Select a verified retained NASR original.");
                var retained = store.VectorDatasets().FirstOrDefault(d => d.JobId == jobId); if (retained is not null) return retained;
            }
            await AviationDownloads.VerifyRetained(JobDirectory(jobId), job, token);
            if (AviationAirspace.IsOriginal(job.Preview.Item))
                return await Task.Run(() =>
                {
                    lock (gate)
                    {
                        if (!CanIndexVectors()) throw new InvalidOperationException("Airspace indexing yields to flight recording.");
                        using var store = Open(Preferences());
                        return store.SaveAirspace(job, Path.Combine(JobDirectory(jobId), "files"), clock.GetUtcNow(), token, CanIndexVectors,
                            () => AviationDownloads.VerifyRetained(JobDirectory(jobId), job, token).GetAwaiter().GetResult());
                    }
                }, token);
            var parsed = await Task.Run(() => AviationNasr.Parse(Path.Combine(JobDirectory(jobId), "files"), job, clock.GetUtcNow(), token, CanIndexVectors), token);
            await AviationDownloads.VerifyRetained(JobDirectory(jobId), job, token);
            lock (gate)
            {
                if (!CanIndexVectors()) throw new InvalidOperationException("Flight recording began. Reference publication is held; retry after flight. Originals are unchanged.");
                using var store = Open(Preferences()); store.SaveVectors(parsed.Dataset, parsed.Features, token, CanIndexVectors); return parsed.Dataset;
            }
        }
        finally { vectorImport.Release(); }
    }
    public AviationAirspacePage AirspaceFeatures(string datasetId, double west, double south, double east, double north, string? kind = null, int offset = 0)
    {
        lock (gate)
        {
            using var store = Open(Preferences());
            var page = store.AirspaceFeatures(datasetId, new(west, south, east, north), kind, offset);
            var cache = store.Status("nasr");
            var status = cache.LastSuccessUtc is {} utc ? (Preferences().OnlineEnabled ? "Cached FAA catalog as of " : "Offline FAA catalog as of ") + utc.ToString("O") : "No successful FAA catalog assessment";
            if (cache.Error is not null || cache.LastSuccessUtc is {} success && clock.GetUtcNow() - success > TimeSpan.FromHours(24)) status += "; stale or refresh unavailable";
            return page with { Status = status + ". " + page.Status };
        }
    }
    public AviationVectorPage VectorFeatures(string datasetId, double west, double south, double east, double north, string? kind = null, int offset = 0)
    {
        lock (gate)
        {
            if (Preferences().StoreId is null) return new(1, null, "Not imported", 0, 0, [], ["No verified FAA reference dataset is imported. This says nothing about real-world coverage."]);
            var preferences = Preferences(); using var store = Open(preferences);
            var page = store.VectorFeatures(datasetId, new(west, south, east, north), kind, offset);
            var catalog = store.Catalog("nasr"); var cache = store.Status("nasr");
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var newer = catalog?.Items.Where(i => string.CompareOrdinal(i.Edition, today) <= 0 && string.CompareOrdinal(i.Edition, page.Dataset!.Edition) > 0).Select(i => i.Edition).OrderDescending().FirstOrDefault();
            var freshness = cache.LastSuccessUtc is {} utc ? (preferences.OnlineEnabled ? "Cached FAA catalog" : "Offline cached FAA catalog") + " retrieved " + utc.ToString("O") : "No successful current FAA catalog assessment";
            if (cache.Error is not null || cache.LastSuccessUtc is {} success && clock.GetUtcNow() - success > TimeSpan.FromHours(24)) freshness += "; stale or last refresh unavailable";
            return page with { Status = freshness + (newer is not null ? "; superseded by advertised dated edition " + newer : "; current operational validity is unverified") + ". Exact UTC expiry and live operational status are not inferred." };
        }
    }
}
