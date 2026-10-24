using System.Net;
using AeroScience.Missions;
using AeroScience.Shared;
using NetTopologySuite.Geometries;

namespace AeroScience.Engine;

public sealed partial class AviationReference
{
    private (AviationTfrOperationalView View, AviationTfrInterpretation? Interpretation) ReadOperations(AviationMetadataStore store, AviationTfrGeometrySnapshot snapshot, string id)
    {
        var cache = store.Status(AviationTfrOperations.Kind(id)); var source = store.TfrOperations(id);
        var sameRevision = source is not null && source.NoticeRevision == AviationTfrOperations.Revision(snapshot, id);
        var fresh = sameRevision && Preferences().OnlineEnabled && cache.Error is null && store.Status(AeroScience.Missions.AviationTfr.Kind).Error is null && clock.GetUtcNow()-snapshot.RetrievedUtc<=TimeSpan.FromMinutes(15) && clock.GetUtcNow() - source!.RetrievedUtc <= TimeSpan.FromMinutes(15);
        var interpretation = sameRevision ? AviationTfrOperations.Parse(source!) : null;
        var now=clock.GetUtcNow();
        string WindowStatus(AviationTfrOperationalArea a)=>!fresh||a.Windows.Length==0?"Current time applicability Unknown":a.Windows.Any(w=>w.StartUtc<=now&&now<=w.EndUtc)?"Within published UTC window; cancellation unverified":a.Windows.All(w=>now<w.StartUtc)?"Upcoming published UTC window; cancellation unverified":"Published UTC windows elapsed; lifecycle unverified";
        var status = source is null ? "Not searched" : !sameRevision ? "Notice revision changed; reassess" : fresh ? "Recently retrieved; cancellation/completeness unverified" : "Stale or offline retained; current applicability Unknown";
        return (new(1,id,status,source?.RetrievedUtc,cache.LastAttemptUtc,cache.Error,source?.XmlSha256,source?.AixmSha256,
            AviationTfrOperations.Url(id),AviationTfrOperations.Url(id,true),AviationTfrOperations.LifecycleUnknown,
            interpretation?.Areas.Select(a=>a with{Polygons=null,WindowStatus=WindowStatus(a)}).ToArray()??[],AviationTfrOperations.Limitations,interpretation?.WorkIdentity,interpretation?.IssuedText),interpretation);
    }
    public AviationTfrOperationalView TfrOperations(string id)
    {
        AviationTfrOperations.Id(id);
        lock(gate)
        {
            using var store=Open(Preferences());var snapshot=ReadTfr(store)?.Snapshot??throw new InvalidOperationException("Refresh the official notice inventory first.");
            AviationTfrOperations.Revision(snapshot,id);return ReadOperations(store,snapshot,id).View;
        }
    }
    public async Task<AviationTfrOperationalView> RefreshTfrOperations(string id,CancellationToken token=default)
    {
        AviationTfrOperations.Id(id);
        if(!await refresh.WaitAsync(0,token))throw new InvalidOperationException("Aviation metadata refresh already running.");
        try
        {
            var attempt=clock.GetUtcNow();string revision;
            lock(gate)
            {
                var p=Preferences();if(!p.OnlineEnabled)throw new InvalidOperationException("FAA online reference consent is disabled.");
                using var store=Open(p);var snapshot=ReadTfr(store)?.Snapshot??throw new InvalidOperationException("Refresh the official notice inventory first.");
                // Stale inventory may refer to an old digit/year reuse of a NOTAM number.
                var inventory=store.Status(AeroScience.Missions.AviationTfr.Kind);
                if(inventory.Error is not null||attempt-snapshot.RetrievedUtc>TimeSpan.FromMinutes(15))throw new InvalidOperationException("Refresh the stale FAA notice inventory before retrieving operational details.");
                revision=AviationTfrOperations.Revision(snapshot,id);var last=store.Status(AviationTfrOperations.Kind(id)).LastAttemptUtc;
                if(last is {} utc&&attempt-utc<TimeSpan.FromMinutes(5)||p.TfrRetryNotBeforeUtc>attempt)throw new InvalidOperationException("FAA detail refresh is throttled; inspect retained evidence.");
                store.Attempt(AviationTfrOperations.Kind(id),attempt,null);
            }
            try
            {
                var xml=await TfrBytes(AviationTfrOperations.Url(id),token,AviationTfrOperations.MaximumBytes);
                var aixm=await TfrBytes(AviationTfrOperations.Url(id,true),token,AviationTfrOperations.MaximumBytes);
                var source=new AviationTfrOperationalSnapshot(1,id,revision,clock.GetUtcNow(),xml,aixm,AviationTfrOperations.Hash(xml),AviationTfrOperations.Hash(aixm));
                var parsed=await Task.Run(()=>AviationTfrOperations.Parse(source),token);
                lock(gate)
                {
                    if(!Preferences().OnlineEnabled)throw new InvalidOperationException("FAA consent was withdrawn.");
                    using var store=Open(Preferences());var snapshot=ReadTfr(store)?.Snapshot??throw new InvalidOperationException("Notice inventory unavailable.");
                    if(AviationTfrOperations.Revision(snapshot,id)!=revision)throw new InvalidOperationException("Notice changed during retrieval; stale result discarded.");
                    if(snapshot.NoticeInventory.Single(n=>n.NotamId==id).Facility!=parsed.Facility)throw new InvalidDataException("TFR packet facility does not match the selected notice.");
                    store.Save(source);store.Attempt(AviationTfrOperations.Kind(id),attempt,null);
                }
            }
            catch(Exception e)when(e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or NotSupportedException or InvalidOperationException or FormatException or OverflowException)
            {
                lock(gate){using var store=Open(Preferences());store.Attempt(AviationTfrOperations.Kind(id),attempt,e is HttpRequestException h&&h.StatusCode==HttpStatusCode.TooManyRequests?"RateLimited":e is HttpRequestException?"ProviderUnavailable":e is OperationCanceledException?"Timeout":"InvalidMetadata");}
                if(token.IsCancellationRequested)throw;
            }
            return TfrOperations(id);
        }
        finally{refresh.Release();}
    }
    private AviationTfrBoundary WithOperations(AviationMetadataStore store,AviationTfrGeometrySnapshot snapshot,AviationTfrBoundary boundary,Dictionary<string,(AviationTfrOperationalView View,AviationTfrInterpretation? Interpretation)> views)
    {
        if(!views.TryGetValue(boundary.NotamId,out var detail)){if(views.Count>=32)return boundary;detail=ReadOperations(store,snapshot,boundary.NotamId);views.Add(boundary.NotamId,detail);}
        var area=detail.Interpretation is {} parsed?AviationTfrOperations.Match(boundary,parsed):null;
        if(area is null)return boundary with{Operational=detail.View with{Areas=[]}};
        return boundary with{Operational=detail.View with{Areas=detail.View.Areas.Where(a=>a.Id==area.Id).ToArray()},SegmentStatus=$"Published area {area.Id} ({area.Name}) matched by original merged polygon; WFS key segment remains unverified",
            TimeStatus=area.Windows.Length==0?area.ScheduleStatus:string.Join("; ",area.Windows.Select(w=>$"Published UTC {w.StartUtc:O} to {w.EndUtc:O}"))+"; "+detail.View.Status,
            AltitudeStatus=$"Published {LevelText(area.Lower)} to {LevelText(area.Upper)}; no terrain/pressure conversion"};
    }
    private static string LevelText(AviationTfrLevel level)=>$"{(level.Value is {} v?v.ToString(System.Globalization.CultureInfo.InvariantCulture):"Unknown")} {level.Unit} {level.Reference} ({level.Inclusion})";
    private AviationTfrScreenFinding ScreenBoundary(AviationTfrBoundary boundary,AviationTfrScreenRequest request)
    {
        var detail=boundary.Operational;var area=detail?.Areas.SingleOrDefault();
        var fresh=detail?.Status=="Recently retrieved; cancellation/completeness unverified";
        var time="Unknown";var altitude="Unknown";
        if(area is not null)
        {
            if(request.TimeBasis=="Real-world planned UTC"&&request.PlannedStartUtc is {} start&&request.PlannedEndUtc is {} end&&area.Windows.Length>0)
                time=area.Windows.Any(w=>start<=w.EndUtc&&end>=w.StartUtc)?"Intersects published UTC window":"Outside published UTC windows";
            // Only comparisons in the supplied MSL datum; AGL and FL never become guessed MSL.
            var below=area.Lower is{Value:{} lower,Reference:"MSL",Unit:"FT"}&&request.MaximumMslFeet is {} max&&(max<lower||max==lower&&area.Lower.Inclusion=="EXCLUDE");
            var above=area.Upper is{Value:{} upper,Reference:"MSL",Unit:"FT"}&&request.MinimumMslFeet is {} min&&(min>upper||min==upper&&area.Upper.Inclusion=="EXCLUDE");
            if(below||above)altitude="Outside published MSL limits";
            else if(area.Lower is{Value:not null,Reference:"MSL",Unit:"FT"}&&area.Upper is{Value:not null,Reference:"MSL",Unit:"FT"}&&request.MinimumMslFeet is not null&&request.MaximumMslFeet is not null)altitude="Intersects published MSL limits";
        }
        var outside=time=="Outside published UTC windows"||altitude=="Outside published MSL limits";
        return new(boundary.NotamId,boundary.Id,area?.Id,"Published horizontal intersection",time,altitude,
            fresh&&outside?"Outside assessed dimensions; lifecycle unverified":"Potential intersection",
            (fresh?"":"Current applicability Unknown: evidence is missing, revised, stale, offline or failed. ")+"Time/altitude findings describe only this retained area. Cancellation/amendment, aircraft applicability and overall snapshot completeness remain unverified; review the official notice.",detail?.RetrievedUtc,detail?.XmlSha256,detail?.AixmSha256);
    }
}
