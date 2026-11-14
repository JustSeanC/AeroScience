using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Engine;

// Maintainer-only metadata probe. Never shipped as a tester launcher, never opens a profile.
internal static class Native080ProfileProbe
{
    internal static void Run(string path,string commit,string instance)
    {
        var context=new AssemblyLoadContext("private080-metadata",isCollectible:true);
        try
        {
            var assembly=context.LoadFromAssemblyPath(Path.GetFullPath(path));
            var profile=ApplicationProfile.FromAssembly(assembly);
            var source=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').LastOrDefault();
            var missions=context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,"AeroScience.Missions.dll"));
            var lidar=missions.GetType("AeroScience.Missions.LidarRuntimeIdentity",throwOnError:true)!;
            string Constant(string name)=>(string)lidar.GetField(name,BindingFlags.Static|BindingFlags.NonPublic)!.GetRawConstantValue()!;
            var shared=context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,"AeroScience.Shared.dll"));
            var resources=new Dictionary<string,string>();
            foreach(var name in new[]{"manifest","schema","en-US","es-ES","fr-FR","de-DE","pt-BR"})
            {using var stream=shared.GetManifestResourceStream("AeroScience.Presentation."+name+".json")??throw new IOException("Published native presentation resource missing.");resources[name]=Convert.ToHexStringLower(SHA256.HashData(stream));}
            if(!profile.Native080||profile.Instance!=instance||source!=commit||assembly.GetName().Version!=new Version(0,8,0,0)
                ||missions.GetName().Version!=new Version(0,8,0,0)||missions.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').LastOrDefault()!=commit
                ||profile.Port!=5088||profile.ToolbarPackage!="aeroscience-validation-v0-8-0-toolbar"
                ||profile.CommandEvent!="AeroScience.Validation.0_8_0.Command.v1"||profile.ReplyEvent!="AeroScience.Validation.0_8_0.Reply.v1")
                throw new InvalidOperationException("Published Engine.dll does not carry this compiled private 0.8.0 identity.");
            Console.WriteLine(JsonSerializer.Serialize(new{profile="validation",version="0.8.0",source_commit=source,instance,
                configuration_directory=profile.ConfigurationDirectory,registry_key=profile.RegistryKey,port=profile.Port,
                cookie=profile.SessionCookieName,command_event=profile.CommandEvent,reply_event=profile.ReplyEvent,
                mutex=ApplicationProfile.DesktopMutex,engine_sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                lidar_source=Constant("Source"),lidar_sha256=Constant("Sha256"),
                presentation_resources=resources,
                installed_acceptance="Not Tested"}));
        }finally{context.Unload();}
    }
}
