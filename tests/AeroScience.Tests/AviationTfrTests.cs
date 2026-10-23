using System.Net;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed class AviationTfrTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-tfr",Guid.NewGuid().ToString("N"));
    private sealed class Clock:TimeProvider{public DateTimeOffset Now=new(2026,10,23,12,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Transport:HttpMessageHandler
    {
        public int Calls;public string? Fault;public HttpStatusCode Status=HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;Assert.Contains(request.RequestUri!.AbsoluteUri,new[]{AviationTfr.NoticesUrl,AviationTfr.NoShapeUrl,AviationTfr.GeometryUrl});
            var content=request.RequestUri.AbsoluteUri==AviationTfr.NoticesUrl?SyntheticTfr.Notices():request.RequestUri.AbsoluteUri==AviationTfr.NoShapeUrl?SyntheticTfr.NoShape():SyntheticTfr.Geometry(Fault);
            var r=new HttpResponseMessage(Status){Content=new ByteArrayContent(content)};if(Status==HttpStatusCode.TooManyRequests)r.Headers.RetryAfter=new(TimeSpan.FromHours(2));return Task.FromResult(r);
        }
    }
    private AviationReference Service(Transport h,Clock clock)=>new(root,root,new HttpClient(h),clock,unmetered:()=>true);
    private static AviationTfrScreenRequest Route(double west,double latitude,double east)=>new(1,[new(latitude,west),new(latitude,east)]);
    [Fact]public async Task ActualOfficialServiceJoinPersistsHolesAndNeverClearsIncompleteTimeAltitudeOrCoverage()
    {
        var h=new Transport();var clock=new Clock();
        using(var s=Service(h,clock))
        {
            s.Configure(true);await s.RefreshTfr();Assert.Equal(3,h.Calls);
            var p=s.TfrBoundaries(-76,39,-74,41);var b=Assert.Single(p.Boundaries);Assert.False(p.SnapshotComplete);Assert.Equal(1,p.NoticesWithoutBoundary);Assert.Equal(2,Assert.Single(b.Polygons).Length);
            Assert.Contains("Unknown",b.TimeStatus);Assert.Contains("Unknown",b.AltitudeStatus);Assert.Contains("unverified",b.SegmentStatus);Assert.Equal(123,b.SourceGeometryId);
            Assert.Empty(s.TfrBoundaries(-75.2,39.8,-74.8,40.2).Boundaries);
            Assert.Equal("Potential intersection",s.ScreenTfr(Route(-77,39.2,-73)).Outcome);Assert.Equal("Unable to determine",s.ScreenTfr(Route(-77,38,-73)).Outcome);
            Assert.Equal("Unable to determine",s.ScreenTfr(Route(-75.2,40,-74.8)).Outcome);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>s.RefreshTfr());Assert.Equal(3,h.Calls);s.Configure(false);
        }
        using var offline=Service(h,clock);Assert.Single(offline.TfrBoundaries(-76,39,-74,41).Boundaries);Assert.Contains("Offline",offline.TfrBoundaries(-76,39,-74,41).Status);Assert.Equal(3,h.Calls);
        Assert.False(File.Exists(Path.Combine(root,"missions.db")));
    }
    [Fact]public async Task FailedRefreshPreservesSnapshotAndSeparatesLastAttemptWithDurableRetryAfter()
    {
        var h=new Transport();var clock=new Clock();using(var s=Service(h,clock)){s.Configure(true);await s.RefreshTfr();clock.Now=clock.Now.AddMinutes(6);h.Status=HttpStatusCode.TooManyRequests;await s.RefreshTfr();var p=s.TfrBoundaries(-76,39,-74,41);Assert.Single(p.Boundaries);Assert.Equal("RateLimited",p.Cache!.Error);Assert.True(p.Cache.LastAttemptUtc>p.Cache.LastSuccessUtc);}
        using var reopened=Service(h,clock);clock.Now=clock.Now.AddMinutes(6);await Assert.ThrowsAsync<InvalidOperationException>(()=>reopened.RefreshTfr());Assert.Equal(4,h.Calls);
    }
    [Fact]public async Task MonitoringIsHostOwnedOptInConsentAndNetworkPolicyBounded()
    {
        var h=new Transport();var clock=new Clock();using var s=Service(h,clock);await s.RefreshTfrIfDue(default);Assert.Equal(0,h.Calls);Assert.Throws<InvalidOperationException>(()=>s.ConfigureTfr(true));
        s.Configure(true);await s.RefreshTfrIfDue(default);Assert.Equal(0,h.Calls);s.ConfigureTfr(true);await s.RefreshTfrIfDue(default);Assert.Equal(3,h.Calls);await s.RefreshTfrIfDue(default);Assert.Equal(3,h.Calls);
        s.Configure(false);clock.Now=clock.Now.AddHours(1);await s.RefreshTfrIfDue(default);Assert.Equal(3,h.Calls);
    }
    [Theory][InlineData("truncated")][InlineData("crs")]
    public async Task MalformedOrTruncatedSnapshotCannotReplaceSuccessfulGeometry(string fault)
    {
        var h=new Transport();var clock=new Clock();using var s=Service(h,clock);s.Configure(true);await s.RefreshTfr();var before=s.TfrBoundaries(-76,39,-74,41).RetrievedUtc;clock.Now=clock.Now.AddMinutes(6);h.Fault=fault;await s.RefreshTfr();var after=s.TfrBoundaries(-76,39,-74,41);Assert.Single(after.Boundaries);Assert.Equal(before,after.RetrievedUtc);Assert.Equal("InvalidMetadata",after.Cache!.Error);
    }
    [Fact]public void MismatchedIdentityIsNotSilentlyAttachedToAnotherNotice()
    {var snapshot=AviationTfr.Parse(SyntheticTfr.Notices(),SyntheticTfr.NoShape(),SyntheticTfr.Geometry("mismatch"),DateTimeOffset.UtcNow);Assert.Empty(snapshot.Boundaries);Assert.Equal(1,snapshot.OmittedFeatures);}
    [Fact]public async Task BoundedScreeningAndMissingDriveFailClosedWithoutChangingRecordingState()
    {
        var h=new Transport();var clock=new Clock();using var s=Service(h,clock);s.Configure(true);await s.RefreshTfr();
        Assert.Throws<ArgumentException>(()=>s.TfrBoundaries(-180,-80,180,80));Assert.Throws<NotSupportedException>(()=>s.ScreenTfr(Route(-170,40,170)));
        Assert.Throws<ArgumentException>(()=>s.ScreenTfr(new(1,[],[new(40,-75),new(41,-74),new(40,-74)])));
        Assert.Throws<NotSupportedException>(()=>s.ScreenTfr(Route(-76,40,-74) with{Version=99}));
        var folder=Path.Combine(root,"AviationReference");Directory.Move(folder,folder+"-missing");Assert.Throws<IOException>(()=>s.TfrBoundaries(-76,39,-74,41));Assert.False(Directory.Exists(folder));Directory.Move(folder+"-missing",folder);
    }
    public void Dispose(){try{Directory.Delete(root,true);}catch(IOException){}}
}
