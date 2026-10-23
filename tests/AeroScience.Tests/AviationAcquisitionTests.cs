using System.IO.Compression;
using System.Net;
using System.Text;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

public sealed class AviationAcquisitionTests : IDisposable
{
    private sealed class UnixSymlinkFactAttribute : FactAttribute
    {
        public UnixSymlinkFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Unix symlink fixture; Windows junction/privilege behavior requires separate installed acceptance."; }
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-aviation-jobs", Guid.NewGuid().ToString("N"));
    private const string Url = "https://aeronav.faa.gov/visual/09-03-2026/sectional-files/Synthetic.zip";
    private sealed class Disk : IStorageCapacity { public long Free = 100L * 1024 * 1024 * 1024; public long FreeBytes(string directory) => Free; }
    private sealed class Clock : TimeProvider { public DateTimeOffset Utc = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Utc; }
    private sealed class Transport : HttpMessageHandler
    {
        public byte[] Archive = Zip("chart.tif"); public HttpStatusCode Code = HttpStatusCode.OK; public int Downloads;
        public Func<Task>? BeforeRead;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Null(request.Headers.Authorization); Assert.Null(request.Headers.Range);
            if (request.RequestUri!.Host == "www.faa.gov") return new(HttpStatusCode.OK) { Content = new StringContent($"<a href='{Url}'>Original</a>") };
            Assert.Equal(Url, request.RequestUri.AbsoluteUri); Downloads++;
            if (BeforeRead is not null) await BeforeRead();
            var response = new HttpResponseMessage(Code) { Content = new ByteArrayContent(Archive) };
            if (Code == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new(TimeSpan.FromMinutes(20));
            if (Code == HttpStatusCode.Redirect) response.Headers.Location = new("https://untrusted.invalid/private");
            return response;
        }
    }
    private static byte[] Zip(string name, byte[]? content = null)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        { var entry = zip.CreateEntry(name); using var stream = entry.Open(); stream.Write(content ?? new byte[] { 73, 73, 42, 0, 0, 0, 0, 0 }); }
        return memory.ToArray();
    }
    private AviationReference Service(Transport transport, Disk? disk = null, Clock? clock = null, Func<bool>? flight = null) =>
        new(root, root, new HttpClient(transport), clock, disk ?? new(), () => true, flight);
    private async Task<AviationDownloadJob> Preview(AviationReference service)
    {
        if (!service.Configuration().Configured) { service.Configure(true); await service.Refresh("vfr"); }
        return await service.PreviewDownload("vfr", service.Page("vfr").Charts[0].Id, 1048576, 1048576);
    }
    private AviationDownloadJob Approve(AviationReference service, AviationDownloadJob job) => service.DownloadControl(job.Id, job.Revision, "approve", job.Preview.SelectionHash);
    [Fact] public async Task ApprovalBindsExactScopeAndReadsNeverTransfer()
    {
        var handler = new Transport(); using var service = Service(handler); var preview = await Preview(service);
        await service.ProcessDownloads(); Assert.Equal(0, handler.Downloads); Assert.Equal("Awaiting Approval", preview.State);
        Assert.Throws<InvalidOperationException>(() => service.DownloadControl(preview.Id, preview.Revision, "approve", "changed"));
        var approved = Approve(service, preview);
        Assert.Throws<InvalidOperationException>(() => Approve(service, preview));
        using var store = new AviationMetadataStore(Path.Combine(root, "AviationReference"));
        Assert.Equal("Queued", store.Job(approved.Id).State);
        Assert.Throws<NotSupportedException>(() => store.SaveJob(approved with { Version = 20 }));
    }
    [Fact] public async Task OriginalIsVerifiedPublishedReopenedOfflineAndReusedWithoutAnotherDownload()
    {
        var handler = new Transport(); Guid id; string originalHash;
        using (var service = Service(handler))
        {
            var job = Approve(service, await Preview(service)); id = job.Id; await service.ProcessDownloads();
            job = Assert.Single(service.DownloadJobs()); Assert.Equal("Completed", job.State); Assert.NotNull(job.OriginalSha256);
            Assert.Equal(handler.Archive.Length, job.DownloadedBytes); Assert.Equal(job.DownloadedBytes, job.ChargedBytes);
            Assert.Equal(8, Assert.Single(job.Files!).Bytes); originalHash = job.OriginalSha256!;
            Assert.Equal(1, handler.Downloads); Assert.True(service.DownloadStorage().UsedBytes > handler.Archive.Length);
            service.Configure(false);
        }
        using var reopened = Service(handler); await reopened.VerifyDownload(id); await reopened.ProcessDownloads();
        var reused = await reopened.PreviewDownload("vfr", reopened.Page("vfr").Charts[0].Id, 1048576, 1048576);
        Assert.Equal(id, reused.Id); Assert.Equal(originalHash, reused.OriginalSha256); Assert.Equal(1, handler.Downloads);
        Assert.False(Directory.Exists(Path.Combine(root, "ScienceLibrary"))); Assert.False(File.Exists(Path.Combine(root, "missions.db")));
    }
    [Fact] public async Task PauseCancelAndRevisionChecksAreDurableAndDoNotPublishPartialContent()
    {
        var handler = new Transport(); using var service = Service(handler); var job = Approve(service, await Preview(service));
        job = service.DownloadControl(job.Id, job.Revision, "pause"); await service.ProcessDownloads(); Assert.Equal(0, handler.Downloads);
        job = service.DownloadControl(job.Id, job.Revision, "resume");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeRead = async () => { entered.SetResult(); await release.Task; };
        var run = service.ProcessDownloads(); await entered.Task;
        job = Assert.Single(service.DownloadJobs()); job = service.DownloadControl(job.Id, job.Revision, "cancel"); release.SetResult(); await run;
        Assert.Equal("Cancelled", Assert.Single(service.DownloadJobs()).State);
        Assert.False(Directory.Exists(Path.Combine(root, "AviationReference", "Originals", job.Id.ToString("N"))));
        Assert.Throws<InvalidOperationException>(() => service.DownloadControl(job.Id, job.Revision, "resume"));
    }
    [Fact] public async Task FlightPriorityYieldsThenResumesAndExplicitPauseIsPreserved()
    {
        var handler = new Transport(); var active = true; using var service = Service(handler, flight: () => active); var job = Approve(service, await Preview(service));
        await service.ProcessDownloads(); Assert.Equal("Waiting for Flight Priority", Assert.Single(service.DownloadJobs()).State); Assert.Equal(0, handler.Downloads);
        active = false; await service.ProcessDownloads(); Assert.Equal("Completed", Assert.Single(service.DownloadJobs()).State);
        Assert.Equal(1, handler.Downloads);
    }
    [Fact] public async Task ProviderBackoffIsDurableAndDoesNotBlindlyRetry()
    {
        var handler = new Transport { Code = HttpStatusCode.TooManyRequests }; var clock = new Clock();
        using var service = Service(handler, clock: clock); var job = Approve(service, await Preview(service));
        await service.ProcessDownloads(); job = Assert.Single(service.DownloadJobs()); Assert.Equal("Waiting for Network", job.State); Assert.Equal(clock.Utc.AddMinutes(20), job.RetryAfterUtc);
        await service.ProcessDownloads(); Assert.Equal(1, handler.Downloads);
        clock.Utc = clock.Utc.AddMinutes(21); handler.Code = HttpStatusCode.OK; await service.ProcessDownloads(); Assert.Equal("Completed", Assert.Single(service.DownloadJobs()).State);
    }
    [Fact] public async Task RedirectAndCorruptArchiveFailWithoutPublishingOrLosingPreviousOriginals()
    {
        var handler = new Transport { Code = HttpStatusCode.Redirect }; using var service = Service(handler); var job = Approve(service, await Preview(service));
        await service.ProcessDownloads(); job = Assert.Single(service.DownloadJobs()); Assert.Equal("Failed", job.State);
        handler.Code = HttpStatusCode.OK; handler.Archive = Encoding.UTF8.GetBytes("not a zip"); service.DownloadControl(job.Id, job.Revision, "retry");
        await service.ProcessDownloads(); job = Assert.Single(service.DownloadJobs()); Assert.Equal("Failed", job.State); Assert.True(job.DownloadedBytes > 0);
        Assert.False(Directory.Exists(Path.Combine(root, "AviationReference", "Originals")));
        Assert.DoesNotContain("untrusted", job.Error!);
    }
    [Fact] public async Task DiskExhaustionAndMissingDriveDoNotCreateReplacementStore()
    {
        var disk = new Disk(); var handler = new Transport(); using var service = Service(handler, disk); var job = await Preview(service); disk.Free = 0;
        Assert.Throws<IOException>(() => Approve(service, job)); Assert.Equal(0, handler.Downloads);
        disk.Free = 100000000; job = Approve(service, job); disk.Free = 0; await service.ProcessDownloads(); Assert.Equal("Failed", Assert.Single(service.DownloadJobs()).State);
        var directory = Path.Combine(root, "AviationReference"); Directory.Move(directory, directory + "-offline");
        Assert.False(service.DownloadStorage().Available); Assert.Throws<IOException>(() => service.DownloadJobs()); Assert.False(Directory.Exists(directory));
    }
    [Theory]
    [InlineData("../escape.tif")][InlineData("/absolute.tif")][InlineData("folder/../escape.tif")][InlineData("C:escape.tif")][InlineData("folder\\escape.tif")][InlineData("nested.zip")][InlineData("run.exe")]
    [InlineData("CON.tif")][InlineData("lpt1.tif")]
    public async Task UnsafeArchiveEntriesAreRejected(string name)
    {
        Directory.CreateDirectory(root); var zip = Path.Combine(root, "bad.zip"); await File.WriteAllBytesAsync(zip, Zip(name));
        await Assert.ThrowsAsync<InvalidDataException>(() => AviationDownloads.VerifyArchive(zip, Path.Combine(root, "files"), 1048576, default));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape.tif")));
    }
    [Fact] public async Task CorruptedRetainedFileCannotBeSilentlyReplacedByDuplicateDownload()
    {
        var handler = new Transport(); using var service = Service(handler); var job = Approve(service, await Preview(service)); await service.ProcessDownloads();
        await File.AppendAllTextAsync(Path.Combine(root, "AviationReference", "Originals", job.Id.ToString("N"), "original.zip"), "tampered");
        await Assert.ThrowsAsync<IOException>(() => service.VerifyDownload(job.Id));
        await Assert.ThrowsAsync<IOException>(() => service.PreviewDownload("vfr", job.Preview.Item.Id, 1048576, 1048576)); Assert.Equal(1, handler.Downloads);
    }
    [Fact] public async Task RestartRecoversActiveIntentAndAtomicPublicationWithoutRepeatingTransfer()
    {
        var handler = new Transport(); AviationDownloadJob job;
        using (var service = Service(handler)) { job = Approve(service, await Preview(service)); await service.ProcessDownloads(); job = Assert.Single(service.DownloadJobs()); }
        using (var store = new AviationMetadataStore(Path.Combine(root, "AviationReference"))) store.SaveJob(job with { State = "Verifying", Revision = job.Revision + 1 }, job.Revision);
        using var reopened = Service(handler); await reopened.ProcessDownloads(); Assert.Equal("Completed", Assert.Single(reopened.DownloadJobs()).State); Assert.Equal(1, handler.Downloads);
        await reopened.VerifyDownload(job.Id);
    }
    [Fact] public async Task SchemaOneMigratesWithVerifiedBackupAndPreservesMetadataAndIdentity()
    {
        var handler = new Transport(); Guid id;
        using (var service = Service(handler)) { await Preview(service); }
        var directory = Path.Combine(root, "AviationReference"); using (var store = new AviationMetadataStore(directory)) id = store.StoreId;
        using (var db = new SqliteConnection($"Data Source={Path.Combine(directory, "aviation.db")};Pooling=False"))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE airspace_features; DROP TABLE download_jobs; DROP TABLE vector_datasets; DROP TABLE vector_features; PRAGMA user_version=1"; cmd.ExecuteNonQuery(); }
        using var migrated = new AviationMetadataStore(directory); Assert.Equal(id, migrated.StoreId); Assert.Single(migrated.Catalog("vfr")!.Items); Assert.Empty(migrated.Jobs());
        foreach (var backup in Directory.GetFiles(Path.Combine(directory, "Backups")))
        { using var db = new SqliteConnection($"Data Source={backup};Pooling=False"); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", cmd.ExecuteScalar()); }
    }
    [Fact] public async Task ExpiredApprovalAndChangedCatalogMetadataRequireFreshPreview()
    {
        var handler = new Transport(); var clock = new Clock(); using var service = Service(handler, clock: clock); var job = await Preview(service);
        clock.Utc = clock.Utc.AddHours(1); Assert.Throws<InvalidOperationException>(() => Approve(service, job));
        job = await service.PreviewDownload("vfr", job.Preview.Item.Id, 1048576, 1048576);
        using (var store = new AviationMetadataStore(Path.Combine(root, "AviationReference")))
        { var catalog = store.Catalog("vfr")!; store.Save("vfr", catalog with { RetrievedUtc = clock.Utc, Items = [catalog.Items[0] with { OriginalBytes = 500000 }] }); }
        Assert.Throws<InvalidOperationException>(() => Approve(service, job)); Assert.Equal(0, handler.Downloads);
    }
    [Fact] public async Task UnmeteredPolicyAndOfflineConsentHoldJobsWithoutNetworkThenResumeSafely()
    {
        var handler = new Transport(); using var service = new AviationReference(root, root, new HttpClient(handler), unmetered: () => null);
        var job = Approve(service, await Preview(service)); await service.ProcessDownloads(); Assert.Equal("Waiting for Network", Assert.Single(service.DownloadJobs()).State); Assert.Equal(0, handler.Downloads);
        service.ConfigureDownloads(false, null); service.Configure(false); await service.ProcessDownloads(); Assert.Equal(0, handler.Downloads);
        service.Configure(true); await service.ProcessDownloads(); Assert.Equal("Completed", Assert.Single(service.DownloadJobs()).State);
    }
    [Fact] public async Task SelectedStorageIsIndependentAndCannotBeSilentlyRelocated()
    {
        var selected = Path.Combine(root, "ChosenAviation"); Directory.CreateDirectory(selected); var handler = new Transport(); using var service = Service(handler);
        service.ConfigureDownloads(false, selected); var job = Approve(service, await Preview(service)); await service.ProcessDownloads();
        Assert.True(File.Exists(Path.Combine(selected, "aviation.db"))); Assert.False(Directory.Exists(Path.Combine(root, "AviationReference")));
        Assert.Throws<InvalidOperationException>(() => service.ConfigureDownloads(false, Path.Combine(root, "OtherAviation")));
        await service.VerifyDownload(job.Id);
    }
    [Fact] public async Task DuplicateApprovalIsRejectedAndCompletedJobsNeverRestartOnReads()
    {
        var handler = new Transport(); using var service = Service(handler); var first = await Preview(service);
        var second = await service.PreviewDownload("vfr", first.Preview.Item.Id, 1048576, 1048576); Approve(service, first);
        Assert.Throws<InvalidOperationException>(() => Approve(service, second)); await service.ProcessDownloads();
        for (var i = 0; i < 3; i++) { service.DownloadJobs(); service.DownloadStorage(); await service.ProcessDownloads(); }
        Assert.Equal(1, handler.Downloads);
    }
    [Fact] public async Task ArchiveDuplicateNamesLinksAndDecompressionBombsAreRejected()
    {
        Directory.CreateDirectory(root);
        foreach (var mode in new[] { "duplicate", "link", "bomb" })
        {
            var zipPath = Path.Combine(root, mode + ".zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("chart.tif"); if (mode == "link") entry.ExternalAttributes = 0xA1FF << 16;
                using (var stream = entry.Open()) stream.Write(mode == "bomb" ? new byte[2097152] : new byte[] { 73, 73, 42, 0 });
                if (mode == "duplicate") { using var stream = zip.CreateEntry("CHART.TIF").Open(); stream.Write(new byte[] { 73, 73, 42, 0 }); }
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => AviationDownloads.VerifyArchive(zipPath, Path.Combine(root, mode), 1048576, default));
        }
    }
    [Fact] public async Task MissingDriveDuringTransferRecoversOnReconnectWithoutRequiringAnApplicationRestart()
    {
        var handler = new Transport(); using var service = Service(handler); var job = Approve(service, await Preview(service));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeRead = async () => { entered.SetResult(); await release.Task; };
        var run = service.ProcessDownloads(); await entered.Task;
        var directory = Path.Combine(root, "AviationReference"); Directory.Move(directory, directory + "-offline"); release.SetResult(); await run;
        Assert.False(Directory.Exists(directory)); Directory.Move(directory + "-offline", directory); await service.ProcessDownloads();
        Assert.Equal("Interrupted", Assert.Single(service.DownloadJobs()).State); Assert.Equal(1, handler.Downloads);
    }
    [UnixSymlinkFact] public async Task RetainedOriginalFileLinksAreRejectedRatherThanFollowed()
    {
        var handler = new Transport(); using var service = Service(handler); var job = Approve(service, await Preview(service)); await service.ProcessDownloads();
        var zip = Path.Combine(root, "AviationReference", "Originals", job.Id.ToString("N"), "original.zip");
        var retained = zip + ".preserved"; File.Move(zip, retained); File.CreateSymbolicLink(zip, retained);
        await Assert.ThrowsAsync<IOException>(() => service.VerifyDownload(job.Id));
        Assert.Equal(handler.Archive, await File.ReadAllBytesAsync(retained));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
