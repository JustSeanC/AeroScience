using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Desktop;

// Common tasks remain native; shared browser maps/wizard open at their exact destination.
public sealed class ScientificSourcesPanel:FlowLayoutPanel
{
    private readonly MainForm app;private readonly IWin32Window owner;
    private static readonly (RemoteKind Kind,string Title,string Provider,string Description)[] categories=
    [
        (RemoteKind.Terrain,"Terrain & Elevation","USGS 3DEP / Copernicus GLO-30","Numeric DEM/DSM elevation, not aircraft laser returns. Local: single-band elevation GeoTIFF."),
        (RemoteKind.Thermal,"Thermal Imaging","Landsat 8/9 Collection 2 Level-2","Historical satellite surface temperature and original QA masks. Local: ST_B10 + QA_PIXEL (+ QA_RADSAT), with original metadata."),
        (RemoteKind.Multispectral,"Multispectral Imaging","Copernicus Sentinel-2 Level-2A","Historical reflectance, RGB and NDVI. Local: B02/B03/B04/B08 and SCL GeoTIFFs with original scaling metadata."),
        (RemoteKind.Hyperspectral,"Hyperspectral Imaging","NASA AVIRIS / original ENVI datasets","Use Library Browse & Download → Hyperspectral for approved Earthdata Download & Import. Original ENVI manual/browser import remains available. EMIT is discovery only.")
    ];
    public ScientificSourcesPanel(MainForm app,IWin32Window owner)
    {
        this.app=app;this.owner=owner;Dock=DockStyle.Fill;AutoScroll=true;FlowDirection=FlowDirection.TopDown;WrapContents=false;Padding=new(16);
        RefreshSources();
    }
    private void RefreshSources()
    {
        Controls.Clear();AddLabel(this,"Scientific Data Sources",true);
        AddLabel(this,"Historical external data retain their original observation dates and provenance. Local datasets work offline within their real coverage. Automatic retrieval uses separate scientific-data consent.");
        SourceAvailability[] sources=[];string? error=null;try{sources=app.ScientificSources();}catch(Exception e)when(e is IOException or InvalidOperationException or UnauthorizedAccessException){error=e.Message;}
        if(error is not null)AddLabel(this,error);
        var preferences=app.ScientificAccess.Preferences;
        AddLabel(this,"Online Scientific Data Access: "+(preferences.OnlineEnabled?"Enabled":"Disabled — local datasets remain available"),true);AddButton(this,"Enable / Configure Online Scientific Data Access",()=>app.OpenScientificSources("access"));
        foreach(var (kind,title,provider,description) in categories)
        {
            var group=new GroupBox{Text=title,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Width=710,Padding=new(14),Margin=new(0,10,0,14)};
            var content=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.TopDown,WrapContents=false,Dock=DockStyle.Fill,Padding=new(4)};group.Controls.Add(content);Controls.Add(group);
            AddLabel(content,provider,true);AddLabel(content,description);
            var modes=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=430,AccessibleName=title+" source mode"};
            var values=kind==RemoteKind.Hyperspectral?new[]{"local"}:new[]{"automatic","local","prefer-local"};
            modes.Items.AddRange(values.Select(v=>v switch{"automatic"=>"Automatic","local"=>"My Datasets Only",_=>"Prefer My Datasets, Allow Automatic Fallback"}).Cast<object>().ToArray());
            modes.SelectedIndex=Math.Max(0,Array.IndexOf(values,preferences.SourceModes?.GetValueOrDefault(kind)??(kind==RemoteKind.Hyperspectral?"local":"automatic")));
            var modeStatus=AddLabel(content,"Source mode applies to selection, not the recording switch.");content.Controls.Add(modes);modes.SelectedIndexChanged+=(_,_)=>{try{app.SetScientificSourceMode(kind,values[modes.SelectedIndex]);modeStatus.Text="Saved. Existing data and products are retained.";}catch(Exception e){app.ShowError(e);}};
            var entries=sources.Where(s=>s.Source.Manifest.Kind==kind).ToArray();var bytes=entries.Sum(e=>e.Source.Files.Sum(f=>f.Bytes));
            AddLabel(content,$"{entries.Length} imported/prepared datasets · {bytes/1048576d:0.0} MiB retained"+(entries.Length==0&&kind==RemoteKind.Hyperspectral?" · Dataset Required":""),true);
            foreach(var collection in entries.GroupBy(e=>new{e.Source.Manifest.Provider,Identity=e.Source.Manifest.Acquisition?.Workflow=="usgs-3dep-numeric"?"USGS composite regions":e.Source.Manifest.SceneId,e.Source.Manifest.ObservationUtc,e.Source.Manifest.VerticalDatum,e.Source.Manifest.License,Resolution=e.Source.Manifest.Acquisition?.NativeResolution,Name=e.Source.Manifest.Acquisition is null?e.Source.Manifest.Name:null,Calibration=System.Text.Json.JsonSerializer.Serialize(e.Source.Manifest.Bands.Select(b=>new{b.Id,b.Unit,b.Scale,b.Offset,b.WavelengthNm,b.BadBand}),WireJson.Options)}))
            {
            var regional=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Visible=false};content.Controls.Add(regional);
            AddButton(content,$"{collection.Key.Provider} — {collection.Count()} prepared regions — {collection.Sum(e=>e.Source.Files.Sum(f=>f.Bytes))/1048576d:0.0} MiB (expand)",()=>regional.Visible=!regional.Visible);
            foreach(var entry in collection.Take(20))
            {
                var source=entry.Source;AddLabel(regional,$"{source.Manifest.Name}\n{source.Manifest.Provider} · {source.Manifest.ObservationUtc?.ToString("u")??"Observation date unknown / composite"}\n{(entry.FilesAvailable?"Available; use View Coverage for mission-area suitability":"Dataset Invalid: "+entry.Error)}");
                var actions=new FlowLayoutPanel{AutoSize=true,WrapContents=true,MaximumSize=new(660,0)};regional.Controls.Add(actions);
                AddButton(actions,"View Coverage",()=>app.OpenScientificSources(kind.ToString().ToLowerInvariant(),dataset:source.Id));
                AddButton(actions,"View Dataset Details",()=>
                {
                    using var detail=new BrandedForm{Text=source.Manifest.Name,Width=680,Height=520,AutoScaleMode=AutoScaleMode.Dpi,StartPosition=FormStartPosition.CenterParent};
                    detail.Controls.Add(new TextBox{ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Text=System.Text.Json.JsonSerializer.Serialize(source,WireJson.Options),WordWrap=true});detail.ShowDialog(owner);
                });
            }
            if(collection.Count()>20)AddButton(regional,"View All Regions and Collection Coverage",()=>app.OpenScientificSources(kind.ToString().ToLowerInvariant()));
            }
            var buttons=new FlowLayoutPanel{AutoSize=true,MaximumSize=new(660,0),WrapContents=true};content.Controls.Add(buttons);
            AddButton(buttons,kind==RemoteKind.Hyperspectral?"Import Hyperspectral Dataset…":"Import Dataset…",()=>app.OpenScientificSources(kind.ToString().ToLowerInvariant(),import:true));
            AddButton(buttons,"Manage Datasets",()=>app.OpenScientificSources(kind.ToString().ToLowerInvariant()));
            AddButton(buttons,"Open Beginner Guide",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://127.0.0.1:"+AeroScience.Engine.ApplicationProfile.Current.Port+"/help/remote-sensing/"+(kind==RemoteKind.Hyperspectral?"using-your-own-hyperspectral-dataset.md":"scientific-data-sources.md#"+kind.ToString().ToLowerInvariant())){UseShellExecute=true}));
        }
        AddButton(this,"Refresh dataset status",RefreshSources);
        AddButton(this,"Online scientific-data privacy and predictive controls",()=>app.OpenScientificSources("access"));
        AddButton(this,"Advanced JSON source collection import…",()=>app.ImportScientificSource(owner));
    }
    private static Label AddLabel(Control parent,string text,bool bold=false){var label=new Label{Text=text,AutoSize=true,MaximumSize=new(650,0),Margin=new(4,6,4,7)};if(bold)label.Font=new(label.Font,FontStyle.Bold);parent.Controls.Add(label);return label;}
    private static void AddButton(Control parent,string text,Action run){var button=new Button{Text=text,AutoSize=true,Margin=new(4,6,4,6)};button.Click+=(_,_)=>{try{run();}catch(Exception e){MessageBox.Show(parent,e.Message,"Scientific Data Sources");}};parent.Controls.Add(button);}
}
