using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Missions;

namespace AeroScience.Engine;

public static class ToolbarInstaller
{
    public static IEnumerable<string> DiscoverCommunityFolders(string? roamingDirectory = null, string? localDirectory = null)
    {
        var roaming = roamingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = localDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var configs = new List<string> {
            Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt") };
        var packages = Path.Combine(local, "Packages");
        try
        {
            if (Directory.Exists(packages))
                foreach (var directory in Directory.EnumerateDirectories(packages, "Microsoft.*"))
                    if (Path.GetFileName(directory).Contains("Limitless", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(directory).Contains("FlightSimulator2024", StringComparison.OrdinalIgnoreCase))
                        configs.Add(Path.Combine(directory, "LocalCache", "UserCfg.opt"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Manual browsing remains available. */ }
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs.Where(File.Exists))
        {
            string contents;
            try { contents = File.ReadAllText(config); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            var match = Regex.Match(contents, "InstalledPackagesPath\\s+\"([^\"]+)\"");
            if (match.Success)
            {
                foreach (var name in new[] { "Community", "Community2024" })
                {
                    var community = Path.Combine(match.Groups[1].Value, name);
                    if (Directory.Exists(community)) found.Add(community);
                }
            }
        }
        return found;
    }

    public static string InstalledStatus(AppPreferences preferences)
    {
        if (preferences.CommunityDirectory is null) return "Community folder not configured";
        var manifest = Path.Combine(preferences.CommunityDirectory, ApplicationProfile.Current.ToolbarPackage, "manifest.json");
        try
        {
            if (!File.Exists(manifest)) return "Not installed in the selected Community folder";
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            return "Installed " + doc.RootElement.GetProperty("package_version").GetString();
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return "Installed package needs inspection"; }
    }

    private static readonly SemaphoreSlim InstallationGate = new(1, 1);
    public static void Install(string input, string community, Func<bool>? simulatorRunning = null, string? previousCommunity = null,
        Func<IEnumerable<string>>? discoverCommunityFolders = null, ToolbarInstallContext? context = null)
    {
        if (!InstallationGate.Wait(0)) throw new IOException("Another toolbar installation is already running.");
        try { InstallCore(input, community, simulatorRunning, previousCommunity, discoverCommunityFolders, context ?? new()); }
        catch (Exception error) { context?.Report(new("Installation stopped", error.Message)); throw; }
        finally { InstallationGate.Release(); }
    }
    private static void InstallCore(string input, string community, Func<bool>? simulatorRunning, string? previousCommunity,
        Func<IEnumerable<string>>? discoverCommunityFolders, ToolbarInstallContext context)
    {
        void Report(string stage, string message, int? completed = null, int? total = null) => context.Report(new(stage, message, completed, total));
        Report("Checking MSFS installation", "Checking the simulator is closed and validating the selected package.");
        simulatorRunning ??= SimulatorRunning;
        if (simulatorRunning()) throw new IOException("Close MSFS before installing/updating the toolbar.");
        var package = Path.Combine(input, ApplicationProfile.Current.ToolbarPackage);
        if (!Directory.Exists(package)) throw new IOException("This developer build has no verified SDK-built toolbar. A complete alpha must include it; no source/placeholder package was installed.");
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(input, "sdk-build.json")));
        if (receipt.RootElement.GetProperty("variant").GetString() != (ApplicationProfile.Current.Validation ? "toolbar-validation" : "toolbar-full")) throw new IOException("Diagnostic toolbar packages cannot be installed as the full release.");
        var expected = receipt.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        var files = ValidatedFiles(package);
        Report("Checking the existing toolbar", "Validating bundled files against their genuine SDK build receipt.");
        if (expected.Count != files.Count) throw new IOException("Toolbar files differ from the SDK receipt.");
        foreach (var (name, file) in files)
        {
            using var stream = File.OpenRead(file);
            if (!expected.TryGetValue(name, out var hash) || !Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Toolbar file differs from SDK provenance: " + name);
        }
        using (var manifest = JsonDocument.Parse(File.ReadAllText(files["manifest.json"])))
        {
            var version = typeof(ToolbarInstaller).Assembly.GetName().Version!;
            if (manifest.RootElement.GetProperty("package_version").GetString() != $"{version.Major}.{version.Minor}.{version.Build}") throw new IOException("Toolbar/app package versions do not match.");
        }
        var binary = File.ReadAllBytes(files[ApplicationProfile.Current.Validation ? "InGamePanels/aeroscience_validation_panel.spb" : "InGamePanels/aeroscience_panel.spb"]);
        if (binary.Length <= 32 || binary[0] == '<' || binary[0] == '{') throw new IOException("A genuine SDK-compiled registration is required.");
        if (!Directory.Exists(community)) throw new IOException("Select an existing configured Community folder.");
        StorageManager.ValidateLocalPath(Path.GetFullPath(community));
        foreach (var directory in Directory.EnumerateDirectories(community))
        {
            if (Path.GetFileName(directory).Equals(ApplicationProfile.Current.ToolbarPackage, StringComparison.OrdinalIgnoreCase)) continue;
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (manifest.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                    && string.Equals(title.GetString(), ApplicationProfile.Current.ToolbarTitle, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A renamed AeroScience toolbar copy exists at " + directory + ". Move it outside Community before installing another copy.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { /* Unrelated unreadable manifests are not modified. */ }
        }
        // Tests supply discovery rooted in their fixture profile. Application callers retain real 2024 discovery.
        var discovered = discoverCommunityFolders is null ? DiscoverCommunityFolders() : discoverCommunityFolders();
        foreach (var candidate in discovered.Concat(new[] { Path.Combine(Path.GetDirectoryName(community)!, "Community2024"), Path.Combine(Path.GetDirectoryName(community)!, "Community") })
            .Concat(previousCommunity is null ? [] : new[] { previousCommunity }))
            if (!string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(community), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(candidate, ApplicationProfile.Current.ToolbarPackage)))
                throw new IOException("Another AeroScience toolbar copy exists at " + candidate + ". Remove/move that copy outside Community before installing another.");
        var target = Path.Combine(community, ApplicationProfile.Current.ToolbarPackage);
        var targetVersion = ToolbarVersions.ReadVersion(package);
        var existingVersion = ToolbarVersions.ReadVersion(target);
        if (targetVersion is null) throw new IOException("The bundled toolbar version is unknown.");
        var status = ToolbarVersions.Inspect(input, community);
        if (status.Status == ToolbarInstallState.Incompatible) throw new IOException(status.Message);
        if (existingVersion is not null && existingVersion > targetVersion) throw new IOException("A newer toolbar is installed. It was not downgraded.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(community))!;
        var stage = Path.Combine(parent, (ApplicationProfile.Current.Validation ? ".aeroscience-validation-stage-" : ".aeroscience-stage-") + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, ApplicationProfile.Current.Validation ? ".aeroscience-validation-backups" : ".aeroscience-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        using var lease = new FileStream(Path.Combine(parent, ".aeroscience-toolbar-install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var movedOld = false;
        var activated = false;
        try
        {
            var copied = 0;
            Report("Copying staged files", "Preparing replacement files outside Community.", 0, files.Count);
            foreach (var (name, file) in files)
            {
                var to = Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (context.CopyFile is null) File.Copy(file, to, false); else context.CopyFile(file, to);
                using var stream = File.OpenRead(to);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected[name], StringComparison.OrdinalIgnoreCase)) throw new IOException("Staged copy verification failed.");
                Report("Copying staged files", name, ++copied, files.Count);
            }
            if (simulatorRunning()) throw new IOException("MSFS started during setup; close it and try again.");
            Report("Preparing a backup", "Preserving the previous AeroScience package outside Community.");
            if (Directory.Exists(target))
            {
                StorageManager.ValidateLocalPath(target);
                using var existing = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "manifest.json")));
                if (!string.Equals(existing.RootElement.GetProperty("title").GetString(), ApplicationProfile.Current.ToolbarTitle, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The target folder contains an unrecognized add-on; it was not changed.");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!); Directory.Move(target, backup); movedOld = true;
            }
            Report("Activating the update", "Switching to the staged, verified toolbar package.");
            Directory.Move(stage, target); activated = true;
            var checkedFiles = 0;
            foreach (var (name, _) in files)
            {
                var installed = Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar));
                context.BeforeVerifyFile?.Invoke(installed);
                using var stream = File.OpenRead(installed);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected[name], StringComparison.OrdinalIgnoreCase)) throw new IOException("Installed verification failed: " + name);
                Report("Verifying installed files", name, ++checkedFiles, files.Count);
            }
            Report("Confirming the new version", "Reading the installed SDK package manifest.");
            if (ToolbarVersions.ReadVersion(target) != targetVersion) throw new IOException("Installed toolbar version verification failed.");
            Report("Installation completed", $"AeroScience Toolbar {targetVersion.ToString(3)} installed successfully. Start or restart MSFS to load it.");
        }
        catch (Exception failure)
        {
            Report("Installation failed", failure.Message + " Restoring the prior package when safe.");
            if (activated && Directory.Exists(target))
                Directory.Move(target, Path.Combine(parent, (ApplicationProfile.Current.Validation ? ".aeroscience-validation-failed-" : ".aeroscience-failed-") + Guid.NewGuid().ToString("N")));
            if (movedOld && !Directory.Exists(target)) Directory.Move(backup, target);
            Report("Recovery completed", movedOld ? "The previous toolbar was restored. The failed replacement is retained outside Community." : "No previous toolbar was replaced. Diagnostic logs are retained.");
            throw;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static string? Remove(string community, bool confirmed, Func<bool>? simulatorRunning = null)
    {
        if (!InstallationGate.Wait(0)) throw new IOException("Another toolbar operation is already running.");
        try
        {
            if (!Directory.Exists(community)) throw new IOException("The configured Community folder is unavailable.");
            using var lease = new FileStream(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(community))!, ".aeroscience-toolbar-install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return RemoveCore(community, confirmed, simulatorRunning);
        }
        finally { InstallationGate.Release(); }
    }
    private static string? RemoveCore(string community, bool confirmed, Func<bool>? simulatorRunning)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm removal of the AeroScience toolbar first.");
        simulatorRunning ??= SimulatorRunning;
        if (simulatorRunning()) throw new IOException("Close MSFS before removing the AeroScience toolbar.");
        StorageManager.ValidateLocalPath(Path.GetFullPath(community));
        var target = Path.Combine(community, ApplicationProfile.Current.ToolbarPackage);
        if (!Directory.Exists(target)) return null;
        StorageManager.ValidateLocalPath(target);
        _ = ValidatedFiles(target);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "manifest.json")));
        if (!string.Equals(manifest.RootElement.GetProperty("title").GetString(), ApplicationProfile.Current.ToolbarTitle, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The toolbar folder is not recognized as AeroScience. It was preserved.");
        var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(community))!, ApplicationProfile.Current.Validation ? ".aeroscience-validation-backups" : ".aeroscience-backups", "removed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        if (simulatorRunning()) throw new IOException("MSFS started during removal. Close it and try again.");
        Directory.Move(target, backup);
        return backup;
    }

    private static Dictionary<string, string> ValidatedFiles(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Toolbar input contains links.");
                if (Directory.Exists(entry)) Walk(entry);
                else if (!files.TryAdd(Path.GetRelativePath(root, entry).Replace('\\', '/'), entry)) throw new IOException("Toolbar input contains case-colliding files.");
            }
        }
        Walk(root); return files;
    }
    public static bool SimulatorRunning()
    {
        foreach (var process in Process.GetProcesses())
            using (process)
                try { if (process.ProcessName.Contains("FlightSimulator", StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { }
        return false;
    }
}
