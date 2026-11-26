using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using AeroScience.XPlane;
using Microsoft.Extensions.DependencyInjection;

internal static partial class Candidate081Probe
{
    // Run only from a scratch harness overlaid with the actual published runtime.
    // Disposable profile and local denial server; no installed simulator is used.
    internal static async Task Startup(string receipt)
    {
        var profile=ApplicationProfile.Current;
        if(!OperatingSystem.IsWindows()||!profile.Native081)throw new InvalidOperationException("Actual candidate Windows graph required.");
        var root=profile.ConfigurationDirectory;
        if(Directory.Exists(root)||File.Exists(receipt))throw new IOException("Preserve existing candidate/probe output; choose a fresh compiled instance.");
        if(File.Exists(Path.Combine(AppContext.BaseDirectory,"SimConnect.dll")))throw new IOException("The SimConnect-free launch probe must not have a native SimConnect DLL.");
        Directory.CreateDirectory(root);PreferenceStore.WriteAtomic(Path.Combine(root,"candidate-profile.json"),new{version=1,source_commit=ApplicationProfile.SourceCommit,instance=profile.Instance});
        using var stop=new CancellationTokenSource();var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;var requests=0;
        var server=Task.Run(async()=>{
            try{while(!stop.IsCancellationRequested){using var client=await listener.AcceptTcpClientAsync(stop.Token);await using var stream=client.GetStream();var buffer=new byte[4096];var count=await stream.ReadAsync(buffer,stop.Token);var request=Encoding.ASCII.GetString(buffer,0,count);if(!request.StartsWith("GET /api/capabilities ",StringComparison.Ordinal))throw new IOException("Unexpected simulator operation in fixture.");Interlocked.Increment(ref requests);await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"),stop.Token);}}
            catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        });
        try{
            var preferences=profile.Preferences();var initial=new StorageManager(preferences).Initialize();preferences.Save(initial with{Simulator=new(1,"xplane12",port)});
            var settings=preferences.Load();var args=new[]{"--AeroScience:Telemetry=XPlane","--AeroScience:XPlanePort="+port,"--AeroScience:ConfigurationDirectory="+root,"--AeroScience:DataDirectory="+settings.MissionDirectory,"--AeroScience:ScienceLibraryDirectory="+settings.ScienceLibraryDirectory,"--AeroScience:Port="+profile.Port,"--AeroScience:RequireExistingDatabase=true"};
            await using var app=EngineApplication.Create(args);await app.StartAsync(stop.Token);
            try{
                var providers=app.Services.GetServices<ITelemetryProvider>().ToArray();if(providers.Length!=1||providers[0] is not XPlaneTelemetryProvider)throw new IOException("Exactly the selected X-Plane provider must run.");
                var coordinator=app.Services.GetRequiredService<MissionCoordinator>();var deadline=DateTimeOffset.UtcNow.AddSeconds(10);
                while(coordinator.Snapshot().Connection.Message!="ApiAccessDenied"&&DateTimeOffset.UtcNow<deadline)await Task.Delay(50);
                var snapshot=coordinator.Snapshot();if(requests<1||snapshot.Connection.Message!="ApiAccessDenied"||snapshot.ActiveMission is not null||snapshot.Telemetry is not null)throw new IOException("API denial must remain honest, without recording or fallback.");
                if(app.Services.GetRequiredService<ToolbarBridgeStatus>().Snapshot().State!="Disabled")throw new IOException("MSFS CommBus must remain disabled.");
                using(var process=System.Diagnostics.Process.GetCurrentProcess())if(process.Modules.Cast<System.Diagnostics.ProcessModule>().Any(m=>m.ModuleName.Equals("SimConnect.dll",StringComparison.OrdinalIgnoreCase)))throw new IOException("Native SimConnect was loaded in X-Plane mode.");
                using var http=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+profile.Port)};var access=app.Services.GetRequiredService<AccessControl>();var session=access.Pair(true,null)!;http.DefaultRequestHeaders.Add("Cookie",access.SessionCookieName+"="+session.Token);
                foreach(var route in new[]{"/api/plans","/api/science/datasets","/api/science/sources","/api/settings/simulator"}){using var response=await http.GetAsync(route);if(!response.IsSuccessStatusCode)throw new IOException("Offline route failed: "+route);}
                if(preferences.Simulator().Id!="xplane12"||app.Services.GetRequiredService<MissionStore>().List().Count!=0)throw new IOException("Startup changed source or created flight evidence.");
            }finally{using var shutdown=new CancellationTokenSource(TimeSpan.FromSeconds(10));await app.StopAsync(shutdown.Token);}
            await File.WriteAllTextAsync(receipt,JsonSerializer.Serialize(new{profile="candidate081",source_commit=ApplicationProfile.SourceCommit,instance=profile.Instance,
                engine_sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"AeroScience.Engine.dll")))),
                simconnect_present=false,selected="xplane12",requests,api_denial="passed",offline_routes="passed",one_provider="passed",commbus_disabled="passed",clean_shutdown="passed",installed_acceptance="Not Tested"}));
        }finally{
            stop.Cancel();listener.Stop();await server.WaitAsync(TimeSpan.FromSeconds(5));Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);
        }
    }
}
