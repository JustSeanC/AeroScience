using AeroScience.Engine;
using AeroScience.Missions;

namespace AeroScience.Desktop;

public sealed class SetupWizardForm : BrandedForm
{
    private readonly MainForm app;
    private readonly Panel content = new() { Dock = DockStyle.Fill, Padding = new(24), AutoScroll = true };
    private readonly Button back = new() { Text = WindowsPresentation.Text("ui.organizationLiveUi.messageBack"), AutoSize = true };
    private readonly Button next = new() { Text = WindowsPresentation.Text("windows.setupWizardForm.next"), AutoSize = true };
    private readonly Button cancel = new() { Text = WindowsPresentation.Text("lidar.cancel"), AutoSize = true };
    private readonly Button skip = new() { Text = WindowsPresentation.Text("windows.setupWizardForm.skipForNow"), AutoSize = true };
    private readonly TextBox mission = new() { Width = 650 };
    private readonly TextBox library = new() { Width = 650 };
    private readonly ComboBox community = new() { Width = 650 };
    private readonly CheckBox install = new() { Text = WindowsPresentation.Text("windows.setupWizardForm.installUpdateTheIncludedMsfs2024Toolbar"), Checked = true, AutoSize = true };
    private readonly Label missionSpace = new() { AutoSize = true };
    private readonly Label librarySpace = new() { AutoSize = true };
    private readonly Label progress = new() { AutoSize = true, MaximumSize = new(690, 0) };
    private readonly CheckBox scientificOnline = new() { Text = WindowsPresentation.Text("windows.setupWizardForm.allowAutomaticScientificDataDiscoveryAndBoundedDownloads"), AutoSize = true };
    private int page;
    private bool working;
    private readonly PresentationPanel presentation;

    public SetupWizardForm(MainForm app)
    {
        this.app = app;
        Text = WindowsPresentation.Text("windows.setupWizardForm.welcomeToAeroscienceSetup"); Size = new(800, 680); MinimumSize = new(760, 600);
        StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var settings = app.Preferences.Load();
        presentation = new(app.Preferences, () => app.RefreshAppearance(true), showSave: false);
        scientificOnline.Checked = new ScientificAccessSettings(app.Preferences.DirectoryPath).Get().Preferences.OnlineEnabled;
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
        var initialMission=mission.Text;var initialLibrary=library.Text;var initialCommunity=community.Text;var initialOnline=scientificOnline.Checked;var initialInstall=install.Checked;
        FormClosing += (_, e) => { if (working) e.Cancel = true;
            else if(DialogResult!=DialogResult.OK&&(presentation.HasChanges||mission.Text!=initialMission||library.Text!=initialLibrary||community.Text!=initialCommunity||scientificOnline.Checked!=initialOnline||install.Checked!=initialInstall)&&MessageBox.Show(this,WindowsPresentation.Text("windows.setupWizardForm.closeSetupWithUnappliedChoicesChooseNoToKeep"),WindowsPresentation.Text("windows.setupWizardForm.unfinishedSetup"),MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)e.Cancel=true; };
        Render();
    }

    private static Label TextLabel(string text) => new() { Text = text, AutoSize = true, MaximumSize = new(690, 0), Margin = new(3, 10, 3, 10) };
    private void Browse(TextBox path, bool database)
    {
        using var dialog = new FolderBrowserDialog { Description = database ? WindowsPresentation.Text("windows.setupWizardForm.chooseTheFolderForMissionsDb") : WindowsPresentation.Text("windows.setupWizardForm.chooseAScientificDataLibraryFolder"), UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = database ? Path.Combine(dialog.SelectedPath, "missions.db") : dialog.SelectedPath;
    }
    private void Capacity(string path, Label label, bool database)
    {
        try
        {
            var folder = database ? Path.GetDirectoryName(path)! : path;
            SetupCoordinator.ValidateDataLocation(folder, AppContext.BaseDirectory);
            var bytes = new LocalStorageCapacity().FreeBytes(folder);
            label.Text = WindowsPresentation.Text("windows.setupWizardForm.availableDriveSpaceGib", new Dictionary<string, object> { ["value1"] = WindowsPresentation.Format($"{bytes / 1073741824.0:0.00}"), ["value2"] = Convert.ToString(((bytes < StorageManager.LibraryLowSpaceWarningBytes ? WindowsPresentation.Text("windows.science.spaceLimited") : "")), System.Globalization.CultureInfo.InvariantCulture) ?? "" });
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            label.Text = error.Message;
            var current = app.Preferences.Load();
            if (!database && current.Initialized && SamePath(path, current.ScienceLibraryDirectory))
                label.Text += WindowsPresentation.Text("windows.setupWizardForm.keepThisLocationToPreserveTheUnavailableLibraryReconnect");
        }
    }
    private void Render()
    {
        content.Controls.Clear();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(new Label { Text = WindowsPresentation.Text("windows.setupWizardForm.aeroscienceStepOf4", new Dictionary<string, object> { ["value1"] = WindowsPresentation.Format($"{Application.ProductVersion.Split('+')[0]}"), ["value2"] = WindowsPresentation.Format($"{page + 1}") }), AutoSize = true, Font = new(Font.FontFamily, 16) });
        content.Controls.Add(panel); back.Enabled = page > 0; skip.Visible = page == 2; next.Text = page == 3 ? WindowsPresentation.Text("windows.setupWizardForm.finishSetup") : WindowsPresentation.Text("windows.setupWizardForm.next");
        if (page == 0)
        {
            panel.Controls.Add(presentation);
            panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.setUpLocalMissionStorageAndTheMsfs2024")));
            panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.existingLocationsAndSettingsArePreselectedUpdatesKeepThem")));
            panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.missionHistoryWorksWithoutMsfsOrInternetLiveRecording")));
        }
        else if (page == 1)
        {
            panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.missionDatabaseFileChooseItsFolderFilenameRemainsMissions"))); panel.Controls.Add(mission);
            var missionBrowse = new Button { Text = WindowsPresentation.Text("windows.setupWizardForm.browseMissionDatabaseFolder"), AutoSize = true }; missionBrowse.Click += (_, _) => Browse(mission, true); panel.Controls.Add(missionBrowse); panel.Controls.Add(missionSpace);
            panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.scientificDataLibraryMayBeOnADifferentInternal"))); panel.Controls.Add(library);
            var libraryBrowse = new Button { Text = WindowsPresentation.Text("windows.setupWizardForm.browseScienceLibraryFolder"), AutoSize = true }; libraryBrowse.Click += (_, _) => Browse(library, false); panel.Controls.Add(libraryBrowse); panel.Controls.Add(librarySpace);
            panel.Controls.Add(TextLabel(StorageManager.LargeDataWarning));
            panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.keepTheSuggestedFoldersToPreserveCurrentStorageNew")));
            Capacity(mission.Text, missionSpace, true); Capacity(library.Text, librarySpace, false);
        }
        else if (page == 2)
        {
            panel.Controls.Add(install); panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.confirmTheMsfs2024CommunityFolderDetectionReadsSteam")));
            panel.Controls.Add(community);
            var browse = new Button { Text = WindowsPresentation.Text("windows.setupWizardForm.browseCommunityFolder"), AutoSize = true };
            browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = WindowsPresentation.Text("windows.setupWizardForm.selectMsfs2024SConfiguredCommunityFolder"), UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) community.Text = dialog.SelectedPath; }; panel.Controls.Add(browse);
            panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.closeMsfsBeforeInstallingOrUpdatingOnlyAeroscienceIs")));
            if (ToolbarInstaller.SimulatorRunning()) panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.msfsIsCurrentlyRunningCloseItBeforeContinuingWith")));
        }
        else
        {
            panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.missionDatabaseScientificDataLibraryToolbar", new Dictionary<string, object> { ["value1"] = WindowsPresentation.Format($"{mission.Text}"), ["value2"] = WindowsPresentation.Format($"{library.Text}"), ["value3"] = WindowsPresentation.Format($"{(install.Checked ? "Install/update in " + community.Text : "Skipped — configure later in Settings")}") })));
            panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.finishSetupConfirmsTheseChoicesChangedStorageLocationsUse")));
            panel.Controls.Add(TextLabel(ScientificAccessSettings.Privacy));
            panel.Controls.Add(scientificOnline);
            panel.Controls.Add(TextLabel(WindowsPresentation.Text("windows.setupWizardForm.optionalAndSeparateFromOnlineMapsLeaveUncheckedFor")));
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
                    && MessageBox.Show(this, WindowsPresentation.Text("windows.setupWizardForm.theScienceLibraryDriveHasLimitedFreeSpaceFuture"), WindowsPresentation.Text("windows.setupWizardForm.storageCapacity"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
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
                app.Preferences.Save(app.Preferences.Load() with { SetupCompleted = true });
            }));
            // Update the picker snapshot on the UI thread, so an optional toolbar
            // retry cannot re-use the revision from before this successful save.
            presentation.Save(); app.RefreshAppearance(true);
            working = false;
            if (installToolbar && !app.InstallToolbar(this, selectedCommunity)) { back.Enabled = next.Enabled = cancel.Enabled = true; return; }
            new ScientificAccessSettings(app.Preferences.DirectoryPath).Save(new ScientificAccessSettings(app.Preferences.DirectoryPath).Get().Preferences with { OnlineEnabled = scientificOnline.Checked, PrivacyAccepted = scientificOnline.Checked });
            DialogResult = DialogResult.OK; Close();
        }
        catch (Exception error) { working = false; back.Enabled = page > 0; next.Enabled = cancel.Enabled = true; app.ShowError(error); }
    }
    private static bool SamePath(string left, string right)
    {
        try { return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
