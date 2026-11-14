using AeroScience.Engine;
using AeroScience.Missions;

namespace AeroScience.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (ScientificHdfWorker.IsCommand(args)) return ScientificHdfWorker.Run();
        ApplicationConfiguration.Initialize();
        var profile = ApplicationProfile.Current;
        try { WindowsPresentation.SetLocale(profile.Preferences().Presentation().Locale); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { WindowsPresentation.SetLocale("en-US"); }
        var directory = profile.ConfigurationDirectory;
        using var instance = new Mutex(true, ApplicationProfile.DesktopMutex, out var created);
        if (!created) { MessageBox.Show(WindowsPresentation.Text("windows.program.aeroscienceIsAlreadyRunningCloseItBeforeInstallationOr"), "AeroScience"); return 1; }
        if (args.Contains("--uninstall-toolbar"))
        {
            try
            {
                var path = profile.Preferences().Load().CommunityDirectory;
                if (path is not null && Directory.Exists(Path.Combine(path, profile.ToolbarPackage))
                    && MessageBox.Show(WindowsPresentation.Text("windows.program.removeTheAeroscienceMsfsToolbarTooOnlyAeroscienceWill", new Dictionary<string, object> { ["value1"] = Convert.ToString((path), System.Globalization.CultureInfo.InvariantCulture) ?? "" }), WindowsPresentation.Text("windows.program.uninstallAeroscience"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    while (ToolbarInstaller.SimulatorRunning())
                        if (MessageBox.Show(WindowsPresentation.Text("windows.program.closeMsfsBeforeRemovingTheAeroscienceToolbarThenChoose"), WindowsPresentation.Text("windows.program.closeMsfs"), MessageBoxButtons.RetryCancel, MessageBoxIcon.Information) != DialogResult.Retry) return 0;
                    ToolbarInstaller.Remove(path, true);
                }
                return 0;
            }
            catch (Exception error) { MessageBox.Show(WindowsPresentation.Text("windows.program.toolbarRemovalDidNotCompleteExistingFilesWerePreserved", new Dictionary<string, object> { ["value1"] = Convert.ToString((error.Message), System.Globalization.CultureInfo.InvariantCulture) ?? "" }), WindowsPresentation.Text("windows.program.aeroscienceUninstall")); return 1; }
        }
        Application.Run(new MainForm(directory, args.Contains("--setup")));
        return 0;
    }
}
