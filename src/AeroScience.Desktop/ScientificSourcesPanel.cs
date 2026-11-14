using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Desktop;

// Common tasks remain native; shared browser maps/wizard open at their exact destination.
public sealed class ScientificSourcesPanel:FlowLayoutPanel
{
    private readonly MainForm app;private readonly IWin32Window owner;
    private readonly (RemoteKind Kind,string Title,string Provider,string Description)[] categories=
    [
        (RemoteKind.Terrain,WindowsPresentation.Text("windows.science.category11Title"),"USGS 3DEP / Copernicus GLO-30",WindowsPresentation.Text("windows.science.category11Description")),
        (RemoteKind.Thermal,WindowsPresentation.Text("windows.science.category12Title"),"Landsat 8/9 Collection 2 Level-2",WindowsPresentation.Text("windows.science.category12Description")),
        (RemoteKind.Multispectral,WindowsPresentation.Text("windows.science.category13Title"),"Copernicus Sentinel-2 Level-2A",WindowsPresentation.Text("windows.science.category13Description")),
        (RemoteKind.Hyperspectral,WindowsPresentation.Text("windows.science.category14Title"),"NASA AVIRIS / original ENVI datasets",WindowsPresentation.Text("windows.science.category14Description"))
    ];
    public ScientificSourcesPanel(MainForm app,IWin32Window owner)
    {
        this.app=app;this.owner=owner;Dock=DockStyle.Fill;AutoScroll=true;FlowDirection=FlowDirection.TopDown;WrapContents=false;Padding=new(16);
        RefreshSources();
    }
    private void RefreshSources()
    {
        Controls.Clear();AddLabel(this,WindowsPresentation.Text("ui.scientificSources.messageScientificDataSources"),true);
        AddLabel(this,WindowsPresentation.Text("windows.scientificSourcesPanel.historicalExternalDataRetainTheirOriginalObservationDatesAnd"));
        SourceAvailability[] sources=[];string? error=null;try{sources=app.ScientificSources();}catch(Exception e)when(e is IOException or InvalidOperationException or UnauthorizedAccessException){error=e.Message;}
        if(error is not null)AddLabel(this,error);
        var preferences=app.ScientificAccess.Preferences;
        AddLabel(this,WindowsPresentation.Text("windows.scientificSourcesPanel.onlineScientificDataAccess", new Dictionary<string, object> { ["value1"] = Convert.ToString(((preferences.OnlineEnabled?"Enabled":WindowsPresentation.Text("windows.science.localAvailable"))), System.Globalization.CultureInfo.InvariantCulture) ?? "" }),true);AddButton(this,WindowsPresentation.Text("windows.scientificSourcesPanel.enableConfigureOnlineScientificDataAccess"),()=>app.OpenScientificSources("access"));
        foreach(var (kind,title,provider,description) in categories)
        {
            var group=new GroupBox{Text=title,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Width=710,Padding=new(14),Margin=new(0,10,0,14)};
            var content=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.TopDown,WrapContents=false,Dock=DockStyle.Fill,Padding=new(4)};group.Controls.Add(content);Controls.Add(group);
            AddLabel(content,provider,true);AddLabel(content,description);
            var modes=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=430,AccessibleName=WindowsPresentation.Text("ui.scientificSources.messageSourceMode1260bf90", new Dictionary<string, object> { ["value1"] = Convert.ToString((title), System.Globalization.CultureInfo.InvariantCulture) ?? "" })};
            var values=kind==RemoteKind.Hyperspectral?new[]{"local"}:new[]{"automatic","local","prefer-local"};
            modes.Items.AddRange(values.Select(v=>v switch{"automatic"=>WindowsPresentation.Text("windows.science.modeAutomatic"),"local"=>WindowsPresentation.Text("windows.science.modeLocal"),_=>WindowsPresentation.Text("windows.science.modePreferLocal")}).Cast<object>().ToArray());
            modes.SelectedIndex=Math.Max(0,Array.IndexOf(values,preferences.SourceModes?.GetValueOrDefault(kind)??(kind==RemoteKind.Hyperspectral?"local":"automatic")));
            var modeStatus=AddLabel(content,WindowsPresentation.Text("windows.scientificSourcesPanel.sourceModeAppliesToSelectionNotTheRecordingSwitch"));content.Controls.Add(modes);modes.SelectedIndexChanged+=(_,_)=>{try{app.SetScientificSourceMode(kind,values[modes.SelectedIndex]);modeStatus.Text=WindowsPresentation.Text("windows.scientificSourcesPanel.savedExistingDataAndProductsAreRetained");}catch(Exception e){app.ShowError(e);}};
            var entries=sources.Where(s=>s.Source.Manifest.Kind==kind).ToArray();var bytes=entries.Sum(e=>e.Source.Files.Sum(f=>f.Bytes));
            AddLabel(content,WindowsPresentation.Text("windows.scientificSourcesPanel.importedPreparedDatasetsMibRetained", new Dictionary<string, object> { ["value1"] = WindowsPresentation.Format($"{entries.Length}"), ["value2"] = WindowsPresentation.Format($"{bytes/1048576d:0.0}"), ["value3"] = Convert.ToString(((entries.Length==0&&kind==RemoteKind.Hyperspectral?WindowsPresentation.Text("windows.science.datasetRequired"):"")), System.Globalization.CultureInfo.InvariantCulture) ?? "" }),true);
            foreach(var collection in entries.GroupBy(e=>new{e.Source.Manifest.Provider,Identity=e.Source.Manifest.Acquisition?.Workflow=="usgs-3dep-numeric"?"USGS composite regions":e.Source.Manifest.SceneId,e.Source.Manifest.ObservationUtc,e.Source.Manifest.VerticalDatum,e.Source.Manifest.License,Resolution=e.Source.Manifest.Acquisition?.NativeResolution,Name=e.Source.Manifest.Acquisition is null?e.Source.Manifest.Name:null,Calibration=System.Text.Json.JsonSerializer.Serialize(e.Source.Manifest.Bands.Select(b=>new{b.Id,b.Unit,b.Scale,b.Offset,b.WavelengthNm,b.BadBand}),WireJson.Options)}))
            {
            var regional=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Visible=false};content.Controls.Add(regional);
            AddButton(content,WindowsPresentation.Text("windows.scientificSourcesPanel.preparedRegionsMibExpand", new Dictionary<string, object> { ["value1"] = WindowsPresentation.Format($"{collection.Key.Provider}"), ["value2"] = WindowsPresentation.Format($"{collection.Count()}"), ["value3"] = WindowsPresentation.Format($"{collection.Sum(e=>e.Source.Files.Sum(f=>f.Bytes))/1048576d:0.0}") }),()=>regional.Visible=!regional.Visible);
            foreach(var entry in collection.Take(20))
            {
                var source=entry.Source;AddLabel(regional,$"{source.Manifest.Name}\n{source.Manifest.Provider} · {source.Manifest.ObservationUtc?.ToString("u")??"Observation date unknown / composite"}\n{(entry.FilesAvailable?"Available; use View Coverage for mission-area suitability":"Dataset Invalid: "+entry.Error)}");
                var actions=new FlowLayoutPanel{AutoSize=true,WrapContents=true,MaximumSize=new(660,0)};regional.Controls.Add(actions);
                AddButton(actions,WindowsPresentation.Text("ui.scientificLibrary.messageViewCoverage"),()=>app.OpenScientificSources(kind.ToString().ToLowerInvariant(),dataset:source.Id));
                AddButton(actions,WindowsPresentation.Text("ui.scientificSources.messageViewDatasetDetails"),()=>
                {
                    using var detail=new BrandedForm{Text=source.Manifest.Name,Width=680,Height=520,AutoScaleMode=AutoScaleMode.Dpi,StartPosition=FormStartPosition.CenterParent};
                    var detailContent=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new(16)};detail.Controls.Add(detailContent);
                    AddLabel(detailContent,WindowsPresentation.Text("windows.scientificSourcesPanel.providerSceneObservedKindBandsVerticalReferenceTermsSource", new Dictionary<string, object> { ["value1"] = WindowsPresentation.Format($"{source.Manifest.Name}"), ["value2"] = WindowsPresentation.Format($"{source.Manifest.Provider}"), ["value3"] = WindowsPresentation.Format($"{source.Manifest.SceneId}"), ["value4"] = WindowsPresentation.Format($"{source.Manifest.ObservationUtc?.ToString("u")??"Unknown"}"), ["value5"] = WindowsPresentation.Format($"{source.Manifest.Kind}"), ["value6"] = WindowsPresentation.Format($"{source.Manifest.Bands.Length}"), ["value7"] = WindowsPresentation.Format($"{source.Manifest.VerticalDatum}"), ["value8"] = WindowsPresentation.Format($"{source.Manifest.License}"), ["value9"] = WindowsPresentation.Format($"{source.Id}") }));
                    AddButton(detailContent,WindowsPresentation.Text("windows.scientificSourcesPanel.advancedDiagnosticsExactSourceMetadata"),()=>{using var diagnostic=new BrandedForm{Text=WindowsPresentation.Text("windows.scientificSourcesPanel.sourceDiagnosticsReviewBeforeSharing"),Width=680,Height=520,StartPosition=FormStartPosition.CenterParent};diagnostic.Controls.Add(new TextBox{ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Text=System.Text.Json.JsonSerializer.Serialize(source,WireJson.Options),WordWrap=true});diagnostic.ShowDialog(detail);});detail.ShowDialog(owner);
                });
            }
            if(collection.Count()>20)AddButton(regional,WindowsPresentation.Text("windows.scientificSourcesPanel.viewAllRegionsAndCollectionCoverage"),()=>app.OpenScientificSources(kind.ToString().ToLowerInvariant()));
            }
            var buttons=new FlowLayoutPanel{AutoSize=true,MaximumSize=new(660,0),WrapContents=true};content.Controls.Add(buttons);
            AddButton(buttons,kind==RemoteKind.Hyperspectral?WindowsPresentation.Text("windows.scientificSourcesPanel.importHyperspectralDataset"):WindowsPresentation.Text("windows.scientificSourcesPanel.importDataset"),()=>app.OpenScientificSources(kind.ToString().ToLowerInvariant(),import:true));
            AddButton(buttons,WindowsPresentation.Text("ui.scientificSources.messageManageDatasets"),()=>app.OpenScientificSources(kind.ToString().ToLowerInvariant()));
            AddButton(buttons,WindowsPresentation.Text("ui.scientificSources.messageOpenBeginnerGuide"),()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://127.0.0.1:"+AeroScience.Engine.ApplicationProfile.Current.Port+"/help/remote-sensing/"+(kind==RemoteKind.Hyperspectral?"using-your-own-hyperspectral-dataset.md":"scientific-data-sources.md#"+kind.ToString().ToLowerInvariant())){UseShellExecute=true}));
        }
        AddButton(this,WindowsPresentation.Text("ui.scientificSources.messageRefreshDatasetStatus"),RefreshSources);
        AddButton(this,WindowsPresentation.Text("windows.scientificSourcesPanel.onlineScientificDataPrivacyAndPredictiveControls"),()=>app.OpenScientificSources("access"));
        AddButton(this,WindowsPresentation.Text("windows.scientificSourcesPanel.advancedJsonSourceCollectionImport"),()=>app.ImportScientificSource(owner));
    }
    private static Label AddLabel(Control parent,string text,bool bold=false){var label=new Label{Text=text,AutoSize=true,MaximumSize=new(650,0),Margin=new(4,6,4,7)};if(bold)label.Font=new(label.Font,FontStyle.Bold);parent.Controls.Add(label);return label;}
    private static void AddButton(Control parent,string text,Action run){var button=new Button{Text=text,AutoSize=true,Margin=new(4,6,4,6)};button.Click+=(_,_)=>{try{run();}catch(Exception e){MessageBox.Show(parent,e.Message,"Scientific Data Sources");}};parent.Controls.Add(button);}
}
