using System.Diagnostics;
using AeroScience.Missions;
namespace AeroScience.Desktop;

public sealed class EarthdataConnectionForm:BrandedForm
{
    public EarthdataConnectionForm(WindowsEarthdataCredentials credentials)
    {
        Text=WindowsPresentation.Text("windows.earthdataConnectionForm.connectNasaEarthdata");ClientSize=new(640,330);StartPosition=FormStartPosition.CenterParent;
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new(20),AutoScroll=true};
        panel.Controls.Add(new Label{Text=WindowsPresentation.Text("windows.earthdataConnectionForm.signInOnlyOnNasaSOfficialWebsiteGenerate"),AutoSize=true,MaximumSize=new(590,0)});
        var open=new Button{Text=WindowsPresentation.Text("windows.earthdataConnectionForm.openNasaSignInTokenPage"),AutoSize=true};open.Click+=(_,_)=>Process.Start(new ProcessStartInfo(EarthdataConnection.ProfileUrl){UseShellExecute=true});panel.Controls.Add(open);
        panel.Controls.Add(new Label{Text=WindowsPresentation.Text("windows.earthdataConnectionForm.pasteNasaUserTokenStoredInWindowsCredentialManager"),AutoSize=true});
        var token=new TextBox{UseSystemPasswordChar=true,Width=580,MaxLength=2560,AccessibleName=WindowsPresentation.Text("windows.earthdataConnectionForm.nasaEarthdataUserToken")};panel.Controls.Add(token);
        panel.Controls.Add(new Label{Text=WindowsPresentation.Text("windows.earthdataConnectionForm.theBrowserDashboardAndPairedDevicesNeverReceiveThis"),AutoSize=true,MaximumSize=new(590,0)});
        var save=new Button{Text=WindowsPresentation.Text("windows.earthdataConnectionForm.saveEarthdataConnection"),AutoSize=true};save.Click+=(_,_)=>{try{credentials.Save(token.Text.Trim());token.Clear();DialogResult=DialogResult.OK;}catch(Exception error)when(error is ArgumentException or IOException or System.ComponentModel.Win32Exception){MessageBox.Show(this,error.Message,WindowsPresentation.Text("windows.earthdataConnectionForm.earthdataConnection"));}};panel.Controls.Add(save);
        var cancel=new Button{Text=WindowsPresentation.Text("lidar.cancel"),DialogResult=DialogResult.Cancel,AutoSize=true};panel.Controls.Add(cancel);CancelButton=cancel;AcceptButton=save;Controls.Add(panel);
        FormClosing+=(_,e)=>{if(DialogResult!=DialogResult.OK&&token.TextLength>0&&MessageBox.Show(this,WindowsPresentation.Text("windows.earthdataConnectionForm.discardTheEnteredTokenAndCloseChooseNoTo"),WindowsPresentation.Text("windows.earthdataConnectionForm.unsavedEarthdataConnection"),MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)e.Cancel=true;};
        FormClosed+=(_,_)=>token.Clear();
    }
}
