using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Desktop;

// Only reachable through the trusted local Windows UI. There is no browser deletion endpoint.
public sealed class DataManagementForm : BrandedForm
{
    private readonly MainForm app;
    private readonly FlowLayoutPanel panel = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(18) };
    private readonly CheckBox plans = new() { Text = "Also remove saved mission plans and their preflight notes when clearing flight data", AutoSize = true };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new(700, 0) };
    private bool working;
    public DataManagementForm(MainForm app)
    {
        this.app = app; Text = "AeroScience · Storage & Data · Data Management"; Size = new(800, 650); AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new(680, 500);
        Font = new Font("Segoe UI", 11); Controls.Add(panel); panel.Controls.Add(new Label { Text = "Export individual completed missions from Mission History. These operations clear whole archives independently. Permanent scientific deletion purges selected scientific content from managed backups; unrelated content is preserved.", AutoSize = true, MaximumSize = new(700, 0) });
        foreach (var science in new[] { false, true })
        {
            var heading = new Label { Text = science ? "Clear Scientific Data" : "Clear Flight & Mission Data", UseMnemonic = false, AutoSize = true, Font = new(Font, FontStyle.Bold), Margin = new(3, 20, 3, 3) }; panel.Controls.Add(heading);
            var preview = new Label { AutoSize = true, MaximumSize = new(700, 0) }; panel.Controls.Add(preview);
            if(science){
                var deleteScience=new Button{Text="Delete Collected Scientific Data",AutoSize=true,BackColor=Color.Firebrick,ForeColor=Color.White};panel.Controls.Add(deleteScience);
                deleteScience.Click+=async(_,_)=>{await DeleteCollected();RefreshPreview();};RefreshPreview();continue;
            }
            if (!science) panel.Controls.Add(plans);
            panel.Controls.Add(new Label { Text = "Deletion options:", AutoSize = true, Margin = new(3, 12, 3, 3) });
            var options = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var noBackup = new RadioButton { Text = "Permanently delete without creating a new backup", Checked = !ArchiveClearPolicy.DefaultBackupRequested, AutoSize = true };
            var backup = new RadioButton { Text = "Create a recovery backup, then delete", Checked = ArchiveClearPolicy.DefaultBackupRequested, AutoSize = true };
            options.Controls.Add(noBackup); options.Controls.Add(backup); panel.Controls.Add(options);
            panel.Controls.Add(new Label { Text = science ? "Confirmation: DELETE SCIENCE DATA" : "Confirmation: DELETE FLIGHT DATA", AutoSize = true });
            var input = new TextBox { Width = 650, PlaceholderText = science ? "Type DELETE SCIENCE DATA" : "Type DELETE FLIGHT DATA" }; panel.Controls.Add(input);
            var button = new Button { Text = science ? "Delete Scientific Data" : "Delete Flight Data", AutoSize = true }; panel.Controls.Add(button);
            void RefreshPreview()
            {
                try { preview.Text = Describe(Preview(science)); }
                catch (Exception error) { preview.Text = "Unavailable: " + error.Message; }
            }
            RefreshPreview(); plans.CheckedChanged += (_, _) => RefreshPreview();
            button.Click += async (_, _) => { await Clear(science, input.Text, backup.Checked); input.Clear(); RefreshPreview(); };
        }
        panel.Controls.Add(status);
        panel.ClientSizeChanged += (_, _) =>
        {
            var available = Math.Max(200, panel.ClientSize.Width - panel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 12);
            foreach (Control control in panel.Controls)
            {
                if (control is Label label) label.MaximumSize = new(available, 0);
                else if (control is TextBox box) box.Width = available;
                else if (control is FlowLayoutPanel options) foreach (Control option in options.Controls) option.MaximumSize = new(available, 0);
                else if (control is CheckBox check) check.MaximumSize = new(available, 0);
            }
        };
        FormClosing += (_, e) => { if (working) e.Cancel = true; };
    }
    private DataClearPreview Preview(bool science)
    {
        var settings = app.Preferences.Load();
        if (!science) return new MissionStore(Path.Combine(settings.MissionDirectory, "missions.db")).PreviewFlightClear(plans.Checked);
        using var store = new ScienceStore(settings.ScienceLibraryDirectory);store.MissionIsProtected=new MissionStore(Path.Combine(settings.MissionDirectory,"missions.db")).IsProtected;return store.PreviewClear();
    }
    private static string Describe(DataClearPreview p) => (p.Kind == "flight" ? $"{p.Missions} recorded missions will be deleted." : $"{p.Datasets} scientific datasets, {p.Observations} observations and {p.ManagedProductFiles} generated product files will be deleted.") + $"\nData to delete from: {p.Directory}\nProtected missions/datasets included in this deliberate clear: {p.ProtectedItems}\nSaved plans to delete: {p.Plans}\nApproximate managed size: {p.ApproximateBytes / 1048576d:0.00} MiB including SQLite companion files and indexed products (clearing rows may retain allocation)\nGenerated product files: {p.ManagedProductFiles} ({p.ManagedProductBytes / 1048576d:0.00} MiB)\n" + string.Join("\n", p.ProductPathPreview ?? []) + $"\nRemoves: {p.Removes}\nPreserves: {p.Preserves}";
    private async Task DeleteCollected()
    {
        if(working||app.Busy||app.Recording){MessageBox.Show(this,"End recording and wait for active operations first.");return;}
        try{
            working=true;panel.Enabled=false;status.Text="Reviewing the exact collected-science selection; background operations are unchanged…";
                var review=await Task.Run(app.PreviewCollectedScience);
                using var confirm=new Form{Text="Delete Collected Mission Science",Size=new(650,400),MinimumSize=new(450,320),StartPosition=FormStartPosition.CenterParent,MaximizeBox=false,MinimizeBox=false};
                var content=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(18),RowCount=2,ColumnCount=1};content.RowStyles.Add(new(SizeType.Percent,100));content.RowStyles.Add(new(SizeType.AutoSize));confirm.Controls.Add(content);
                content.Controls.Add(new Label{Dock=DockStyle.Fill,AutoSize=false,Text=$"{review.Missions} missions · {review.Datasets} datasets · {review.Observations} observations · {review.Products} generated products. Estimated managed file space: {review.ManagedBytes/1048576d:0.00} MiB.\n\nDeletion is permanent. Scientific measurements, generated analysis and dependent grades are purged, including their managed copies. No recovery backup is created. Flight recordings and external source datasets remain untouched.\n\n"+string.Join("\n",review.Blockers)});
                var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.RightToLeft};var delete=new Button{Text="Delete All Collected Science",AutoSize=true,BackColor=Color.Firebrick,ForeColor=Color.White,DialogResult=DialogResult.OK,Enabled=review.Blockers.Length==0};var cancel=new Button{Text="Cancel",AutoSize=true,DialogResult=DialogResult.Cancel};actions.Controls.Add(delete);actions.Controls.Add(cancel);content.Controls.Add(actions);confirm.CancelButton=cancel;confirm.AcceptButton=cancel;confirm.Shown+=(_,_)=>cancel.Focus();
                if(confirm.ShowDialog(this)!=DialogResult.OK){status.Text="Cancelled. No scientific data was changed.";return;}
                status.Text="Permanently deleting reviewed collected science…";
                var result=await Task.Run(()=>app.DeleteCollectedScience(new(review.Id,review.SelectionHash)));
                status.Text=$"{result.State}: {result.RemovedFiles} files removed, {result.PendingFiles} pending. {result.Warning}";
        }catch(Exception error){status.Text="Deletion is not complete: "+error.Message;app.ShowError(error);}finally{working=false;panel.Enabled=true;}
    }
    private async Task Clear(bool science, string typed, bool backupRequested)
    {
        if (working || app.Busy || app.Recording) { MessageBox.Show(this, "End recording and wait for other operations first."); return; }
        try
        {
            var preview = Preview(science);
            if (typed != preview.ConfirmationPhrase) throw new InvalidOperationException("Type the exact confirmation phrase shown for this archive.");
            if(preview.ProtectedItems>0&&MessageBox.Show(this,$"This deliberate archive clear includes {preview.ProtectedItems} protected mission(s)/dataset(s). Protection prevents automatic cleanup; this explicit typed-confirmation operation will delete them. Continue to final confirmation?","Protected research data",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            var selection = ArchiveClearPolicy.ChooseBackup(backupRequested, () =>
            {
                using var browse = new FolderBrowserDialog
                {
                    Description = science ? "Choose a recovery BACKUP destination for Scientific Data — not a new Science Library location" : "Choose a recovery BACKUP destination for Flight & Mission Data — not a new mission storage location",
                    UseDescriptionForTitle = true,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    AutoUpgradeEnabled = true
                };
                return browse.ShowDialog(this) == DialogResult.OK ? browse.SelectedPath : null;
            }, destination =>
            {
                SetupCoordinator.ValidateDataLocation(destination, AppContext.BaseDirectory);
                var locations = app.Preferences.Load();
                ArchiveClearPolicy.ValidateBackupDestination(destination, locations.MissionDirectory, locations.ScienceLibraryDirectory, AppContext.BaseDirectory);
            });
            if (selection.Cancelled) return;
            var backupFolder = selection.Directory;
            if (MessageBox.Show(this, Describe(preview) + "\n\n" + ArchiveClearPolicy.ConfirmationWarning(backupFolder) + "\n\nPermanently delete these records?", science ? "Confirm scientific data deletion" : "Confirm flight data deletion", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            working = true; panel.Enabled = false; status.Text = backupFolder is null ? "Stopping services and clearing transactionally without a new backup…" : "Stopping services, verifying the recovery backup and clearing transactionally…";
            var includePlans = plans.Checked;
            var result = "";
            await app.RunStoppedAsync(() => Task.Run(() =>
            {
                var settings = app.Preferences.Load(); using var missionLease = StorageManager.Acquire(settings.MissionDirectory);
                var missions = new MissionStore(Path.Combine(settings.MissionDirectory, "missions.db"));
                if (missions.List(1000, 0).Any(m => m.Status == MissionStatus.Recording)) throw new InvalidOperationException("A recording remains active; no data was cleared.");
                using var library = new ScienceStore(settings.ScienceLibraryDirectory);
                if (backupFolder is not null) result = DataBackup.Create(missions.DatabasePath, backupFolder, false);
                missions.ClearFlightData(typed, includePlans);
            }));
            status.Text = "Completed. " + (result.Length > 0 ? "Verified backup: " + result : "No backup requested.");
        }
        catch (Exception error) { status.Text = "Operation stopped: " + error.Message + " Committed records and reference availability are reconciled when services restart."; app.ShowError(error); }
        finally { working = false; panel.Enabled = true; }
    }
}
