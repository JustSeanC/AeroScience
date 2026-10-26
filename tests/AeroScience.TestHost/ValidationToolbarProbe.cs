using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Engine;

// Isolated structural fixtures only. Never a replacement for genuine SDK/MSFS acceptance.
internal static class ValidationToolbarProbe
{
    internal static void Run()
    {
        var profile=ApplicationProfile.Current;
        Check(profile.Validation,"Use the separately compiled validation graph.");
        var root=Path.Combine(Path.GetTempPath(),"aeroscience-validation-toolbar-probe-"+Guid.NewGuid().ToString("N"));
        try
        {
            var input=Path.Combine(root,"input");var package=Path.Combine(input,profile.ToolbarPackage);Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package,"manifest.json"),JsonSerializer.Serialize(new{title=profile.ToolbarTitle,package_version=typeof(ToolbarInstaller).Assembly.GetName().Version!.ToString(3)}));
            File.WriteAllText(Path.Combine(package,"layout.json"),"{}");
            var panels=Path.Combine(package,"InGamePanels");Directory.CreateDirectory(panels);
            // Independent literal from SDK staging; do not derive the fixture's
            // filename from the runtime lookup being tested.
            var registration=Path.Combine(panels,"aeroscience_validation_v070_panel.spb");File.WriteAllBytes(registration,new byte[64]);
            var web=Path.Combine(package,"html_ui","InGamePanels",profile.ToolbarFolder);Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web,"toolbar-compatibility.json"),JsonSerializer.Serialize(new{profile="validation",sourceCommit=ApplicationProfile.SourceCommit,protocol=1}));
            Receipt(input,package);
            var community=Path.Combine(root,"Packages","Community");var production=Path.Combine(community,"aeroscience-toolbar");Directory.CreateDirectory(production);
            var marker=Path.Combine(production,"manifest.json");File.WriteAllText(marker,"{\"title\":\"AeroScience\",\"package_version\":\"0.5.0\"}");var before=File.ReadAllBytes(marker);
            void Install()=>ToolbarInstaller.Install(input,community,()=>false,discoverCommunityFolders:()=>[]);
            Install();Install();
            var installed=Path.Combine(community,profile.ToolbarPackage,"InGamePanels","aeroscience_validation_v070_panel.spb");
            Check(File.Exists(installed)&&ToolbarVersions.Inspect(input,community).Status==ToolbarInstallState.Current,"Validation registration was not installed/verified.");
            var original=File.ReadAllBytes(installed);
            File.Move(registration,Path.Combine(panels,"aeroscience_validation_panel.spb"));Receipt(input,package);
            var rejected=false;try{Install();}catch(IOException e){rejected=e.Message.Contains("registration",StringComparison.OrdinalIgnoreCase);}
            Check(rejected,"A legacy registration name was not rejected clearly.");
            Check(original.SequenceEqual(File.ReadAllBytes(installed))&&before.SequenceEqual(File.ReadAllBytes(marker)),"A rejected package changed the installed validation or production toolbar.");
            Console.WriteLine("PASS: compiled 0.6 validation toolbar first/repeat install and legacy registration rejection preserve production. Structural fixtures only; no genuine SDK/MSFS acceptance.");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static void Receipt(string input,string package)
    {
        var hashes=Directory.GetFiles(package,"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(package,p).Replace('\\','/'),p=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
        File.WriteAllText(Path.Combine(input,"sdk-build.json"),JsonSerializer.Serialize(new{variant="toolbar-validation",files=hashes}));
    }
    private static void Check(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
}
