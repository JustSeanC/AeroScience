using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
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
    public SettingsForm(MainForm app, bool storage)
    {
        this.app = app; Text = "AeroScience Settings"; Width = 840; Height = 690; MinimumSize = new(600, 450); AutoScaleMode = AutoScaleMode.Dpi;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var general = new TabPage("General & Toolbar"); var storageTab = new TabPage("Storage");
        var generalPanel = Panel(); general.Controls.Add(generalPanel); storageTab.Controls.Add(storagePanel);
        tabs.TabPages.Add(general); tabs.TabPages.Add(storageTab); tabs.SelectedIndex = storage ? 1 : 0; Controls.Add(tabs);
        BuildGeneral(generalPanel); RefreshStorage();
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
        var installed = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var selected = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        if (selected.Equals(installed, StringComparison.OrdinalIgnoreCase) || selected.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "Choose a data folder outside the application installation, so updates preserve your archive."); return; }
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
        Label(panel, "MSFS Community folder");
        var community = new ComboBox { Width = 700 };
        community.Items.AddRange(ToolbarInstaller.DiscoverCommunityFolders().Select(p => (object)p).ToArray());
        community.Text = preferences.CommunityDirectory ?? (community.Items.Count > 0 ? community.Items[0]?.ToString() ?? "" : ""); panel.Controls.Add(community);
        Button(panel, "Browse Community folder…", (_, _) => { using var browse = new FolderBrowserDialog { Description = "Select the configured MSFS Community folder", UseDescriptionForTitle = true }; if (browse.ShowDialog(this) == DialogResult.OK) community.Text = browse.SelectedPath; });
        Label(panel, ToolbarInstaller.InstalledStatus(preferences));
        Button(panel, "Install / Update Toolbar", async (_, _) =>
        {
            var selectedCommunity = community.Text;
            if (MessageBox.Show(this, "Install/update AeroScience only in this folder? MSFS must be closed. Any previous AeroScience package will be backed up outside Community.\n" + selectedCommunity, "Toolbar installation", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await Execute(() => app.RunStoppedAsync(() => Task.Run(() =>
            {
                ToolbarInstaller.Install(Path.Combine(AppContext.BaseDirectory, "Toolbar"), selectedCommunity);
                app.Preferences.Save(app.Preferences.Load() with { CommunityDirectory = selectedCommunity });
            })));
        });
        Button(panel, "Install official SimConnect runtime…", async (_, _) =>
        {
            using var browse = new OpenFileDialog { Filter = "Official native runtime (SimConnect.dll)|SimConnect.dll", Title = "Choose SimConnect.dll from an official installation" };
            if (browse.ShowDialog(this) != DialogResult.OK) return;
            var selected = browse.FileName;
            await Execute(() => app.RunStoppedAsync(() => Task.Run(() =>
            {
                using (var reader = new PEReader(File.OpenRead(selected)))
                    if (reader.PEHeaders.CoffHeader.Machine != Machine.Amd64 || reader.HasMetadata) throw new IOException("Select the official native Windows x64 runtime.");
                var library = NativeLibrary.Load(selected);
                try
                {
                    foreach (var name in new[] { "SimConnect_Open", "SimConnect_Close", "SimConnect_AddToDataDefinition", "SimConnect_RequestDataOnSimObject", "SimConnect_CallDispatch", "SimConnect_RequestSystemState", "SimConnect_SubscribeToSystemEvent", "SimConnect_SubscribeToCommBusEvent", "SimConnect_CallCommBusEvent" })
                        if (!NativeLibrary.TryGetExport(library, name, out _)) throw new IOException("Official runtime lacks a required export: " + name);
                }
                finally { NativeLibrary.Free(library); }
                var target = Path.Combine(AppContext.BaseDirectory, "SimConnect.dll");
                if (File.Exists(target)) throw new IOException("A runtime is already installed. Runtime replacement needs an application restart; existing files were preserved.");
                File.Copy(selected, target, overwrite: false);
            })));
        });
        Label(panel, File.Exists(Path.Combine(AppContext.BaseDirectory, "SimConnect.dll"))
            ? "SimConnect runtime is installed. Use an official runtime and its supported prerequisites."
            : "This developer preview has no SimConnect runtime. An installation-ready alpha needs a verified official prerequisite/bundling route.");
    }
}
