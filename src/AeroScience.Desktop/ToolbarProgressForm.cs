using AeroScience.Engine;

namespace AeroScience.Desktop;

public sealed class ToolbarProgressForm : BrandedForm
{
    private readonly Label stage = new() { AutoSize = true, MaximumSize = new(620, 0) };
    private readonly Label message = new() { AutoSize = true, MaximumSize = new(620, 0) };
    private readonly ProgressBar progress = new() { Width = 620, Style = ProgressBarStyle.Marquee };
    private readonly Button done = new() { Text = "Close", Enabled = false, AutoSize = true };
    private bool working, succeeded;
    public ToolbarProgressForm(MainForm app, string community)
    {
        Text = "AeroScience · Toolbar installation"; Size = new(700, 400); StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(22), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true }; Controls.Add(panel);
        var versions = ToolbarVersions.Inspect(Path.Combine(AppContext.BaseDirectory, "Toolbar"), community);
        panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new(620, 0), Text = $"Installed: {versions.InstalledVersion ?? "Unknown / not installed"}\nAvailable: {versions.BundledVersion ?? "Unknown"}\nCommunity: {community}" });
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
                stage.Text = "Installation completed"; message.Text = "The installed package and version were verified. Start or restart MSFS to load the updated toolbar.";
            }
            catch (Exception error) { stage.Text = "Installation stopped"; message.Text = error.Message + "\nPrevious files are restored when possible. Details are in AeroScience's toolbar logs."; DialogResult = DialogResult.None; }
            finally { working = false; done.Enabled = true; progress.Style = ProgressBarStyle.Continuous; }
        };
    }
}

public sealed class ToolbarUpdateForm : BrandedForm
{
    public ToolbarUpdateForm(MainForm app, ToolbarVersionStatus status)
    {
        Text = "AeroScience MSFS Toolbar"; Size = new(640, 300); StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), FlowDirection = FlowDirection.TopDown, WrapContents = false }; Controls.Add(panel);
        panel.Controls.Add(new Label { Text = status.Message + "\n\nClose MSFS before updating. The companion can continue when you postpone this action.", MaximumSize = new(570, 0), AutoSize = true });
        var update = new Button { Text = status.Status == ToolbarInstallState.Missing ? "Install Toolbar" : "Update Toolbar", AutoSize = true }; panel.Controls.Add(update);
        update.Click += (_, _) => { if (app.InstallToolbar(this, status.CommunityDirectory)) { DialogResult = DialogResult.OK; Close(); } };
        var later = new Button { Text = "Remind Me Later", AutoSize = true }; later.Click += (_, _) => Close(); panel.Controls.Add(later);
    }
}
