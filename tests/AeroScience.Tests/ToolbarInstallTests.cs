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
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{\"title\":\"AeroScience\",\"package_version\":\"0.1.1\"}");
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
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
