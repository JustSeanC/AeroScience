using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Desktop;

// Only reachable through the trusted local Windows UI. There is no browser deletion endpoint.
public sealed class DataManagementForm : Form
{
    private readonly MainForm app;
    private readonly FlowLayoutPanel panel = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(18) };
    private readonly CheckBox plans = new() { Text = "Also remove saved mission plans and their preflight notes when clearing flight data", AutoSize = true };
    private readonly CheckBox backup = new() { Text = "Create and verify a SQLite backup before clearing (recommended)", Checked = true, AutoSize = true };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new(700, 0) };
    private bool working;
    public DataManagementForm(MainForm app)
    {
        this.app = app; Text = "AeroScience · Storage & Data · Data Management"; Size = new(800, 650); AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(panel); panel.Controls.Add(new Label { Text = "Export individual completed missions from Mission History. These operations clear whole archives independently.", AutoSize = true, MaximumSize = new(700, 0) });
        panel.Controls.Add(plans); panel.Controls.Add(backup);
        foreach (var science in new[] { false, true })
        {
            var heading = new Label { Text = science ? "Scientific Data Library" : "Flight / Mission Data", AutoSize = true, Font = new(Font, FontStyle.Bold), Margin = new(3, 20, 3, 3) }; panel.Controls.Add(heading);
            var preview = new Label { AutoSize = true, MaximumSize = new(700, 0) }; panel.Controls.Add(preview);
            var input = new TextBox { Width = 650, PlaceholderText = science ? "Type DELETE SCIENCE DATA" : "Type DELETE FLIGHT DATA" }; panel.Controls.Add(input);
            var button = new Button { Text = science ? "Clear All Scientific Data…" : "Clear All Flight & Mission Data…", AutoSize = true }; panel.Controls.Add(button);
            void RefreshPreview()
            {
                try { preview.Text = Describe(Preview(science)); }
                catch (Exception error) { preview.Text = "Unavailable: " + error.Message; }
            }
            RefreshPreview(); plans.CheckedChanged += (_, _) => RefreshPreview();
            button.Click += async (_, _) => { await Clear(science, input.Text); input.Clear(); RefreshPreview(); };
        }
        panel.Controls.Add(status);
        FormClosing += (_, e) => { if (working) e.Cancel = true; };
    }
    private DataClearPreview Preview(bool science)
    {
        var settings = app.Preferences.Load();
        if (!science) return new MissionStore(Path.Combine(settings.MissionDirectory, "missions.db")).PreviewFlightClear(plans.Checked);
        using var store = new ScienceStore(settings.ScienceLibraryDirectory); return store.PreviewClear();
    }
    private static string Describe(DataClearPreview p) => $"Selected location: {p.Directory}\nMissions: {p.Missions} · Plans: {p.Plans} · Datasets: {p.Datasets} · Observations: {p.Observations}\nManaged database size: {p.ApproximateBytes / 1048576d:0.00} MiB (clearing rows may retain SQLite allocation)\nRemoves: {p.Removes}\nPreserves: {p.Preserves}\nRequired confirmation: {p.ConfirmationPhrase}";
    private async Task Clear(bool science, string typed)
    {
        if (working || app.Busy || app.Recording) { MessageBox.Show(this, "End recording and wait for other operations first."); return; }
        try
        {
            var preview = Preview(science);
            if (typed != preview.ConfirmationPhrase) throw new InvalidOperationException("Type the exact confirmation phrase shown for this archive.");
            string? backupFolder = null;
            if (backup.Checked)
            {
                using var browse = new FolderBrowserDialog { Description = "Choose a backup folder outside the application installation", UseDescriptionForTitle = true };
                if (browse.ShowDialog(this) != DialogResult.OK) return; backupFolder = browse.SelectedPath;
                SetupCoordinator.ValidateDataLocation(backupFolder, AppContext.BaseDirectory);
            }
            if (MessageBox.Show(this, Describe(preview) + "\n\nPermanently clear these records? " + (backupFolder is null ? "No backup was requested." : "A verified backup will be retained."), "Confirm archive clearing", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            working = true; panel.Enabled = false; status.Text = "Stopping services, verifying backup and clearing transactionally…";
            var includePlans = plans.Checked;
            var result = "";
            await app.RunStoppedAsync(() => Task.Run(() =>
            {
                var settings = app.Preferences.Load(); using var missionLease = StorageManager.Acquire(settings.MissionDirectory);
                var missions = new MissionStore(Path.Combine(settings.MissionDirectory, "missions.db"));
                if (missions.List(1000, 0).Any(m => m.Status == MissionStatus.Recording)) throw new InvalidOperationException("A recording remains active; no data was cleared.");
                using var library = new ScienceStore(settings.ScienceLibraryDirectory);
                if (science)
                {
                    library.Initialize();
                    if (backupFolder is not null) result = DataBackup.Create(library.DatabasePath, backupFolder, true);
                    library.Clear(typed, false);
                    missions.ReconcileDatasetReferences(library.LibraryId, library.ContainsDataset);
                }
                else
                {
                    if (backupFolder is not null) result = DataBackup.Create(missions.DatabasePath, backupFolder, false);
                    missions.ClearFlightData(typed, includePlans);
                }
            }));
            status.Text = "Completed. " + (result.Length > 0 ? "Verified backup: " + result : "No backup requested.");
        }
        catch (Exception error) { status.Text = "Operation stopped: " + error.Message + " Committed records and reference availability are reconciled when services restart."; app.ShowError(error); }
        finally { working = false; panel.Enabled = true; }
    }
}
