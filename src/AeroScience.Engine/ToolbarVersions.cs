using System.Text.Json;
using AeroScience.Missions;

namespace AeroScience.Engine;

public enum ToolbarInstallState { Missing, Current, UpdateAvailable, Unknown, Incompatible }
public sealed record ToolbarVersionStatus(string? InstalledVersion, string? BundledVersion, string? CommunityDirectory,
    ToolbarInstallState Status, string Message, bool CanInstall, bool CanRepair = false, int? InstalledProtocol = null);
public sealed record ToolbarProgress(string Stage, string Message, int? Completed = null, int? Total = null)
{
    public int? Percent => Completed is { } done && Total is > 0 ? (int)Math.Clamp(done * 100L / Total.Value, 0, 100) : null;
}
public sealed record ToolbarInstallContext(IProgress<ToolbarProgress>? Progress = null, string? LogDirectory = null,
    Action<string, string>? CopyFile = null, Action<string>? BeforeVerifyFile = null)
{
    private readonly string logName = "toolbar-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log";
    public void Report(ToolbarProgress update)
    {
        Progress?.Report(update);
        if (LogDirectory is null) return;
        try { Directory.CreateDirectory(LogDirectory); File.AppendAllText(Path.Combine(LogDirectory, logName), $"{DateTimeOffset.UtcNow:O} {update.Stage}: {update.Message} ({update.Completed}/{update.Total}){Environment.NewLine}"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Installation outcome must remain visible even if its log destination is unavailable. */ }
    }
}

public static class ToolbarVersions
{
    public static Version? ReadVersion(string folder)
    {
        try
        {
            using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            if (!data.RootElement.TryGetProperty("title", out var title) || !string.Equals(title.GetString(), "AeroScience", StringComparison.OrdinalIgnoreCase)) return null;
            var value = data.RootElement.GetProperty("package_version").GetString();
            return Version.TryParse(value, out var version) && version.Build >= 0 && version.Revision <= 0 ? version : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
    private static int? Protocol(string folder)
    {
        try
        {
            using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "html_ui", "InGamePanels", "AeroScience", "toolbar-compatibility.json")));
            return data.RootElement.GetProperty("protocol").GetInt32();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }
    public static ToolbarVersionStatus Inspect(string bundle, string? community)
    {
        var bundled = ReadVersion(Path.Combine(bundle, "aeroscience-toolbar"));
        var companion = typeof(ToolbarVersions).Assembly.GetName().Version!;
        if (bundled is null || bundled.ToString(3) != companion.ToString(3)) return new(null, bundled?.ToString(3), community, ToolbarInstallState.Unknown,
            "The bundled SDK toolbar is missing, unknown, or does not match this companion. Repair the application with a complete installer.", false);
        var target = community is null ? null : Path.Combine(community, "aeroscience-toolbar");
        if (target is null || !Directory.Exists(target)) return new(null, bundled.ToString(3), community, ToolbarInstallState.Missing, "AeroScience MSFS Toolbar is not installed.", true);
        var installed = ReadVersion(target); var protocol = Protocol(target);
        if (installed is null) return new(null, bundled.ToString(3), community, ToolbarInstallState.Unknown, "The installed toolbar version cannot be determined. Inspect or repair the selected package; no version was assumed.", false, true, protocol);
        if (protocol is not null && protocol != 1) return new(installed.ToString(3), bundled.ToString(3), community, ToolbarInstallState.Incompatible, "The installed toolbar protocol is unsupported. Flight recording and the browser remain available.", false, false, protocol);
        if (installed > bundled) return new(installed.ToString(3), bundled.ToString(3), community, ToolbarInstallState.Incompatible, "A newer toolbar is installed. It will not be automatically downgraded.", false, false, protocol);
        if (installed < bundled) return new(installed.ToString(3), bundled.ToString(3), community, ToolbarInstallState.UpdateAvailable, $"AeroScience Toolbar {installed.ToString(3)} is installed. Version {bundled.ToString(3)} is available.", true, false, protocol);
        return new(installed.ToString(3), bundled.ToString(3), community, ToolbarInstallState.Current, "AeroScience Toolbar is up to date.", true, true, protocol);
    }
}

public sealed class ToolbarPromptPolicy
{
    private readonly HashSet<string> dismissed = [];
    private static string Key(ToolbarVersionStatus status) => $"{status.CommunityDirectory}|{status.InstalledVersion}|{status.BundledVersion}";
    public bool ShouldPrompt(ToolbarVersionStatus status) => status.Status is ToolbarInstallState.Missing or ToolbarInstallState.UpdateAvailable && !dismissed.Contains(Key(status));
    public void Dismiss(ToolbarVersionStatus status) => dismissed.Add(Key(status));
}
