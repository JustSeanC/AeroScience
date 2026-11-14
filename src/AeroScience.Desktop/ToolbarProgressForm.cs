using AeroScience.Engine;

namespace AeroScience.Desktop;

public sealed class ToolbarProgressForm : BrandedForm
{
    private readonly Label stage = new() { AutoSize = true, MaximumSize = new(620, 0) };
    private readonly Label message = new() { AutoSize = true, MaximumSize = new(620, 0) };
    private readonly ProgressBar progress = new() { Width = 620, Style = ProgressBarStyle.Marquee };
    private readonly Button done = new() { Text = WindowsPresentation.Text("ui.aviationReference.messageClose"), Enabled = false, AutoSize = true };
    private bool working, succeeded;
    public ToolbarProgressForm(MainForm app, string community)
    {
        Text = WindowsPresentation.Text("windows.toolbarProgressForm.aeroscienceToolbarInstallation"); Size = new(700, 400); StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(22), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true }; Controls.Add(panel);
        var versions = ToolbarVersions.Inspect(Path.Combine(AppContext.BaseDirectory, "Toolbar"), community);
        panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new(620, 0), Text = WindowsPresentation.Text("windows.toolbarProgressForm.installedAvailableCommunity", new Dictionary<string, object> { ["value1"] = WindowsPresentation.Format($"{versions.InstalledVersion ?? "Unknown / not installed"}"), ["value2"] = WindowsPresentation.Format($"{versions.BundledVersion ?? "Unknown"}"), ["value3"] = WindowsPresentation.Format($"{community}") }) });
        panel.Controls.Add(stage); panel.Controls.Add(progress); panel.Controls.Add(message); panel.Controls.Add(done);
        done.Click += (_, _) => { DialogResult = succeeded ? DialogResult.OK : DialogResult.Cancel; Close(); }; FormClosing += (_, e) => { if (working) e.Cancel = true; else if (succeeded) DialogResult = DialogResult.OK; };
        Shown += async (_, _) =>
        {
            working = true;
            try
            {
                IProgress<ToolbarProgress> reporter = new Progress<ToolbarProgress>(update =>
                {
                    stage.Text = update.Stage + (update.Percent is { } pct ? $" · {pct}% ({update.Completed}/{update.Total} files)" : ""); message.Text = update.Message;
                    progress.Style = update.Percent is null ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                    if (update.Percent is { } percent) progress.Value = percent;
                });
                await app.RunStoppedAsync(() => Task.Run(() =>
                {
                    var previous = app.Preferences.Load().CommunityDirectory;
                    ToolbarInstaller.Install(Path.Combine(AppContext.BaseDirectory, "Toolbar"), community, previousCommunity: previous,
                        context: new(reporter, Path.Combine(app.Preferences.DirectoryPath, "logs")));
                    app.Preferences.Save(app.Preferences.Load() with { CommunityDirectory = community });
                }));
                succeeded = true;
                stage.Text = WindowsPresentation.Text("windows.toolbarProgressForm.installationCompleted"); message.Text = WindowsPresentation.Text("windows.toolbarProgressForm.theInstalledPackageAndVersionWereVerifiedStartOr");
            }
            catch (Exception error) { stage.Text = WindowsPresentation.Text("windows.toolbarProgressForm.installationStopped"); message.Text = WindowsPresentation.Text("windows.toolbarProgressForm.previousFilesAreRestoredWhenPossibleDetailsAreIn", new Dictionary<string, object> { ["value1"] = Convert.ToString((error.Message), System.Globalization.CultureInfo.InvariantCulture) ?? "" }); DialogResult = DialogResult.None; }
            finally { working = false; done.Enabled = true; progress.Style = ProgressBarStyle.Continuous; }
        };
    }
}

public sealed class ToolbarUpdateForm : BrandedForm
{
    public ToolbarUpdateForm(MainForm app, ToolbarVersionStatus status)
    {
        Text = WindowsPresentation.Text("windows.toolbarProgressForm.aeroscienceMsfsToolbar"); Size = new(640, 300); StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), FlowDirection = FlowDirection.TopDown, WrapContents = false }; Controls.Add(panel);
        panel.Controls.Add(new Label { Text = WindowsPresentation.Text("windows.toolbarProgressForm.closeMsfsBeforeUpdatingTheCompanionCanContinueWhen", new Dictionary<string, object> { ["value1"] = Convert.ToString((status.Message), System.Globalization.CultureInfo.InvariantCulture) ?? "" }), MaximumSize = new(570, 0), AutoSize = true });
        var update = new Button { Text = status.Status == ToolbarInstallState.Missing ? WindowsPresentation.Text("windows.toolbarProgressForm.installToolbar") : WindowsPresentation.Text("windows.toolbarProgressForm.updateToolbar"), AutoSize = true }; panel.Controls.Add(update);
        update.Click += (_, _) => { if (app.InstallToolbar(this, status.CommunityDirectory)) { DialogResult = DialogResult.OK; Close(); } };
        var later = new Button { Text = WindowsPresentation.Text("ui.applicationUpdates.messageRemindMeLater"), AutoSize = true }; later.Click += (_, _) => Close(); panel.Controls.Add(later);
    }
}
