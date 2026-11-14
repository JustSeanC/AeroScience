using System.Diagnostics;
using AeroScience.Missions;

namespace AeroScience.Desktop;

internal static class NativeFolder
{
    internal static void Open(string path)
    {
        StorageManager.ValidateLocalPath(path);
        if(!Directory.Exists(path))throw new IOException(WindowsPresentation.Text("windows.error.folderUnavailable"));
        Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
}
