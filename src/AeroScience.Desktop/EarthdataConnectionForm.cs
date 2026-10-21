using System.Diagnostics;
using AeroScience.Missions;
namespace AeroScience.Desktop;

public sealed class EarthdataConnectionForm:BrandedForm
{
    public EarthdataConnectionForm(WindowsEarthdataCredentials credentials)
    {
        Text="Connect NASA Earthdata";ClientSize=new(640,330);StartPosition=FormStartPosition.CenterParent;
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new(20),AutoScroll=true};
        panel.Controls.Add(new Label{Text="Sign in only on NASA's official website. Generate a User Token from your NASA profile (NASA documents a 60-day lifetime). Approve ORNL DAAC access there if requested. AeroScience never needs your NASA password.",AutoSize=true,MaximumSize=new(590,0)});
        var open=new Button{Text="Open NASA sign-in / token page",AutoSize=true};open.Click+=(_,_)=>Process.Start(new ProcessStartInfo(EarthdataConnection.ProfileUrl){UseShellExecute=true});panel.Controls.Add(open);
        panel.Controls.Add(new Label{Text="Paste NASA User Token (stored in Windows Credential Manager for this profile):",AutoSize=true});
        var token=new TextBox{UseSystemPasswordChar=true,Width=580,MaxLength=2560,AccessibleName="NASA Earthdata User Token"};panel.Controls.Add(token);
        panel.Controls.Add(new Label{Text="The browser dashboard and paired devices never receive this credential. Access is checked by NASA during preview and download. Disconnect removes the saved token; NASA's profile page can revoke it.",AutoSize=true,MaximumSize=new(590,0)});
        var save=new Button{Text="Save Earthdata connection",AutoSize=true};save.Click+=(_,_)=>{try{credentials.Save(token.Text.Trim());token.Clear();DialogResult=DialogResult.OK;}catch(Exception error)when(error is ArgumentException or IOException or System.ComponentModel.Win32Exception){MessageBox.Show(this,error.Message,"Earthdata connection");}};panel.Controls.Add(save);
        var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true};panel.Controls.Add(cancel);CancelButton=cancel;AcceptButton=save;Controls.Add(panel);
        FormClosing+=(_,e)=>{if(DialogResult!=DialogResult.OK&&token.TextLength>0&&MessageBox.Show(this,"Discard the entered token and close? Choose No to keep editing. Saved connections and downloads will not change.","Unsaved Earthdata connection",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)e.Cancel=true;};
        FormClosed+=(_,_)=>token.Clear();
    }
}
