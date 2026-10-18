using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AeroScience.Engine;
using AeroScience.Missions;

namespace AeroScience.Desktop;

public sealed class SettingsForm : Form
{
    private readonly MainForm app;
    private readonly FlowLayoutPanel storagePanel = Panel();
    private readonly Label progress = new() { AutoSize = true, MaximumSize = new(720, 0) };
    private readonly ProgressBar progressBar = new() { Width = 650, Style = ProgressBarStyle.Marquee, Visible = false };
    private static FlowLayoutPanel Panel() => new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(18), FlowDirection = FlowDirection.TopDown, WrapContents = false };
    public SettingsForm(MainForm app, bool storage, bool toolbar = false)
    {
        this.app = app; Text = "AeroScience Settings"; Width = 840; Height = 690; MinimumSize = new(600, 450); AutoScaleMode = AutoScaleMode.Dpi;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var general = new TabPage("General"); var storageTab = new TabPage("Storage & Data"); var toolbarTab = new TabPage("Toolbar");
        var generalPanel = Panel(); general.Controls.Add(generalPanel); storageTab.Controls.Add(storagePanel);
        tabs.TabPages.Add(general); tabs.TabPages.Add(storageTab); tabs.TabPages.Add(toolbarTab); var toolbarPanel = Panel(); toolbarTab.Controls.Add(toolbarPanel); tabs.SelectedIndex = storage ? 1 : toolbar ? 2 : 0; Controls.Add(tabs);
        BuildGeneral(generalPanel); BuildToolbar(toolbarPanel); RefreshStorage();
        var mapsTab = new TabPage("Maps & Basemaps"); var mapsPanel = Panel(); mapsTab.Controls.Add(mapsPanel); tabs.TabPages.Add(mapsTab);
        Label(mapsPanel, "New installations suggest OpenStreetMap / Streets after a privacy choice before the first tile request. Choose offline-only at that prompt or change Maps & Basemaps in the dashboard. Existing preferences are preserved. Offline geography remains available without an account. Optional Esri satellite imagery requires your own configured credentials.");
        Button(mapsPanel, "Open Maps & Basemaps settings", (_, _) => app.OpenDashboard(settings: true));
        var sourcesTab = new TabPage("Scientific Data Sources"); var sourcesPanel = Panel(); sourcesTab.Controls.Add(sourcesPanel); tabs.TabPages.Add(sourcesTab);
        Label(sourcesPanel, "Automatic preparation discovers and downloads bounded numeric terrain, Landsat thermal and Sentinel-2 multispectral data directly from public providers after your separate scientific-data consent. Use dashboard Settings for access, offline-only, bandwidth/date policy, cancel/retry, coverage and readiness. Advanced manual GeoTIFF/ENVI import remains available below; it is not required for supported automatic workflows.");
        Button(sourcesPanel, "Import scientific source collection…", (_, _) => app.ImportScientificSource());
        Button(sourcesPanel, "Automatic data access / Readiness / Advanced sources", (_, _) => app.OpenDashboard(settings: true));
        var updatesTab = new TabPage("Updates"); var updatesPanel = Panel(); updatesTab.Controls.Add(updatesPanel); tabs.TabPages.Add(updatesTab);
        Label(updatesPanel, "AeroScience " + ApplicationUpdates.CurrentVersion + ". Dashboard Settings → Updates shows your installed version and optional GitHub release notifications. Checks are cached daily, never block offline archives, and never execute an installer automatically. You can disable automatic checks or include/exclude alpha releases there.");
        Button(updatesPanel, "Open update settings / Check for updates", (_, _) => app.OpenDashboard(settings: true));
        FormClosing += (_, e) => { if (app.Busy) { e.Cancel = true; MessageBox.Show(this, "Wait for the current operation to finish."); } };
    }
    private static Button Button(Control parent, string text, EventHandler action)
    { var button = new Button { Text = text, AutoSize = true }; button.Click += action; parent.Controls.Add(button); return button; }
    private static Label Label(Control parent, string text)
    { var label = new Label { Text = text, AutoSize = true, MaximumSize = new(720, 0), Margin = new(3, 8, 3, 8) }; parent.Controls.Add(label); return label; }
    private void RefreshStorage()
    {
        storagePanel.Controls.Clear();
        foreach (var kind in new[] { StorageKind.Missions, StorageKind.ScienceLibrary })
        {
            var status = app.Storage.Status(kind);
            Label(storagePanel, kind == StorageKind.Missions ? "Mission Database" : "Scientific Data Library").Font = new(Font, FontStyle.Bold);
            var path = new TextBox { Text = status.Directory, ReadOnly = true, Width = 700 }; storagePanel.Controls.Add(path);
            Label(storagePanel, status.Available ? $"Used: {SizeText(status.UsedBytes)} · Available: {SizeText(status.FreeBytes)}" : "Unavailable: " + status.Error);
            if (status.FreeBytes < (kind == StorageKind.ScienceLibrary ? StorageManager.LibraryLowSpaceWarningBytes : StorageManager.MinimumFreeBytes))
                Label(storagePanel, "Limited available capacity. Consider another drive; this is a capacity warning, not a prediction of science-data size.");
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 720 };
            Button(buttons, "Browse / Change Location…", async (_, _) => await SelectStorage(kind, false));
            Button(buttons, "Open Folder", (_, _) =>
            {
                if (Directory.Exists(status.Directory)) Process.Start(new ProcessStartInfo(status.Directory) { UseShellExecute = true });
                else MessageBox.Show(this, "The selected drive/folder is unavailable. No replacement was created.");
            });
            Button(buttons, "Restore Default…", async (_, _) => await SelectStorage(kind, true));
            storagePanel.Controls.Add(buttons);
        }
        Label(storagePanel, StorageManager.LargeDataWarning);
        Button(storagePanel, "Data Management…", (_, _) => app.ShowDataManagement());
        Button(storagePanel, "Storage Management / Limits / Preview Cleanup", (_, _) => app.OpenDashboard(settings: true));
        Button(storagePanel, "Recover original location from interrupted migration…", async (_, _) =>
        {
            if (!File.Exists(app.Preferences.JournalPath)) { MessageBox.Show(this, "No interrupted migration needs rollback."); return; }
            if (MessageBox.Show(this, "Restore the original verified storage setting? Both copies will be retained.", "Storage recovery", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await Execute(async () => await app.RunStoppedAsync(() => Task.Run(() => app.Storage.RollbackInterrupted(true))));
        });
        storagePanel.Controls.Add(progress); storagePanel.Controls.Add(progressBar);
    }
    private static string SizeText(long? bytes) => bytes is null ? "Unknown" : bytes >= 1073741824 ? $"{bytes / 1073741824.0:0.00} GiB" : $"{bytes / 1048576.0:0.0} MiB";
    private async Task SelectStorage(StorageKind kind, bool restoreDefault)
    {
        if (app.Recording) { MessageBox.Show(this, "End the mission before changing either storage location."); return; }
        if (kind == StorageKind.ScienceLibrary) MessageBox.Show(this, StorageManager.LargeDataWarning, "Science Library capacity");
        string destination;
        if (restoreDefault) destination = kind == StorageKind.Missions ? app.Preferences.DefaultMissionDirectory : app.Preferences.DefaultLibraryDirectory;
        else
        {
            using var browse = new FolderBrowserDialog { Description = "Choose an empty destination or an existing AeroScience store", UseDescriptionForTitle = true };
            if (browse.ShowDialog(this) != DialogResult.OK) return;
            destination = browse.SelectedPath;
        }
        var settings = app.Preferences.Load();
        try { SetupCoordinator.ValidateDataLocation(destination, AppContext.BaseDirectory); }
        catch (Exception error) { app.ShowError(error); return; }
        if (kind == StorageKind.ScienceLibrary)
        {
            try
            {
                var free = new LocalStorageCapacity().FreeBytes(destination);
                if (free < StorageManager.LibraryLowSpaceWarningBytes && MessageBox.Show(this,
                    $"Available capacity: {SizeText(free)}. This drive has limited free space. Future science-data requirements are unknown. Continue?",
                    "Limited capacity", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            catch (Exception error) { app.ShowError(error); return; }
        }
        var current = kind == StorageKind.Missions ? settings.MissionDirectory : settings.ScienceLibraryDirectory;
        if (Path.GetFullPath(current).TrimEnd('\\') == Path.GetFullPath(destination).TrimEnd('\\')) return;
        var marker = kind == StorageKind.Missions ? "missions.db" : StorageManager.LibraryManifest;
        var existing = File.Exists(Path.Combine(destination, marker));
        string capacity;
        try { capacity = "\nAvailable destination capacity: " + SizeText(new LocalStorageCapacity().FreeBytes(destination)); }
        catch (Exception error) { app.ShowError(error); return; }
        var message = existing
            ? $"This folder already contains a store:\n{destination}\n\nOpen that existing store instead? It may contain a different/older archive. Current data at {current} will be retained. Nothing will be merged or overwritten. Choose No to select another empty folder."
            : $"Copy and verify data from:\n{current}\n\nto:\n{destination}\n\nThe app will pause its local services. Originals remain intact after successful migration. Continue?";
        message += capacity;
        if (MessageBox.Show(this, message, "Confirm storage change", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await Execute(async () => await app.RunStoppedAsync(() => Task.Run(() =>
        {
            if (existing) app.Storage.UseExisting(kind, destination, true, false);
            else app.Storage.Migrate(kind, destination, true, false, new Progress<string>(text => BeginInvoke(() => progress.Text = text)));
        })));
    }
    private async Task Execute(Func<Task> operation)
    {
        if (app.Busy) return;
        progressBar.Visible = true; progress.Text = "Working…";
        try { await operation(); progress.Text = "Complete. Original data retained."; }
        catch (Exception error) { progress.Text = error.Message; app.ShowError(error); }
        finally { progressBar.Visible = false; RefreshStorage(); }
    }
    private void BuildGeneral(Control panel)
    {
        var preferences = app.Preferences.Load();
        Label(panel, "AeroScience " + Application.ProductVersion);
        Button(panel, "Run guided setup…", (_, _) => app.ShowSetup(this));
        var lan = new CheckBox { Text = "Enable private-LAN / iPad access", Checked = preferences.EnableLan, AutoSize = true }; panel.Controls.Add(lan);
        var addresses = new ComboBox { Width = 400, DropDownStyle = ComboBoxStyle.DropDownList };
        addresses.Items.AddRange(Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && EngineOptions.IsPrivateV4(a)).Select(a => (object)a.ToString()).ToArray());
        if (preferences.LanAddress is not null && !addresses.Items.Contains(preferences.LanAddress)) addresses.Items.Add(preferences.LanAddress);
        if (addresses.Items.Count > 0) addresses.SelectedIndex = 0;
        if (preferences.LanAddress is not null) addresses.SelectedItem = preferences.LanAddress;
        panel.Controls.Add(addresses);
        Label(panel, "Recording interval (milliseconds; SimConnect is requested at 1 Hz).");
        var interval = new NumericUpDown { Minimum = 1000, Maximum = 60000, Increment = 1000, Value = Math.Max(1000, preferences.SamplingIntervalMs), Width = 130 }; panel.Controls.Add(interval);
        Button(panel, "Apply connection settings", async (_, _) => await Execute(() => app.RunStoppedAsync(() =>
        {
            var next = app.Preferences.Load() with { EnableLan = lan.Checked, LanAddress = addresses.SelectedItem as string, SamplingIntervalMs = (int)interval.Value };
            new EngineOptions { EnableLan = next.EnableLan, LanAddress = next.LanAddress, SamplingIntervalMs = next.SamplingIntervalMs }.Validate();
            app.Preferences.Save(next); return Task.CompletedTask;
        })));
        Label(panel, "LAN is optional. Allow AeroScience on the Windows Private network profile only when using iPad/LAN. The pairing code is in dashboard Settings. Local operation requires no firewall change.");
    }
    private void BuildToolbar(Control panel)
    {
        var preferences = app.Preferences.Load();
        Label(panel, "MSFS Community folder");
        var community = new ComboBox { Width = 700 };
        community.Items.AddRange(ToolbarInstaller.DiscoverCommunityFolders().Select(p => (object)p).ToArray());
        community.Text = preferences.CommunityDirectory ?? (community.Items.Count > 0 ? community.Items[0]?.ToString() ?? "" : ""); panel.Controls.Add(community);
        Button(panel, "Browse Community folder…", (_, _) => { using var browse = new FolderBrowserDialog { Description = "Select the configured MSFS Community folder", UseDescriptionForTitle = true }; if (browse.ShowDialog(this) == DialogResult.OK) community.Text = browse.SelectedPath; });
        var toolbarStatus = Label(panel, "");
        void RefreshToolbar() { var value = ToolbarVersions.Inspect(Path.Combine(AppContext.BaseDirectory, "Toolbar"), community.Text); toolbarStatus.Text = $"Installed: {value.InstalledVersion ?? "Unknown / not installed"} · Available: {value.BundledVersion ?? "Unknown"}\nStatus: {value.Status}\n{value.Message}"; }
        community.TextChanged += (_, _) => RefreshToolbar(); RefreshToolbar();
        Button(panel, "Install / Update / Repair Toolbar…", (_, _) => { app.InstallToolbar(this, community.Text); RefreshToolbar(); });
        Button(panel, "Remove AeroScience toolbar…", (_, _) =>
        {
            var path = app.Preferences.Load().CommunityDirectory;
            if (path is null) { MessageBox.Show(this, "No Community folder has been configured."); return; }
            if (MessageBox.Show(this, "Remove only AeroScience from this Community folder? Its files will be moved to a backup outside Community. Close MSFS first.\n" + path, "Remove toolbar", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try { var backup = ToolbarInstaller.Remove(path, true); MessageBox.Show(this, backup is null ? "The toolbar was not installed in that folder." : "Toolbar removed. Backup retained at:\n" + backup); }
            catch (Exception error) { app.ShowError(error); }
        });
        Label(panel, File.Exists(Path.Combine(AppContext.BaseDirectory, "SimConnect.dll"))
            ? "The native SimConnect runtime is included. Connection status is shown in the main AeroScience window and dashboard."
            : "The native SimConnect runtime is missing. Run AeroScience Setup again to repair the installation; keep your mission data and settings.");
        Button(panel, "Microsoft Visual C++ prerequisite…", (_, _) => Process.Start(new ProcessStartInfo(
            "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist")
        { UseShellExecute = true }));
        Label(panel, "If Windows reports a missing Visual C++ dependency, use Microsoft's official page above to install the x64 redistributable graphically, then restart AeroScience.");
    }
}
