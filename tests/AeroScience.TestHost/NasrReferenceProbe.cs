using System.Net;
using System.Security.Cryptography;
using AeroScience.Missions;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Shared;

namespace AeroScience.Tests;

// Explicit bounded genuine network mode, plus a separate offline process with network refused.
public static class NasrReferenceProbe
{
    private sealed class Offline : HttpMessageHandler
    { public int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; throw new IOException("Offline probe must not access the network."); } }
    private sealed class Observed : DelegatingHandler
    {
        public Observed() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Console.WriteLine("Public FAA request: " + request.RequestUri!.AbsoluteUri);
            var response = await base.SendAsync(request, token);
            Console.WriteLine("Public FAA response: " + (int)response.StatusCode);
            return response;
        }
    }
    public static async Task Run(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("network" or "offline")) throw new ArgumentException("Use nasr-reference-probe network/offline and an isolated evidence directory.");
        var mode = args[0]; var directory = Path.GetFullPath(args[1]);
        if (mode == "network") { if (Directory.Exists(directory)) throw new IOException("Use a new genuine NASR evidence directory."); Directory.CreateDirectory(directory); }
        var offline = new Offline(); using var client = mode == "network" ? new HttpClient(new Observed()) { Timeout = TimeSpan.FromSeconds(20) } : new HttpClient(offline);
        using var service = mode == "network" ? new AviationReference(directory, directory, client, unmetered: () => true) : new AviationReference(directory, directory, client);
        if (mode == "network")
        {
            service.ConfigureDownloads(false, null); service.Configure(true); var catalog = await service.Refresh("nasr");
            // The reviewed cycle is deliberately explicit, never silently replaced by a newer scene/cycle.
            var selected = catalog.Charts.Where(i => i.Product == AviationProduct.Nasr && i.Edition == "2026-10-01" && i.OriginalFileName.EndsWith("_CSV.zip",StringComparison.Ordinal)).ToArray();
            if (selected.Length != 3) throw new IOException("Reviewed NASR 2026-10-01 APT/NAV/FIX originals are not in the actual FAA catalog: " + catalog.Status + " / " + catalog.Cache?.Error + ". No substitution.");
            foreach (var item in selected)
            {
                var preview = await service.PreviewDownload("nasr", item.Id, 16777216, 134217728);
                service.DownloadControl(preview.Id, preview.Revision, "approve", preview.Preview.SelectionHash);
                await service.ProcessDownloads(); var job = service.DownloadJobs().Single(j => j.Id == preview.Id);
                if (job.State != "Completed") throw new IOException("Genuine NASR acquisition failed: " + job.State + ": " + job.Error);
                var dataset = await service.ImportVectors(job.Id); Console.WriteLine(dataset.Family + ": " + dataset.Features + " actual reference features, " + dataset.OmittedRecords + " explicitly omitted records.");
            }
        }
        else
        {
            service.Configure(false);
            // Explicit validation command may import missing indexes from already verified
            // originals. It does not create approvals, download or run on an ordinary read.
            foreach (var retained in service.DownloadJobs().Where(j => j.State == "Completed" && j.Preview.Item.Product == AviationProduct.Nasr))
                await service.ImportVectors(retained.Id);
        }
        var datasets = service.VectorDatasets(); if (datasets.Length != 3) throw new IOException("Incomplete retained NASR inventories.");
        foreach (var dataset in datasets) await service.VerifyDownload(dataset.JobId);
        var reparsed = new List<object>();
        foreach (var dataset in datasets)
        {
            var job = service.DownloadJobs().Single(j => j.Id == dataset.JobId);
            var originals = Path.Combine(directory, "AviationReference", "Originals", dataset.JobId.ToString("N"), "files");
            var current = AviationNasr.Parse(originals, job, DateTimeOffset.UtcNow);
            using var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "AviationReference", "aviation.db") + ";Mode=ReadOnly;Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM vector_features WHERE dataset_id=$id ORDER BY id"; command.Parameters.AddWithValue("$id", dataset.Id);
            using var reader = command.ExecuteReader();
            IEnumerable<AviationVectorFeature> ReadRetained()
            { while (reader.Read()) yield return JsonSerializer.Deserialize<AviationVectorFeature>(reader.GetString(0), WireJson.Options)!; }
            var currentGeometry = GeometryHash(current.Features);
            var storedGeometry = GeometryHash(ReadRetained());
            if (currentGeometry != storedGeometry || current.Features.Length != dataset.Features) throw new IOException("Current parser does not reproduce retained genuine reference geometry.");
            reparsed.Add(new { dataset.Family, current.Dataset.Features, geometrySha256 = currentGeometry });
        }
        var pages = datasets.Select(dataset => new { dataset, massachusetts = service.VectorFeatures(dataset.Id, -72, 41, -70, 43), california = service.VectorFeatures(dataset.Id, -123, 36, -121, 39) }).ToArray();
        if (pages.Any(p => p.massachusetts.Total < 1 || p.california.Total < 1)) throw new IOException("Genuine references missing in one of two required regions.");
        if (offline.Calls != 0 || File.Exists(Path.Combine(directory, "missions.db"))) throw new IOException("Probe unexpectedly used the network offline or created a mission.");
        File.WriteAllText(Path.Combine(directory, "nasr-" + mode + "-receipt.json"), JsonSerializer.Serialize(new { evidenceClass = "Genuine official NASR originals, actual application worker/import/store and two regional queries; not owner-installed or geodetic-accuracy acceptance", mode, offlineNetworkCalls = offline.Calls, datasets, reparsed, jobs = service.DownloadJobs(), pages, missionCreated = false }, WireJson.Options));
        Console.WriteLine("PASS: genuine NASR retained hash/inventory/two-region queries. " + mode + " process; no mission/science credit.");
    }
    private static string GeometryHash(IEnumerable<AviationVectorFeature> features)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var feature in features)
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { feature.Id, feature.Coordinates }, WireJson.Options));
            hash.AppendData(new byte[] { 10 });
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

}
