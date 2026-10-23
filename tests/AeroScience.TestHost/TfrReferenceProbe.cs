using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Tests;

public static class TfrReferenceProbe
{
    private sealed class Observed(string root):DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false})
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var name=request.RequestUri!.AbsoluteUri switch{AviationTfr.NoticesUrl=>"notices",AviationTfr.NoShapeUrl=>"no-shape",AviationTfr.GeometryUrl=>"geometry",_=>throw new InvalidOperationException("Unexpected provider endpoint.")};
            var response=await base.SendAsync(request,token);response.EnsureSuccessStatusCode();
            using var output=new MemoryStream();await using var input=await response.Content.ReadAsStreamAsync(token);var buffer=new byte[32768];
            while(true){var count=await input.ReadAsync(buffer,token);if(count==0)break;if(output.Length+count>AviationCatalog.MaximumMetadataBytes)throw new IOException("Bounded metadata exceeded.");output.Write(buffer,0,count);}
            var bytes=output.ToArray();File.WriteAllBytes(Path.Combine(root,name+".json"),bytes);response.Content.Dispose();response.Content=new ByteArrayContent(bytes);return response;
        }
    }
    private sealed class Offline:HttpMessageHandler
    {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){Calls++;throw new IOException("Offline probe must not access FAA.");}}
    public static async Task Run(string[] args)
    {
        if(args.Length!=2||args[0] is not("network" or "offline"))throw new ArgumentException("Use network/offline and isolated evidence directory.");
        var network=args[0]=="network";var root=Path.GetFullPath(args[1]);if(network){if(Directory.Exists(root))throw new IOException("Use a new evidence directory.");Directory.CreateDirectory(root);}
        var offline=new Offline();using var http=network?new HttpClient(new Observed(root)){Timeout=TimeSpan.FromSeconds(30)}:new HttpClient(offline);
        using var service=new AviationReference(root,root,http,unmetered:()=>true);if(network){service.Configure(true);await service.RefreshTfr();}else service.Configure(false);
        var summary=service.TfrSummary();using(var json=JsonDocument.Parse(JsonSerializer.Serialize(summary,WireJson.Options)))if(json.RootElement.GetProperty("cache").GetProperty("error").ValueKind!=JsonValueKind.Null)throw new IOException("Actual TFR refresh failed; inspect retained raw metadata.");
        var east=service.TfrBoundaries(-85,25,-65,45);var west=service.TfrBoundaries(-125,30,-105,49);
        if(east.Boundaries.Length==0||west.Boundaries.Length==0)throw new IOException("No joined original TFR geometry in one of two test regions; no substitution.");
        var b=east.Boundaries[0];var p=b.Polygons[0][0][0];var screen=service.ScreenTfr(new(1,[p]));if(screen.Outcome!="Potential intersection"||screen.SnapshotComplete)throw new IOException("Unknown time/altitude/coverage improperly became clearance.");
        if(offline.Calls!=0||File.Exists(Path.Combine(root,"missions.db")))throw new IOException("Unexpected offline network or mission creation.");
        File.WriteAllText(Path.Combine(root,"tfr-"+args[0]+"-receipt.json"),JsonSerializer.Serialize(new{evidenceClass="Real application refresh/persistence and genuine FAA graphical geometry joined to official notice metadata; not NOTAM text/segment/altitude/time/complete-feed or installed acceptance",summary,eastCount=east.Boundaries.Length,westCount=west.Boundaries.Length,screen,offlineNetworkCalls=offline.Calls},WireJson.Options));
        Console.WriteLine("PASS: genuine joined TFR geometry, bounded two-region queries, cautious screening and offline-compatible persistence.");
    }
}
