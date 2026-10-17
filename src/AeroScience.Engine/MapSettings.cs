using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Missions;

namespace AeroScience.Engine;

public sealed record MapPreferences(bool OnlineEnabled = false, string Layer = "offline", string Provider = "esri");
public sealed record MapSettingsRequest(bool OnlineEnabled, string Layer = "offline", string Provider = "esri", string? PublicApiKey = null, bool RemoveKey = false, bool PrivacyAccepted = false);
public sealed record MapConfiguration(MapPreferences Preferences, bool KeyConfigured, string? Warning = null, string Revision = "default");
internal sealed record StoredMapSettings(MapPreferences Preferences, string? ProtectedKey);
public sealed record BasemapTiles(string Url, string Attribution, int MaxZoom = 19);
public sealed record BasemapClient(string Layer, BasemapTiles[] Tiles, string? Error = null);
public interface IMapKeyProtector { string Protect(string value); string Unprotect(string value); }

// DPAPI CurrentUser. No plaintext fallback or dependencies on a shared developer account.
public sealed class WindowsMapKeyProtector : IMapKeyProtector
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Configure map keys in the Windows companion. No unprotected key will be saved.");
        var handle = GCHandle.Alloc(input, GCHandleType.Pinned); Blob output = default;
        try
        {
            var source = new Blob { Size = input.Length, Data = handle.AddrOfPinnedObject() };
            var ok = protect ? CryptProtectData(ref source, "AeroScience public map client key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows could not protect/read the map key. Configure it again in Settings.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); handle.Free(); CryptographicOperations.ZeroMemory(input); }
    }
}

public sealed class MapSettings
{
    public const string Privacy = "Online maps send your IP address, viewed geographic areas and ordinary HTTP/browser information to the selected tile providers. Scientific observations, missions and private notes are not uploaded. Provider usage can be billable; set account-side quotas and alerts. AeroScience cannot enforce your provider billing cap.";
    private readonly string path;
    private readonly IMapKeyProtector protector;
    private readonly HttpClient http;
    private readonly object gate = new();
    private BasemapClient? cached;
    private DateTimeOffset cachedUtc;
    public MapSettings(string directory, IMapKeyProtector? protector = null, HttpClient? http = null)
    {
        path = Path.Combine(directory, "maps.json"); this.protector = protector ?? new WindowsMapKeyProtector();
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 2097152 };
    }
    private StoredMapSettings Load() => File.Exists(path) ? JsonSerializer.Deserialize<StoredMapSettings>(File.ReadAllText(path)) ?? throw new IOException("Map settings need repair; offline geography remains available.") : new(new(), null);
    public MapConfiguration Get()
    {
        lock (gate)
        {
            try { var s = Load(); return new(s.Preferences, !string.IsNullOrWhiteSpace(s.ProtectedKey), Revision: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s))))); }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return new(new(), false, "Map settings unavailable. Offline geography remains available; reconfigure Maps & Basemaps."); }
        }
    }
    public MapConfiguration Save(MapSettingsRequest request)
    {
        if (request.Layer is not ("offline" or "streets" or "satellite" or "satellite-labels") || request.Provider != "esri") throw new ArgumentException("Select a supported map layer/provider.");
        if (request.OnlineEnabled && !request.PrivacyAccepted) throw new ArgumentException("Review and accept the online-map privacy and billing notice first.");
        if (!request.OnlineEnabled && request.Layer != "offline") throw new ArgumentException("Enable online maps before choosing an online layer.");
        lock (gate)
        {
            var old = Load(); var key = request.RemoveKey ? null : old.ProtectedKey;
            if (!string.IsNullOrWhiteSpace(request.PublicApiKey))
            {
                if (request.PublicApiKey.Length > 4096 || request.PublicApiKey.Any(char.IsWhiteSpace)) throw new ArgumentException("Enter a public, restricted Esri API key, without whitespace. Privileged secrets are not supported.");
                try { key = protector.Protect(request.PublicApiKey); }
                catch (Exception error) when (error is PlatformNotSupportedException or CryptographicException) { throw new InvalidOperationException("The map key could not be protected. Configure it in the Windows companion; no plaintext key was saved."); }
            }
            if (request.Layer.StartsWith("satellite", StringComparison.Ordinal) && key is null) throw new ArgumentException("Configure your own public Esri basemap API key for satellite imagery, or use Offline/Streets.");
            PreferenceStore.WriteAtomic(path, new StoredMapSettings(new(request.OnlineEnabled, request.Layer, request.Provider), key)); cached = null;
            return Get();
        }
    }
    public MapConfiguration SelectLayer(string layer)
    {
        var s = Get(); return Save(new(s.Preferences.OnlineEnabled, layer, s.Preferences.Provider, PrivacyAccepted: s.Preferences.OnlineEnabled));
    }
    public async Task<BasemapClient> Client(string? selected = null, CancellationToken cancellationToken = default)
    {
        var settings = Get(); var layer = selected ?? settings.Preferences.Layer;
        if (layer == "offline") return new("offline", []);
        if (!settings.Preferences.OnlineEnabled) return new("offline", [], "Online maps are disabled. Bundled offline geography remains available.");
        if (layer == "streets") return new(layer, [new("https://tile.openstreetmap.org/{z}/{x}/{y}.png", "© OpenStreetMap contributors", 19)]);
        if (layer is not ("satellite" or "satellite-labels")) throw new ArgumentException("Unknown map layer.");
        lock (gate) if (cached is { } c && c.Layer == layer && DateTimeOffset.UtcNow - cachedUtc < TimeSpan.FromMinutes(5)) return c;
        try
        {
            string? key; lock (gate) key = Load().ProtectedKey;
            if (key is null) return new("offline", [], "Configure an Esri public API key in Settings → Maps & Basemaps.");
            key = protector.Unprotect(key);
            // Use the documented authenticated style to discover its authorized raster source.
            using var style = await Fetch("https://basemapstyles-api.arcgis.com/arcgis/rest/services/styles/v2/styles/arcgis/imagery", key, cancellationToken);
            var tiles = new List<BasemapTiles>();
            foreach (var source in style.RootElement.GetProperty("sources").EnumerateObject())
            {
                var value = source.Value;
                if (!value.TryGetProperty("type", out var type) || type.GetString() != "raster") continue;
                if (tiles.Count >= 4) throw new InvalidOperationException("The imagery style contains too many raster layers.");
                JsonDocument? sourceDoc = null;
                try
                {
                    if (!value.TryGetProperty("tiles", out _) && value.TryGetProperty("url", out var url))
                    { sourceDoc = await Fetch(url.GetString() ?? throw new InvalidOperationException("The imagery source URL is unavailable."), key, cancellationToken); value = sourceDoc.RootElement; }
                    var tileList = value.GetProperty("tiles");
                    if (tileList.ValueKind != JsonValueKind.Array || tileList.GetArrayLength() == 0) throw new InvalidOperationException("The imagery source contains no tiles.");
                    var tile = tileList[0].GetString() ?? throw new InvalidOperationException("The imagery tile URL is unavailable."); ValidateProviderUrl(tile);
                    var attribution = value.TryGetProperty("attribution", out var credit) ? credit.GetString() : null;
                    // Some documented raster styles omit TileJSON attribution. Use the
                    // authorized raster source's MapServer metadata, never an uncredited fallback.
                    if (string.IsNullOrWhiteSpace(attribution) && tile.IndexOf("/tile/", StringComparison.OrdinalIgnoreCase) is var split && split > 0)
                    {
                        using var metadata = await Fetch(tile[..split] + "?f=pjson", key, cancellationToken);
                        if (metadata.RootElement.TryGetProperty("copyrightText", out var copyright)) attribution = copyright.GetString();
                    }
                    if (string.IsNullOrWhiteSpace(attribution)) throw new InvalidOperationException("The imagery provider did not return required attribution. Offline geography remains available.");
                    var safeCredit = Regex.Replace(attribution, "<[^>]*>", " ");
                    var maximum = value.TryGetProperty("maxzoom", out var z) ? Math.Min(z.GetInt32(), 19) : 19;
                    tiles.Add(new(WithToken(tile, key), "Powered by Esri · " + safeCredit, maximum));
                }
                finally { sourceDoc?.Dispose(); }
            }
            if (tiles.Count == 0) throw new InvalidOperationException("The Esri style returned no compatible raster imagery. Offline geography remains available.");
            if (layer == "satellite-labels")
            {
                const string labels = "https://static-map-tiles-api.arcgis.com/arcgis/rest/services/static-basemap-tiles-service/v1/arcgis/imagery/labels/static";
                using var metadata = await Fetch(labels + "?f=pjson", key, cancellationToken);
                var attribution = metadata.RootElement.GetProperty("copyrightText").GetString();
                if (string.IsNullOrWhiteSpace(attribution)) throw new InvalidOperationException("The label layer is missing required attribution.");
                tiles.Add(new(WithToken(labels + "/tile/{z}/{y}/{x}", key), "Esri imagery reference labels · " + Regex.Replace(attribution, "<[^>]*>", " "), 19));
            }
            var result = new BasemapClient(layer, tiles.ToArray());
            lock (gate)
            {
                if (Get().Revision != settings.Revision) return new("offline", [], "Map settings changed; use the current layer selection.");
                cached = result; cachedUtc = DateTimeOffset.UtcNow;
            }
            return result;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException or PlatformNotSupportedException or FormatException or IOException)
        { return new("offline", [], "Satellite imagery unavailable: check your key, basemap scope, provider status and account quota. Offline geography and recording remain available."); }
    }
    private async Task<JsonDocument> Fetch(string url, string key, CancellationToken token)
    {
        ValidateProviderUrl(url);
        using var response = await http.GetAsync(WithToken(url, key), token); response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (json.RootElement.TryGetProperty("error", out _)) { json.Dispose(); throw new HttpRequestException("Provider rejected request."); } return json;
    }
    private static string WithToken(string url, string key) => url.Contains("token=", StringComparison.OrdinalIgnoreCase) ? url : url + (url.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(key);
    private static void ValidateProviderUrl(string url)
    {
        if (!Uri.TryCreate(url.Replace("{", "").Replace("}", ""), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Host is not ("basemapstyles-api.arcgis.com" or "ibasemaps-api.arcgis.com" or "basemaps-api.arcgis.com" or "static-map-tiles-api.arcgis.com" or "services.arcgisonline.com")) throw new InvalidOperationException("Provider returned an unsupported source. No request was sent to it.");
    }
}
