using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Missions;

namespace AeroScience.Engine;

public sealed record UpdatePreferences(bool Automatic = true, bool IncludePrereleases = false);
public sealed record AvailableRelease(string Version, string ReleaseUrl, string InstallerName, bool Prerelease);
public sealed record UpdateSnapshot(string CurrentVersion, UpdatePreferences Preferences, AvailableRelease? Available = null,
    DateTimeOffset? LastCheckedUtc = null, string? Message = null, DateTimeOffset? RemindAfterUtc = null);
public static class ReleaseVersions
{
    private static (Version Base, string[] Pre) Parse(string value)
    {
        var match = Regex.Match(value, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success || !Version.TryParse($"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}", out var version)) throw new ArgumentException("Invalid semantic version.");
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (pre.Any(p => p.All(char.IsDigit) && p.Length > 1 && p[0] == '0')) throw new ArgumentException("Numeric prerelease identifiers cannot have leading zeros.");
        return (version, pre);
    }
    public static int Compare(string left, string right)
    {
        var a = Parse(left); var b = Parse(right); var main = a.Base.CompareTo(b.Base); if (main != 0) return main;
        if (a.Pre.Length == 0 || b.Pre.Length == 0) return a.Pre.Length == b.Pre.Length ? 0 : a.Pre.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var x = a.Pre[i]; var y = b.Pre[i]; var xn = x.All(char.IsDigit); var yn = y.All(char.IsDigit);
            var result = xn && yn ? x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y)
                : xn != yn ? xn ? -1 : 1 : string.CompareOrdinal(x, y);
            if (result != 0) return result;
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }
}
public sealed class ApplicationUpdates
{
    public const string CurrentVersion = "0.5.0";
    private readonly string path;
    private readonly HttpClient http;
    private readonly SemaphoreSlim check = new(1, 1);
    private readonly object gate = new();
    private UpdateSnapshot state;
    public ApplicationUpdates(string directory, HttpClient? client = null)
    {
        path = Path.Combine(directory, "updates.json"); http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 2097152 };
        state = new(CurrentVersion, new());
        try { if (File.Exists(path)) state = (JsonSerializer.Deserialize<UpdateSnapshot>(File.ReadAllText(path)) ?? state) with { CurrentVersion = CurrentVersion }; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { state = state with { Message = "Update preferences unavailable; configure them in Settings. Offline work remains available.", Preferences = new(false) }; }
        if(state.Preferences is null)state=state with{Preferences=new(false),Available=null,Message="Update preferences are invalid; offline work remains available. Review Settings → Updates."};
        if (state.Available is { } cached && !ValidCachedRelease(cached)) state = state with { Available = null, LastCheckedUtc = null };
    }
    private static bool ValidCachedRelease(AvailableRelease release)
    {
        try
        {
            return ReleaseVersions.Compare(release.Version, CurrentVersion) > 0
                && release.InstallerName == "AeroScience-Setup-v" + release.Version.TrimStart('v') + ".exe"
                && release.ReleaseUrl == "https://github.com/JustSeanC/AeroScience/releases/tag/" + Uri.EscapeDataString(release.Version);
        }
        catch (ArgumentException) { return false; }
    }
    public UpdateSnapshot Get() { lock (gate) return state; }
    private void Save() => PreferenceStore.WriteAtomic(path, state);
    public UpdateSnapshot Configure(UpdatePreferences preferences)
    { lock (gate) { state = state with { Preferences = preferences, Available = null, LastCheckedUtc = null }; Save(); return state; } }
    public UpdateSnapshot RemindLater()
    { lock (gate) { state = state with { RemindAfterUtc = DateTimeOffset.UtcNow.AddDays(1) }; Save(); return state; } }
    public async Task<UpdateSnapshot> Check(bool force = false, CancellationToken token = default)
    {
        if (ApplicationProfile.Current.Validation)
        {
            lock (gate) return state = state with { Available=null, Preferences=new(false), Message="Private validation profile. Rebuild from develop/v0.5.0; public installers do not update this profile." };
        }
        await check.WaitAsync(token);
        try
        {
            var current = Get();
            if (!force && (!current.Preferences.Automatic || current.LastCheckedUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromDays(1))) return current;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/JustSeanC/AeroScience/releases?per_page=30");
                request.Headers.UserAgent.ParseAdd("AeroScience/" + CurrentVersion); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using var response = await http.SendAsync(request, token); response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected GitHub releases.");
                AvailableRelease? available = null;
                foreach (var release in document.RootElement.EnumerateArray())
                {
                    try
                    {
                        if (release.GetProperty("draft").GetBoolean()) continue;
                        var tag = release.GetProperty("tag_name").GetString()!;
                        var pre = release.GetProperty("prerelease").GetBoolean() || tag.Contains('-');
                        if (!current.Preferences.IncludePrereleases && pre || ReleaseVersions.Compare(tag, CurrentVersion) <= 0) continue;
                        var name = "AeroScience-Setup-v" + tag.TrimStart('v') + ".exe";
                        var releaseUrl = "https://github.com/JustSeanC/AeroScience/releases/tag/" + Uri.EscapeDataString(tag);
                        if (release.GetProperty("html_url").GetString() != releaseUrl) continue;
                        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name || a.GetProperty("name").GetString() == name + ".sha256").ToArray();
                        if (assets.Length != 2 || assets.Any(a => a.GetProperty("browser_download_url").GetString() != "https://github.com/JustSeanC/AeroScience/releases/download/" + Uri.EscapeDataString(tag) + "/" + a.GetProperty("name").GetString())) continue;
                        if (available is null || ReleaseVersions.Compare(tag, available.Version) > 0) available = new(tag, releaseUrl, name, pre);
                    }
                    catch (Exception e) when (e is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException) { /* Ignore malformed individual release entries. */ }
                }
                lock (gate)
                {
                    if (state.Preferences != current.Preferences) return state;
                    state = state with { Available = available, LastCheckedUtc = DateTimeOffset.UtcNow, Message = available is null ? "No newer release with the official setup EXE and checksum was found." : "A newer AeroScience release is available. Download manually from the official GitHub release page; installers are not automatically executed." }; Save(); return state;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
            {
                lock (gate) { state = state with { LastCheckedUtc = DateTimeOffset.UtcNow, Message = "GitHub update information is unavailable. AeroScience and the offline archive continue normally." }; try { Save(); } catch (Exception writeError) when(writeError is IOException or UnauthorizedAccessException) { } return state; }
            }
        }
        finally { check.Release(); }
    }
}
public sealed class ApplicationUpdateWorker(ApplicationUpdates updates) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { do { await updates.Check(token: stoppingToken); await Task.Delay(TimeSpan.FromHours(6), stoppingToken); } while (!stoppingToken.IsCancellationRequested); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
