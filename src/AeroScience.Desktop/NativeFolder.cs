using System.Diagnostics;
using AeroScience.Missions;

namespace AeroScience.Desktop;

internal static class NativeFolder
{
    internal static void Open(string path)
    {
        StorageManager.ValidateLocalPath(path);
        if(!Directory.Exists(path))throw new IOException("The selected drive/folder is unavailable. Reconnect it; no replacement was created.");
        Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
}
