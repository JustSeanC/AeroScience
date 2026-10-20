using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AeroScience.Tests")]

namespace AeroScience.Engine;

public sealed class DataDirectoryLease : IDisposable
{
    private readonly FileStream handle;
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Released => released.Task;
    public DataDirectoryLease(EngineOptions options)
    {
        if (options.RequireExistingDatabase) AeroScience.Missions.StorageManager.RequireMissionDatabase(options.DataDirectory);
        Directory.CreateDirectory(options.DataDirectory);
        // Acquire ownership before recovery; a second engine must not interrupt a live recording.
        handle = new(Path.Combine(options.DataDirectory, "engine.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() { handle.Dispose(); released.TrySetResult(); }
}
