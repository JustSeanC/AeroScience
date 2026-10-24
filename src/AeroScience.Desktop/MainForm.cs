using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AeroScience.Desktop;

public sealed class MainForm : BrandedForm, IDesktopActions, IScientificFolderOpener
{
    public PreferenceStore Preferences { get; }
    public StorageManager Storage { get; }
    private WebApplication? engine;
    private readonly Label status = new() { AutoSize = true, MaximumSize = new(700, 0) };
    private readonly Button dashboard = new() { Text = "Open Dashboard", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private bool closing;
    private readonly DesktopActionGate desktopActions = new();
    private Form? auxiliary;
    private int focusQueued;
    private bool busy,choosingSource;
    public bool Busy { get => busy || choosingSource || (engine?.Services.GetService<ScientificImportSession>()?.IsBusy ?? false); private set => busy=value; }
    public bool Recording => engine?.Services.GetRequiredService<MissionCoordinator>().Snapshot().ActiveMission is not null;
    private readonly ToolbarPromptPolicy toolbarPrompts = new();
    public ToolbarVersionStatus Toolbar => ToolbarVersions.Inspect(Path.Combine(AppContext.BaseDirectory, "Toolbar"), PreferredCommunity());
    private string? PreferredCommunity() => Preferences.Load().CommunityDirectory ?? ToolbarInstaller.DiscoverCommunityFolders().OrderByDescending(p => Directory.Exists(Path.Combine(p, ApplicationProfile.Current.ToolbarPackage))).FirstOrDefault();
    public StorageStatus MissionStorage => Storage.Status(StorageKind.Missions);
    public StorageStatus ScienceStorage => Storage.Status(StorageKind.ScienceLibrary);

    public MainForm(string configurationDirectory, bool showSetup = false)
    {
        Preferences = new(configurationDirectory, ApplicationProfile.Current.Validation ? ApplicationProfile.Current.ConfigurationDirectory : null); Storage = new(Preferences);
        Preferences.ValidateDirectory(configurationDirectory);
        new MapSettings(configurationDirectory).InitializeDefaults(File.Exists(Preferences.SettingsPath) || File.Exists(Path.Combine(configurationDirectory, "missions.db")));
        Text = ApplicationProfile.Current.Name + " " + Application.ProductVersion.Split('+')[0]; MinimumSize = new(560, 290); Size = new(760, 350);
        AutoScaleMode = AutoScaleMode.Dpi;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var brand=new FlowLayoutPanel{AutoSize=true,WrapContents=false};brand.Controls.Add(PrimaryMark());
        brand.Controls.Add(new Label { Text = ApplicationProfile.Current.Name + " · Science Mission Operations", AutoSize = true, Margin=new(8,12,0,0), Font = new(Font.FontFamily, 16) });panel.Controls.Add(brand);
        panel.Controls.Add(status); panel.Controls.Add(dashboard);
        dashboard.Click += (_, _) => OpenDashboard();
        AddButton(panel, "Settings", () => ShowSettings(false));
        AddButton(panel, "Storage", () => ShowSettings(true));
        AddButton(panel, "Collect Feedback", ShowFeedback);
        if (ApplicationProfile.Current.Validation)
            AddButton(panel, "Copy archives for validation", async () =>
            {
                if (Busy || Recording) return;
                using var mission = new FolderBrowserDialog { Description="Choose the ORIGINAL mission database folder to COPY. It will not be migrated.", UseDescriptionForTitle=true };
                if(mission.ShowDialog(this)!=DialogResult.OK)return;
                using var science = new FolderBrowserDialog { Description="Choose the ORIGINAL Science Library folder to COPY. Originals will be preserved.", UseDescriptionForTitle=true };
                if(science.ShowDialog(this)!=DialogResult.OK)return;
                if(MessageBox.Show(this,"Create a verified disposable copy of both archives inside the Validation profile, then open it with online scientific access disabled? Enable access later in Settings when ready. Originals and production settings are retained. Close other applications using these archives first.","Copy archives for validation",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                try { await RunStoppedAsync(()=>Task.Run(()=>
                {
                    var destination=Path.Combine(Preferences.DirectoryPath,"Copies",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
                    var copy=ValidationArchiveCopy.Create(Preferences,mission.SelectedPath,science.SelectedPath,destination);
                    ValidationArchiveCopy.Select(Preferences,copy);
                })); }catch(Exception error){ShowError(error);}
            });
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
                    if (ShowAuxiliary(wizard) != DialogResult.OK) { Close(); return; }
                }
                Storage.Initialize(); await StartEngineAsync(); OpenDashboard();
                var toolbar = Toolbar;
                if (toolbarPrompts.ShouldPrompt(toolbar)) { toolbarPrompts.Dismiss(toolbar); using var prompt = new ToolbarUpdateForm(this, toolbar); ShowAuxiliary(prompt); }
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
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ApplicationProfile.Current.RegistryKey))
        {
            key.SetValue("MissionDirectory", preferences.MissionDirectory);
            key.SetValue("ScienceLibraryDirectory", preferences.ScienceLibraryDirectory);
        }
        var args = new[] {
            "--AeroScience:Telemetry=SimConnect",
            "--AeroScience:Port=" + ApplicationProfile.Current.Port,
            "--AeroScience:ConfigurationDirectory=" + Preferences.DirectoryPath,
            "--AeroScience:DataDirectory=" + preferences.MissionDirectory,
            "--AeroScience:ScienceLibraryDirectory=" + preferences.ScienceLibraryDirectory,
            "--AeroScience:RequireExistingDatabase=true", "--AeroScience:EnableToolbarBridge=true",
            "--AeroScience:SamplingIntervalMs=" + preferences.SamplingIntervalMs,
            "--AeroScience:EnableLan=" + preferences.EnableLan,
            "--AeroScience:LanAddress=" + preferences.LanAddress
        };
        var app = EngineApplication.Create(args, services => {services.AddSingleton<IDesktopActions>(this);services.AddSingleton<IScientificFolderOpener>(this);services.AddSingleton<IEarthdataCredentials>(new WindowsEarthdataCredentials(Preferences.DirectoryPath));});
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
    internal CollectedScienceDeletionPreview PreviewCollectedScience()
    {
        var services=engine?.Services??throw new InvalidOperationException("Start the companion and reconnect its configured storage before reviewing scientific deletion.");
        return services.GetRequiredService<ScienceStore>().PreviewCollectedDeletion(new(All:true),services.GetRequiredService<MissionStore>());
    }
    internal CollectedScienceDeletionResult DeleteCollectedScience(SourceDeletionExecution request)
    {
        var services=engine?.Services??throw new InvalidOperationException("The companion stopped; resume the reviewed deletion after restart.");var science=services.GetRequiredService<ScienceStore>();
        var result=science.DeleteCollectedScience(request,services.GetRequiredService<MissionStore>(),services.GetRequiredService<MissionCoordinator>().EnterMaintenance);science.Management?.RefreshScience();return result;
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
        status.Text = $"{ApplicationProfile.Current.Name} · Engine running · {snapshot.Connection.Message}\n{(snapshot.ActiveMission is null ? "No active mission" : "Mission recording")} · Toolbar: {Toolbar.Message}";
    }
    public void OpenDashboard(bool settings = false)
    {
        if (engine is null) { MessageBox.Show(this, "The engine is stopped. Check Storage/Settings."); return; }
        Process.Start(new ProcessStartInfo("http://127.0.0.1:" + ApplicationProfile.Current.Port + (settings ? "/#settings" : "")) { UseShellExecute = true });
    }
    private void ShowSettings(bool storage)
    {
        if (Busy) return;
        try
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); Activate();
            using var form = new SettingsForm(this, storage); ShowAuxiliary(form);
        }
        catch (Exception error) { ShowError(error); }
    }
    public void ShowSetup(IWin32Window? owner = null)
    {
        if (Busy || Recording) { MessageBox.Show(this, "End recording and wait for current operations before running setup."); return; }
        using var wizard = new SetupWizardForm(this);
        if (ShowAuxiliary(wizard, owner) == DialogResult.OK) OpenDashboard();
    }
    public bool InstallToolbar(IWin32Window owner, string? community)
    {
        if (Busy || Recording) { MessageBox.Show(owner, "End recording and wait for other operations before updating the toolbar."); return false; }
        if (string.IsNullOrWhiteSpace(community) || !Directory.Exists(community))
        {
            using var browse = new FolderBrowserDialog { Description = "Choose your MSFS 2024 Community folder", UseDescriptionForTitle = true };
            if (browse.ShowDialog(owner) != DialogResult.OK) return false; community = browse.SelectedPath;
        }
        var toolbarStatus=ToolbarVersions.Inspect(Path.Combine(AppContext.BaseDirectory,"Toolbar"),community);
        if(!toolbarStatus.CanInstall&&!toolbarStatus.CanRepair){MessageBox.Show(owner,toolbarStatus.Message);return false;}
        if (ToolbarInstaller.SimulatorRunning()) { MessageBox.Show(owner, "Close MSFS before installing or updating AeroScience's toolbar."); return false; }
        if (MessageBox.Show(owner, $"Install the bundled AeroScience toolbar in:\n{community}\n\nIts previous files will be backed up outside Community. Unrelated add-ons are preserved.", "Confirm toolbar installation", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        using var form = new ToolbarProgressForm(this, community); return ShowAuxiliary(form, owner) == DialogResult.OK;
    }
    private void FocusAuxiliary()
    {
        var target = auxiliary is { IsDisposed: false } ? auxiliary : this;
        if (target.WindowState == FormWindowState.Minimized) target.WindowState = FormWindowState.Normal;
        target.Show(); target.Activate(); target.BringToFront();
    }
    private DialogResult ShowAuxiliary(Form form, IWin32Window? owner = null)
    {
        // Settings may own a legitimate child. Browser requests never supply an owner.
        if (auxiliary is { IsDisposed: false } && owner != auxiliary) { FocusAuxiliary(); return DialogResult.Cancel; }
        var previous = auxiliary;
        auxiliary = form;
        try { return form.ShowDialog(owner ?? this); }
        finally { auxiliary = previous is { IsDisposed: false } ? previous : null; }
    }
    private void Dispatch(Action action)
    {
        var lease = desktopActions.TryEnter();
        if (lease is null)
        {
            if (Interlocked.Exchange(ref focusQueued, 1) != 0) return;
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { try { FocusAuxiliary(); } finally { Interlocked.Exchange(ref focusQueued, 0); } }); else Interlocked.Exchange(ref focusQueued, 0); }
            catch (InvalidOperationException) { Interlocked.Exchange(ref focusQueued, 0); }
            return;
        }
        try
        {
            if (IsDisposed || !IsHandleCreated) { lease.Dispose(); return; }
            BeginInvoke(() =>
            {
                using (lease)
                {
                    try
                    {
                        if (auxiliary is { IsDisposed: false } || Busy) { FocusAuxiliary(); return; }
                        action();
                    }
                    catch (Exception error) { ShowError(error); }
                }
            });
        }
        catch (InvalidOperationException) { lease.Dispose(); }
    }
    void IScientificFolderOpener.OpenScientificFolder(Guid libraryId,Guid? sourceId)=>Dispatch(()=>
    {
        var science=engine?.Services.GetRequiredService<ScienceStore>()??throw new InvalidOperationException("The companion is stopped; no folder was opened.");
        NativeFolder.Open(science.ScientificFolder(libraryId,sourceId));
    });
    void IDesktopActions.OpenToolbar() => Dispatch(() => { using var form = new SettingsForm(this, false, toolbar: true); ShowAuxiliary(form); });
    void IDesktopActions.OpenSettings() => Dispatch(() => ShowSettings(false));
    void IDesktopActions.OpenStorage() => Dispatch(() => ShowSettings(true));
    void IDesktopActions.OpenDataManagement() => Dispatch(() => ShowDataManagement());
    public void ShowDataManagement(IWin32Window? owner = null) { if (!Busy) { using var form = new DataManagementForm(this); ShowAuxiliary(form, owner); } }
    void IDesktopActions.CollectFeedback() => Dispatch(ShowFeedback);
    public ScientificAccessConfiguration ScientificAccess => engine?.Services.GetRequiredService<ScientificAccessSettings>().Get()??new(new(),ScientificAccessSettings.Privacy);
    public SourceAvailability[] ScientificSources() => (engine??throw new InvalidOperationException("The local engine is stopped; source storage has not been read. Start it or review Storage Settings. Existing data was not replaced.")).Services.GetRequiredService<ScienceStore>().Availability().ToArray();
    public void SetScientificSourceMode(RemoteKind kind,string mode)
    {
        var automatic=engine?.Services.GetRequiredService<AutomaticScientificData>()??throw new InvalidOperationException("Start the companion first.");
        var p=ScientificAccess.Preferences;var modes=new Dictionary<RemoteKind,string>(p.SourceModes??[]){[kind]=mode};automatic.Configure(p with{SourceModes=modes});
    }
    public void OpenScientificSources(string kind,bool import=false,Guid? dataset=null)
    {if(engine is not null)Process.Start(new ProcessStartInfo("http://127.0.0.1:"+ApplicationProfile.Current.Port+"/#settings/science?"+(kind=="access"?"section=access":"source="+kind)+(import?"&import=1":"")+(dataset is null?"":"&dataset="+dataset)){UseShellExecute=true});}
    private void DispatchSourcePicker(Func<IWin32Window,Task> action)
    {
        var lease=desktopActions.TryEnter();if(lease is null){Dispatch(()=>{});return;}
        try
        {
            if(IsDisposed||!IsHandleCreated){lease.Dispose();return;}
            BeginInvoke(async()=>
            {
                using(lease)
                {
                    // A picker may be a legitimate child of Settings, never of migration/deletion dialogs.
                    if(Busy||auxiliary is {IsDisposed:false} and not SettingsForm){FocusAuxiliary();return;}
                    choosingSource=true;
                    try{await action(auxiliary is {IsDisposed:false}?auxiliary:this);}
                    catch(Exception error){ShowError(error);}
                    finally{choosingSource=false;RefreshStatus();}
                }
            });
        }catch(InvalidOperationException){lease.Dispose();}
    }
    void IDesktopActions.ChooseScientificDataset(string kind)=>DispatchSourcePicker(owner=>ChooseScientificDataset(kind,owner));
    private async Task ChooseScientificDataset(string kind,IWin32Window owner)
    {
        if(engine is null)return;string path;var type=Enum.Parse<RemoteKind>(kind,true);
        if(type==RemoteKind.Terrain){using var picker=new OpenFileDialog{Title="Choose a numeric elevation DEM GeoTIFF",Filter="Numeric GeoTIFF (*.tif;*.tiff)|*.tif;*.tiff"};if(picker.ShowDialog(owner)!=DialogResult.OK)return;path=picker.FileName;}
        else{using var picker=new FolderBrowserDialog{Description="Choose the "+kind+" collection folder",UseDescriptionForTitle=true};if(picker.ShowDialog(owner)!=DialogResult.OK)return;path=picker.SelectedPath;}
        await engine.Services.GetRequiredService<ScientificImportSession>().Select(path,type);
    }
    void IDesktopActions.ChooseHyperspectral(bool folder)=>DispatchSourcePicker(async owner=>
    {
        if(engine is null)return;string path;
        if(folder){using var picker=new FolderBrowserDialog{Description="Choose the folder containing an ENVI .hdr and its original hyperspectral binary",UseDescriptionForTitle=true};if(picker.ShowDialog(owner)!=DialogResult.OK)return;path=picker.SelectedPath;}
        else{using var picker=new OpenFileDialog{Title="Choose the original hyperspectral ENVI header",Filter="ENVI header (*.hdr)|*.hdr",CheckFileExists=true};if(picker.ShowDialog(owner)!=DialogResult.OK)return;path=picker.FileName;}
        await engine.Services.GetRequiredService<ScientificImportSession>().Select(path);
    });
    void IDesktopActions.ChooseEmitOriginals()=>DispatchSourcePicker(async owner=>
    {
        if(engine is null)return;
        using var picker=new OpenFileDialog{Title="Select matching EMIT RFL002, RFLUNCERT002 and MASK003 originals",Filter="Original NetCDF (*.nc)|*.nc",Multiselect=true,CheckFileExists=true};
        if(picker.ShowDialog(owner)!=DialogResult.OK)return;
        await engine.Services.GetRequiredService<EmitLibrary>().SelectLocal(picker.FileNames);
    });
    void IDesktopActions.ImportScientificSource() => Dispatch(() => ImportScientificSource());
    void IDesktopActions.ConnectEarthdata()=>DispatchSourcePicker(owner=>
    {
        if(engine is not null){using var dialog=new EarthdataConnectionForm((WindowsEarthdataCredentials)engine.Services.GetRequiredService<IEarthdataCredentials>());dialog.ShowDialog(owner);}
        return Task.CompletedTask;
    });
    void IDesktopActions.CompactStorage(bool science) => Dispatch(async () =>
    {
        if(Busy||Recording){MessageBox.Show(this,"End recording and wait for active setup/storage work before compacting.");return;}
        if(MessageBox.Show(this,"Compact the "+(science?"science":"flight")+" database? Services will stop temporarily. A verified backup and sufficient temporary space are required. Original observations are retained; actual recovered database bytes will be reported.","Confirm database maintenance",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
        try{var result="";await RunStoppedAsync(()=>Task.Run(()=>result=Storage.Compact(science?StorageKind.ScienceLibrary:StorageKind.Missions,true,false)));MessageBox.Show(this,result,"Compaction completed");}catch(Exception error){ShowError(error);}
    });
    public async void ImportScientificSource(IWin32Window? owner = null)
    {
        try { await ImportScientificSourceAsync(owner); }
        catch (Exception error) { ShowError(error); }
    }
    private async Task ImportScientificSourceAsync(IWin32Window? owner)
    {
        if (auxiliary is { IsDisposed: false } && owner != auxiliary) { FocusAuxiliary(); return; }
        if (Busy || engine is null) { MessageBox.Show(owner ?? this, "Wait for current setup/storage work and start the local engine first."); return; }
        using var picker = new OpenFileDialog { Title = "Import a real scientific source collection manifest", Filter = "AeroScience source manifest (*.json)|*.json", CheckFileExists = true };
        if (picker.ShowDialog(owner ?? this) != DialogResult.OK) return;
        try
        {
            var inspection = ScienceStore.InspectImport(picker.FileName);
            var science = engine.Services.GetRequiredService<ScienceStore>();
            if (MessageBox.Show(owner ?? this, $"Import {inspection.Manifest.Name}?\n\nSource: {inspection.Manifest.Provider}\nObservation: {inspection.Manifest.ObservationUtc?.ToString("u") ?? "Unknown (not live)"}\nCopy {inspection.Bytes / 1048576d:0.0} MiB into {science.DirectoryPath}\nAvailable: {new LocalStorageCapacity().FreeBytes(science.DirectoryPath) / 1073741824d:0.0} GiB\n\nOriginal files remain untouched. No files are downloaded and no data are uploaded. Review the source license: {inspection.Manifest.License}", "Confirm scientific source import", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Busy = true;
            var progress = new Progress<string>(value => status.Text = value);
            var imported = await Task.Run(() => science.ImportSourceAsync(picker.FileName, progress: progress));
            MessageBox.Show(owner ?? this, "Imported " + imported.Manifest.Name + ". Open dashboard Settings → Scientific Data Sources to inspect coverage and metadata.");
        }
        catch (Exception error) { ShowError(error); }
        finally { Busy = false; RefreshStatus(); }
    }
    public void ShowError(Exception error) => MessageBox.Show(this, error.Message, "AeroScience", MessageBoxButtons.OK, MessageBoxIcon.Error);
    private void ShowFeedback()
    {
        using var notes = new BrandedForm { Text = "Collect Feedback", Width = 560, Height = 300, StartPosition = FormStartPosition.CenterParent };
        var text = new TextBox { Multiline = true, Dock = DockStyle.Fill, PlaceholderText = "Optional reproduction steps and notes" };
        var save = new Button { Text = "Save feedback ZIP…", Dock = DockStyle.Bottom, Height = 42 };
        notes.Controls.Add(text); notes.Controls.Add(save);
        save.Click += (_, _) =>
        {
            if(MessageBox.Show(notes,"The bundle includes geographic preparation areas, instrument/provider timing, errors and local logs. Review it before sharing. No scientific originals, databases or credentials are included. Continue?","Diagnostic privacy",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
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
                    Add("scientific-diagnostics.json", JsonSerializer.Serialize(ScientificDiagnostics.Snapshot(), WireJson.Options));
                    Add("REPORT.txt", $"AeroScience {Application.ProductVersion}\n{RuntimeInformation.OSDescription}\n{RuntimeInformation.FrameworkDescription}\nUTC {DateTimeOffset.UtcNow:O}\n{status.Text}\n\n{text.Text}\n\nLocal report only. Review before sharing; no mission database or pairing secrets included.");
                    var logDirectory = Path.Combine(Preferences.Load().MissionDirectory, "logs");
                    if (Directory.Exists(logDirectory))
                        foreach (var file in Directory.GetFiles(logDirectory, "engine-*.log").OrderDescending().Take(7))
                            if (new FileInfo(file).Length < 5_000_001) Add("logs/" + Path.GetFileName(file), File.ReadAllText(file));
                    var scientificLog=Path.Combine(Preferences.DirectoryPath,"logs","scientific-diagnostics.json");if(File.Exists(scientificLog)&&new FileInfo(scientificLog).Length<5000001)Add("previous-scientific-diagnostics.json",File.ReadAllText(scientificLog));
                    var toolbarLogs=Path.Combine(Preferences.DirectoryPath,"logs");
                    if(Directory.Exists(toolbarLogs))
                        foreach(var file in Directory.GetFiles(toolbarLogs,"toolbar-*.log").OrderDescending().Take(7))
                            if(new FileInfo(file).Length<5_000_001)Add("toolbar-logs/"+Path.GetFileName(file),File.ReadAllText(file));
                }
                File.Move(dialog.FileName + ".partial", dialog.FileName, overwrite: true);
                MessageBox.Show(notes, "Saved locally. Review the ZIP before sharing it."); notes.Close();
            }
            catch (Exception error) { ShowError(error); }
        };
        ShowAuxiliary(notes);
    }
}
