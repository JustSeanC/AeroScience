using System.Security.Cryptography;
using System.Text;

namespace AeroScience.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroScience");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..24];
        using var instance = new Mutex(true, "Local\\AeroScience-" + identity, out var created);
        if (!created) { MessageBox.Show("AeroScience is already running. Open its window from the taskbar.", "AeroScience"); return; }
        Application.Run(new MainForm(directory));
    }
}
