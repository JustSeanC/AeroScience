using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.IO.Compression;
using AeroScience.Engine;
using AeroScience.Missions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AeroScience.Desktop;

public sealed class MainForm : Form, IDesktopActions
{
    public PreferenceStore Preferences { get; }
    public StorageManager Storage { get; }
    private WebApplication? engine;
    private readonly Label status = new() { AutoSize = true, MaximumSize = new(700, 0) };
    private readonly Button dashboard = new() { Text = "Open Dashboard", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private bool closing;
    public bool Busy { get; private set; }
    public bool Recording => engine?.Services.GetRequiredService<MissionCoordinator>().Snapshot().ActiveMission is not null;
    public StorageStatus MissionStorage => Storage.Status(StorageKind.Missions);
    public StorageStatus ScienceStorage => Storage.Status(StorageKind.ScienceLibrary);

    public MainForm(string configurationDirectory, bool showSetup = false)
    {
        Preferences = new(configurationDirectory); Storage = new(Preferences);
        Text = "AeroScience " + Application.ProductVersion.Split('+')[0]; MinimumSize = new(560, 290); Size = new(760, 350);
        AutoScaleMode = AutoScaleMode.Dpi;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        panel.Controls.Add(new Label { Text = "AeroScience · Science Mission Operations", AutoSize = true, Font = new(Font.FontFamily, 16) });
        panel.Controls.Add(status); panel.Controls.Add(dashboard);
        dashboard.Click += (_, _) => OpenDashboard();
        AddButton(panel, "Settings", () => ShowSettings(false));
        AddButton(panel, "Storage", () => ShowSettings(true));
        AddButton(panel, "Collect Feedback", ShowFeedback);
        AddButton(panel, "Exit", Close); Controls.Add(panel);
        timer.Tick += (_, _) => RefreshStatus();
        Shown += async (_, _) =>
        {
            try
            {
                var recovery = Storage.RecoverInterrupted();
                if (recovery is not null) MessageBox.Show(this, recovery, "Storage recovery");
                if (showSetup || !Preferences.Load().SetupCompleted)
                {
                    using var wizard = new SetupWizardForm(this);
                    if (wizard.ShowDialog(this) != DialogResult.OK) { Close(); return; }
                }
                Storage.Initialize(); await StartEngineAsync(); OpenDashboard();
            }
            catch (Exception error) { ShowError(error); }
            timer.Start(); RefreshStatus();
        };
        FormClosing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            if (Busy) { MessageBox.Show(this, "Wait for the storage/setup operation to finish."); return; }
            if (Recording && MessageBox.Show(this, "A mission is recording. Exit and preserve it as interrupted?", "Exit AeroScience", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            timer.Stop();
            try { await StopEngineAsync(); }
            catch (Exception error) { ShowError(error); }
            finally { closing = true; Close(); }
        };
    }

    private static void AddButton(Control panel, string label, Action action)
    { var button = new Button { Text = label, AutoSize = true }; button.Click += (_, _) => action(); panel.Controls.Add(button); }
    public async Task StartEngineAsync()
    {
        if (engine is not null) return;
        var preferences = Preferences.Load();
        foreach (var path in new[] { preferences.MissionDirectory, preferences.ScienceLibraryDirectory })
            SetupCoordinator.ValidateDataLocation(path, AppContext.BaseDirectory, requireAvailable: false);
        preferences = Storage.Initialize();
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\AeroScience"))
        {
            key.SetValue("MissionDirectory", preferences.MissionDirectory);
            key.SetValue("ScienceLibraryDirectory", preferences.ScienceLibraryDirectory);
        }
        var args = new[] {
            "--AeroScience:Telemetry=SimConnect",
            "--AeroScience:DataDirectory=" + preferences.MissionDirectory,
            "--AeroScience:ScienceLibraryDirectory=" + preferences.ScienceLibraryDirectory,
            "--AeroScience:RequireExistingDatabase=true", "--AeroScience:EnableToolbarBridge=true",
            "--AeroScience:SamplingIntervalMs=" + preferences.SamplingIntervalMs,
            "--AeroScience:EnableLan=" + preferences.EnableLan,
            "--AeroScience:LanAddress=" + preferences.LanAddress
        };
        var app = EngineApplication.Create(args, services => services.AddSingleton<IDesktopActions>(this));
        try { await app.StartAsync(); engine = app; }
        catch { await app.DisposeAsync(); throw; }
        dashboard.Enabled = true;
    }
    public async Task StopEngineAsync()
    {
        var app = engine; engine = null; dashboard.Enabled = false;
        if (app is null) return;
        try { await app.StopAsync(); }
        finally { await app.DisposeAsync(); }
    }
    public async Task RunStoppedAsync(Func<Task> action)
    {
        if (Busy) throw new InvalidOperationException("Another setup/storage operation is running.");
        // Atomically reject recording and block new toolbar/browser starts before shutdown.
        using var maintenance = engine?.Services.GetRequiredService<MissionCoordinator>().EnterMaintenance();
        Busy = true;
        try { await StopEngineAsync(); await action(); }
        finally
        {
            try { await StartEngineAsync(); } catch (Exception error) { ShowError(error); }
            Busy = false;
            RefreshStatus();
        }
    }
    private void RefreshStatus()
    {
        if (Busy) return;
        if (engine is null) { status.Text = "Engine stopped. Open Storage/Settings to resolve startup or storage errors. Your existing archive has not been replaced."; dashboard.Enabled = false; return; }
        if (engine.Lifetime.ApplicationStopped.IsCancellationRequested)
        { status.Text = "The local engine stopped after an error. Collect Feedback and check Storage/Settings; existing records are retained."; dashboard.Enabled = false; return; }
        var snapshot = engine.Services.GetRequiredService<MissionCoordinator>().Snapshot();
        status.Text = $"Engine running · {snapshot.Connection.Message}\n{(snapshot.ActiveMission is null ? "No active mission" : "Mission recording")} · Toolbar: {ToolbarInstaller.InstalledStatus(Preferences.Load())}";
    }
    public void OpenDashboard()
    {
        if (engine is null) { MessageBox.Show(this, "The engine is stopped. Check Storage/Settings."); return; }
        Process.Start(new ProcessStartInfo("http://127.0.0.1:5080") { UseShellExecute = true });
    }
    private void ShowSettings(bool storage)
    {
        if (Busy) return;
        try
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); Activate();
            using var form = new SettingsForm(this, storage); form.ShowDialog(this);
        }
        catch (Exception error) { ShowError(error); }
    }
    public void ShowSetup(IWin32Window? owner = null)
    {
        if (Busy || Recording) { MessageBox.Show(this, "End recording and wait for current operations before running setup."); return; }
        using var wizard = new SetupWizardForm(this);
        if (wizard.ShowDialog(owner ?? this) == DialogResult.OK) OpenDashboard();
    }
    private void Dispatch(Action action)
    { if (!IsDisposed) BeginInvoke(action); }
    void IDesktopActions.OpenSettings() => Dispatch(() => ShowSettings(false));
    void IDesktopActions.OpenStorage() => Dispatch(() => ShowSettings(true));
    void IDesktopActions.OpenDataManagement() => Dispatch(ShowDataManagement);
    public void ShowDataManagement() { if (!Busy) { using var form = new DataManagementForm(this); form.ShowDialog(this); } }
    void IDesktopActions.CollectFeedback() => Dispatch(ShowFeedback);
    public void ShowError(Exception error) => MessageBox.Show(this, error.Message, "AeroScience", MessageBoxButtons.OK, MessageBoxIcon.Error);
    private void ShowFeedback()
    {
        using var notes = new Form { Text = "Collect Feedback", Width = 560, Height = 300, StartPosition = FormStartPosition.CenterParent };
        var text = new TextBox { Multiline = true, Dock = DockStyle.Fill, PlaceholderText = "Optional reproduction steps and notes" };
        var save = new Button { Text = "Save feedback ZIP…", Dock = DockStyle.Bottom, Height = 42 };
        notes.Controls.Add(text); notes.Controls.Add(save);
        save.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "Feedback ZIP (*.zip)|*.zip", FileName = $"AeroScience-feedback-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip", OverwritePrompt = true };
            if (dialog.ShowDialog(notes) != DialogResult.OK) return;
            try
            {
                using (var zip = ZipFile.Open(dialog.FileName + ".partial", ZipArchiveMode.Create))
                {
                    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    void Add(string name, string value)
                    {
                        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                        writer.Write(value.Replace(profile, "[USERPROFILE]", StringComparison.OrdinalIgnoreCase));
                    }
                    Add("REPORT.txt", $"AeroScience {Application.ProductVersion}\n{RuntimeInformation.OSDescription}\n{RuntimeInformation.FrameworkDescription}\nUTC {DateTimeOffset.UtcNow:O}\n{status.Text}\n\n{text.Text}\n\nLocal report only. Review before sharing; no mission database or pairing secrets included.");
                    var logDirectory = Path.Combine(Preferences.Load().MissionDirectory, "logs");
                    if (Directory.Exists(logDirectory))
                        foreach (var file in Directory.GetFiles(logDirectory, "engine-*.log").OrderDescending().Take(7))
                            if (new FileInfo(file).Length < 5_000_001) Add("logs/" + Path.GetFileName(file), File.ReadAllText(file));
                }
                File.Move(dialog.FileName + ".partial", dialog.FileName, overwrite: true);
                MessageBox.Show(notes, "Saved locally. Review the ZIP before sharing it."); notes.Close();
            }
            catch (Exception error) { ShowError(error); }
        };
        notes.ShowDialog(this);
    }
}
