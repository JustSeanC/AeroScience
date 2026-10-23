using System.Net;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed record AviationReferencePreferences(int Version = 1, bool OnlineEnabled = false, Guid? StoreId = null,
    string? StorageDirectory = null, bool UnmeteredOnly = true);
public sealed record AviationReferenceConfiguration(bool OnlineEnabled, bool Configured, string Privacy,
    int MaximumMetadataBytes = AviationCatalog.MaximumMetadataBytes, int MinimumRefreshSeconds = 300);
public sealed record AviationReferencePage(string Kind, string Status, AviationCacheStatus? Cache,
    int Offset, int Total, AviationCatalogItem[] Charts, AviationTfrNotice[] Notices, string[] Limitations);

// User-requested public metadata only. Original-download jobs/rendering are later phases.
// Explicit aviation consent/budget; no scientific quotas, source IDs, mission creation or provider calls on reads.
public sealed partial class AviationReference(string configurationDirectory, string dataDirectory, HttpClient? transport = null, TimeProvider? time = null,
    IStorageCapacity? capacity = null, Func<bool?>? unmetered = null, Func<bool>? flightActive = null) : IDisposable
{
    public const string Privacy = "FAA requests reveal your IP address to FAA providers. Aviation reference access is separate from online maps and scientific-data consent. Each metadata request is bounded to 4 MiB; NASR refresh reads its index plus at most two advertised cycle pages. Original ZIPs require a separate exact-edition preview, explicit approval and aviation transfer/storage allowances. Opening maps or catalogs never downloads originals. Retained records remain available offline. Flight-simulation reference only, not a real-world safety product.";
    private readonly string settingsPath = Path.Combine(configurationDirectory, "aviation-reference.json");
    private string directory => Preferences().StorageDirectory ?? Path.Combine(dataDirectory, "AviationReference");
    private readonly HttpClient http = transport ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim refresh = new(1);
    private readonly object gate = new();
    private AviationReferencePreferences Preferences()
    {
        if (!File.Exists(settingsPath)) return new();
        if (new FileInfo(settingsPath).Length > 4096) throw new IOException("Aviation preferences are invalid; existing files were preserved.");
        var p = JsonSerializer.Deserialize<AviationReferencePreferences>(File.ReadAllText(settingsPath)) ?? throw new IOException("Missing aviation preferences.");
        if (p.Version != 1) throw new NotSupportedException("Unsupported aviation preferences version. Preserve this configuration.");
        return p;
    }
    public AviationReferenceConfiguration Configuration()
    { lock (gate) { var p = Preferences(); return new(p.OnlineEnabled, p.StoreId is not null, Privacy); } }
    public AviationReferenceConfiguration Configure(bool enabled)
    {
        lock (gate)
        {
            var p = Preferences();
            if (p.StoreId is null && enabled)
            {
                if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("An unregistered aviation directory already exists. Preserve it for recovery; no replacement was created.");
                using var store = new AviationMetadataStore(directory, create: true); p = p with { StoreId = store.StoreId };
            }
            PreferenceStore.WriteAtomic(settingsPath, p with { OnlineEnabled = enabled }); return Configuration();
        }
    }
    private AviationIntegrityStamp? integrityStamp;
    private DateTimeOffset? lastIntegrityUtc;
    // Diagnostics only; original-file hash/CRC verification is a separate unchanged path.
    public DateTimeOffset? LastIntegrityValidationUtc { get { lock (gate) return lastIntegrityUtc; } }
    private AviationMetadataStore Open(AviationReferencePreferences p)
    {
        if (p.StoreId is not {} expected) throw new InvalidOperationException("Enable FAA reference access on the companion PC before the first search.");
        var now = clock.GetUtcNow();
        var fresh = lastIntegrityUtc is {} last && now >= last && now - last < TimeSpan.FromMinutes(5);
        try
        {
            // Schema, identity, path/link checks and one-writer ownership still run on
            // every open. Avoid scanning a large immutable reference inventory for
            // every 64-KiB transfer accounting operation or idle worker poll.
            var store = new AviationMetadataStore(directory, integrity: fresh ? integrityStamp : null);
            if (store.StoreId != expected) { store.Dispose(); throw new IOException("Configured aviation storage identity changed. Reconnect the original store; no replacement was opened."); }
            integrityStamp = store.IntegrityStamp;
            if (store.FullIntegrityCheckPerformed) lastIntegrityUtc = now;
            return store;
        }
        catch { integrityStamp = null; lastIntegrityUtc = null; throw; }
    }
    private static string Url(string kind) => kind switch
    { "vfr" => AviationCatalog.VfrUrl, "ifr" => AviationCatalog.IfrUrl, "nasr" => AviationCatalog.NasrUrl, "tfr" => AviationCatalog.TfrUrl, _ => throw new ArgumentException("Select VFR, IFR, NASR or TFR references.") };
    public AviationReferencePage Page(string kind, int offset = 0)
    {
        _ = Url(kind); if (offset is < 0 or > 2048 || offset % 50 != 0) throw new ArgumentException("Use a bounded 50-item aviation page.");
        lock (gate)
        {
            var p = Preferences();
            if (p.StoreId is null) return new(kind, "Not searched", null, offset, 0, [], [], ["No cached FAA search. Enabling access does not download original charts."]);
            using var store = Open(p); var status = store.Status(kind);
            var stale = status.LastSuccessUtc is {} success && clock.GetUtcNow() - success > TimeSpan.FromHours(kind == "tfr" ? 0.25 : 24);
            var state = status.LastSuccessUtc is null ? "Not searched" : !p.OnlineEnabled ? "Offline cached" : stale || status.Error is not null ? "Stale cached" : "Cached";
            if (kind == "tfr")
            {
                var snapshot = store.Notices(); return new(kind, state, status, offset, snapshot?.Notices.Length ?? 0, [], snapshot?.Notices.Skip(offset).Take(50).ToArray() ?? [], snapshot?.Limitations ?? ["No successful notice refresh. Restriction status is Unknown."]);
            }
            var charts = store.Catalog(kind); return new(kind, state, status, offset, charts?.Items.Length ?? 0, charts?.Items.Skip(offset).Take(50).ToArray() ?? [], [], charts?.Limitations ?? ["No successful chart catalog refresh. Coverage is Unknown."]);
        }
    }
    public async Task<AviationReferencePage> Refresh(string kind, CancellationToken token = default)
    {
        var url = Url(kind); if (!await refresh.WaitAsync(0, token)) throw new InvalidOperationException("Aviation metadata refresh is already running.");
        var attempt = clock.GetUtcNow();
        try
        {
            lock (gate)
            {
                var p = Preferences(); if (!p.OnlineEnabled) throw new InvalidOperationException("FAA online reference access is disabled. Retained metadata is available offline.");
                using var store = Open(p); var previous = store.Status(kind).LastAttemptUtc;
                if (previous is {} last && attempt - last < TimeSpan.FromMinutes(5)) throw new InvalidOperationException("FAA refresh is limited to once per category every five minutes. This is an application policy, not an FAA availability guarantee.");
                store.Attempt(kind, attempt, null);
            }
            byte[] bytes;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.UserAgent.ParseAdd("AeroScience/0.6.0");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("Unsupported FAA redirect; no request was forwarded.");
                if (!response.IsSuccessStatusCode)
                {
                    Failure(kind, attempt, response.StatusCode == HttpStatusCode.TooManyRequests ? "RateLimited" : response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "NotAuthorized" : "ProviderUnavailable");
                    return Page(kind);
                }
                if (response.Content.Headers.ContentLength > AviationCatalog.MaximumMetadataBytes) throw new InvalidDataException("FAA metadata exceeds the 4 MiB request limit.");
                await using var input = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream(); var buffer = new byte[32768];
                while (true)
                {
                    var count = await input.ReadAsync(buffer, token); if (count == 0) break;
                    if (output.Length + count > AviationCatalog.MaximumMetadataBytes) throw new InvalidDataException("FAA metadata exceeds the 4 MiB request limit.");
                    output.Write(buffer, 0, count);
                }
                bytes = output.ToArray();
                // Validate before taking the writer; malformed content never replaces successful snapshots.
                var retrieved = clock.GetUtcNow();
                AviationCatalogSnapshot? charts;
                if (kind == "nasr")
                {
                    var pages = new List<byte[]>();
                    foreach (var cycle in AviationCatalog.NasrCycles(bytes, DateOnly.FromDateTime(retrieved.UtcDateTime)))
                        pages.Add(await NasrMetadata(cycle, token));
                    charts = AviationCatalog.NasrCharts(pages, retrieved);
                }
                else charts = kind == "tfr" ? null : AviationCatalog.Charts(bytes, retrieved);
                var notices = kind == "tfr" ? AviationCatalog.Notices(bytes, retrieved) : null;
                lock (gate)
                {
                    var p = Preferences(); if (!p.OnlineEnabled) return Page(kind); // Consent revoked during request.
                    using var store = Open(p); if (charts is not null) store.Save(kind, charts); else store.Save(notices!);
                    store.Attempt(kind, attempt, null);
                }
                return Page(kind);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
            {
                Failure(kind, attempt, error is TaskCanceledException ? "Timeout" : error is HttpRequestException ? "NetworkUnavailable" : "InvalidMetadata");
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                return Page(kind);
            }
        }
        finally { refresh.Release(); }
    }
    private void Failure(string kind, DateTimeOffset attempt, string code)
    { lock (gate) { using var store = Open(Preferences()); store.Attempt(kind, attempt, code); } }
    public void Dispose() { shutdown.Cancel(); refresh.Dispose(); if (transport is null) http.Dispose(); }
}
