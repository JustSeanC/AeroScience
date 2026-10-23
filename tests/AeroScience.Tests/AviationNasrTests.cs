using System.Net;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;
public sealed class AviationNasrTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-nasr", Guid.NewGuid().ToString("N"));
    private sealed class Transport : HttpMessageHandler
    {
        public int Calls; public bool Partial; public bool Incomplete; public bool DuplicateNavaid; public bool BadCoordinates; public int ManyFixes; public string Family = "FIX";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Assert.Null(request.Headers.Authorization);
            var url = request.RequestUri!.AbsoluteUri;
            if (url == AviationCatalog.NasrUrl) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<a href='./../NASR_Subscription/2026-10-01'>Current</a>") });
            if (url.StartsWith(AviationCatalog.NasrUrl, StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<a href='" + SyntheticNasr.Url(Family) + "'>Original</a>") });
            Assert.Equal(SyntheticNasr.Url(Family), url); Assert.Equal(0, Assert.Single(request.Headers.Range!.Ranges).From);
            var bytes = SyntheticNasr.Archive(Family);
            if (DuplicateNavaid || BadCoordinates || ManyFixes > 0)
            {
                using var memory = new MemoryStream();
                using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
                    foreach (var file in SyntheticNasr.Files(Family))
                    {
                        var text = file.Value;
                        if (ManyFixes > 0) text = text.Split('\n')[0] + "\n" + string.Join("\n", Enumerable.Range(0, ManyFixes).Select(i => SyntheticNasr.Date + $",MANY{i:D4},K6,PA,US,40,0,0,N,40,75,0,0,W,-75")) + "\n";
                        if (DuplicateNavaid) text += text.Split('\n')[1].Replace("PHILADELPHIA", "OTHER CITY") + "\n";
                        if (BadCoordinates) text = text.Replace(",N,40,", ",N,41,");
                        using var output = new StreamWriter(archive.CreateEntry(file.Key).Open()); output.Write(text);
                    }
                bytes = memory.ToArray();
            }
            var response = new HttpResponseMessage(Partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (Partial) response.Content.Headers.ContentRange = new(0, bytes.Length - 1, bytes.Length + (Incomplete ? 1 : 0));
            return Task.FromResult(response);
        }
    }
    private AviationReference Service(Transport handler) => new(root, root, new HttpClient(handler), unmetered: () => true);
    private async Task<AviationDownloadJob> Original(AviationReference service)
    {
        service.Configure(true); var page = await service.Refresh("nasr"); var item = Assert.Single(page.Charts);
        var job = await service.PreviewDownload("nasr", item.Id, 1048576, 1048576); service.DownloadControl(job.Id, job.Revision, "approve", job.Preview.SelectionHash);
        await service.ProcessDownloads(); return Assert.Single(service.DownloadJobs());
    }
    [Theory] [InlineData("FIX", 2)] [InlineData("NAV", 1)] [InlineData("APT", 5)]
    public async Task ActualWorkerApprovalImportIndexedViewportAndOfflineReopening(string family, int count)
    {
        var handler = new Transport { Family = family, Partial = true }; string id; int calls;
        using (var service = Service(handler))
        {
            var job = await Original(service); Assert.Equal("Completed", job.State); var dataset = await service.ImportVectors(job.Id);
            Assert.Equal(count, dataset.Features); Assert.Contains("NAD83", dataset.SourceCrs); id = dataset.Id;
            Assert.Equal(dataset.Id, (await service.ImportVectors(job.Id)).Id); Assert.Single(service.VectorDatasets());
            var page = service.VectorFeatures(id, -76, 39, -74, 41); Assert.NotEmpty(page.Features);
            Assert.All(page.Features, f => Assert.Equal(1, f.Version));
            if (family == "FIX") { Assert.Single(page.Features); Assert.Single(service.VectorFeatures(id, -123, 36, -121, 38).Features); }
            if (family == "APT") { Assert.Equal(2, Assert.Single(page.Features, f => f.Kind == "runway").Coordinates.Length); Assert.Contains(page.Features, f => f.Name.Contains("Synthetic, Field", StringComparison.Ordinal)); }
            Assert.Throws<ArgumentException>(() => service.VectorFeatures(id, 170, -1, -170, 1));
            Assert.Throws<ArgumentException>(() => service.VectorFeatures(id, -180, -80, 180, 80));
            Assert.Throws<ArgumentException>(() => service.VectorFeatures(id, -76, 39, -74, 41, "invented"));
            Assert.Throws<ArgumentException>(() => service.VectorFeatures(id, -76, 39, -74, 41, offset: 1));
            service.Configure(false); calls = handler.Calls;
        }
        using var reopened = Service(handler); Assert.Equal(id, Assert.Single(reopened.VectorDatasets()).Id);
        Assert.NotEmpty(reopened.VectorFeatures(id, -76, 39, -74, 41).Features); Assert.Equal(calls, handler.Calls);
        Assert.False(File.Exists(Path.Combine(root, "missions.db"))); Assert.False(Directory.Exists(Path.Combine(root, "ScienceLibrary")));
    }
    [Fact] public async Task IncompleteRangeCannotPublishOrImport()
    {
        using var service = Service(new() { Partial = true, Incomplete = true }); var job = await Original(service);
        Assert.Equal("Failed", job.State); Assert.Empty(service.VectorDatasets()); await Assert.ThrowsAsync<ArgumentException>(() => service.ImportVectors(job.Id));
    }
    [Fact] public async Task ChangedOriginalAndConflictingCycleCannotBeIndexed()
    {
        using var service = Service(new()); var job = await Original(service); var path = Path.Combine(root, "AviationReference", "Originals", job.Id.ToString("N"), "files", "FIX_BASE.csv");
        var text = await File.ReadAllTextAsync(path); await File.WriteAllTextAsync(path, text.Replace("2026/10/01", "2026/10/29"));
        await Assert.ThrowsAsync<IOException>(() => service.ImportVectors(job.Id)); Assert.Empty(service.VectorDatasets());
    }
    [Fact] public async Task SchemaTwoMigrationPreservesJobsAndBackupAndFailedImportIsAtomic()
    {
        using var service = Service(new()); var job = await Original(service); var folder = Path.Combine(root, "AviationReference");
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(folder, "aviation.db") + ";Pooling=False"))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE vector_features; DROP TABLE vector_datasets; PRAGMA user_version=2"; cmd.ExecuteNonQuery(); }
        var dataset = await service.ImportVectors(job.Id); Assert.Single(service.VectorDatasets()); Assert.Equal(job.Id, Assert.Single(service.DownloadJobs()).Id);
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(folder, "Backups"), "aviation-schema2-*.sqlite"));
        using var saved = new SqliteConnection("Data Source=" + backup + ";Mode=ReadOnly;Pooling=False"); saved.Open(); using var check = saved.CreateCommand(); check.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", check.ExecuteScalar()); check.CommandText = "PRAGMA user_version"; Assert.Equal(2L, check.ExecuteScalar());
        var feature = service.VectorFeatures(dataset.Id, -76, 39, -74, 41).Features[0];
        using var store = new AviationMetadataStore(folder); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.SaveVectors(dataset with { Id = new string('e',64), JobId = job.Id, Features = 1 }, [feature], cancelled.Token));
        Assert.Single(store.VectorDatasets());
    }
    [Fact] public async Task SameNavaidIdentifierInDifferentCitiesIsNotCollapsed()
    {
        using var service = Service(new() { Family = "NAV", DuplicateNavaid = true }); var job = await Original(service);
        var dataset = await service.ImportVectors(job.Id); Assert.Equal(2, dataset.Features);
        var features = service.VectorFeatures(dataset.Id, -76, 39, -74, 41).Features;
        Assert.Equal(2, features.Select(f => f.Id).Distinct().Count()); Assert.Equal(2, features.Select(f => f.Attributes["CITY"]).Distinct().Count());
    }
    [Fact] public async Task DecimalDmsDisagreementPreservesOriginalAndRejectsIndex()
    {
        using var service = Service(new() { BadCoordinates = true }); var job = await Original(service);
        Assert.Equal("Completed", job.State); await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportVectors(job.Id)); Assert.Empty(service.VectorDatasets());
    }
    [Theory] [InlineData(3)] [InlineData(8)]
    public async Task RecordingInterruptsIndexingAndTransactionalPublicationWithoutLosingOriginal(int stopAt)
    {
        var armed = false; var checks = 0; var handler = new Transport { ManyFixes = 1024 };
        using var service = new AviationReference(root, root, new HttpClient(handler), unmetered: () => true, flightActive: () => armed && ++checks >= stopAt);
        var job = await Original(service); armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportVectors(job.Id));
        Assert.Empty(service.VectorDatasets()); Assert.Equal("Completed", Assert.Single(service.DownloadJobs()).State);
        armed = false; var dataset = await service.ImportVectors(job.Id); Assert.Equal(1024, dataset.Features);
        var first = service.VectorFeatures(dataset.Id, -76, 39, -74, 41); var second = service.VectorFeatures(dataset.Id, -76, 39, -74, 41, offset:250);
        Assert.Equal(1024, first.Total); Assert.Equal(250, first.Features.Length); Assert.Equal(250, second.Features.Length);
        Assert.Empty(first.Features.Select(f => f.Id).Intersect(second.Features.Select(f => f.Id)));
    }
    [Fact] public async Task MissingDriveDoesNotReturnCachedGeometryOrCreateAReplacement()
    {
        var handler = new Transport(); using var service = Service(handler); var job = await Original(service); var dataset = await service.ImportVectors(job.Id);
        Assert.NotEmpty(service.VectorFeatures(dataset.Id, -76, 39, -74, 41).Features);
        var folder = Path.Combine(root, "AviationReference"); Directory.Move(folder, folder + "-offline");
        Assert.Throws<IOException>(() => service.VectorFeatures(dataset.Id, -76, 39, -74, 41)); Assert.False(Directory.Exists(folder));
        Directory.Move(folder + "-offline", folder); Assert.NotEmpty(service.VectorFeatures(dataset.Id, -76, 39, -74, 41).Features); Assert.Equal(3, handler.Calls);
    }
    private sealed class Capacity : IStorageCapacity
    { public long Free = 1073741824; public long FreeBytes(string directory) => Free; }
    [Fact] public async Task IndexingRespectsPhysicalFreeSpaceAndPreservesVerifiedOriginal()
    {
        var disk = new Capacity(); using var service = new AviationReference(root, root, new HttpClient(new Transport()), capacity: disk, unmetered: () => true);
        var job = await Original(service); disk.Free = 0;
        await Assert.ThrowsAsync<IOException>(() => service.ImportVectors(job.Id)); Assert.Empty(service.VectorDatasets());
        Assert.Equal("Completed", Assert.Single(service.DownloadJobs()).State); disk.Free = 1073741824;
        Assert.Equal(2, (await service.ImportVectors(job.Id)).Features);
    }
    [Fact] public void UnadvertisedCyclesAndUnsafeNasrOriginalsAreNotGuessed()
    {
        Assert.Throws<InvalidDataException>(() => AviationCatalog.NasrCycles("<p>Maintenance</p>"u8.ToArray(), new(2026, 10, 23)));
        Assert.Throws<InvalidDataException>(() => AviationCatalog.OriginalIdentity("https://nfdc.faa.gov/webContent/28DaySub/extra/01_Oct_2026_CSV.zip"));
        Assert.Throws<InvalidDataException>(() => AviationCatalog.OriginalIdentity(SyntheticNasr.Url("FIX") + "?token=private"));
        var cycles = AviationCatalog.NasrCycles("<a href='NASR_Subscription/2026-10-01'>Current</a><a href='NASR_Subscription/2026-10-29'>Next</a>"u8.ToArray(), new(2026, 10, 23)); Assert.Equal(2, cycles.Length);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
