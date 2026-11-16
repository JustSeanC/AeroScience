using AeroScience.Engine;
namespace AeroScience.Desktop;

internal sealed class BetaFeedbackForm : Form
{
    public BetaFeedbackForm()
    {
        Text="AeroScience Beta 1 — Report a problem";MinimumSize=new(600,600);Size=new(760,800);AutoScaleMode=AutoScaleMode.Font;Font=SystemFonts.MessageBoxFont;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,AutoScroll=true,Padding=new(16)};Controls.Add(layout);
        layout.Controls.Add(new Label{AutoSize=true,MaximumSize=new(650,0),Text="This saves a local report for you to review and share manually. It includes build information and only the text/image you choose. No logs, scientific files, location history or organization records are collected automatically."});
        var fields=new List<TextBox>();
        foreach(var label in new[]{"Test area or optional card","What did you expect?","What happened? Exact error if available","Steps to reproduce","Optional: aircraft, MSFS version, hardware or accessibility setup"})
        {
            layout.Controls.Add(new Label{Text=label,AutoSize=true});var input=new TextBox{Multiline=true,Dock=DockStyle.Top,Height=70,MaxLength=4000,ScrollBars=ScrollBars.Vertical,AccessibleName=label};fields.Add(input);layout.Controls.Add(input);
        }
        string? screenshot=null;var attach=new Button{Text="Choose a reviewed screenshot (optional)",AutoSize=true};layout.Controls.Add(attach);
        attach.Click+=(_,_)=>{using var dialog=new OpenFileDialog{Filter="Screenshot (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",CheckFileExists=true};if(dialog.ShowDialog(this)==DialogResult.OK){screenshot=dialog.FileName;attach.Text="Screenshot selected — review it before sharing";}};
        var consent=new CheckBox{Text="I reviewed these fields and any screenshot for credentials, location and private details.",AutoSize=true,MaximumSize=new(650,0)};layout.Controls.Add(consent);
        var save=new Button{Text="Save local feedback ZIP",AutoSize=true};layout.Controls.Add(save);
        save.Click+=(_,_)=>
        {
            if(!consent.Checked){MessageBox.Show(this,"Review the report and tick the checkbox before exporting.");return;}
            using var dialog=new SaveFileDialog{Filter="Feedback ZIP (*.zip)|*.zip",FileName="AeroScience-0.8.0-beta.1-feedback.zip",OverwritePrompt=true};if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            var temporary=dialog.FileName+"."+Guid.NewGuid().ToString("N")+".partial";
            try{using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write))BetaFeedback.Write(output,new(fields[0].Text,fields[1].Text,fields[2].Text,fields[3].Text,fields[4].Text),screenshot);File.Move(temporary,dialog.FileName,true);MessageBox.Show(this,"Saved locally. Open and review the ZIP before sharing it manually. Nothing was uploaded.");Close();}
            catch(Exception error){MessageBox.Show(this,error.Message,"Feedback was not exported",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        };
    }
}
