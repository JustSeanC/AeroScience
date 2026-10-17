using AeroScience.Missions;

namespace AeroScience.Engine;

// The Windows wizard and Settings share the existing verified storage/toolbar services.
public sealed class SetupCoordinator(PreferenceStore preferences, StorageManager storage, string applicationDirectory)
{
    public static void ValidateDataLocation(string directory, string applicationDirectory, bool requireAvailable = true)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new IOException("Choose an absolute data folder on a local or external drive.");
        if (requireAvailable) StorageManager.ValidateLocalPath(directory);
        var selected = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var installed = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (selected.Equals(installed, StringComparison.OrdinalIgnoreCase)
            || selected.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || installed.StartsWith(selected + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a separate data folder outside the application installation directory.");
    }

    public void ConfigureStorage(string missionDirectory, string libraryDirectory, bool confirmed, IProgress<string>? progress = null)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm setup before creating or migrating storage.");
        var selected = new[] { missionDirectory, libraryDirectory }.Select(Path.GetFullPath).ToArray();
        var current = preferences.Load();
        var original = new[] { current.MissionDirectory, current.ScienceLibraryDirectory };
        for (var i = 0; i < selected.Length; i++)
        {
            var retainingUnavailableLibrary = i == 1 && current.Initialized && SamePath(selected[i], original[i]);
            ValidateDataLocation(selected[i], applicationDirectory, requireAvailable: !retainingUnavailableLibrary);
            if (!SamePath(selected[i], original[i]) && Directory.Exists(selected[i]) && Directory.EnumerateFileSystemEntries(selected[i]).Any())
                throw new IOException("A chosen destination is not empty. Keep its current location or choose an empty folder. To open a different existing archive/library, use Settings → Storage and explicitly confirm that choice.");
        }
        if (SamePath(selected[0], selected[1]) || selected[0].StartsWith(selected[1].TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Keep the mission database outside the Scientific Data Library.");
        // Open/upgrade any existing archive before migrating it; never abandon legacy default history.
        storage.Initialize();
        for (var i = 0; i < selected.Length; i++)
            if (!SamePath(selected[i], original[i])) storage.Migrate(i == 0 ? StorageKind.Missions : StorageKind.ScienceLibrary, selected[i], true, false, progress);
    }

    private static bool SamePath(string left, string right) => Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
        .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
