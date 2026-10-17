using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Engine;
using Xunit;

namespace AeroScience.Tests;

public sealed class ToolbarInstallTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-install", Guid.NewGuid().ToString("N"));
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
        ToolbarInstaller.Install(input, community, () => false);
        File.WriteAllText(Path.Combine(community, "aeroscience-toolbar", "previous-version.txt"), "old installation");
        ToolbarInstaller.Install(input, community, () => false);
        Assert.True(File.Exists(Path.Combine(community, "aeroscience-toolbar", "manifest.json")));
        Assert.False(File.Exists(Path.Combine(community, "aeroscience-toolbar", "previous-version.txt")));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "Packages", ".aeroscience-backups"), "previous-version.txt", SearchOption.AllDirectories));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(community, "unrelated.txt")));
    }
    [Fact]
    public void RunningSimulatorOrChangedSdkFilesPreventInstallation()
    {
        var input = Fixture(); var community = Path.Combine(root, "Community"); Directory.CreateDirectory(community);
        Assert.Throws<IOException>(() => ToolbarInstaller.Install(input, community, () => true));
        File.AppendAllText(Path.Combine(input, "aeroscience-toolbar", "manifest.json"), "tampered");
        Assert.Throws<IOException>(() => ToolbarInstaller.Install(input, community, () => false));
        Assert.Empty(Directory.GetFileSystemEntries(community));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
