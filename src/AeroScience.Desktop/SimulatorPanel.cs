using AeroScience.Engine;
using AeroScience.Missions;

namespace AeroScience.Desktop;

// Uses the same versioned settings service as the authenticated dashboard.
internal sealed class SimulatorPanel : FlowLayoutPanel
{
    private sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
    private SimulatorSettingsView saved;
    private readonly ComboBox source = new() { DropDownStyle=ComboBoxStyle.DropDownList, Width=380 };
    private readonly NumericUpDown port = new() { Minimum=1, Maximum=65535, Width=130 };
    private readonly Label feedback = new() { AutoSize=true, MaximumSize=new(660,0), AccessibleRole=AccessibleRole.StaticText };
    private readonly Label active = new() { AutoSize=true, MaximumSize=new(660,0) };
    private readonly Button save = new() { AutoSize=true };
    public bool HasChanges => saved.Preferences is { } p && (source.SelectedItem is Choice choice && choice.Id!=p.Id || (int)port.Value!=p.XPlanePort);
    public SimulatorPanel(MainForm app)
    {
        AutoSize=true; AutoSizeMode=AutoSizeMode.GrowAndShrink; FlowDirection=FlowDirection.TopDown; WrapContents=false;
        saved=app.SimulatorSettings();
        void TextLine(string key) => Controls.Add(new Label { Text=WindowsPresentation.Text(key), AutoSize=true, MaximumSize=new(660,0) });
        TextLine("simulator.title"); source.AccessibleName=WindowsPresentation.Text("simulator.title");
        source.Items.AddRange(new object[]{new Choice(SimulatorPreferences.Msfs2024,SimulatorPreferences.Name(SimulatorPreferences.Msfs2024)),new Choice(SimulatorPreferences.XPlane12,SimulatorPreferences.Name(SimulatorPreferences.XPlane12))});
        Controls.Add(source); Controls.Add(active);
        var advanced=new CheckBox { Text=WindowsPresentation.Text("simulator.advanced"),AutoSize=true };
        var advancedPanel=new FlowLayoutPanel { AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Visible=false };
        advancedPanel.Controls.Add(new Label{Text=WindowsPresentation.Text("simulator.port"),AutoSize=true});
        port.AccessibleName=WindowsPresentation.Text("simulator.port");advancedPanel.Controls.Add(port);
        advancedPanel.Controls.Add(new Label{Text=WindowsPresentation.Text("simulator.localOnly"),AutoSize=true,MaximumSize=new(660,0)});
        advanced.CheckedChanged+=(_,_)=>advancedPanel.Visible=advanced.Checked;Controls.Add(advanced);Controls.Add(advancedPanel);
        TextLine("simulator.restart"); save.Text=WindowsPresentation.Text("simulator.save");Controls.Add(save);
        var reload=new Button{Text=WindowsPresentation.Text("simulator.reload"),AutoSize=true};Controls.Add(reload);Controls.Add(feedback);
        void Refresh()
        {
            if(saved.Preferences is { } p){source.SelectedItem=source.Items.Cast<Choice>().Single(c=>c.Id==p.Id);port.Value=p.XPlanePort;}
            active.Text=WindowsPresentation.Text("simulator.active",new Dictionary<string,object>{["name"]=SimulatorPreferences.Name(saved.Effective.Id)});
            source.Enabled=port.Enabled=save.Enabled=saved.CanWrite;
            feedback.Text=saved.Warning??(saved.RestartRequired?WindowsPresentation.Text("simulator.restart"):WindowsPresentation.Text(saved.StatusKey,new Dictionary<string,object>{["name"]=SimulatorPreferences.Name(saved.Effective.Id)}));
        }
        save.Click+=(_,_)=>{try{saved=app.SaveSimulator(new(saved.Preferences?.Revision,(source.SelectedItem as Choice)?.Id,(int)port.Value));Refresh();feedback.Text=WindowsPresentation.Text(saved.RestartRequired?"simulator.restart":"simulator.savedNotice");}catch(Exception error){feedback.Text=error.Message;}};
        reload.Click+=(_,_)=>{try{saved=app.SimulatorSettings();Refresh();}catch(Exception error){feedback.Text=error.Message;}};
        Refresh();
    }
}
