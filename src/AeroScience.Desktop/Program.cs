using AeroScience.Engine;
using AeroScience.Missions;

namespace AeroScience.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroScience");
        using var instance = new Mutex(true, @"Local\AeroScience.Desktop", out var created);
        if (!created) { MessageBox.Show("AeroScience is already running. Close it before installation or removal.", "AeroScience"); return 1; }
        if (args.Contains("--uninstall-toolbar"))
        {
            try
            {
                var path = new PreferenceStore(directory).Load().CommunityDirectory;
                if (path is not null && Directory.Exists(Path.Combine(path, "aeroscience-toolbar"))
                    && MessageBox.Show("Remove the AeroScience MSFS toolbar too?\n\n" + path + "\n\nOnly AeroScience will be moved to a backup outside Community. Mission history, scientific data, configuration and unrelated add-ons will be retained. Choose No to keep the toolbar.", "Uninstall AeroScience", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    while (ToolbarInstaller.SimulatorRunning())
                        if (MessageBox.Show("Close MSFS before removing the AeroScience toolbar, then choose Retry. Cancel keeps the toolbar and continues uninstalling the application.", "Close MSFS", MessageBoxButtons.RetryCancel, MessageBoxIcon.Information) != DialogResult.Retry) return 0;
                    ToolbarInstaller.Remove(path, true);
                }
                return 0;
            }
            catch (Exception error) { MessageBox.Show("Toolbar removal did not complete. Existing files were preserved.\n" + error.Message, "AeroScience uninstall"); return 1; }
        }
        Application.Run(new MainForm(directory, args.Contains("--setup")));
        return 0;
    }
}
