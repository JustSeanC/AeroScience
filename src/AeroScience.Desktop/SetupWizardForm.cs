using AeroScience.Engine;
using AeroScience.Missions;

namespace AeroScience.Desktop;

public sealed class SetupWizardForm : Form
{
    private readonly MainForm app;
    private readonly Panel content = new() { Dock = DockStyle.Fill, Padding = new(24), AutoScroll = true };
    private readonly Button back = new() { Text = "Back", AutoSize = true };
    private readonly Button next = new() { Text = "Next", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true };
    private readonly Button skip = new() { Text = "Skip for Now", AutoSize = true };
    private readonly TextBox mission = new() { Width = 650 };
    private readonly TextBox library = new() { Width = 650 };
    private readonly ComboBox community = new() { Width = 650 };
    private readonly CheckBox install = new() { Text = "Install / update the included MSFS 2024 toolbar", Checked = true, AutoSize = true };
    private readonly Label missionSpace = new() { AutoSize = true };
    private readonly Label librarySpace = new() { AutoSize = true };
    private readonly Label progress = new() { AutoSize = true, MaximumSize = new(690, 0) };
    private int page;
    private bool working;

    public SetupWizardForm(MainForm app)
    {
        this.app = app;
        Text = "Welcome to AeroScience · Setup"; Size = new(800, 680); MinimumSize = new(760, 600);
        StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var settings = app.Preferences.Load();
        mission.Text = Path.Combine(settings.MissionDirectory, "missions.db"); library.Text = settings.ScienceLibraryDirectory;
        community.Items.AddRange(ToolbarInstaller.DiscoverCommunityFolders().Select(p => (object)p).ToArray());
        community.Text = settings.CommunityDirectory ?? (community.Items.Count > 0 ? community.Items[0]?.ToString() ?? "" : "");
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, Padding = new(18), FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.AddRange([cancel, next, back, skip]); Controls.Add(content); Controls.Add(footer);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        back.Click += (_, _) => { page--; Render(); };
        skip.Click += (_, _) => { install.Checked = false; page = 3; Render(); };
        next.Click += async (_, _) => await Advance();
        mission.TextChanged += (_, _) => Capacity(mission.Text, missionSpace, true);
        library.TextChanged += (_, _) => Capacity(library.Text, librarySpace, false);
        FormClosing += (_, e) => { if (working) e.Cancel = true; };
        Render();
    }

    private static Label TextLabel(string text) => new() { Text = text, AutoSize = true, MaximumSize = new(690, 0), Margin = new(3, 10, 3, 10) };
    private void Browse(TextBox path, bool database)
    {
        using var dialog = new FolderBrowserDialog { Description = database ? "Choose the folder for missions.db" : "Choose a Scientific Data Library folder", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = database ? Path.Combine(dialog.SelectedPath, "missions.db") : dialog.SelectedPath;
    }
    private void Capacity(string path, Label label, bool database)
    {
        try
        {
            var folder = database ? Path.GetDirectoryName(path)! : path;
            SetupCoordinator.ValidateDataLocation(folder, AppContext.BaseDirectory);
            var bytes = new LocalStorageCapacity().FreeBytes(folder);
            label.Text = $"Available drive space: {bytes / 1073741824.0:0.00} GiB" + (bytes < StorageManager.LibraryLowSpaceWarningBytes ? " · Limited free space" : "");
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            label.Text = error.Message;
            var current = app.Preferences.Load();
            if (!database && current.Initialized && SamePath(path, current.ScienceLibraryDirectory))
                label.Text += " · Keep this location to preserve the unavailable library; reconnect its drive later. Your available mission archive can still open.";
        }
    }
    private void Render()
    {
        content.Controls.Clear();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(new Label { Text = $"AeroScience {Application.ProductVersion.Split('+')[0]} · Step {page + 1} of 4", AutoSize = true, Font = new(Font.FontFamily, 16) });
        content.Controls.Add(panel); back.Enabled = page > 0; skip.Visible = page == 2; next.Text = page == 3 ? "Finish Setup" : "Next";
        if (page == 0)
        {
            panel.Controls.Add(TextLabel("Set up local mission storage and the MSFS 2024 toolbar. The app will then start its services and open your dashboard automatically."));
            panel.Controls.Add(TextLabel("Existing locations and settings are preselected. Updates keep them. Changing a location copies and verifies existing data only after your confirmation; originals are retained."));
            panel.Controls.Add(TextLabel("Mission History works without MSFS or internet. Live recording becomes available when an aircraft is connected."));
        }
        else if (page == 1)
        {
            panel.Controls.Add(TextLabel("Mission Database file (choose its folder; filename remains missions.db)")); panel.Controls.Add(mission);
            var missionBrowse = new Button { Text = "Browse Mission Database Folder…", AutoSize = true }; missionBrowse.Click += (_, _) => Browse(mission, true); panel.Controls.Add(missionBrowse); panel.Controls.Add(missionSpace);
            panel.Controls.Add(TextLabel("Scientific Data Library — may be on a different internal or external drive")); panel.Controls.Add(library);
            var libraryBrowse = new Button { Text = "Browse Science Library Folder…", AutoSize = true }; libraryBrowse.Click += (_, _) => Browse(library, false); panel.Controls.Add(libraryBrowse); panel.Controls.Add(librarySpace);
            panel.Controls.Add(TextLabel(StorageManager.LargeDataWarning));
            panel.Controls.Add(TextLabel("Keep the suggested folders to preserve current storage. New destinations must be empty. Existing stores can be selected explicitly through Settings → Storage after setup."));
            Capacity(mission.Text, missionSpace, true); Capacity(library.Text, librarySpace, false);
        }
        else if (page == 2)
        {
            panel.Controls.Add(install); panel.Controls.Add(TextLabel("Confirm the MSFS 2024 Community folder. Detection reads Steam / Microsoft Store configuration, including custom package paths. Use Browse if necessary."));
            panel.Controls.Add(community);
            var browse = new Button { Text = "Browse Community Folder…", AutoSize = true };
            browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Select MSFS 2024's configured Community folder", UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) community.Text = dialog.SelectedPath; }; panel.Controls.Add(browse);
            panel.Controls.Add(TextLabel("Close MSFS before installing or updating. Only AeroScience is changed; the previous AeroScience package is backed up outside Community. Skip for Now leaves Community untouched; installation remains available in Settings."));
            if (ToolbarInstaller.SimulatorRunning()) panel.Controls.Add(TextLabel("MSFS is currently running. Close it before continuing with toolbar installation, or choose Skip for Now."));
        }
        else
        {
            panel.Controls.Add(TextLabel($"Mission Database: {mission.Text}\n\nScientific Data Library: {library.Text}\n\nToolbar: {(install.Checked ? "Install/update in " + community.Text : "Skipped — configure later in Settings")}"));
            panel.Controls.Add(TextLabel("Finish Setup confirms these choices. Changed storage locations use verified migration with originals retained. Existing destination data is never overwritten or silently merged. You can revisit setup and storage through Settings."));
            panel.Controls.Add(progress);
        }
    }
    private async Task Advance()
    {
        try
        {
            if (page == 1)
            {
                if (!Path.GetFileName(mission.Text).Equals("missions.db", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a database folder using Browse; the database filename must remain missions.db.");
                var settings = app.Preferences.Load();
                var folders = new[] { Path.GetDirectoryName(mission.Text)!, library.Text };
                for (var index = 0; index < folders.Length; index++)
                {
                    var unchanged = settings.Initialized && SamePath(folders[index], index == 0 ? settings.MissionDirectory : settings.ScienceLibraryDirectory);
                    SetupCoordinator.ValidateDataLocation(folders[index], AppContext.BaseDirectory, requireAvailable: index == 0 || !unchanged);
                    if (!unchanged && new LocalStorageCapacity().FreeBytes(folders[index]) < StorageManager.MinimumFreeBytes) throw new IOException("There is insufficient free space on a selected storage drive.");
                }
                if (!(settings.Initialized && SamePath(library.Text, settings.ScienceLibraryDirectory)) && new LocalStorageCapacity().FreeBytes(library.Text) < StorageManager.LibraryLowSpaceWarningBytes
                    && MessageBox.Show(this, "The Science Library drive has limited free space. Future science formats and data rates are not implemented; no size forecast is available. Continue?", "Storage capacity", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            if (page == 2 && install.Checked)
            {
                if (ToolbarInstaller.SimulatorRunning()) throw new IOException("Close MSFS before installing the toolbar, or choose Skip for Now.");
                if (!Directory.Exists(community.Text)) throw new IOException("Choose an existing Community folder, or choose Skip for Now.");
            }
            if (page < 3) { page++; Render(); return; }
            working = true; back.Enabled = next.Enabled = cancel.Enabled = false;
            var missionFolder = Path.GetDirectoryName(Path.GetFullPath(mission.Text))!;
            var libraryFolder = Path.GetFullPath(library.Text); var selectedCommunity = community.Text; var installToolbar = install.Checked;
            IProgress<string> reporter = new Progress<string>(message => progress.Text = message);
            await app.RunStoppedAsync(() => Task.Run(() =>
            {
                new SetupCoordinator(app.Preferences, app.Storage, AppContext.BaseDirectory).ConfigureStorage(missionFolder, libraryFolder, true, reporter);
                if (installToolbar)
                {
                    reporter.Report("Installing the verified MSFS toolbar…");
                    ToolbarInstaller.Install(Path.Combine(AppContext.BaseDirectory, "Toolbar"), selectedCommunity, previousCommunity: app.Preferences.Load().CommunityDirectory);
                }
                app.Preferences.Save(app.Preferences.Load() with { SetupCompleted = true, CommunityDirectory = installToolbar ? selectedCommunity : app.Preferences.Load().CommunityDirectory });
            }));
            working = false; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception error) { working = false; back.Enabled = page > 0; next.Enabled = cancel.Enabled = true; app.ShowError(error); }
    }
    private static bool SamePath(string left, string right)
    {
        try { return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
