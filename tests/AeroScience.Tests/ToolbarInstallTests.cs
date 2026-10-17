using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Engine;
using Xunit;

namespace AeroScience.Tests;

public sealed class ToolbarInstallTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-install", Guid.NewGuid().ToString("N"));
    private IEnumerable<string> DiscoverFixtureCommunityFolders() =>
        ToolbarInstaller.DiscoverCommunityFolders(Path.Combine(root, "roaming"), Path.Combine(root, "local"));
    private void InstallFixture(string input, string community, Func<bool> simulatorRunning, string? previousCommunity = null) =>
        ToolbarInstaller.Install(input, community, simulatorRunning, previousCommunity, DiscoverFixtureCommunityFolders);

    private string Fixture()
    {
        // Structural fixtures only: never an official SDK build or simulator proof.
        var input = Path.Combine(root, "input"); var package = Path.Combine(input, "aeroscience-toolbar"); Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(new { title = "AeroScience", package_version = typeof(ToolbarInstaller).Assembly.GetName().Version!.ToString(3) }));
        File.WriteAllText(Path.Combine(package, "layout.json"), "{}");
        Directory.CreateDirectory(Path.Combine(package, "InGamePanels"));
        File.WriteAllBytes(Path.Combine(package, "InGamePanels", "aeroscience_panel.spb"), new byte[64]);
        var hashes = Directory.GetFiles(package, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(package, p).Replace('\\', '/'), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant());
        File.WriteAllText(Path.Combine(input, "sdk-build.json"), JsonSerializer.Serialize(new { variant = "toolbar-full", files = hashes }));
        return input;
    }
    [Fact]
    public void InstallAndUpdatePreserveOtherAddonsAndBackUpOriginalOutsideCommunity()
    {
        var input = Fixture(); var community = Path.Combine(root, "Packages", "Community"); Directory.CreateDirectory(community);
        File.WriteAllText(Path.Combine(community, "unrelated.txt"), "preserve");
        InstallFixture(input, community, () => false);
        File.WriteAllText(Path.Combine(community, "aeroscience-toolbar", "previous-version.txt"), "old installation");
        InstallFixture(input, community, () => false);
        Assert.True(File.Exists(Path.Combine(community, "aeroscience-toolbar", "manifest.json")));
        Assert.False(File.Exists(Path.Combine(community, "aeroscience-toolbar", "previous-version.txt")));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "Packages", ".aeroscience-backups"), "previous-version.txt", SearchOption.AllDirectories));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(community, "unrelated.txt")));
    }
    [Fact]
    public void RunningSimulatorOrChangedSdkFilesPreventInstallation()
    {
        var input = Fixture(); var community = Path.Combine(root, "Community"); Directory.CreateDirectory(community);
        Assert.Throws<IOException>(() => InstallFixture(input, community, () => true));
        File.AppendAllText(Path.Combine(input, "aeroscience-toolbar", "manifest.json"), "tampered");
        Assert.Throws<IOException>(() => InstallFixture(input, community, () => false));
        Assert.Empty(Directory.GetFileSystemEntries(community));
    }

    [Fact]
    public void RemovalRequiresConsentClosedSimulatorAndPreservesBackupsAndOtherAddons()
    {
        var input = Fixture(); var community = Path.Combine(root, "Packages", "Community"); Directory.CreateDirectory(community);
        InstallFixture(input, community, () => false);
        File.WriteAllText(Path.Combine(community, "unrelated.txt"), "preserve");
        Assert.Throws<InvalidOperationException>(() => ToolbarInstaller.Remove(community, false, () => false));
        Assert.Throws<IOException>(() => ToolbarInstaller.Remove(community, true, () => true));
        var backup = ToolbarInstaller.Remove(community, true, () => false);
        Assert.True(File.Exists(Path.Combine(backup!, "manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(community, "aeroscience-toolbar")));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(community, "unrelated.txt")));
        Assert.Null(ToolbarInstaller.Remove(community, true, () => false));
    }

    [Fact]
    public void DiscoveryReads2024SteamAndStoreCustomPathsWithoutSelecting2020()
    {
        var roaming = Path.Combine(root, "roaming"); var local = Path.Combine(root, "local");
        var steamConfig = Path.Combine(roaming, "Microsoft Flight Simulator 2024");
        var storeConfig = Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache");
        var legacyConfig = Path.Combine(roaming, "Microsoft Flight Simulator");
        foreach (var (config, packages) in new[] { (steamConfig, "Steam custom"), (storeConfig, "Store custom"), (legacyConfig, "Legacy 2020") })
        {
            Directory.CreateDirectory(config); var path = Path.Combine(root, packages); Directory.CreateDirectory(Path.Combine(path, "Community"));
            File.WriteAllText(Path.Combine(config, "UserCfg.opt"), "InstalledPackagesPath \"" + path + "\"");
        }
        var discovered = ToolbarInstaller.DiscoverCommunityFolders(roaming, local).ToArray();
        Assert.Equal(2, discovered.Length); Assert.Contains(Path.Combine(root, "Steam custom", "Community"), discovered);
        Assert.Contains(Path.Combine(root, "Store custom", "Community"), discovered);
        Assert.DoesNotContain(Path.Combine(root, "Legacy 2020", "Community"), discovered);
    }

    [Fact]
    public void PreviousConfiguredCommunityCopyCannotBeSilentlyDuplicated()
    {
        var input = Fixture(); var old = Path.Combine(root, "old", "Community"); var next = Path.Combine(root, "next", "Community");
        Directory.CreateDirectory(old); Directory.CreateDirectory(next); InstallFixture(input, old, () => false);
        Assert.Throws<IOException>(() => InstallFixture(input, next, () => false, old));
        Assert.Empty(Directory.GetFileSystemEntries(next)); Assert.True(File.Exists(Path.Combine(old, "aeroscience-toolbar", "manifest.json")));
    }

    [Fact]
    public void DiscoveredCommunityCopyPreventsInstallationWithoutChangingEitherFolder()
    {
        var input = Fixture(); var community = Path.Combine(root, "selected", "Community"); Directory.CreateDirectory(community);
        var packages = Path.Combine(root, "Discovered packages"); var existingCommunity = Path.Combine(packages, "Community");
        var existing = Path.Combine(existingCommunity, "aeroscience-toolbar"); Directory.CreateDirectory(existing);
        var marker = Path.Combine(existing, "retained.txt"); File.WriteAllText(marker, "existing user toolbar");
        var config = Path.Combine(root, "roaming", "Microsoft Flight Simulator 2024"); Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "UserCfg.opt"), "InstalledPackagesPath \"" + packages + "\"");

        var error = Assert.Throws<IOException>(() => InstallFixture(input, community, () => false));
        Assert.Contains(existingCommunity, error.Message);
        Assert.Equal("existing user toolbar", File.ReadAllText(marker));
        Assert.Empty(Directory.GetFileSystemEntries(community));
        Assert.False(Directory.Exists(Path.Combine(root, "selected", ".aeroscience-backups")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "selected"), ".aeroscience-stage-*"));
    }

    [Fact]
    public void RenamedCopiesAndUnrecognizedTargetArePreserved()
    {
        var input = Fixture(); var community = Path.Combine(root, "Community"); Directory.CreateDirectory(community);
        InstallFixture(input, community, () => false);
        var target = Path.Combine(community, "aeroscience-toolbar"); var renamed = Path.Combine(community, "old-copy");
        Directory.Move(target, renamed);
        Assert.Throws<InvalidOperationException>(() => InstallFixture(input, community, () => false));
        Assert.True(File.Exists(Path.Combine(renamed, "manifest.json"))); Assert.False(Directory.Exists(target));
        Directory.Move(renamed, target);
        File.WriteAllText(Path.Combine(target, "manifest.json"), "{\"title\":\"Some other add-on\"}");
        Assert.Throws<IOException>(() => InstallFixture(input, community, () => false));
        Assert.Throws<IOException>(() => ToolbarInstaller.Remove(community, true, () => false));
        Assert.Contains("Some other add-on", File.ReadAllText(Path.Combine(target, "manifest.json")));
    }

    private sealed class Reporter(List<ToolbarProgress> events) : IProgress<ToolbarProgress>
    { public void Report(ToolbarProgress progress) => events.Add(progress); }
    [Fact]
    public void ActualManifestVersionsDriveStatusesWithoutRepeatedSessionPrompts()
    {
        var bundle = Fixture(); var community = Path.Combine(root, "Community"); Directory.CreateDirectory(community);
        var missing = ToolbarVersions.Inspect(bundle, community); Assert.Equal(ToolbarInstallState.Missing, missing.Status);
        var policy = new ToolbarPromptPolicy(); Assert.True(policy.ShouldPrompt(missing)); policy.Dismiss(missing); Assert.False(policy.ShouldPrompt(missing));
        var target = Path.Combine(community, "aeroscience-toolbar"); Directory.CreateDirectory(target);
        void Manifest(string version) => File.WriteAllText(Path.Combine(target, "manifest.json"), JsonSerializer.Serialize(new { title = "AeroScience", package_version = version }));
        Manifest("0.1.1"); var old = ToolbarVersions.Inspect(bundle, community); Assert.Equal(ToolbarInstallState.UpdateAvailable, old.Status); Assert.True(policy.ShouldPrompt(old));
        Manifest(typeof(ToolbarInstaller).Assembly.GetName().Version!.ToString(3)); Assert.Equal(ToolbarInstallState.Current, ToolbarVersions.Inspect(bundle, community).Status);
        Manifest("9.0.0"); Assert.Equal(ToolbarInstallState.Incompatible, ToolbarVersions.Inspect(bundle, community).Status); Assert.Throws<IOException>(() => InstallFixture(bundle, community, () => false));
        File.WriteAllText(Path.Combine(target, "manifest.json"), "bad JSON"); Assert.Equal(ToolbarInstallState.Unknown, ToolbarVersions.Inspect(bundle, community).Status);
        Manifest("0.1.1"); var metadata = Path.Combine(target, "html_ui", "InGamePanels", "AeroScience"); Directory.CreateDirectory(metadata); File.WriteAllText(Path.Combine(metadata, "toolbar-compatibility.json"), "{\"protocol\":99}"); Assert.Equal(ToolbarInstallState.Incompatible, ToolbarVersions.Inspect(bundle, community).Status);
    }
    [Fact]
    public void RealProgressReportsMeasuredFilesAndFinalHashFailureRestoresOriginal()
    {
        var bundle = Fixture(); var community = Path.Combine(root, "Packages", "Community"); Directory.CreateDirectory(community); InstallFixture(bundle, community, () => false);
        var target = Path.Combine(community, "aeroscience-toolbar"); File.WriteAllText(Path.Combine(target, "original.txt"), "keep old");
        var events = new List<ToolbarProgress>(); var logs = Path.Combine(root, "logs");
        Assert.Throws<IOException>(() => ToolbarInstaller.Install(bundle, community, () => false, discoverCommunityFolders: DiscoverFixtureCommunityFolders,
            context: new(new Reporter(events), logs, BeforeVerifyFile: file => File.AppendAllText(file, "changed after activation"))));
        Assert.Equal("keep old", File.ReadAllText(Path.Combine(target, "original.txt"))); Assert.Contains(events, e => e.Stage == "Recovery completed"); Assert.DoesNotContain(events, e => e.Stage == "Installation completed");
        events.Clear(); ToolbarInstaller.Install(bundle, community, () => false, discoverCommunityFolders: DiscoverFixtureCommunityFolders, context: new(new Reporter(events), logs));
        Assert.Contains(events, e => e.Stage == "Installation completed"); Assert.Contains(events, e => e.Stage == "Verifying installed files" && e.Percent == 100);
        Assert.All(events.Where(e => e.Percent is not null), e => Assert.Equal((int)(e.Completed! * 100 / e.Total!), e.Percent)); Assert.All(events.Where(e => e.Total is null), e => Assert.Null(e.Percent)); Assert.NotEmpty(Directory.GetFiles(logs, "*.log"));
    }
    [Fact]
    public void InterruptedCopyAndConcurrentInstallCannotReplaceExistingToolbar()
    {
        var bundle = Fixture(); var community = Path.Combine(root, "Packages", "Community"); Directory.CreateDirectory(community); InstallFixture(bundle, community, () => false);
        var file = Path.Combine(community, "aeroscience-toolbar", "manifest.json"); var original = File.ReadAllText(file);
        Assert.Throws<IOException>(() => ToolbarInstaller.Install(bundle, community, () => false, discoverCommunityFolders: DiscoverFixtureCommunityFolders,
            context: new(CopyFile: (from, to) => { Assert.Throws<IOException>(() => InstallFixture(bundle, community, () => false)); throw new IOException("disk disconnected"); })));
        Assert.Equal(original, File.ReadAllText(file)); Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(community)!, ".aeroscience-stage-*"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
