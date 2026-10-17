using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed class SetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-setup", Guid.NewGuid().ToString("N"));
    private PreferenceStore Preferences => new(Path.Combine(root, "config"));
    private StorageManager Storage => new(Preferences);
    private SetupCoordinator Setup => new(Preferences, Storage, Path.Combine(root, "application"));

    [Fact]
    public void GuidedMigrationRetainsLegacyHistoryModesAndOriginals()
    {
        var path = Path.Combine(Preferences.DefaultMissionDirectory, "missions.db");
        var legacy = new MissionStore(path); legacy.Initialize();
        var id = legacy.Create("Previous recording", null, DateTimeOffset.UtcNow, ScienceMode.Realistic);
        legacy.Finish(id, MissionStatus.Completed, DateTimeOffset.UtcNow, "Done");
        var mission = Path.Combine(root, "drive-b", "missions"); var library = Path.Combine(root, "drive-c", "science");
        Setup.ConfigureStorage(mission, library, true);
        Assert.True(File.Exists(path)); Assert.Equal(mission, Preferences.Load().MissionDirectory);
        Assert.Equal(library, Preferences.Load().ScienceLibraryDirectory);
        Assert.Equal(ScienceMode.Realistic, new MissionStore(Path.Combine(mission, "missions.db")).Detail(id)!.Mission.Mode);
        Assert.Empty(Directory.GetFiles(Path.Combine(library, "Imagery")));
        Assert.Equal(StorageManager.LibraryId(Preferences.DefaultLibraryDirectory), StorageManager.LibraryId(library));
    }

    [Fact]
    public void UpdatingSetupPreservesSettingsAndExistingData()
    {
        var initial = Storage.Initialize() with { SetupCompleted = true, EnableLan = true, LanAddress = "192.168.1.20", SamplingIntervalMs = 2000 };
        Preferences.Save(initial);
        var libraryId = StorageManager.LibraryId(initial.ScienceLibraryDirectory);
        File.WriteAllText(Path.Combine(initial.ScienceLibraryDirectory, "Reports", "retained.txt"), "existing user data");
        Setup.ConfigureStorage(initial.MissionDirectory, initial.ScienceLibraryDirectory, true);
        Assert.Equal(initial, Preferences.Load());
        Assert.Equal(libraryId, StorageManager.LibraryId(initial.ScienceLibraryDirectory));
        Assert.Equal("existing user data", File.ReadAllText(Path.Combine(initial.ScienceLibraryDirectory, "Reports", "retained.txt")));
    }

    [Fact]
    public void ConfirmationAndConflictsAreCheckedBeforeCreatingOrChangingStores()
    {
        var mission = Path.Combine(root, "mission"); var library = Path.Combine(root, "science");
        Assert.Throws<InvalidOperationException>(() => Setup.ConfigureStorage(mission, library, false));
        Assert.False(File.Exists(Preferences.SettingsPath));
        Directory.CreateDirectory(library); File.WriteAllText(Path.Combine(library, "retained.txt"), "preserve");
        Assert.Throws<IOException>(() => Setup.ConfigureStorage(mission, library, true));
        Assert.False(File.Exists(Preferences.SettingsPath)); Assert.False(Directory.Exists(mission));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(library, "retained.txt")));
    }

    [Fact]
    public void InstallDirectoryAndItsChildrenCannotContainUserData()
    {
        var installed = Path.Combine(root, "application");
        Assert.Throws<IOException>(() => SetupCoordinator.ValidateDataLocation(installed, installed));
        Assert.Throws<IOException>(() => SetupCoordinator.ValidateDataLocation(Path.Combine(installed, "data"), installed));
        Assert.Throws<IOException>(() => SetupCoordinator.ValidateDataLocation(root, installed));
        SetupCoordinator.ValidateDataLocation(Path.Combine(root, "unavailable-science-drive"), installed, requireAvailable: false);
    }

    [Fact]
    public void KeepingUnavailableLibraryDuringOnboardingDoesNotBlockOrReplaceArchive()
    {
        var settings = Storage.Initialize();
        var store = new MissionStore(Path.Combine(settings.MissionDirectory, "missions.db"));
        var id = store.Create("Retained archive", null, DateTimeOffset.UtcNow);
        store.Finish(id, MissionStatus.Completed, DateTimeOffset.UtcNow, "Done");
        Directory.Move(settings.ScienceLibraryDirectory, Path.Combine(root, "disconnected-library"));
        Setup.ConfigureStorage(settings.MissionDirectory, settings.ScienceLibraryDirectory, true);
        Assert.False(Directory.Exists(settings.ScienceLibraryDirectory));
        Assert.Equal(settings, Preferences.Load());
        Assert.Equal(MissionStatus.Completed, store.Detail(id)!.Mission.Status);
        Assert.False(Storage.Status(StorageKind.ScienceLibrary).Available);
        Assert.True(Storage.Status(StorageKind.Missions).Available);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
