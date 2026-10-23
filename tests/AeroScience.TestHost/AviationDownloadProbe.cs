using System.IO.Compression;
using System.Net;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Tests;

// Explicit real-process probe. Network mode uses the production FAA catalog and
// download path; synthetic interruption modes are labeled and never FAA evidence.
public static class AviationDownloadProbe
{
    private const string FixtureUrl = "https://aeronav.faa.gov/visual/09-03-2026/sectional-files/Synthetic.zip";
    private sealed class Handler(string directory, bool interrupt) : HttpMessageHandler
    {
        public int Transfers;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "www.faa.gov") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"<a href='{FixtureUrl}'>Synthetic original</a>") });
            Transfers++;
            using var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            { using var file = archive.CreateEntry("synthetic.tif").Open(); var content = new byte[131072]; new Random(42).NextBytes(content); new byte[] { 73, 73, 42, 0 }.CopyTo(content, 0); file.Write(content); }
            HttpContent payload = interrupt ? new StreamContent(new InterruptedRead(bytes.ToArray(), directory)) : new ByteArrayContent(bytes.ToArray());
            payload.Headers.ContentLength = bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = payload });
        }
    }
    private sealed class InterruptedRead(byte[] bytes, string directory) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await File.WriteAllTextAsync(Path.Combine(directory, "read-reserved.marker"), "synthetic outstanding read", token); await Task.Delay(Timeout.Infinite, token); return 0; }
    }
    public static async Task Run(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("interrupt" or "recover" or "resume" or "network" or "offline")) throw new ArgumentException("Use aviation-probe interrupt/recover/resume/network/offline with an explicit isolated output directory.");
        var mode = args[0]; var directory = Path.GetFullPath(args[1]);
        if (mode is "interrupt" or "network")
        { if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("Choose a new empty probe directory."); Directory.CreateDirectory(directory); }
        var handler = new Handler(directory, mode == "interrupt");
        using var http = new HttpClient(handler);
        using var service = mode == "network" ? new AviationReference(directory, directory, unmetered: () => true) : new AviationReference(directory, directory, http, unmetered: () => true);
        if (mode is "interrupt" or "network")
        {
            service.ConfigureDownloads(false, null); service.Configure(true);
            var url = mode == "network" && args.Length == 3 ? args[2] : FixtureUrl;
            var product = AviationCatalog.OriginalIdentity(url).Product;
            var kind = product is AviationProduct.IfrHigh or AviationProduct.IfrLow ? "ifr" : "vfr";
            await service.Refresh(kind);
            var all = Enumerable.Range(0, 41).SelectMany(i => service.Page(kind, i * 50).Charts);
            var item = all.SingleOrDefault(i => i.SourceUrl == url) ?? throw new IOException("Exact original is not in the actual current FAA catalog; no substitution was made.");
            var job = await service.PreviewDownload(kind, item.Id, mode == "network" ? 67108864 : 1048576, mode == "network" ? 536870912 : 1048576);
            service.DownloadControl(job.Id, job.Revision, "approve", job.Preview.SelectionHash);
            await service.ProcessDownloads();
        }
        else
        {
            await service.ProcessDownloads();
            if (mode == "recover")
            {
                var interrupted = service.DownloadJobs().Single();
                if (interrupted.State != "Interrupted" || interrupted.ChargedBytes <= 0 || interrupted.DownloadedBytes != 0 || handler.Transfers != 0)
                    throw new IOException("Interrupted aviation intent or durable allowance recovery failed.");
            }
            if (mode == "resume")
            { var job = service.DownloadJobs().Single(); service.DownloadControl(job.Id, job.Revision, "resume"); await service.ProcessDownloads(); }
        }
        var jobs = service.DownloadJobs();
        if (mode != "recover") foreach (var job in jobs)
        {
            if (job.State != "Completed") throw new IOException("Aviation original not completed: " + job.State + ": " + job.Error); await service.VerifyDownload(job.Id);
            if (mode == "offline")
            {
                var audit = Path.Combine(directory, "Audit-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var archive = Path.Combine(directory, "AviationReference", "Originals", job.Id.ToString("N"), "original.zip");
                    var verified = await AviationDownloads.VerifyArchive(archive, audit, job.Preview.MaximumExpandedBytes, default);
                    if (!verified.OrderBy(f => f.Name).SequenceEqual(job.Files!.OrderBy(f => f.Name))) throw new IOException("Offline original archive differs from its published verified files.");
                }
                finally { if (Directory.Exists(audit)) Directory.Delete(audit, true); }
            }
        }
        var receipt = new { evidenceClass = mode == "network" ? "Genuine public FAA original, application acquisition and retention only; geographic placement unassessed" : "Synthetic real-process recovery/offline probe", mode, handler.Transfers, jobs, storage = service.DownloadStorage(), missionCreated = File.Exists(Path.Combine(directory, "missions.db")) };
        PreferenceStore.WriteAtomic(Path.Combine(directory, "aviation-" + mode + "-receipt.json"), JsonSerializer.SerializeToElement(receipt, WireJson.Options));
        Console.WriteLine(JsonSerializer.Serialize(new { mode, jobs = jobs.Length, states = jobs.Select(j => j.State), handler.Transfers }, WireJson.Options));
    }
}
