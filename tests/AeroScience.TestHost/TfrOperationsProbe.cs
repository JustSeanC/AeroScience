using System.Net;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Tests;

// Replays byte-pinned genuine public FAA originals through the real cache. No live-current claim.
public static class TfrOperationsProbe
{
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>new(2026,10,24,19,0,0,TimeSpan.Zero);}
    private sealed class Transport:HttpMessageHandler
    {
        public int Calls;public bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)
        {
            if(Offline)throw new IOException("Offline replay attempted a provider request.");Calls++;var uri=r.RequestUri!.AbsoluteUri;
            byte[] bytes=uri switch{AviationTfr.NoticesUrl=>TfrOriginalFiles.Read("notices"),AviationTfr.NoShapeUrl=>TfrOriginalFiles.Read("no-shape"),AviationTfr.GeometryUrl=>TfrOriginalFiles.Read("geometry"),_=>TfrOriginalFiles.Packet(new[]{"6/2070","6/2069","6/2014","6/9169"}.Single(id=>uri==AviationTfrOperations.Url(id)||uri==AviationTfrOperations.Url(id,true)),uri.EndsWith(".aixm50",StringComparison.Ordinal))};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
        }
    }
    public static async Task Run(string[] args)
    {
        if(args.Length!=1||Directory.Exists(args[0]))throw new ArgumentException("Use a new isolated evidence directory.");
        var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);var handler=new Transport();using var http=new HttpClient(handler);var clock=new Clock();
        var views=new List<AviationTfrOperationalView>();var boundaries=new List<AviationTfrBoundary>();var screens=new List<AviationTfrScreenResult>();
        using(var service=new AviationReference(root,root,http,clock,unmetered:()=>true))
        {
            service.Configure(true);await service.RefreshTfr();
            foreach(var id in new[]{"6/2070","6/2069","6/2014","6/9169"})views.Add(await service.RefreshTfrOperations(id));
            foreach(var view in views)if(view.Error is not null||view.Areas.Length==0)throw new InvalidDataException("Recorded official packet replay failed.");
            foreach(var bounds in new[]{new[]{-99d,26,-96,29},new[]{-97d,38,-94,41},new[]{-117d,43,-114,46},new[]{-80d,34,-77,37}})boundaries.AddRange(service.TfrBoundaries(bounds[0],bounds[1],bounds[2],bounds[3]).Boundaries.Where(b=>views.Any(v=>v.NotamId==b.NotamId)));
            foreach(var b in boundaries)
            {
                var w=b.Operational?.Areas.SingleOrDefault()?.Windows.FirstOrDefault();
                screens.Add(service.ScreenTfr(new(1,[b.Polygons[0][0][0]],PlannedStartUtc:w?.StartUtc,PlannedEndUtc:w?.EndUtc,MinimumMslFeet:8000,MaximumMslFeet:9000,TimeBasis:"Real-world planned UTC")));
            }
            service.Configure(false);
        }
        handler.Offline=true;var before=handler.Calls;
        using(var reopened=new AviationReference(root,root,http,clock,unmetered:()=>true))
            foreach(var v in views){var retained=reopened.TfrOperations(v.NotamId);if(retained.XmlSha256!=v.XmlSha256||retained.AixmSha256!=v.AixmSha256||!retained.Status.Contains("offline",StringComparison.Ordinal))throw new IOException("Offline retained identity failed.");}
        if(handler.Calls!=before||File.Exists(Path.Combine(root,"missions.db")))throw new IOException("Unexpected mission or network side effect.");
        File.WriteAllText(Path.Combine(root,"operations-receipt.json"),JsonSerializer.Serialize(new{version=1,evidenceClass="Real-process HTTP replay/cache/restart of genuine recorded FAA originals; not current feed, legal applicability or owner-device acceptance",applicationClockUtc=clock.GetUtcNow(),httpCalls=before,offlineCalls=handler.Calls-before,views,boundaries,screens},WireJson.Options));
        Console.WriteLine("PASS: exact recorded FAA packets, real cache, area/time/altitude screening and offline restart; no mission created.");
    }
}
