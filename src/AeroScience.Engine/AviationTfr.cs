using System.Net;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using Coordinates=AeroScience.Shared.Coordinates;

namespace AeroScience.Engine;

public sealed partial class AviationReference
{
    private (Guid Store,DateTimeOffset Retrieved,AviationTfrGeometrySnapshot Snapshot,STRtree<(AviationTfrBoundary Boundary,Geometry Geometry)> Index)? tfrIndex;
    public object TfrConfiguration(){lock(gate){var p=Preferences();return new{p.TfrMonitoringEnabled,p.OnlineEnabled,minimumRefreshSeconds=300,privacy="One companion host retrieves three bounded inventory/geometry responses. Selected operational details require an explicit retrieval of two bounded official packets per notice. Paired devices read the host cache; polling is opt-in. No complete real-world restriction feed is promised."};}}
    public object ConfigureTfr(bool enabled)
    {
        lock(gate){var p=Preferences();if(enabled&&!p.OnlineEnabled)throw new InvalidOperationException("Enable FAA reference consent before TFR monitoring.");PreferenceStore.WriteAtomic(settingsPath,p with{TfrMonitoringEnabled=enabled});return TfrConfiguration();}
    }
    public async Task RefreshTfrIfDue(CancellationToken token)
    {
        lock(gate)
        {
            var p=Preferences();if(!p.TfrMonitoringEnabled||!p.OnlineEnabled||p.StoreId is null||p.UnmeteredOnly&&(unmetered??NetworkCost.IsUnmetered)()!=true)return;
            using var store=Open(p);var last=store.Status(AeroScience.Missions.AviationTfr.Kind).LastAttemptUtc;
            if(last is {} utc&&clock.GetUtcNow()-utc<TimeSpan.FromMinutes(5)||p.TfrRetryNotBeforeUtc>clock.GetUtcNow())return;
        }
        try{await RefreshTfr(token);}catch(InvalidOperationException){/* Another explicit refresh or consent change won the race; no automatic retry loop. */}
    }
    private async Task<byte[]> TfrBytes(string url,CancellationToken token,int maximumBytes=AviationCatalog.MaximumMetadataBytes)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));token=deadline.Token;
        lock(gate){var p=Preferences();if(!p.OnlineEnabled||p.UnmeteredOnly&&(unmetered??NetworkCost.IsUnmetered)()!=true)throw new InvalidOperationException("FAA consent or network policy prevents retrieval.");}
        using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.UserAgent.ParseAdd("AeroScience/0.6.0");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var requested=response.Headers.RetryAfter?.Delta??(response.Headers.RetryAfter?.Date-clock.GetUtcNow())??TimeSpan.FromMinutes(5);
            lock(gate)PreferenceStore.WriteAtomic(settingsPath,Preferences() with{TfrRetryNotBeforeUtc=clock.GetUtcNow()+(requested<TimeSpan.FromMinutes(5)?TimeSpan.FromMinutes(5):requested)});
            throw new HttpRequestException("FAA retry guidance retained",null,response.StatusCode);
        }
        if((int)response.StatusCode is >=300 and <400||response.Headers.Location is not null)throw new InvalidDataException("Unsupported FAA redirect; no authentication forwarded.");
        response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>maximumBytes)throw new InvalidDataException("FAA metadata exceeds its bounded request allowance.");
        using var output=new MemoryStream();await using var input=await response.Content.ReadAsStreamAsync(token);var buffer=new byte[32768];
        while(true){var count=await input.ReadAsync(buffer,token);if(count==0)break;if(output.Length+count>maximumBytes)throw new InvalidDataException("FAA metadata exceeds its bounded request allowance.");output.Write(buffer,0,count);}
        return output.ToArray();
    }
    public async Task<object> RefreshTfr(CancellationToken token=default)
    {
        if(!await refresh.WaitAsync(0,token))throw new InvalidOperationException("Aviation metadata refresh already running.");
        var attempt=clock.GetUtcNow();
        try
        {
            lock(gate)
            {
                var p=Preferences();if(!p.OnlineEnabled)throw new InvalidOperationException("FAA online reference consent is disabled.");using var store=Open(p);
                var last=store.Status(AeroScience.Missions.AviationTfr.Kind).LastAttemptUtc;
                if(last is {} utc&&attempt-utc<TimeSpan.FromMinutes(5)||p.TfrRetryNotBeforeUtc>attempt)throw new InvalidOperationException("FAA TFR refresh is throttled; retained data remains available.");
                store.Attempt(AeroScience.Missions.AviationTfr.Kind,attempt,null);
            }
            try
            {
                var notices=await TfrBytes(AeroScience.Missions.AviationTfr.NoticesUrl,token);var noShape=await TfrBytes(AeroScience.Missions.AviationTfr.NoShapeUrl,token);var geometry=await TfrBytes(AeroScience.Missions.AviationTfr.GeometryUrl,token);
                var snapshot=await Task.Run(()=>AeroScience.Missions.AviationTfr.Parse(notices,noShape,geometry,clock.GetUtcNow()),token);
                lock(gate){if(!Preferences().OnlineEnabled)return TfrConfiguration();using var store=Open(Preferences());store.Save(snapshot);store.Attempt(AeroScience.Missions.AviationTfr.Kind,attempt,null);tfrIndex=null;PreferenceStore.WriteAtomic(settingsPath,Preferences() with{TfrRetryNotBeforeUtc=null});}
            }
            catch(Exception e)when(e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException or NotSupportedException or KeyNotFoundException or FormatException or InvalidOperationException or OverflowException)
            {
                lock(gate){using var store=Open(Preferences());store.Attempt(AeroScience.Missions.AviationTfr.Kind,attempt,e is HttpRequestException h&&h.StatusCode==HttpStatusCode.TooManyRequests?"RateLimited":e is HttpRequestException or IOException?"NetworkUnavailable":e is OperationCanceledException?"Timeout":"InvalidMetadata");}
                if(token.IsCancellationRequested)throw;
            }
            return TfrSummary();
        }
        finally{refresh.Release();}
    }
    private (AviationTfrGeometrySnapshot Snapshot,STRtree<(AviationTfrBoundary Boundary,Geometry Geometry)> Index)? ReadTfr(AviationMetadataStore store)
    {
        var utc=store.Status(AeroScience.Missions.AviationTfr.Kind).LastSuccessUtc;if(utc is null)return null;
        if(tfrIndex is {} current&&current.Store==store.StoreId&&current.Retrieved==utc)return(current.Snapshot,current.Index);
        var snapshot=store.TfrBoundaries()??throw new IOException("Missing retained TFR snapshot.");var tree=new STRtree<(AviationTfrBoundary,Geometry)>();
        foreach(var b in snapshot.Boundaries){var geometry=AeroScience.Missions.AviationTfr.Geometry(b.Polygons);tree.Insert(geometry.EnvelopeInternal,(b,geometry));}
        tree.Build();tfrIndex=(store.StoreId,snapshot.RetrievedUtc,snapshot,tree);return(snapshot,tree);
    }
    private string TfrFreshness(AviationCacheStatus? status)
    {
        if(status?.LastSuccessUtc is not {} utc)return "Not searched";
        if(!Preferences().OnlineEnabled)return "Offline retained; operational freshness unverified";
        return status.Error is not null||clock.GetUtcNow()-utc>TimeSpan.FromMinutes(15)?"Stale retained; operational freshness unverified":"Recently retrieved; operational completeness unverified";
    }
    public object TfrSummary()
    {
        lock(gate){if(Preferences().StoreId is null)return new{status="Not searched",snapshotComplete=false};using var store=Open(Preferences());var status=store.Status(AeroScience.Missions.AviationTfr.Kind);var s=ReadTfr(store)?.Snapshot;
            return new{status=TfrFreshness(status),cache=status,snapshotComplete=false,noticeCount=s?.NoticeIds.Length,boundaryCount=s?.Boundaries.Length,noticesWithoutBoundary=s?.NoticeIds.Except(s.Boundaries.Select(b=>b.NotamId)).Count(),omittedFeatures=s?.OmittedFeatures,limitations=AeroScience.Missions.AviationTfr.Limitations};}
    }
    public object TfrNotices(int offset=0,bool missingOnly=false)
    {
        if(offset is <0 or >2048||offset%50!=0)throw new ArgumentException("Use bounded 50-notice pages.");
        lock(gate)
        {
            if(Preferences().StoreId is null)return new{status="Not searched",offset,total=0,notices=Array.Empty<AviationTfrNoticeMetadata>(),snapshotComplete=false};
            using var store=Open(Preferences());var status=store.Status(AeroScience.Missions.AviationTfr.Kind);var snapshot=ReadTfr(store)?.Snapshot;
            var records=snapshot?.NoticeInventory.Where(n=>!missingOnly||!snapshot.Boundaries.Any(b=>b.NotamId==n.NotamId)).ToArray()??[];
            return new{status=TfrFreshness(status),cache=status,offset,total=records.Length,notices=records.Skip(offset).Take(50).ToArray(),snapshotComplete=false};
        }
    }
    public AviationTfrBoundaryPage TfrBoundaries(double west,double south,double east,double north,int offset=0)
    {
        if(new[]{west,south,east,north}.Any(v=>!double.IsFinite(v))||west< -180||east>180||south< -85||north>85||west>=east||south>=north||east-west>30||north-south>30||offset is <0 or >2000)throw new ArgumentException("Use a bounded TFR viewport up to 30 degrees, split dateline/polar regions and a valid cursor.");
        lock(gate)
        {
            if(Preferences().StoreId is null)return new(1,"Not searched",null,null,false,offset,null,0,0,[],0,AeroScience.Missions.AviationTfr.Limitations);
            using var store=Open(Preferences());var status=store.Status(AeroScience.Missions.AviationTfr.Kind);var retained=ReadTfr(store);
            if(retained is null)return new(1,TfrFreshness(status),status,null,false,offset,null,0,0,[],0,AeroScience.Missions.AviationTfr.Limitations);
            var (snapshot,index)=retained.Value;var bounds=new Envelope(west,east,south,north);var region=new GeometryFactory().ToGeometry(bounds);
            var candidates=index.Query(bounds).OrderBy(e=>e.Boundary.Id,StringComparer.Ordinal).ToArray();var result=new List<AviationTfrBoundary>();var vertices=0;var cursor=offset;
            var views=new Dictionary<string,(AviationTfrOperationalView,AviationTfrInterpretation?)>();
            foreach(var b in candidates.Skip(offset).Take(100)){if(vertices+b.Geometry.NumPoints>25000)break;cursor++;if(!b.Geometry.Intersects(region))continue;vertices+=b.Geometry.NumPoints;result.Add(WithOperations(store,snapshot,b.Boundary,views));}
            return new(1,TfrFreshness(status),status,snapshot.RetrievedUtc,false,offset,cursor<candidates.Length?cursor:null,candidates.Length,vertices,result.ToArray(),snapshot.NoticeIds.Except(snapshot.Boundaries.Select(b=>b.NotamId)).Count(),snapshot.Limitations);
        }
    }
    public AviationTfrScreenResult ScreenTfr(AviationTfrScreenRequest request)
    {
        if(request.Version!=1)throw new NotSupportedException("Unsupported TFR screening request version.");
        if(request.Route is null||request.Route.Length>512||request.Area?.Length>1024||request.Route.Concat(request.Area??[]).Any(c=>c is null||!double.IsFinite(c.Latitude)||!double.IsFinite(c.Longitude)||Math.Abs(c.Latitude)>85||Math.Abs(c.Longitude)>180)||request.TimeBasis is not("Unspecified" or "Real-world planned UTC" or "Simulator time"))throw new ArgumentException("Bound route/area inputs and identify real-world versus simulator time.");
        var points=request.Route.Concat(request.Area??[]).ToArray();if(points.Length==0)throw new ArgumentException("Supply an actual route or study polygon.");
        if(points.Max(c=>c.Longitude)-points.Min(c=>c.Longitude)>30||points.Max(c=>c.Latitude)-points.Min(c=>c.Latitude)>30)throw new NotSupportedException("Split extreme/dateline routes before TFR screening.");
        if(request.MinimumMslFeet is {} min&&!double.IsFinite(min)||request.MaximumMslFeet is {} max&&!double.IsFinite(max)||request.MinimumMslFeet>request.MaximumMslFeet||request.PlannedStartUtc>request.PlannedEndUtc)throw new ArgumentException("Invalid planned altitude/time interval.");
        var factory=new GeometryFactory();var shapes=new List<Geometry>();Coordinate Point(Coordinates c)=>new(c.Longitude,c.Latitude);
        if(request.Route.Length>=2)shapes.Add(factory.CreateLineString(request.Route.Select(Point).ToArray()));else if(request.Route.Length==1)shapes.Add(factory.CreatePoint(Point(request.Route[0])));
        if(request.Area is {Length:>0} area){if(area.Length<4||area[0]!=area[^1])throw new ArgumentException("Study polygon must be explicitly closed.");var polygon=factory.CreatePolygon(area.Select(Point).ToArray());if(!polygon.IsValid)throw new ArgumentException("Invalid study polygon.");shapes.Add(polygon);}
        var geometry=factory.CreateGeometryCollection(shapes.ToArray());
        lock(gate)
        {
            AviationCacheStatus? cache=null;AviationTfrGeometrySnapshot? snapshot=null;var findings=new List<AviationTfrScreenFinding>();
            if(Preferences().StoreId is not null){using var store=Open(Preferences());cache=store.Status(AeroScience.Missions.AviationTfr.Kind);var retained=ReadTfr(store);if(retained is not null){snapshot=retained.Value.Snapshot;var views=new Dictionary<string,(AviationTfrOperationalView,AviationTfrInterpretation?)>();foreach(var b in retained.Value.Index.Query(geometry.EnvelopeInternal).Where(b=>b.Geometry.Intersects(geometry)).OrderBy(b=>b.Boundary.Id,StringComparer.Ordinal))findings.Add(ScreenBoundary(WithOperations(store,snapshot,b.Boundary,views),request));}}
            var matches=findings.Where(f=>f.Outcome=="Potential intersection").Select(f=>f.NotamId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return new(1,matches.Length>0?"Potential intersection":"Unable to determine",matches.Length>0?"Published horizontal boundaries intersect the supplied plan. Per-area findings distinguish supported time/altitude evidence from Unknown. Cancellation, legal applicability and snapshot completeness remain unverified; review the official notices.":"No potentially intersecting area was established in assessed dimensions. Missing boundaries, stale/partial coverage and unverified lifecycle prevent an all-clear.",matches,snapshot?.RetrievedUtc,TfrFreshness(cache),false,AeroScience.Missions.AviationTfr.Limitations,findings.ToArray());
        }
    }
}
