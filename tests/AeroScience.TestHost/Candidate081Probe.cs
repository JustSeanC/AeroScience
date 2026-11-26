using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Engine;

internal static partial class Candidate081Probe
{
    internal static void Inspect(string path,string source,string instance)
    {
        path=Path.GetFullPath(path);var folder=Path.GetDirectoryName(path)!;
        var context=new AssemblyLoadContext("candidate081-published-metadata",isCollectible:true);
        try{
            var engine=context.LoadFromAssemblyPath(path);var profile=ApplicationProfile.FromAssembly(engine);
            string Hash(string name)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder,name))));
            var identities=new Dictionary<string,string>();
            foreach(var name in new[]{"AeroScience.Engine","AeroScience.Missions","AeroScience.Shared","AeroScience.Telemetry","AeroScience.XPlane","AeroScience.SimConnect"})
            {
                var assembly=name=="AeroScience.Engine"?engine:context.LoadFromAssemblyPath(Path.Combine(folder,name+".dll"));
                var info=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if(assembly.GetName().Version!=new Version(0,8,1,0)||info!="0.8.1-candidate+"+source)throw new IOException("Published candidate assembly version/source mismatch: "+name);
                identities[name]=Hash(name+".dll");
            }
            var executable=System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(folder,"AeroScience.exe"));
            if(!profile.Native081||profile.Instance!=instance||profile.Port!=5091||profile.ToolbarPackage!="aeroscience-candidate-v0-8-1-toolbar"
                ||executable.FileVersion!="0.8.1.0"||executable.ProductVersion!="0.8.1-candidate+"+source||executable.ProductName!="AeroScience 0.8.1 Candidate")
                throw new IOException("Actual published Windows executable/profile identity differs.");
            var shared=context.Assemblies.Single(a=>a.GetName().Name=="AeroScience.Shared");var resources=new Dictionary<string,string>();
            foreach(var name in new[]{"manifest","schema","en-US","es-ES","fr-FR","de-DE","pt-BR"})
            {using var stream=shared.GetManifestResourceStream("AeroScience.Presentation."+name+".json")??throw new IOException("Missing candidate locale resource.");resources[name]=Convert.ToHexStringLower(SHA256.HashData(stream));}
            Console.WriteLine(JsonSerializer.Serialize(new{profile="candidate081",version="0.8.1-candidate",source_commit=source,instance,port=5091,
                folder="%LOCALAPPDATA%/"+profile.Folder,registry=profile.RegistryKey,cookie=profile.SessionCookieName,command_event=profile.CommandEvent,
                engine_sha256=Hash("AeroScience.Engine.dll"),xplane_sha256=Hash("AeroScience.XPlane.dll"),assemblies=identities,presentation_resources=resources,installed_acceptance="Not Tested"}));
        }finally{context.Unload();}
    }
}
