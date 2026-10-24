using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed class AviationTfrOperationsTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-tfr-operations",Guid.NewGuid().ToString("N"));
    private sealed class Clock:TimeProvider{public DateTimeOffset Now=new(2026,10,24,19,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Transport:HttpMessageHandler
    {
        public int Calls;public HttpStatusCode Status=HttpStatusCode.OK;public Func<byte[],byte[]>? XmlChange;public bool RevisionChanged;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;var uri=request.RequestUri!.AbsoluteUri;byte[] data;
            if(uri==AviationTfr.NoticesUrl)data=TfrOriginalFiles.Read("notices");
            else if(uri==AviationTfr.NoShapeUrl)data=TfrOriginalFiles.Read("no-shape");
            else if(uri==AviationTfr.GeometryUrl){data=TfrOriginalFiles.Read("geometry");if(RevisionChanged)data=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("202610241817","202610241818",StringComparison.Ordinal));}
            else
            {
                var id=new[]{"6/2070","6/2069","6/2014","6/9169"}.Single(id=>uri==AviationTfrOperations.Url(id)||uri==AviationTfrOperations.Url(id,true));
                data=TfrOriginalFiles.Packet(id,uri.EndsWith(".aixm50",StringComparison.Ordinal));
                if(!uri.EndsWith(".aixm50",StringComparison.Ordinal)&&XmlChange is not null)data=XmlChange(data);
            }
            var response=new HttpResponseMessage(Status){Content=new ByteArrayContent(data)};
            if(Status==HttpStatusCode.Redirect)response.Headers.Location=new("https://untrusted.invalid/notice");
            if(Status==HttpStatusCode.TooManyRequests)response.Headers.RetryAfter=new(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        }
    }
    private static AviationTfrGeometrySnapshot Geometry()=>AviationTfr.Parse(TfrOriginalFiles.Read("notices"),TfrOriginalFiles.Read("no-shape"),TfrOriginalFiles.Read("geometry"),new(2026,10,24,19,0,0,TimeSpan.Zero));
    private static AviationTfrOperationalSnapshot Source(string id="6/2070",byte[]? xml=null,byte[]? aixm=null)
    {
        xml??=TfrOriginalFiles.Packet(id);aixm??=TfrOriginalFiles.Packet(id,true);
        return new(1,id,AviationTfrOperations.Revision(Geometry(),id),new(2026,10,24,19,0,0,TimeSpan.Zero),xml,aixm,AviationTfrOperations.Hash(xml),AviationTfrOperations.Hash(aixm));
    }
    private AviationReference Service(Transport transport,Clock clock)=>new(root,root,new HttpClient(transport),clock,unmetered:()=>true);
    private static Coordinates Point(AviationTfrBoundary b)=>b.Polygons[0][0][0];
    [Theory][InlineData("6/2070",1)][InlineData("6/2069",2)][InlineData("6/2014",1)][InlineData("6/9169",1)]
    public void RecordedOfficialPacketsRetainAreaIdentityAltitudeOriginalsAndExactGeometryJoins(string id,int count)
    {
        var source=Source(id);var parsed=AviationTfrOperations.Parse(source);Assert.Equal(count,parsed.Areas.Length);Assert.Equal(id,parsed.NotamId);Assert.Contains("Unknown",parsed.LifecycleStatus);
        foreach(var boundary in Geometry().Boundaries.Where(b=>b.NotamId==id))
        {
            var area=AviationTfrOperations.Match(boundary,parsed);Assert.NotNull(area);Assert.NotNull(area.Polygons);Assert.NotNull(area.Upper.Value);Assert.Equal("FT",area.Upper.Unit);Assert.Equal(id=="6/2014"?"MSL":"AGL",area.Upper.Reference);
        }
        var damaged=source.XmlOriginal.ToArray();damaged[100]^=1;Assert.Throws<InvalidDataException>(()=>AviationTfrOperations.Parse(source with{XmlOriginal=damaged}));
    }
    [Fact]public void ExplicitUtcAixmTimesheetsDisambiguateUnzonedXmlAndSeparateAreas()
    {
        var a=AviationTfrOperations.Parse(Source()).Areas.Single();var w=Assert.Single(a.Windows);Assert.Equal(DateTimeOffset.Parse("2026-10-27T16:00:00Z"),w.StartUtc);Assert.Equal(DateTimeOffset.Parse("2026-10-27T22:30:00Z"),w.EndUtc);
        var multi=AviationTfrOperations.Parse(Source("6/2069"));Assert.Equal(new[]{"2026-10-26T20:15:00.0000000+00:00","2026-10-26T20:45:00.0000000+00:00"},multi.Areas.Select(a=>Assert.Single(a.Windows).StartUtc.ToString("O")));
        var mismatched=TfrOriginalFiles.Packet("6/2070",true);
        // Namespace prefixes are preserved by the official original; test a DOM mutation independently of encoding/prefix.
        var doc=XDocument.Load(new MemoryStream(mismatched));XNamespace ns="http://www.aixm.aero/schema/5.0";doc.Descendants(ns+"timeReference").Single().Value="LOCAL";
        using var output=new MemoryStream();doc.Save(output);Assert.Empty(AviationTfrOperations.Parse(Source(aixm:output.ToArray())).Areas.Single().Windows);
    }
    [Theory][InlineData("6/2014")][InlineData("6/9169")]
    public void GenuineRecurringSchedulesAreNotInventedAsContinuousActivation(string id)
    {var area=Assert.Single(AviationTfrOperations.Parse(Source(id)).Areas);Assert.Empty(area.Windows);Assert.Contains("Unknown",area.ScheduleStatus);}
    [Theory][InlineData("doctype")][InlineData("version")][InlineData("identity")][InlineData("companion")][InlineData("oversized")][InlineData("cancel")]
    public void UntrustedMalformedOrUnsupportedPacketsCannotBecomeOperationalFacts(string fault)
    {
        var source=Source();var xml=source.XmlOriginal;var aixm=source.AixmOriginal;
        var text=Encoding.UTF8.GetString(xml);
        if(fault=="doctype")xml=Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///not-permitted'>]>"+text.TrimStart('\uFEFF'));
        if(fault=="version")xml=Encoding.UTF8.GetBytes(text.Replace("version=\"0.1\"","version=\"9.0\""));
        if(fault=="identity")xml=Encoding.UTF8.GetBytes(text.Replace("<noSeqNo>2070</noSeqNo>","<noSeqNo>2071</noSeqNo>"));
        if(fault=="cancel")xml=Encoding.UTF8.GetBytes(text.Replace("<Add>","<Cancel>").Replace("</Add>","</Cancel>"));
        if(fault=="oversized")xml=new byte[AviationTfrOperations.MaximumBytes+1];
        if(fault=="companion")aixm=TfrOriginalFiles.Packet("6/2069",true);
        if(fault is "version" or "cancel")Assert.Throws<NotSupportedException>(()=>AviationTfrOperations.Parse(Source(xml:xml,aixm:aixm)));
        else Assert.Throws<InvalidDataException>(()=>AviationTfrOperations.Parse(Source(xml:xml,aixm:aixm)));
    }
    [Fact]public async Task ActualCacheScreensTimeAndDatumWithoutClearanceThenRestartsOfflineWithoutNetworkOrMissionMutation()
    {
        var h=new Transport();var clock=new Clock();
        using(var service=Service(h,clock))
        {
            service.Configure(true);await service.RefreshTfr();await service.RefreshTfrOperations("6/2070");Assert.Equal(5,h.Calls);
            var view=service.TfrOperations("6/2070");Assert.Contains("Recently",view.Status);Assert.Contains("Upcoming",view.Areas.Single().WindowStatus);Assert.Null(view.Areas.Single().Polygons);
            var b=service.TfrBoundaries(-99,26,-96,29).Boundaries.Single(b=>b.NotamId=="6/2070");Assert.NotNull(b.Operational);Assert.Contains("1419",b.SegmentStatus);
            var request=new AviationTfrScreenRequest(1,[Point(b)],PlannedStartUtc:DateTimeOffset.Parse("2026-10-27T17:00:00Z"),PlannedEndUtc:DateTimeOffset.Parse("2026-10-27T18:00:00Z"),MinimumMslFeet:500,MaximumMslFeet:1000,TimeBasis:"Real-world planned UTC");
            var result=service.ScreenTfr(request);var f=Assert.Single(result.Findings!,f=>f.NotamId=="6/2070");Assert.Equal("Intersects published UTC window",f.Time);Assert.Equal("Unknown",f.Altitude);Assert.Equal("Potential intersection",result.Outcome);
            f=service.ScreenTfr(request with{TimeBasis="Simulator time"}).Findings!.Single(f=>f.NotamId=="6/2070");Assert.Equal("Unknown",f.Time);
            result=service.ScreenTfr(request with{PlannedStartUtc=request.PlannedStartUtc!.Value.AddDays(2),PlannedEndUtc=request.PlannedEndUtc!.Value.AddDays(2)});Assert.Contains(result.Findings!,f=>f.NotamId=="6/2070"&&f.Time=="Outside published UTC windows");Assert.NotEqual("No intersection",result.Outcome);Assert.False(result.SnapshotComplete);
            clock.Now=DateTimeOffset.Parse("2026-10-27T17:00:00Z");Assert.Contains("Unknown",service.TfrOperations("6/2070").Areas.Single().WindowStatus); // Old evidence is never current activation.
            service.Configure(false);
        }
        var calls=h.Calls;using var offline=Service(h,clock);Assert.NotEmpty(offline.TfrOperations("6/2070").Areas);Assert.Contains("offline",offline.TfrOperations("6/2070").Status);Assert.Equal(calls,h.Calls);Assert.False(File.Exists(Path.Combine(root,"missions.db")));
    }
    [Fact]public async Task GenuineMslUpperLimitCanExcludeAnAltitudeWithoutGuessingRecurringTimeOrAgl()
    {
        var h=new Transport();var clock=new Clock();using var s=Service(h,clock);s.Configure(true);await s.RefreshTfr();await s.RefreshTfrOperations("6/2014");
        var b=Geometry().Boundaries.Single(b=>b.NotamId=="6/2014");var r=s.ScreenTfr(new(1,[Point(b)],MinimumMslFeet:8000,MaximumMslFeet:9000));var f=r.Findings!.Single(f=>f.NotamId==b.NotamId);
        Assert.Equal("Outside published MSL limits",f.Altitude);Assert.Equal("Unknown",f.Time);Assert.Equal("Unable to determine",r.Outcome);Assert.False(r.SnapshotComplete);
        f=s.ScreenTfr(new(1,[Point(b)],MinimumMslFeet:6000,MaximumMslFeet:7000)).Findings!.Single(f=>f.NotamId==b.NotamId);Assert.Equal("Intersects published MSL limits",f.Altitude);
    }
    [Theory][InlineData(HttpStatusCode.Redirect,"InvalidMetadata")][InlineData(HttpStatusCode.TooManyRequests,"RateLimited")][InlineData(HttpStatusCode.ServiceUnavailable,"ProviderUnavailable")]
    public async Task FailurePreservesOriginalsButPreventsFreshClearanceAndRespectsBackoff(HttpStatusCode status,string code)
    {
        var h=new Transport();var clock=new Clock();using var s=Service(h,clock);s.Configure(true);await s.RefreshTfr();var before=await s.RefreshTfrOperations("6/2070");clock.Now=clock.Now.AddMinutes(6);h.Status=status;
        var failed=await s.RefreshTfrOperations("6/2070");Assert.Equal(code,failed.Error);Assert.Equal(before.XmlSha256,failed.XmlSha256);Assert.Contains("Stale",failed.Status);Assert.Contains("Unknown",failed.Areas.Single().WindowStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.RefreshTfrOperations("6/2070"));
        clock.Now=clock.Now.AddMinutes(6);h.Status=HttpStatusCode.OK;
        if(status==HttpStatusCode.TooManyRequests)await Assert.ThrowsAsync<InvalidOperationException>(()=>s.RefreshTfrOperations("6/2070"));
        else Assert.Null((await s.RefreshTfrOperations("6/2070")).Error);
    }
    [Fact]public async Task NoticeRevisionChangesInvalidateOperationalEvidenceAndPathInjectionNeverReachesNetwork()
    {
        var h=new Transport();var clock=new Clock();using var s=Service(h,clock);s.Configure(true);await s.RefreshTfr();await s.RefreshTfrOperations("6/2070");clock.Now=clock.Now.AddMinutes(6);h.RevisionChanged=true;await s.RefreshTfr();
        var view=s.TfrOperations("6/2070");Assert.Contains("revision changed",view.Status);Assert.Empty(view.Areas);
        var calls=h.Calls;await Assert.ThrowsAsync<ArgumentException>(()=>s.RefreshTfrOperations("../../secret"));await Assert.ThrowsAsync<ArgumentException>(()=>s.RefreshTfrOperations("6/2070\n"));await Assert.ThrowsAsync<InvalidOperationException>(()=>s.RefreshTfrOperations("6/0000"));Assert.Equal(calls,h.Calls);
    }
    [Fact]public async Task UnsupportedCancellationPacketDoesNotEraseHistoryOrPretendOldEvidenceIsCurrent()
    {
        var h=new Transport();var clock=new Clock();using var s=Service(h,clock);s.Configure(true);await s.RefreshTfr();var previous=await s.RefreshTfrOperations("6/2070");clock.Now=clock.Now.AddMinutes(6);
        h.XmlChange=bytes=>Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("<Add>","<Cancel>").Replace("</Add>","</Cancel>"));
        var failed=await s.RefreshTfrOperations("6/2070");Assert.Equal("InvalidMetadata",failed.Error);Assert.Equal(previous.XmlSha256,failed.XmlSha256);Assert.Contains("Unknown",failed.Areas.Single().WindowStatus);
        h.XmlChange=null;clock.Now=clock.Now.AddMinutes(6);Assert.Null((await s.RefreshTfrOperations("6/2070")).Error);
    }
    [Fact]public void ContradictoryCompanionAltitudeIsRejectedAndUnsupportedGeometryCannotAcquireAnAreaJoin()
    {
        var doc=XDocument.Load(new MemoryStream(TfrOriginalFiles.Packet("6/2070",true)));XNamespace a="http://www.aixm.aero/schema/5.0";doc.Descendants(a+"upperLimit").Single().Value="3000";
        using var output=new MemoryStream();doc.Save(output);Assert.Throws<InvalidDataException>(()=>AviationTfrOperations.Parse(Source(aixm:output.ToArray())));
        var xml=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(TfrOriginalFiles.Packet("6/2070")).Replace("<codeType>GRC</codeType>","<codeType>UNKNOWN</codeType>"));
        var parsed=AviationTfrOperations.Parse(Source(xml:xml));Assert.Null(parsed.Areas.Single().Polygons);Assert.Null(AviationTfrOperations.Match(Geometry().Boundaries.Single(b=>b.NotamId=="6/2070"),parsed));
    }
    public void Dispose(){try{Directory.Delete(root,true);}catch(IOException){}}
}
