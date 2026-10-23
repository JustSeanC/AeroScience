using System.Net;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed partial class AviationReference
{
    private readonly IStorageCapacity disk = capacity ?? new LocalStorageCapacity();
    private readonly SemaphoreSlim acquisition = new(1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<Guid> recovered = [];
    private const long MinimumBytes = 1048576;
    private bool FlightActive => flightActive?.Invoke() == true;
    private string JobDirectory(Guid id, bool staging = false) => Path.Combine(directory, staging ? "Staging" : "Originals", id.ToString("N"));
    private static long Reservation(AviationDownloadJob job) => checked(job.Preview.MaximumTransferBytes + job.Preview.MaximumExpandedBytes);
    public object DownloadConfiguration()
    {
        lock (gate)
        {
            var p = Preferences(); return new { p.UnmeteredOnly, p.OnlineEnabled, configured = p.StoreId is not null,
                maximumTransferBytes = AviationDownloads.MaximumTransferBytes, maximumExpandedBytes = AviationDownloads.MaximumExpandedBytes,
                policy = "Whole original FAA ZIPs are downloaded. Separate aviation allowances; no scientific budget is used. Interrupted transfers restart safely, counting all attempts. Originals are not silently deleted." };
        }
    }
    public object ConfigureDownloads(bool unmeteredOnly, string? storageDirectory)
    {
        lock (gate)
        {
            var p = Preferences();
            if (storageDirectory is not null)
            {
                StorageManager.ValidateLocalPath(storageDirectory);
                if (ApplicationProfile.Current.Validation)
                    new PreferenceStore(configurationDirectory, configurationDirectory).ValidateDirectory(storageDirectory);
                if (p.StoreId is not null && Path.GetFullPath(storageDirectory) != Path.GetFullPath(directory))
                    throw new InvalidOperationException("Existing aviation storage is preserved. Automatic relocation is not supported; reconnect the original location.");
                if (p.StoreId is null && Directory.Exists(storageDirectory) && Directory.EnumerateFileSystemEntries(storageDirectory).Any())
                    throw new IOException("Choose a new empty aviation-only folder. Existing data was preserved.");
                p = p with { StorageDirectory = Path.GetFullPath(storageDirectory) };
            }
            PreferenceStore.WriteAtomic(settingsPath, p with { UnmeteredOnly = unmeteredOnly }); return DownloadConfiguration();
        }
    }
    public AviationStorageSummary DownloadStorage()
    {
        lock (gate)
        {
            if (Preferences().StoreId is null) return new(false, null, 0, null, "Aviation storage is not yet configured.");
            try
            {
                using var store = Open(Preferences());
                return new(true, FolderBytes(directory), store.PendingJobs().Sum(Reservation), disk.FreeBytes(directory), null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            { return new(false, null, 0, null, "Configured aviation storage is unavailable. Reconnect the original drive; no replacement was created."); }
        }
    }
    private static long FolderBytes(string folder)
    {
        StorageManager.ValidateLocalPath(folder); long bytes = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            StorageManager.ValidateLocalPath(entry);
            bytes = checked(bytes + (Directory.Exists(entry) ? FolderBytes(entry) : new FileInfo(entry).Length));
        }
        return bytes;
    }
    public AviationDownloadJob[] DownloadJobs(int offset = 0)
    { lock (gate) { if (offset < 0 || offset > 10000 || offset % 50 != 0) throw new ArgumentException("Use 50-job pages."); if (Preferences().StoreId is null) return []; using var store = Open(Preferences()); return store.Jobs(offset); } }
    private AviationDownloadJob Change(AviationMetadataStore store, AviationDownloadJob job, string state, string? error = null) =>
        store.SaveJob(job with { State = state, Revision = job.Revision + 1, UpdatedUtc = clock.GetUtcNow(), Error = error }, job.Revision);
    public async Task<AviationDownloadJob> PreviewDownload(string kind, string itemId, long transfer, long expanded, CancellationToken token = default)
    {
        AviationDownloadJob[] retained; AviationCatalogItem item;
        lock (gate)
        {
            if (kind is not ("vfr" or "ifr" or "nasr")) throw new ArgumentException("Choose a cached VFR, IFR or supported NASR original.");
            using var store = Open(Preferences());
            item = store.Catalog(kind)?.Items.SingleOrDefault(i => i.Id == itemId) ?? throw new ArgumentException("Exact FAA edition not in the retained catalog. Refresh and select it again.");
            if (transfer is < MinimumBytes or > AviationDownloads.MaximumTransferBytes || expanded is < MinimumBytes or > AviationDownloads.MaximumExpandedBytes || item.OriginalBytes > transfer)
                throw new ArgumentException("Choose bounded aviation transfer and expanded-storage allowances sufficient for this original.");
            retained = store.RetainedJobs(item.Id).Where(j => AviationDownloads.Hash(j.Preview.Item) == AviationDownloads.Hash(item)).ToArray();
        }
        foreach (var old in retained)
            if (await AviationDownloads.VerifyRetained(JobDirectory(old.Id), old, token)) return old;
        lock (gate)
        {
            using var store = Open(Preferences());
            if (store.PendingJobs().Length >= 256) throw new InvalidOperationException("At most 256 pending aviation jobs. Complete or cancel existing work first.");
            var now = clock.GetUtcNow(); var id = Guid.NewGuid();
            var preview = new AviationDownloadPreview(1, id, item, AviationDownloads.SelectionHash(item, transfer, expanded), now, now.AddMinutes(30), transfer, expanded, item.OriginalBytes,
                ["Entire official original ZIP, not a remotely cropped geographic subset. Size Unknown unless published by FAA.",
                 "Transfer limits include failed/restarted attempts. Safe range resumption is not established; partial bytes are never appended.",
                 "ZIP integrity and local SHA-256 do not establish geographic chart accuracy. Rendering/neatlines are a separate acceptance gate.",
                 "FAA public reference. Flight-simulation use only. No scientific observations or mission credit are created."]);
            return store.SaveJob(new(1, id, preview, "Awaiting Approval", 0, 0, 0, now));
        }
    }
    public AviationDownloadJob DownloadControl(Guid id, int revision, string action, string? selectionHash = null)
    {
        lock (gate)
        {
            using var store = Open(Preferences()); var job = store.Job(id);
            if (job.Revision != revision) throw new InvalidOperationException("Aviation job changed; refresh before confirming.");
            if (action == "approve")
            {
                var p = Preferences();
                if (!p.OnlineEnabled || job.State != "Awaiting Approval" || job.Preview.ExpiresUtc < clock.GetUtcNow() || selectionHash != job.Preview.SelectionHash)
                    throw new InvalidOperationException("FAA preview changed/expired, access is disabled or approval already exists. Review a fresh preview.");
                var current = store.Catalog(job.Preview.Item.Product == AviationProduct.Nasr ? "nasr" : job.Preview.Item.Product is AviationProduct.IfrLow or AviationProduct.IfrHigh ? "ifr" : "vfr")?.Items.SingleOrDefault(i => i.Id == job.Preview.Item.Id);
                if (current != job.Preview.Item) throw new InvalidOperationException("FAA edition metadata changed. Review a fresh preview.");
                if (store.PendingJobs().Any(j => j.Preview.Item.Id == job.Preview.Item.Id)) throw new InvalidOperationException("This exact FAA original is already approved. Use its existing job.");
                if (disk.FreeBytes(directory) < Reservation(job) + store.PendingJobs().Sum(Reservation) + StorageManager.MinimumFreeBytes)
                    throw new IOException("Insufficient physical storage for approved aviation reservations. No sources were deleted.");
                return Change(store, job with { Approved = true }, "Queued");
            }
            if (job.State is "Completed" or "Cancelled" or "Awaiting Approval") throw new InvalidOperationException("This aviation job cannot be controlled in its current state.");
            return action switch
            {
                "pause" => Change(store, job, "Paused by User"),
                "cancel" => Change(store, job, "Cancelled"),
                "resume" or "retry" when job.State is "Paused by User" or "Interrupted" or "Failed" or "Waiting for Network" or "Waiting for Flight Priority" =>
                    job.ChargedBytes >= job.Preview.MaximumTransferBytes ? throw new InvalidOperationException("Approved transfer allowance exhausted. Review a new preview; allowances cannot silently expand.") :
                    disk.FreeBytes(directory) < Reservation(job) + store.PendingJobs().Where(j => j.Id != id).Sum(Reservation) + StorageManager.MinimumFreeBytes ? throw new IOException("Insufficient physical capacity to resume this approved aviation job.") : Change(store, job, "Queued"),
                _ => throw new ArgumentException("Use an applicable pause, resume, retry or cancel action.")
            };
        }
    }
    public async Task VerifyDownload(Guid id, CancellationToken token = default)
    {
        AviationDownloadJob job; lock (gate) { using var store = Open(Preferences()); job = store.Job(id); }
        if (!await AviationDownloads.VerifyRetained(JobDirectory(id), job, token)) throw new InvalidOperationException("This original is not verified and completed.");
    }
    // One host worker. Reads never initiate network work; durable stored approval is mandatory.
    public async Task ProcessDownloads(CancellationToken token = default)
    {
        if (!await acquisition.WaitAsync(0, token)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token); token = linked.Token;
        AviationDownloadJob? active = null;
        try
        {
            await RecoverPublications(token);
            lock (gate)
            {
                var p = Preferences(); if (p.StoreId is null) return; using var store = Open(p);
                if (recovered.Add(store.StoreId))
                    foreach (var job in store.PendingJobs().Where(j => j.State is "Preparing" or "Downloading" or "Verifying"))
                        Change(store, job, "Interrupted", "Application interrupted. Resume explicitly; retained originals and prior attempt bytes are preserved.");
                foreach (var job in store.PendingJobs().Where(j => j.State is "Queued" or "Waiting for Flight Priority" or "Waiting for Network").OrderBy(j => j.Preview.CreatedUtc))
                {
                    var hold = !p.OnlineEnabled || p.UnmeteredOnly && (unmetered ?? NetworkCost.IsUnmetered)() != true ? "Waiting for Network" : FlightActive ? "Waiting for Flight Priority" : null;
                    if (hold is not null) { if (job.State != hold) Change(store, job, hold); continue; }
                    if (job.RetryAfterUtc > clock.GetUtcNow()) continue;
                    active = Change(store, job with { AttemptBytes = 0, RetryAfterUtc = null }, "Preparing"); break;
                }
            }
            if (active is null) return;
            await TransferOriginal(active, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { if (active is not null) Fail(active.Id, "Interrupted", "Application interrupted. Resume explicitly."); }
        catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
        { if (active is not null) Fail(active.Id, error is HttpRequestException or OperationCanceledException ? "Waiting for Network" : "Failed", error is HttpRequestException or OperationCanceledException ? "Network unavailable. Retry after checking connectivity; approved allowances still apply." : "Original acquisition/verification failed. Check drive capacity, permissions, archive compatibility and approved limits. Existing originals were preserved."); }
        finally { acquisition.Release(); }
    }
    private void Fail(Guid id, string state, string message)
    {
        lock (gate)
        {
            try { using var store = Open(Preferences()); var current = store.Job(id); if (current.State is "Preparing" or "Downloading" or "Verifying") Change(store, current with { RetryAfterUtc = state == "Waiting for Network" ? clock.GetUtcNow().AddMinutes(5) : null }, state, message); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            { recovered.Clear(); /* Reconnected storage must reconcile its last durable active state even without restarting the host. */ }
        }
    }
    private bool ContinueTransfer(Guid id)
    {
        lock (gate)
        {
            using var store = Open(Preferences()); var job = store.Job(id); if (job.State is not ("Preparing" or "Downloading" or "Verifying")) return false;
            var p = Preferences(); var hold = !p.OnlineEnabled || p.UnmeteredOnly && (unmetered ?? NetworkCost.IsUnmetered)() != true ? "Waiting for Network" : FlightActive ? "Waiting for Flight Priority" : null;
            if (hold is not null) { Change(store, job, hold); return false; } return true;
        }
    }
    private async Task TransferOriginal(AviationDownloadJob job, CancellationToken token)
    {
        var stage = JobDirectory(job.Id, true); var final = JobDirectory(job.Id); StorageManager.ValidateLocalPath(stage); StorageManager.ValidateLocalPath(final);
        if (Directory.Exists(final)) throw new IOException("Uncommitted aviation publication exists. Preserve it for recovery; nothing was overwritten.");
        // Only this job's non-published scratch is reset. Verified originals are never deleted.
        if (Directory.Exists(stage)) { _ = FolderBytes(stage); Directory.Delete(stage, true); }
        Directory.CreateDirectory(stage); var zip = Path.Combine(stage, "original.zip");
        if (!ContinueTransfer(job.Id)) return;
        if (disk.FreeBytes(directory) < Reservation(job) + StorageManager.MinimumFreeBytes) throw new IOException("Insufficient aviation staging capacity.");
        using var request = new HttpRequestMessage(HttpMethod.Get, job.Preview.Item.SourceUrl); request.Headers.UserAgent.ParseAdd("AeroScience/0.6.0");
        // NASR supports bounded complete-entity range GETs even when HEAD/ordinary
        // GET is unavailable. This always starts at zero in a fresh staging file.
        if (job.Preview.Item.Product == AviationProduct.Nasr)
            request.Headers.Range = new(0, job.Preview.MaximumTransferBytes - job.ChargedBytes - 1);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            lock (gate)
            {
                using var store = Open(Preferences()); var current = store.Job(job.Id); if (current.State != "Preparing") return;
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.FromMinutes(5);
                // Never retry earlier than requested. A large provider delay remains durable.
                Change(store, current with { RetryAfterUtc = clock.GetUtcNow() + (delay < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delay) }, "Waiting for Network", "FAA rate limited or unavailable. Provider retry guidance is retained.");
            }
            return;
        }
        var completeRange = response.Content.Headers.ContentRange;
        var supportedRange = job.Preview.Item.Product == AviationProduct.Nasr && response.StatusCode == HttpStatusCode.PartialContent &&
            completeRange is { Unit: "bytes", From: 0, Length: > 0 } && completeRange.To == completeRange.Length - 1 &&
            response.Content.Headers.ContentLength == completeRange.Length;
        if (response.StatusCode != HttpStatusCode.OK && !supportedRange || response.Headers.Location is not null)
            throw new InvalidDataException("Unsupported FAA response, incomplete range or redirect. No credentials are forwarded.");
        var expected = response.Content.Headers.ContentLength;
        if (expected > job.Preview.MaximumTransferBytes - job.ChargedBytes || job.Preview.ExpectedBytes is {} published && expected != published)
            throw new InvalidDataException("FAA length changed or exceeds the remaining approved transfer allowance.");
        lock (gate) { using var store = Open(Preferences()); var current = store.Job(job.Id); if (current.State != "Preparing") return; job = Change(store, current, "Downloading"); }
        await using (var input = await response.Content.ReadAsStreamAsync(token))
        await using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        {
            var buffer = new byte[65536];
            while (true)
            {
                if (!ContinueTransfer(job.Id)) return;
                var remaining = job.Preview.MaximumTransferBytes - job.ChargedBytes;
                if (remaining <= 0 && (expected is null || job.AttemptBytes < expected)) throw new IOException("Aviation transfer allowance exhausted.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var readSize = (int)Math.Min(buffer.Length, Math.Max(1, remaining));
                // Durably reserve before the network read. A process kill cannot erase bytes
                // in flight; the uncertain final block stays charged to the original approval.
                lock (gate)
                {
                    using var store = Open(Preferences()); var current = store.Job(job.Id); if (current.State != "Downloading") return;
                    job = store.SaveJob(current with { ChargedBytes = current.ChargedBytes + readSize, Revision = current.Revision + 1 }, current.Revision);
                }
                var count = await input.ReadAsync(buffer.AsMemory(0, readSize), deadline.Token);
                lock (gate)
                {
                    using var store = Open(Preferences()); var current = store.Job(job.Id);
                    job = store.SaveJob(current with { ChargedBytes = current.ChargedBytes - readSize + count,
                        DownloadedBytes = current.DownloadedBytes + count, AttemptBytes = current.AttemptBytes + count,
                        Revision = current.Revision + 1, UpdatedUtc = clock.GetUtcNow() }, current.Revision);
                }
                if (count == 0) break;
                if (count > remaining) throw new IOException("Aviation transfer allowance exhausted.");
                lock (gate)
                {
                    using var store = Open(Preferences()); var current = store.Job(job.Id);
                    // Account received bytes even if a pause/cancel arrived during the network read.
                    job = current;
                    if (job.State != "Downloading") return;
                    if (disk.FreeBytes(directory) < StorageManager.MinimumFreeBytes + count) throw new IOException("Aviation drive has insufficient free capacity.");
                }
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                if (expected == job.AttemptBytes) break;
            }
            output.Flush(true);
        }
        if (expected is {} length && job.AttemptBytes != length || job.AttemptBytes == 0) throw new InvalidDataException("Incomplete FAA original.");
        if (!ContinueTransfer(job.Id)) return;
        lock (gate) { using var store = Open(Preferences()); var current = store.Job(job.Id); if (current.State != "Downloading") return; job = Change(store, current, "Verifying"); }
        var sha = await AviationDownloads.FileHash(zip, token);
        if (job.Preview.Item.OriginalSha256 is {} providerHash && providerHash != sha) throw new InvalidDataException("FAA original checksum mismatch.");
        if (disk.FreeBytes(directory) < job.Preview.MaximumExpandedBytes + StorageManager.MinimumFreeBytes) throw new IOException("Insufficient extraction capacity.");
        var files = await AviationDownloads.VerifyArchive(zip, Path.Combine(stage, "files"), job.Preview.MaximumExpandedBytes, token, () => ContinueTransfer(job.Id), nasr: job.Preview.Item.Product == AviationProduct.Nasr);
        if (!ContinueTransfer(job.Id)) return;
        lock (gate)
        {
            using var store = Open(Preferences()); var current = store.Job(job.Id); if (current.State != "Verifying") return;
            var complete = current with { State = "Completed", Revision = current.Revision + 1, OriginalSha256 = sha, Files = files, UpdatedUtc = clock.GetUtcNow(), Error = null };
            PreferenceStore.WriteAtomic(Path.Combine(stage, "publication.json"), complete);
            var parent = Path.GetDirectoryName(final)!; StorageManager.ValidateLocalPath(parent); Directory.CreateDirectory(parent);
            Directory.Move(stage, final); store.SaveJob(complete, current.Revision);
        }
    }
    private async Task RecoverPublications(CancellationToken token)
    {
        AviationDownloadJob[] jobs;
        lock (gate) { if (Preferences().StoreId is null) return; using var store = Open(Preferences()); jobs = store.PendingJobs().Where(j => j.State is "Verifying" or "Interrupted").ToArray(); }
        foreach (var job in jobs)
        {
            var final = JobDirectory(job.Id); var receipt = Path.Combine(final, "publication.json"); StorageManager.ValidateLocalPath(receipt);
            if (!File.Exists(receipt)) continue;
            if (new FileInfo(receipt).Length > 65536) throw new IOException("Oversized aviation publication receipt.");
            var completed = System.Text.Json.JsonSerializer.Deserialize<AviationDownloadJob>(await File.ReadAllTextAsync(receipt, token), WireJson.Options) ?? throw new IOException("Invalid aviation publication receipt.");
            AviationDownloads.Validate(completed);
            if (completed.Id != job.Id || completed.Preview != job.Preview && AviationDownloads.Hash(completed.Preview) != AviationDownloads.Hash(job.Preview) ||
                completed.DownloadedBytes != job.DownloadedBytes || completed.ChargedBytes != job.ChargedBytes || completed.State != "Completed")
                throw new IOException("Aviation publication does not match its durable approved intent.");
            await AviationDownloads.VerifyRetained(final, completed, token);
            lock (gate) { using var store = Open(Preferences()); var current = store.Job(job.Id); if (current.Revision == job.Revision) store.SaveJob(completed with { Revision = current.Revision + 1, UpdatedUtc = clock.GetUtcNow() }, current.Revision); }
        }
    }
}
