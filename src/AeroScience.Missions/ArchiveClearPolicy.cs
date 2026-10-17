namespace AeroScience.Missions;

public sealed record BackupSelection(bool Cancelled, string? Directory);
public static class ArchiveClearPolicy
{
    // A fresh dialog always starts with permanent deletion; this is not a saved preference.
    public const bool DefaultBackupRequested = false;
    public static string ConfirmationWarning(string? backupDirectory) => backupDirectory is null
        ? "No new recovery backup will be created. Permanent deletion may be irreversible. Previously created backups are preserved."
        : "Recovery backup destination (separate from the data to delete):\n" + backupDirectory + "\nDeletion proceeds only after the recovery backup has been verified. Previously created backups are preserved.";
    public static BackupSelection ChooseBackup(bool requested, Func<string?> browse, Action<string> validate)
    {
        if (!requested) return new(false, null);
        var selected = browse(); if (selected is null) return new(true, null);
        validate(selected); return new(false, selected);
    }
    public static void ValidateBackupDestination(string destination, params string[] protectedRoots)
    {
        StorageManager.ValidateLocalPath(destination);
        var target = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var root in protectedRoots)
        {
            var path = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (target.Equals(path, StringComparison.OrdinalIgnoreCase) || target.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a backup destination outside both active data locations and the application installation. The archive to delete and its recovery backup must be separate.");
        }
    }
}
