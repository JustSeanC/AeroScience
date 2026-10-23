using System.Net;
using System.Xml;
using AeroScience.Engine;
using AeroScience.Missions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

public sealed class AviationAirspaceTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-airspace",Guid.NewGuid().ToString("N"));
    private sealed class Transport(bool saa=false,string? fault=null):HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;var url=request.RequestUri!.AbsoluteUri;
            if(url==AviationCatalog.NasrUrl)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<a href='/NASR_Subscription/2026-10-01/'>cycle</a>")});
            if(url.StartsWith(AviationCatalog.NasrUrl,StringComparison.Ordinal))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<a href='"+(saa?SyntheticAirspace.SaaUrl:SyntheticAirspace.ClassUrl)+"'>Official fixture</a>")});
            Assert.Equal(saa?SyntheticAirspace.SaaUrl:SyntheticAirspace.ClassUrl,url);
            var b=saa?SyntheticAirspace.SaaArchive(fault):SyntheticAirspace.ClassArchive(fault);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(b)});
        }
    }
    private async Task<AeroScience.Shared.AviationDownloadJob> Acquire(AviationReference service)
    {
        service.Configure(true);var page=await service.Refresh("nasr");var item=Assert.Single(page.Charts);
        var preview=await service.PreviewDownload("nasr",item.Id,1048576,1048576);service.DownloadControl(preview.Id,preview.Revision,"approve",preview.Preview.SelectionHash);
        await service.ProcessDownloads();return Assert.Single(service.DownloadJobs());
    }
    [Fact] public async Task ActualClassImportPreservesHolesMultipartAltitudeOriginalsAndOfflineQueries()
    {
        var handler=new Transport();string id;int calls;
        using(var service=new AviationReference(root,root,new HttpClient(handler),unmetered:()=>true))
        {
            var job=await Acquire(service);Assert.Equal("Completed",job.State);var d=await service.ImportVectors(job.Id);id=d.Id;
            Assert.Equal("CLASS",d.Family);Assert.Equal(2,d.Features);Assert.Equal(0,d.OmittedRecords);
            var page=service.AirspaceFeatures(id,-76,39,-74,41,"class-b");var feature=Assert.Single(page.Features);
            Assert.Equal(2,Assert.Single(feature.Polygons).Length);Assert.Equal(10,feature.OriginalVertices);
            Assert.Equal("Known",feature.Lower.Status);Assert.Equal(0,feature.Lower.Value);Assert.Equal("Above surface",feature.Lower.Reference);Assert.Equal(10000,feature.Upper.Value);Assert.Equal("MSL",feature.Upper.Reference);
            Assert.Contains("activation is unverified",feature.Schedule.Interpretation);
            Assert.Empty(service.AirspaceFeatures(id,-75.2,39.8,-74.8,40.2).Features); // Viewport entirely inside source hole.
            var ca=Assert.Single(service.AirspaceFeatures(id,-124,35,-118,39).Features);Assert.Equal(2,ca.Polygons.Length);Assert.Null(ca.Upper.Value);Assert.Equal("Unknown",ca.Upper.Status);Assert.Equal("-9998",ca.Upper.RawValue);
            Assert.True(page.ReturnedVertices<=25000);Assert.Equal(job.OriginalSha256,d.OriginalSha256);
            Assert.Equal(id,(await service.ImportVectors(job.Id)).Id);await service.VerifyDownload(job.Id);service.Configure(false);calls=handler.Calls;
            Assert.Throws<ArgumentException>(()=>service.AirspaceFeatures(id,-180,-80,180,80));Assert.Throws<ArgumentException>(()=>service.VectorFeatures(id,-76,39,-74,41));
        }
        using var reopened=new AviationReference(root,root,new HttpClient(handler));Assert.Single(reopened.AirspaceFeatures(id,-76,39,-74,41).Features);Assert.Equal(calls,handler.Calls);
        Assert.False(File.Exists(Path.Combine(root,"missions.db")));Assert.False(Directory.Exists(Path.Combine(root,"ScientificLibrary")));
    }
    [Fact] public async Task VerifiedOfficialNestedSaaContractImportsLiteralGeometryAndPreservesActivationUncertainty()
    {
        using var service=new AviationReference(root,root,new HttpClient(new Transport(true)),unmetered:()=>true);var job=await Acquire(service);
        Assert.Equal("Completed",job.State);Assert.Equal(2,job.Files!.Length);var d=await service.ImportVectors(job.Id);Assert.Equal("SUA",d.Family);
        var f=Assert.Single(service.AirspaceFeatures(d.Id,-76,39,-74,41).Features);Assert.Equal("sua",f.Kind);Assert.Equal("OGC:CRS84 (WGS84 longitude/latitude)",d.SourceCrs);
        Assert.Equal(0,f.Lower.Value);Assert.Equal("AVBL_FOR_ACTIVATION",f.Attributes["ACTIVATION_SOURCE_STATUS"]);Assert.Equal("NOTAM",f.Schedule.Code);Assert.Contains("Unknown",f.Schedule.Interpretation);
        Assert.Equal("2020-01-01T09:01:00Z",f.Attributes["DEFINITION_BEGIN"]);Assert.Contains("activation",d.Limitations.Single(x=>x.Contains("certified",StringComparison.Ordinal)));
    }
    [Theory] [InlineData("index")] [InlineData("huge-count")] [InlineData("crs")]
    public async Task InvalidClassIndexDimensionsOrUnverifiedCrsCannotPublish(string fault)
    {
        using var service=new AviationReference(root,root,new HttpClient(new Transport(false,fault)),unmetered:()=>true);var job=await Acquire(service);Assert.Equal("Completed",job.State);
        if(fault=="crs")await Assert.ThrowsAsync<NotSupportedException>(()=>service.ImportVectors(job.Id));else await Assert.ThrowsAsync<InvalidDataException>(()=>service.ImportVectors(job.Id));
        Assert.Empty(service.VectorDatasets());Assert.Equal("Completed",Assert.Single(service.DownloadJobs()).State);
    }
    [Theory] [InlineData("composite")] [InlineData("curve")]
    public async Task UnsupportedSaaOperationsAreExplicitOmissionsRatherThanMisleadingPolygons(string fault)
    {
        using var service=new AviationReference(root,root,new HttpClient(new Transport(true,fault)),unmetered:()=>true);var job=await Acquire(service);var d=await service.ImportVectors(job.Id);
        Assert.Equal(1,d.Features);Assert.Equal(1,d.OmittedRecords);Assert.Single(service.AirspaceFeatures(d.Id,-76,39,-74,41).Features);
        Assert.Contains(d.Limitations,x=>x.Contains(fault=="composite"?"SUBTR/UNION":"Curved SAA",StringComparison.Ordinal));await service.VerifyDownload(job.Id);
    }
    [Fact] public async Task AirspaceIndexingYieldsToRecordingAndOriginalsRemainReusable()
    {
        var flying=false;using var service=new AviationReference(root,root,new HttpClient(new Transport()),unmetered:()=>true,flightActive:()=>flying);var job=await Acquire(service);flying=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ImportVectors(job.Id));Assert.Empty(service.VectorDatasets());await service.VerifyDownload(job.Id);
        flying=false;Assert.Equal(2,(await service.ImportVectors(job.Id)).Features);
    }
    [Theory] [InlineData("traversal")] [InlineData("nested")]
    public async Task SaaNestingIsExactAndDoesNotEnableArbitraryNestedArchiveExtraction(string fault)
    {
        using var service=new AviationReference(root,root,new HttpClient(new Transport(true,fault)),unmetered:()=>true);var job=await Acquire(service);Assert.Equal("Failed",job.State);Assert.Empty(service.VectorDatasets());
    }
    [Theory] [InlineData("dtd")] [InlineData("axis")]
    public async Task UnsafeXmlAndAmbiguousSaaAxisOrderCannotPublish(string fault)
    {
        using var service=new AviationReference(root,root,new HttpClient(new Transport(true,fault)),unmetered:()=>true);var job=await Acquire(service);Assert.Equal("Completed",job.State);
        if(fault=="dtd")await Assert.ThrowsAsync<XmlException>(()=>service.ImportVectors(job.Id));else await Assert.ThrowsAsync<NotSupportedException>(()=>service.ImportVectors(job.Id));Assert.Empty(service.VectorDatasets());
    }
    [Fact] public async Task SchemaThreeBackupPreservesApprovalAndOriginalIdentity()
    {
        using var service=new AviationReference(root,root,new HttpClient(new Transport()),unmetered:()=>true);var job=await Acquire(service);var folder=Path.Combine(root,"AviationReference");
        using(var db=new SqliteConnection("Data Source="+Path.Combine(folder,"aviation.db")+";Pooling=False")){db.Open();using var c=db.CreateCommand();c.CommandText="DROP TABLE airspace_features; PRAGMA user_version=3";c.ExecuteNonQuery();}
        var d=await service.ImportVectors(job.Id);Assert.Equal(job.OriginalSha256,d.OriginalSha256);Assert.Equal(job.Id,Assert.Single(service.DownloadJobs()).Id);
        using var backup=new SqliteConnection("Data Source="+Assert.Single(Directory.GetFiles(Path.Combine(folder,"Backups"),"aviation-schema3-*.sqlite"))+";Mode=ReadOnly;Pooling=False");backup.Open();using var check=backup.CreateCommand();check.CommandText="PRAGMA user_version";Assert.Equal(3L,check.ExecuteScalar());check.CommandText="PRAGMA integrity_check";Assert.Equal("ok",check.ExecuteScalar());
    }
    [Fact] public async Task MissingDriveAndOversizedPersistedDisplayFailClosed()
    {
        using var service=new AviationReference(root,root,new HttpClient(new Transport()),unmetered:()=>true);var job=await Acquire(service);var d=await service.ImportVectors(job.Id);_ = service.AirspaceFeatures(d.Id,-76,39,-74,41);
        var folder=Path.Combine(root,"AviationReference");Directory.Move(folder,folder+"-offline");Assert.Throws<IOException>(()=>service.AirspaceFeatures(d.Id,-76,39,-74,41));Assert.False(Directory.Exists(folder));Directory.Move(folder+"-offline",folder);
        using var db=new SqliteConnection("Data Source="+Path.Combine(folder,"aviation.db")+";Pooling=False");db.Open();using var c=db.CreateCommand();c.CommandText="UPDATE airspace_features SET display=zeroblob(600000)";c.ExecuteNonQuery();Assert.Throws<IOException>(()=>service.AirspaceFeatures(d.Id,-76,39,-74,41));
    }
    [Theory] [InlineData("FT","MSL","1000","Known")] [InlineData("FL","STD","180","Known")] [InlineData("FT","SFC","GND","Known")] [InlineData("","","-9998","Unknown")] [InlineData("FT","","500","Unknown")] [InlineData("FT","MSL","UNL","Published unlimited")]
    public void AltitudeNeverTurnsSentinelsOrMissingDatumIntoKnownLimits(string unit,string reference,string raw,string expected)
    {var limit=AviationAirspace.Altitude(new(){["UPPER_VAL"]=raw,["UPPER_UOM"]=unit,["UPPER_CODE"]=reference},"UPPER");Assert.Equal(expected,limit.Status);if(raw=="-9998")Assert.Null(limit.Value);}
    public void Dispose(){try{Directory.Delete(root,true);}catch(IOException){}}
}
