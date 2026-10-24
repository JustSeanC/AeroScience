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
    [Fact] public async Task ExplicitFullSaaCircleUsesOriginalUnitsAndRetainsOfflineProvenance()
    {
        var handler=new Transport(true,"circle");string id;int calls;
        using(var service=new AviationReference(root,root,new HttpClient(handler),unmetered:()=>true))
        {
            var job=await Acquire(service);var d=await service.ImportVectors(job.Id);id=d.Id;
            var feature=Assert.Single(service.AirspaceFeatures(id,-76,39,-74,41).Features);
            var ring=Assert.Single(Assert.Single(feature.Polygons));
            Assert.True(ring.Length is >=36 and <=4097);Assert.Equal(ring[0],ring[^1]);
            foreach(var point in ring)Assert.InRange(Geodesy.Distance(new(40,-75),point),1851.999999,1852.000001);
            Assert.InRange(ring[0].Latitude,40.0166794,40.0166795);Assert.InRange(ring[0].Longitude,-75.000000001,-74.999999999);
            Assert.Contains("0.5 m",feature.Attributes["GEOMETRY_INTERPOLATION"]);Assert.Equal(job.OriginalSha256,d.OriginalSha256);
            Assert.Contains(d.Limitations,x=>x.Contains("adapter 4",StringComparison.Ordinal));await service.VerifyDownload(job.Id);
            Assert.Equal(id,(await service.ImportVectors(job.Id)).Id);service.Configure(false);calls=handler.Calls;
        }
        using var reopened=new AviationReference(root,root,new HttpClient(handler));
        Assert.Single(reopened.AirspaceFeatures(id,-76,39,-74,41).Features);Assert.Equal(calls,handler.Calls);
    }
    [Theory] [InlineData("circle-unit")] [InlineData("circle-negative")] [InlineData("circle-mixed")] [InlineData("circle-dateline")]
    public async Task UnverifiedCircleUnitsMixedSegmentsAndUnsupportedBoundsCannotPublish(string fault)
    {
        using var service=new AviationReference(root,root,new HttpClient(new Transport(true,fault)),unmetered:()=>true);var job=await Acquire(service);
        var error = await Assert.ThrowsAsync<InvalidDataException>(()=>service.ImportVectors(job.Id));Assert.Contains("No supported valid airspace polygons", error.Message);Assert.Empty(service.VectorDatasets());await service.VerifyDownload(job.Id);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SchemaFourLiteralInterpretationAndOriginalSurviveVersionedReimport(bool interruptMigration)
    {
        var handler=new Transport(true);Guid jobId;string oldId;string oldJson;string originalHash;
        using(var service=new AviationReference(root,root,new HttpClient(handler),unmetered:()=>true))
        {
            var job=await Acquire(service);jobId=job.Id;originalHash=job.OriginalSha256!;var d=await service.ImportVectors(jobId);
            oldId=AviationDownloads.Hash(new{airspaceParser=1,job.Preview.Item.Id,job.OriginalSha256});
            oldJson=System.Text.Json.JsonSerializer.Serialize(d with{Id=oldId,Limitations=["Synthetic retained adapter-1 literal interpretation"]},AeroScience.Shared.WireJson.Options);
        }
        var folder=Path.Combine(root,"AviationReference");var file=Path.Combine(folder,"aviation.db");
        using(var db=new SqliteConnection("Data Source="+file+";Pooling=False"))
        {
            db.Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;
            c.CommandText="CREATE TABLE schema4_datasets(id TEXT PRIMARY KEY,job_id TEXT NOT NULL UNIQUE,payload TEXT NOT NULL); INSERT INTO schema4_datasets SELECT $old,job_id,$json FROM vector_datasets; DROP TABLE vector_datasets; ALTER TABLE schema4_datasets RENAME TO vector_datasets; UPDATE airspace_features SET dataset_id=$old,id=$feature; PRAGMA user_version=4;";
            c.Parameters.AddWithValue("$old",oldId);c.Parameters.AddWithValue("$json",oldJson);c.Parameters.AddWithValue("$feature",AviationDownloads.Hash(new{id=oldId,Number=1}));c.ExecuteNonQuery();
            if(interruptMigration){c.CommandText="CREATE TABLE vector_datasets_v5(sentinel TEXT)";c.ExecuteNonQuery();}tx.Commit();
        }
        if(interruptMigration)
        {
            Assert.Throws<SqliteException>(()=>new AviationReference(root,root,new HttpClient(handler)).VectorDatasets());
            using var db=new SqliteConnection("Data Source="+file+";Pooling=False");db.Open();using var c=db.CreateCommand();c.CommandText="PRAGMA user_version";Assert.Equal(4L,c.ExecuteScalar());
            c.CommandText="SELECT payload FROM vector_datasets";Assert.Equal(oldJson,c.ExecuteScalar());return;
        }
        using(var service=new AviationReference(root,root,new HttpClient(handler)))
        {
            var calls=handler.Calls;var upgraded=await service.ImportVectors(jobId);Assert.NotEqual(oldId,upgraded.Id);Assert.Equal(2,service.VectorDatasets().Length);
            Assert.Single(service.AirspaceFeatures(oldId,-76,39,-74,41).Features);Assert.Single(service.AirspaceFeatures(upgraded.Id,-76,39,-74,41).Features);
            Assert.Equal(originalHash,upgraded.OriginalSha256);Assert.Equal(calls,handler.Calls);await service.VerifyDownload(jobId);
            Assert.Equal(upgraded.Id,(await service.ImportVectors(jobId)).Id);
        }
        var backup=Assert.Single(Directory.GetFiles(Path.Combine(folder,"Backups"),"aviation-schema4-*.sqlite"));
        using var saved=new SqliteConnection("Data Source="+backup+";Mode=ReadOnly;Pooling=False");saved.Open();using var check=saved.CreateCommand();
        check.CommandText="PRAGMA integrity_check";Assert.Equal("ok",check.ExecuteScalar());check.CommandText="PRAGMA user_version";Assert.Equal(4L,check.ExecuteScalar());
        check.CommandText="SELECT payload FROM vector_datasets";Assert.Equal(oldJson,check.ExecuteScalar());
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
    [Fact] public async Task ExplicitMinorArcPreservesLiteralVerticesAndIndependentWgs84SamplesOffline()
    {
        var handler = new Transport(true, "arc"); string id; int calls;
        using (var service = new AviationReference(root, root, new HttpClient(handler), unmetered: () => true))
        {
            var job = await Acquire(service); var dataset = await service.ImportVectors(job.Id); id = dataset.Id;
            Assert.Equal(2, dataset.Features); Assert.Equal(0, dataset.OmittedRecords);
            var feature = Assert.Single(service.AirspaceFeatures(id, -76, 39, -74, 41).Features, f => f.Attributes.ContainsKey("GEOMETRY_INTERPOLATION"));
            var points = Assert.Single(Assert.Single(feature.Polygons));
            Assert.Contains(points, p => p.Longitude == -74.978312 && p.Latitude == 39.999998);
            Assert.Contains(points, p => p.Longitude == -75 && p.Latitude == 40);
            // Retain the rounded original endpoints. Every derived arc interior is
            // one nautical mile from its original center, independently checked above.
            Assert.Contains(points, p => p.Latitude == 40.016679 && p.Longitude == -75);
            foreach (var point in points.Where(p => p.Latitude != 40 || p.Longitude != -75))
                Assert.InRange(Geodesy.Distance(new(40, -75), point), 1851.9, 1852.1);
            Assert.Equal(points[0], points[^1]); Assert.Contains("CRS84", feature.Attributes["GEOMETRY_INTERPOLATION"]);
            await service.VerifyDownload(job.Id); service.Configure(false); calls = handler.Calls;
        }
        using var reopened = new AviationReference(root, root, new HttpClient(handler));
        Assert.Equal(2, reopened.AirspaceFeatures(id, -76, 39, -74, 41).Features.Length); Assert.Equal(calls, handler.Calls);
    }
    [Fact] public async Task SchemaFiveKeepsPriorCircleInterpretationDuringAdapterThreeReimport()
    {
        var handler = new Transport(true, "circle"); Guid jobId; string oldId; string oldPayload;
        using (var service = new AviationReference(root, root, new HttpClient(handler), unmetered: () => true))
        {
            var job = await Acquire(service); jobId = job.Id; var current = await service.ImportVectors(jobId);
            oldId = AviationDownloads.Hash(new { airspaceParser = 2, job.Preview.Item.Id, job.OriginalSha256 });
            oldPayload = System.Text.Json.JsonSerializer.Serialize(current with { Id = oldId, Limitations = ["Synthetic retained circle adapter 2"] }, AeroScience.Shared.WireJson.Options);
            using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "AviationReference", "aviation.db") + ";Pooling=False"); db.Open(); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "UPDATE vector_datasets SET id=$old,payload=$json WHERE id=$current; UPDATE airspace_features SET dataset_id=$old WHERE dataset_id=$current";
            command.Parameters.AddWithValue("$old", oldId); command.Parameters.AddWithValue("$json", oldPayload); command.Parameters.AddWithValue("$current", current.Id); command.ExecuteNonQuery(); tx.Commit();
        }
        using var reopened = new AviationReference(root, root, new HttpClient(handler)); var calls = handler.Calls;
        var next = await reopened.ImportVectors(jobId); Assert.NotEqual(oldId, next.Id); Assert.Equal(2, reopened.VectorDatasets().Length);
        Assert.Single(reopened.AirspaceFeatures(oldId, -76, 39, -74, 41).Features); Assert.Single(reopened.AirspaceFeatures(next.Id, -76, 39, -74, 41).Features);
        Assert.Equal(oldPayload, System.Text.Json.JsonSerializer.Serialize(reopened.VectorDatasets().Single(d => d.Id == oldId), AeroScience.Shared.WireJson.Options));
        Assert.Equal(calls, handler.Calls); await reopened.VerifyDownload(jobId);
    }
    [Theory] [InlineData("arc-unit")] [InlineData("arc-wrap")] [InlineData("arc-gap")] [InlineData("arc-link")]
    [InlineData("arc-axis")] [InlineData("arc-major")] [InlineData("arc-reverse")]
    public async Task UnverifiedArcSemanticsNeverPublishGuessedOrRepairedBoundaries(string fault)
    {
        using var service = new AviationReference(root, root, new HttpClient(new Transport(true, fault)), unmetered: () => true);
        var job = await Acquire(service); var dataset = await service.ImportVectors(job.Id);
        Assert.Equal(1, dataset.Features); Assert.Equal(1, dataset.OmittedRecords);
        Assert.Contains(dataset.Limitations, x => x.Contains("Curved SAA omitted", StringComparison.Ordinal)); await service.VerifyDownload(job.Id);
    }
    [Fact] public async Task AirspaceIndexingYieldsToRecordingAndOriginalsRemainReusable()
    {
        var flying=false;using var service=new AviationReference(root,root,new HttpClient(new Transport()),unmetered:()=>true,flightActive:()=>flying);var job=await Acquire(service);flying=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ImportVectors(job.Id));Assert.Empty(service.VectorDatasets());await service.VerifyDownload(job.Id);
        flying=false;Assert.Equal(2,(await service.ImportVectors(job.Id)).Features);
    }
    [Theory] [InlineData("arc-unwrapped")] [InlineData("arc-adjacent")]
    public async Task ExplicitUnwrappedAnglesAndAdjacentMinorArcsKeepLiteralVerticesAndRadius(string variant)
    {
        using var service=new AviationReference(root,root,new HttpClient(new Transport(true,variant)),unmetered:()=>true);
        var job=await Acquire(service);var d=await service.ImportVectors(job.Id);Assert.Equal(2,d.Features);Assert.Equal(0,d.OmittedRecords);
        var f=Assert.Single(service.AirspaceFeatures(d.Id,-76,39,-74,41).Features,x=>x.Attributes.ContainsKey("GEOMETRY_INTERPOLATION"));
        var ring=Assert.Single(Assert.Single(f.Polygons));Assert.Equal(ring[0],ring[^1]);
        Assert.Contains(ring,p=>p.Longitude==-74.978312&&p.Latitude==39.999998);
        foreach(var p in ring.Where(p=>p.Latitude!=40||p.Longitude!=-75))Assert.InRange(Geodesy.Distance(new(40,-75),p),1851.9,1852.1);
        Assert.Contains("increasing explicit minor span",f.Attributes["GEOMETRY_INTERPOLATION"]);await service.VerifyDownload(job.Id);
    }
    [Theory] [InlineData("composite-subtract",1,2)] [InlineData("composite-union",2,2)]
    [InlineData("composite-split",2,2)] [InlineData("composite-nested",2,3)]
    public async Task OrderedSameVolumeCompositesPreserveHolesMultipartAndOfflineOriginals(string variant,int pieces,int rings)
    {
        var handler=new Transport(true,variant);string id;int calls;string original;
        using(var service=new AviationReference(root,root,new HttpClient(handler),unmetered:()=>true))
        {
            var job=await Acquire(service);original=job.OriginalSha256!;var d=await service.ImportVectors(job.Id);id=d.Id;
            Assert.Equal(2,d.Features);Assert.Equal(0,d.OmittedRecords);
            var feature=Assert.Single(service.AirspaceFeatures(id,-77,38,-70,42).Features,f=>f.Attributes.ContainsKey("GEOMETRY_COMPOSITION"));
            Assert.Equal(pieces,feature.Polygons.Length);Assert.Equal(rings,feature.Polygons.Sum(p=>p.Length));
            Assert.Equal("MSL",feature.Upper.Reference);Assert.Equal(10000,feature.Upper.Value);Assert.Equal("Above surface",feature.Lower.Reference);
            using var manifest=System.Text.Json.JsonDocument.Parse(feature.Attributes["SOURCE_COMPONENTS"]);
            var components=manifest.RootElement.GetProperty("components");Assert.Equal(10,components[0].GetProperty("sequence").GetInt32());
            Assert.Equal(2,components[0].GetProperty("sourceComponent").GetInt32()); // XML order is not operation order.
            Assert.Equal("BASE",components[0].GetProperty("operation").GetString());
            Assert.Contains("Activation and UTC intervals are Unknown",feature.Schedule.Interpretation);Assert.Equal(original,d.OriginalSha256);
            if(variant=="composite-subtract")Assert.DoesNotContain(service.AirspaceFeatures(id,-75.2,39.8,-74.8,40.2).Features,f=>f.Id==feature.Id);
            if(variant=="composite-nested")
            {Assert.DoesNotContain(service.AirspaceFeatures(id,-75.4,39.6,-75.3,39.7).Features,f=>f.Id==feature.Id);Assert.Contains(service.AirspaceFeatures(id,-75.1,39.9,-74.9,40.1).Features,f=>f.Id==feature.Id);}
            await service.VerifyDownload(job.Id);service.Configure(false);calls=handler.Calls;
        }
        using var reopened=new AviationReference(root,root,new HttpClient(handler));
        Assert.Equal(2,reopened.AirspaceFeatures(id,-77,38,-70,42).Features.Length);Assert.Equal(calls,handler.Calls);
        Assert.Equal(original,reopened.VectorDatasets().Single(d=>d.Id==id).OriginalSha256);
    }
    [Theory] [InlineData("composite-vertical")] [InlineData("composite-unknown")] [InlineData("composite-duplicate")]
    [InlineData("composite-base-order")] [InlineData("composite-empty")] [InlineData("composite-axis")]
    [InlineData("composite-missing")] [InlineData("composite-cycle")] [InlineData("composite-excess")]
    [InlineData("composite-self-intersect")] [InlineData("composite-dateline")] [InlineData("composite-polar")] [InlineData("composite-work")]
    public async Task UnsafeCompositesAreIsolatedOmissionsWithoutFlatteningOrResolvingReferences(string variant)
    {
        using var service=new AviationReference(root,root,new HttpClient(new Transport(true,variant)),unmetered:()=>true);
        var job=await Acquire(service);var d=await service.ImportVectors(job.Id);Assert.Equal(1,d.Features);Assert.Equal(1,d.OmittedRecords);
        Assert.Single(service.AirspaceFeatures(d.Id,-77,38,-70,42).Features);
        Assert.Contains(d.Limitations,s=>s.Contains("Composite SAA omitted",StringComparison.Ordinal));await service.VerifyDownload(job.Id);
    }
    [Fact] public async Task CurvedCompositeWorkYieldsMidImportAndRollsBackWithoutRedownloading()
    {
        var armed=false;var checks=0;var handler=new Transport(true,"arc-adjacent");
        using var service=new AviationReference(root,root,new HttpClient(handler),unmetered:()=>true,flightActive:()=>armed&&++checks>20);
        var job=await Acquire(service);var calls=handler.Calls;armed=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ImportVectors(job.Id));Assert.Empty(service.VectorDatasets());
        armed=false;Assert.Equal(2,(await service.ImportVectors(job.Id)).Features);Assert.Equal(calls,handler.Calls);await service.VerifyDownload(job.Id);
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
