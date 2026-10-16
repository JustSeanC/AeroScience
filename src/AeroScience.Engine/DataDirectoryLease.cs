namespace AeroScience.Engine;

public sealed class DataDirectoryLease : IDisposable
{
    private readonly FileStream handle;
    public DataDirectoryLease(EngineOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        // Acquire ownership before recovery; a second engine must not interrupt a live recording.
        handle = new(Path.Combine(options.DataDirectory, "engine.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() => handle.Dispose();
}
