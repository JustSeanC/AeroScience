using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-storage", Guid.NewGuid().ToString("N"));
    private PreferenceStore Preferences => new(Path.Combine(root, "config"));
    private StorageManager Manager => new(Preferences);
    private static Guid Completed(MissionStore store, ScienceMode mode, TelemetrySource source)
    {
        var frame = new TelemetryFrame(DateTimeOffset.UtcNow, source, "T-38", 32, -106, 8000, 3000, 160, 180, 162, 75, 71, 4, 0, -2000, MachNumber: .82);
        var id = store.Create("Preserve", frame, frame.TimestampUtc, mode); store.Append(id, frame);
        store.Finish(id, MissionStatus.Completed, frame.TimestampUtc.AddSeconds(1), "Done"); return id;
    }

    [Fact]
    public void IndependentMigrationPreservesModesSourcesMachAndPortableDatasetReferences()
    {
        var initial = Manager.Initialize();
        var store = new MissionStore(Path.Combine(initial.MissionDirectory, "missions.db"));
        var live = Completed(store, ScienceMode.Realistic, TelemetrySource.Simulator);
        var historic = Completed(store, ScienceMode.Universal, TelemetrySource.Simulated);
        var libraryId = StorageManager.LibraryId(initial.ScienceLibraryDirectory);
        Directory.CreateDirectory(Path.Combine(initial.ScienceLibraryDirectory, "Reports"));
        Directory.CreateDirectory(Path.Combine(initial.ScienceLibraryDirectory, "Reports", "empty-user-folder"));
        File.WriteAllText(Path.Combine(initial.ScienceLibraryDirectory, "Reports", "test.json"), "test fixture");
        var reference = new DatasetReference(Guid.NewGuid(), libraryId, "Reports/test.json", "{}");
        store.AddDatasetReference(live, reference);
        var missionDestination = Path.Combine(root, "drive-b", "missions");
        Manager.Migrate(StorageKind.Missions, missionDestination, confirmed: true, recording: false);
        Assert.Equal(initial.ScienceLibraryDirectory, Preferences.Load().ScienceLibraryDirectory);
        Assert.True(File.Exists(Path.Combine(initial.MissionDirectory, "missions.db")));
        var libraryDestination = Path.Combine(root, "drive-c", "library");
        Manager.Migrate(StorageKind.ScienceLibrary, libraryDestination, true, false);
        var restart = Preferences.Load();
        Assert.Equal(missionDestination, restart.MissionDirectory); Assert.Equal(libraryDestination, restart.ScienceLibraryDirectory);
        var reopened = new MissionStore(Path.Combine(restart.MissionDirectory, "missions.db")); reopened.Initialize();
        Assert.Equal(ScienceMode.Realistic, reopened.Detail(live)!.Mission.Mode);
        Assert.Equal(TelemetrySource.Simulated, reopened.Detail(historic)!.Mission.Source);
        Assert.Equal(.82, reopened.Samples(live, 1, 0)[0].MachNumber);
        Assert.Equal(reference, reopened.DatasetReferences(live).Single());
        Assert.Equal("test fixture", File.ReadAllText(StorageManager.ResolveDataset(libraryDestination, libraryId, reference.RelativePath)));
        Assert.Equal(libraryId, StorageManager.LibraryId(initial.ScienceLibraryDirectory));
        Assert.True(Directory.Exists(Path.Combine(libraryDestination, "Reports", "empty-user-folder")));
    }

    [Fact]
    public void ActiveRecordingOwnershipAndExistingDestinationAreProtected()
    {
        var initial = Manager.Initialize(); var store = new MissionStore(Path.Combine(initial.MissionDirectory, "missions.db"));
        var id = store.Create("Recording", null, DateTimeOffset.UtcNow);
        var destination = Path.Combine(root, "new");
        Assert.Throws<InvalidOperationException>(() => Manager.Migrate(StorageKind.Missions, destination, true, false));
        Assert.Throws<InvalidOperationException>(() => Manager.Migrate(StorageKind.ScienceLibrary, destination, true, true));
        store.Finish(id, MissionStatus.Completed, DateTimeOffset.UtcNow, "Done");
        using (StorageManager.Acquire(initial.MissionDirectory))
            Assert.Throws<IOException>(() => Manager.Migrate(StorageKind.Missions, destination, true, false));
        Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(destination, "missions.db"), "do not overwrite");
        Assert.Throws<IOException>(() => Manager.Migrate(StorageKind.Missions, destination, true, false));
        Assert.Equal("do not overwrite", File.ReadAllText(Path.Combine(destination, "missions.db")));
        Assert.Throws<InvalidOperationException>(() => Manager.Migrate(StorageKind.Missions, destination, false, false));
    }

    [Fact]
    public void MaintenanceBlocksConcurrentMissionStartsBeforeEngineShutdown()
    {
        var initial = Manager.Initialize(); var coordinator = new MissionCoordinator(new MissionStore(Path.Combine(initial.MissionDirectory, "missions.db")), 1000);
        using (coordinator.EnterMaintenance())
        {
            Assert.Throws<InvalidOperationException>(() => coordinator.Start("Toolbar start during migration"));
            Assert.Throws<InvalidOperationException>(() => coordinator.EnterMaintenance());
        }
        var mission = coordinator.Start("Normal");
        Assert.Throws<InvalidOperationException>(() => coordinator.EnterMaintenance());
        coordinator.End(mission.Id);
    }

    private sealed class NoSpace : IStorageCapacity { public long FreeBytes(string _) => 0; }
    private sealed class NoPermission : IStorageCapacity { public long FreeBytes(string _) => throw new UnauthorizedAccessException("Permission denied"); }
    [Fact]
    public void PermissionFailuresNeverChangeSettingsAndMissingDatabaseIsNotRecreatedOnRead()
    {
        var initial = Manager.Initialize();
        Assert.Throws<UnauthorizedAccessException>(() => new StorageManager(Preferences, new NoPermission()).Migrate(StorageKind.Missions, Path.Combine(root, "denied"), true, false));
        Assert.Equal(initial, Preferences.Load());
        var store = new MissionStore(Path.Combine(initial.MissionDirectory, "missions.db"));
        File.Move(store.DatabasePath, store.DatabasePath + ".unplugged");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.List());
        Assert.False(File.Exists(store.DatabasePath));
    }
    [Fact]
    public void LowSpaceUnavailableDrivesAndUnwritablePathsDoNotSwitchOrCreateNewArchives()
    {
        var initial = Manager.Initialize(); var destination = Path.Combine(root, "full-drive");
        Assert.Throws<IOException>(() => new StorageManager(Preferences, new NoSpace()).Migrate(StorageKind.Missions, destination, true, false));
        Assert.Equal(initial, Preferences.Load());
        var blocker = Path.Combine(root, "not-a-folder"); File.WriteAllText(blocker, "occupied");
        Assert.ThrowsAny<IOException>(() => Manager.Migrate(StorageKind.Missions, Path.Combine(blocker, "child"), true, false));
        var libraryMissing = Path.Combine(root, "disconnected-library"); Directory.Move(initial.ScienceLibraryDirectory, libraryMissing);
        Assert.False(Manager.Status(StorageKind.ScienceLibrary).Available);
        Assert.True(Manager.Status(StorageKind.Missions).Available); // Archive remains usable independently.
        var missionMissing = Path.Combine(root, "disconnected-mission");
        File.Move(Path.Combine(initial.MissionDirectory, "missions.db"), missionMissing);
        Assert.Throws<IOException>(() => Manager.Initialize());
        Assert.False(File.Exists(Path.Combine(initial.MissionDirectory, "missions.db")));
    }

    [Fact]
    public void InterruptedMigrationRetainsOriginalAndCanRestoreDefaultExplicitly()
    {
        var initial = Manager.Initialize(); var destination = Path.Combine(root, "new");
        Manager.Migrate(StorageKind.Missions, destination, true, false);
        // Simulate process loss after verified settings switch but before journal cleanup.
        PreferenceStore.WriteAtomic(Preferences.JournalPath, new MigrationJournal(StorageKind.Missions, initial.MissionDirectory, destination,
            "verified", StorageManager.DatabaseFingerprint(Path.Combine(destination, "missions.db"))));
        Assert.Contains("recovered", Manager.RecoverInterrupted());
        Assert.Equal(destination, Preferences.Load().MissionDirectory);
        Manager.UseExisting(StorageKind.Missions, Preferences.DefaultMissionDirectory, confirmed: true, recording: false);
        Assert.Equal(initial.MissionDirectory, Preferences.Load().MissionDirectory);
        // Simulate failure partway through a second copy, before activation.
        var partial = Path.Combine(root, "partial"); Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, "missions.db.partial"), "incomplete");
        PreferenceStore.WriteAtomic(Preferences.JournalPath, new MigrationJournal(StorageKind.Missions, initial.MissionDirectory, partial, "copying"));
        Assert.NotNull(Manager.RecoverInterrupted()); Assert.Equal(initial.MissionDirectory, Preferences.Load().MissionDirectory);
        Assert.True(File.Exists(Path.Combine(partial, "missions.db.partial")));
        Assert.True(File.Exists(Path.Combine(initial.MissionDirectory, "missions.db")));
    }

    [Fact]
    public void LibraryCannotEscapeOrFollowLinksOrMoveIntoItself()
    {
        var settings = Manager.Initialize(); var id = StorageManager.LibraryId(settings.ScienceLibraryDirectory);
        Assert.Throws<ArgumentException>(() => StorageManager.ResolveDataset(settings.ScienceLibraryDirectory, id, "../missions.db"));
        Assert.Throws<IOException>(() => Manager.Migrate(StorageKind.ScienceLibrary, Path.Combine(settings.ScienceLibraryDirectory, "nested"), true, false));
        Assert.Throws<IOException>(() => StorageManager.ValidateLocalPath("\\\\server\\share\\data"));
    }

    [Fact]
    public void FailedActivatedDestinationCanRollBackWithoutDeletingEitherCopy()
    {
        var initial = Manager.Initialize(); var destination = Path.Combine(root, "destination");
        Manager.Migrate(StorageKind.Missions, destination, true, false);
        PreferenceStore.WriteAtomic(Preferences.JournalPath, new MigrationJournal(StorageKind.Missions, initial.MissionDirectory, destination, "verified", "invalid fingerprint"));
        Assert.Throws<IOException>(() => Manager.RecoverInterrupted());
        Manager.RollbackInterrupted(confirmed: true);
        Assert.Equal(initial.MissionDirectory, Preferences.Load().MissionDirectory);
        Assert.True(File.Exists(Path.Combine(destination, "missions.db")));
        Assert.True(File.Exists(Path.Combine(initial.MissionDirectory, "missions.db")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
