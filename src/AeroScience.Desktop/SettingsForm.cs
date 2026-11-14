using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AeroScience.Engine;
using AeroScience.Missions;

namespace AeroScience.Desktop;

public sealed class SettingsForm : BrandedForm
{
    private readonly MainForm app;
    private readonly List<Func<bool>> pendingEdits=[];
    private readonly FlowLayoutPanel storagePanel = Panel();
    private readonly Label progress = new() { AutoSize = true, MaximumSize = new(720, 0) };
    private readonly ProgressBar progressBar = new() { Width = 650, Style = ProgressBarStyle.Marquee, Visible = false };
    private static FlowLayoutPanel Panel() => new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(18), FlowDirection = FlowDirection.TopDown, WrapContents = false };
    public SettingsForm(MainForm app, bool storage, bool toolbar = false)
    {
        this.app = app; Text = WindowsPresentation.Text("windows.settingsForm.aeroscienceSettings"); Width = 840; Height = 690; MinimumSize = new(600, 450); AutoScaleMode = AutoScaleMode.Dpi;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var general = new TabPage(WindowsPresentation.Text("ui.settingsNavigation.messageGeneral")); var storageTab = new TabPage(WindowsPresentation.Text("ui.settingsNavigation.messageStorageData")); var toolbarTab = new TabPage(WindowsPresentation.Text("windows.settingsForm.toolbar"));
        var generalPanel = Panel(); general.Controls.Add(generalPanel); storageTab.Controls.Add(storagePanel);
        tabs.TabPages.Add(general); tabs.TabPages.Add(storageTab); tabs.TabPages.Add(toolbarTab); var toolbarPanel = Panel(); toolbarTab.Controls.Add(toolbarPanel); tabs.SelectedIndex = storage ? 1 : toolbar ? 2 : 0; Controls.Add(tabs);
        BuildGeneral(generalPanel); BuildToolbar(toolbarPanel); RefreshStorage();
        var mapsTab = new TabPage(WindowsPresentation.Text("ui.basemaps.messageMapsBasemaps")); var mapsPanel = Panel(); mapsTab.Controls.Add(mapsPanel); tabs.TabPages.Add(mapsTab);
        Label(mapsPanel, WindowsPresentation.Text("windows.settingsForm.newInstallationsSuggestOpenstreetmapStreetsAfterAPrivacyChoice"));
        Button(mapsPanel, WindowsPresentation.Text("windows.settingsForm.openMapsBasemapsSettings"), (_, _) => app.OpenDashboard(settings: true));
        var sourcesTab=new TabPage(WindowsPresentation.Text("ui.scientificSources.messageScientificDataSources"));sourcesTab.Controls.Add(new ScientificSourcesPanel(app,this));tabs.TabPages.Add(sourcesTab);
        var updatesTab = new TabPage(WindowsPresentation.Text("ui.applicationUpdates.messageUpdates")); var updatesPanel = Panel(); updatesTab.Controls.Add(updatesPanel); tabs.TabPages.Add(updatesTab);
        Label(updatesPanel, WindowsPresentation.Text("windows.settingsForm.aeroscienceDashboardSettingsUpdatesShowsYourInstalledVersionAnd", new Dictionary<string, object> { ["value1"] = Convert.ToString((ApplicationUpdates.CurrentVersion), System.Globalization.CultureInfo.InvariantCulture) ?? "" }));
        Button(updatesPanel, WindowsPresentation.Text("windows.settingsForm.openUpdateSettingsCheckForUpdates"), (_, _) => app.OpenDashboard(settings: true));
        FormClosing += (_, e) => { if (app.Busy) { e.Cancel = true; MessageBox.Show(this, WindowsPresentation.Text("windows.settingsForm.waitForTheCurrentOperationToFinish")); }
            else if(pendingEdits.Any(check=>check())&&MessageBox.Show(this,WindowsPresentation.Text("windows.settingsForm.discardUnappliedSettingsAndCloseChooseNoToKeep"),WindowsPresentation.Text("windows.settingsForm.unsavedSettings"),MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)e.Cancel=true; };
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
            Label(storagePanel, kind == StorageKind.Missions ? WindowsPresentation.Text("ui.app.messageMissionDatabase") : WindowsPresentation.Text("ui.app.messageScientificDataLibrary")).Font = new(Font, FontStyle.Bold);
            var path = new TextBox { Text = status.Directory, ReadOnly = true, Width = 700 }; storagePanel.Controls.Add(path);
            Label(storagePanel, status.Available ? WindowsPresentation.Text("windows.settingsForm.usedAvailable", new Dictionary<string, object> { ["value1"] = WindowsPresentation.Format($"{SizeText(status.UsedBytes)}"), ["value2"] = WindowsPresentation.Format($"{SizeText(status.FreeBytes)}") }) : WindowsPresentation.Text("windows.settingsForm.unavailable", new Dictionary<string, object> { ["value1"] = Convert.ToString((status.Error), System.Globalization.CultureInfo.InvariantCulture) ?? "" }));
            if (status.FreeBytes < (kind == StorageKind.ScienceLibrary ? StorageManager.LibraryLowSpaceWarningBytes : StorageManager.MinimumFreeBytes))
                Label(storagePanel, WindowsPresentation.Text("windows.settingsForm.limitedAvailableCapacityConsiderAnotherDriveThisIsA"));
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 720 };
            Button(buttons, WindowsPresentation.Text("windows.settingsForm.browseChangeLocation"), async (_, _) => await SelectStorage(kind, false));
            Button(buttons, WindowsPresentation.Text("windows.settingsForm.openFolder"), (_, _) =>
            {
                try { NativeFolder.Open(status.Directory); }
                catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){MessageBox.Show(this,error.Message);}
            });
            Button(buttons, WindowsPresentation.Text("windows.settingsForm.restoreDefault"), async (_, _) => await SelectStorage(kind, true));
            storagePanel.Controls.Add(buttons);
        }
        Label(storagePanel, StorageManager.LargeDataWarning);
        Button(storagePanel, WindowsPresentation.Text("windows.settingsForm.dataManagement"), (_, _) => app.ShowDataManagement(this));
        Button(storagePanel, WindowsPresentation.Text("windows.settingsForm.storageManagementLimitsPreviewCleanup"), (_, _) => app.OpenDashboard(settings: true));
        Button(storagePanel, WindowsPresentation.Text("windows.settingsForm.recoverOriginalLocationFromInterruptedMigration"), async (_, _) =>
        {
            if (!File.Exists(app.Preferences.JournalPath)) { MessageBox.Show(this, WindowsPresentation.Text("windows.settingsForm.noInterruptedMigrationNeedsRollback")); return; }
            if (MessageBox.Show(this, WindowsPresentation.Text("windows.settingsForm.restoreTheOriginalVerifiedStorageSettingBothCopiesWill"), WindowsPresentation.Text("windows.mainForm.storageRecovery"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await Execute(async () => await app.RunStoppedAsync(() => Task.Run(() => app.Storage.RollbackInterrupted(true))));
        });
        storagePanel.Controls.Add(progress); storagePanel.Controls.Add(progressBar);
    }
    private static string SizeText(long? bytes) => bytes is null ? "Unknown" : bytes >= 1073741824 ? $"{bytes / 1073741824.0:0.00} GiB" : $"{bytes / 1048576.0:0.0} MiB";
    private async Task SelectStorage(StorageKind kind, bool restoreDefault)
    {
        if (app.Recording) { MessageBox.Show(this, WindowsPresentation.Text("windows.settingsForm.endTheMissionBeforeChangingEitherStorageLocation")); return; }
        if (kind == StorageKind.ScienceLibrary) MessageBox.Show(this, StorageManager.LargeDataWarning, WindowsPresentation.Text("windows.settingsForm.scienceLibraryCapacity"));
        string destination;
        if (restoreDefault) destination = kind == StorageKind.Missions ? app.Preferences.DefaultMissionDirectory : app.Preferences.DefaultLibraryDirectory;
        else
        {
            using var browse = new FolderBrowserDialog { Description = WindowsPresentation.Text("windows.settingsForm.chooseAnEmptyDestinationOrAnExistingAeroscienceStore"), UseDescriptionForTitle = true };
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
                    WindowsPresentation.Text("windows.settingsForm.availableCapacityThisDriveHasLimitedFreeSpaceFuture", new Dictionary<string, object> { ["value1"] = WindowsPresentation.Format($"{SizeText(free)}") }),
                    WindowsPresentation.Text("windows.settingsForm.limitedCapacity"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            catch (Exception error) { app.ShowError(error); return; }
        }
        var current = kind == StorageKind.Missions ? settings.MissionDirectory : settings.ScienceLibraryDirectory;
        if (Path.GetFullPath(current).TrimEnd('\\') == Path.GetFullPath(destination).TrimEnd('\\')) return;
        var marker = kind == StorageKind.Missions ? "missions.db" : StorageManager.LibraryManifest;
        var existing = File.Exists(Path.Combine(destination, marker));
        string capacity;
        try { capacity = WindowsPresentation.Text("windows.storage.capacity", new Dictionary<string, object> { ["size"] = SizeText(new LocalStorageCapacity().FreeBytes(destination)) }); }
        catch (Exception error) { app.ShowError(error); return; }
        var message = WindowsPresentation.Text(existing ? "windows.storage.openExisting" : "windows.storage.migrate", new Dictionary<string, object> { ["destination"] = destination, ["current"] = current });
        message += capacity;
        if (MessageBox.Show(this, message, WindowsPresentation.Text("windows.settingsForm.confirmStorageChange"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await Execute(async () => await app.RunStoppedAsync(() => Task.Run(() =>
        {
            if (existing) app.Storage.UseExisting(kind, destination, true, false);
            else app.Storage.Migrate(kind, destination, true, false, new Progress<string>(text => BeginInvoke(() => progress.Text = text)));
        })));
    }
    private async Task Execute(Func<Task> operation)
    {
        if (app.Busy) return;
        progressBar.Visible = true; progress.Text = WindowsPresentation.Text("windows.settingsForm.working");
        try { await operation(); progress.Text = WindowsPresentation.Text("windows.settingsForm.completeOriginalDataRetained"); }
        catch (Exception error) { progress.Text = error.Message; app.ShowError(error); }
        finally { progressBar.Visible = false; RefreshStorage(); }
    }
    private void BuildGeneral(Control panel)
    {
        var appearance = new PresentationPanel(app.Preferences, () => app.RefreshAppearance(true));
        panel.Controls.Add(appearance); pendingEdits.Add(() => appearance.HasChanges);
        var preferences = app.Preferences.Load();
        Label(panel, "AeroScience " + Application.ProductVersion);
        Button(panel, WindowsPresentation.Text("windows.settingsForm.runGuidedSetup"), (_, _) => app.ShowSetup(this));
        var lan = new CheckBox { Text = WindowsPresentation.Text("windows.settingsForm.enablePrivateLanIpadAccess"), Checked = preferences.EnableLan, AutoSize = true }; panel.Controls.Add(lan);
        var addresses = new ComboBox { Width = 400, DropDownStyle = ComboBoxStyle.DropDownList };
        addresses.Items.AddRange(Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && EngineOptions.IsPrivateV4(a)).Select(a => (object)a.ToString()).ToArray());
        if (preferences.LanAddress is not null && !addresses.Items.Contains(preferences.LanAddress)) addresses.Items.Add(preferences.LanAddress);
        if (addresses.Items.Count > 0) addresses.SelectedIndex = 0;
        if (preferences.LanAddress is not null) addresses.SelectedItem = preferences.LanAddress;
        panel.Controls.Add(addresses);
        Label(panel, WindowsPresentation.Text("windows.settingsForm.recordingIntervalMillisecondsSimconnectIsRequestedAt1Hz"));
        var interval = new NumericUpDown { Minimum = 1000, Maximum = 60000, Increment = 1000, Value = Math.Max(1000, preferences.SamplingIntervalMs), Width = 130 }; panel.Controls.Add(interval);
        pendingEdits.Add(()=>{var saved=app.Preferences.Load();return lan.Checked!=saved.EnableLan||(int)interval.Value!=Math.Max(1000,saved.SamplingIntervalMs)||lan.Checked&&addresses.SelectedItem as string!=saved.LanAddress;});
        Button(panel, WindowsPresentation.Text("windows.settingsForm.applyConnectionSettings"), async (_, _) => await Execute(() => app.RunStoppedAsync(() =>
        {
            var next = app.Preferences.Load() with { EnableLan = lan.Checked, LanAddress = addresses.SelectedItem as string, SamplingIntervalMs = (int)interval.Value };
            ApplicationProfile.Current.EngineOptions(next).Validate();
            app.Preferences.Save(next); return Task.CompletedTask;
        })));
        Label(panel, WindowsPresentation.Text("windows.settingsForm.lanIsOptionalAllowAeroscienceOnTheWindowsPrivate"));
    }
    private void BuildToolbar(Control panel)
    {
        var preferences = app.Preferences.Load();
        Label(panel, WindowsPresentation.Text("windows.settingsForm.msfsCommunityFolder"));
        var community = new ComboBox { Width = 700 };
        community.Items.AddRange(ToolbarInstaller.DiscoverCommunityFolders().Select(p => (object)p).ToArray());
        community.Text = preferences.CommunityDirectory ?? (community.Items.Count > 0 ? community.Items[0]?.ToString() ?? "" : ""); panel.Controls.Add(community);
        var originalCommunity=community.Text;pendingEdits.Add(()=>community.Text!=(app.Preferences.Load().CommunityDirectory??originalCommunity));
        Button(panel, WindowsPresentation.Text("windows.settingsForm.browseCommunityFolder"), (_, _) => { using var browse = new FolderBrowserDialog { Description = WindowsPresentation.Text("windows.settingsForm.selectTheConfiguredMsfsCommunityFolder"), UseDescriptionForTitle = true }; if (browse.ShowDialog(this) == DialogResult.OK) community.Text = browse.SelectedPath; });
        var toolbarStatus = Label(panel, "");
        void RefreshToolbar() { var value = ToolbarVersions.Inspect(Path.Combine(AppContext.BaseDirectory, "Toolbar"), community.Text); toolbarStatus.Text = WindowsPresentation.Text("windows.settingsForm.installedAvailableStatus", new Dictionary<string, object> { ["value1"] = WindowsPresentation.Format($"{value.InstalledVersion ?? "Unknown / not installed"}"), ["value2"] = WindowsPresentation.Format($"{value.BundledVersion ?? "Unknown"}"), ["value3"] = WindowsPresentation.Format($"{value.Status}"), ["value4"] = WindowsPresentation.Format($"{value.Message}") }); }
        community.TextChanged += (_, _) => RefreshToolbar(); RefreshToolbar();
        Button(panel, WindowsPresentation.Text("windows.settingsForm.installUpdateRepairToolbar"), (_, _) => { app.InstallToolbar(this, community.Text); RefreshToolbar(); });
        Button(panel, WindowsPresentation.Text("windows.settingsForm.removeAeroscienceToolbar"), (_, _) =>
        {
            var path = app.Preferences.Load().CommunityDirectory;
            if (path is null) { MessageBox.Show(this, WindowsPresentation.Text("windows.settingsForm.noCommunityFolderHasBeenConfigured")); return; }
            if (MessageBox.Show(this, WindowsPresentation.Text("windows.settingsForm.removeOnlyAeroscienceFromThisCommunityFolderItsFiles", new Dictionary<string, object> { ["value1"] = Convert.ToString((path), System.Globalization.CultureInfo.InvariantCulture) ?? "" }), WindowsPresentation.Text("windows.settingsForm.removeToolbar"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try { var backup = ToolbarInstaller.Remove(path, true); MessageBox.Show(this, backup is null ? WindowsPresentation.Text("windows.settingsForm.theToolbarWasNotInstalledInThatFolder") : WindowsPresentation.Text("windows.settingsForm.toolbarRemovedBackupRetainedAt", new Dictionary<string, object> { ["value1"] = Convert.ToString((backup), System.Globalization.CultureInfo.InvariantCulture) ?? "" })); }
            catch (Exception error) { app.ShowError(error); }
        });
        Label(panel, File.Exists(Path.Combine(AppContext.BaseDirectory, "SimConnect.dll"))
            ? WindowsPresentation.Text("windows.settingsForm.theNativeSimconnectRuntimeIsIncludedConnectionStatusIs")
            : WindowsPresentation.Text("windows.settingsForm.theNativeSimconnectRuntimeIsMissingRunAeroscienceSetup"));
        Button(panel, WindowsPresentation.Text("windows.settingsForm.microsoftVisualCPrerequisite"), (_, _) => Process.Start(new ProcessStartInfo(
            "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist")
        { UseShellExecute = true }));
        Label(panel, WindowsPresentation.Text("windows.settingsForm.ifWindowsReportsAMissingVisualCDependencyUse"));
    }
}
