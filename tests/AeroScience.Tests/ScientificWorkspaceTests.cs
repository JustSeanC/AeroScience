using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;

// Small numeric fixtures prove algorithm behavior, not provider acceptance. Genuine retained-data comparisons are separate.
public sealed class ScientificWorkspaceTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-workspace",Guid.NewGuid().ToString("N"));
    private readonly MissionStore missions;private readonly ScienceStore science;private readonly ScientificSource source;private readonly Mission mission;private readonly ScienceDataset dataset;
    private readonly RasterGrid grid=new(4,4,"EPSG:4326",-110,35,.0001,-.0001);
    private readonly DateTimeOffset utc=new(2026,10,19,12,0,0,TimeSpan.Zero);
    public ScientificWorkspaceTests()
    {
        var preference=new PreferenceStore(root);var paths=new StorageManager(preference).Initialize();missions=new(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;
        var input=Path.Combine(root,"input");Directory.CreateDirectory(input);
        ScientificRaster.WriteGeoTiff(Path.Combine(input,"terrain.tif"),grid,[Enumerable.Range(0,16).Select(i=>(double?)(100+i)).ToArray()],["m"],new{fixture=true});
        var manifest=new ScientificSourceManifest(1,"Algorithm fixture",RemoteKind.Terrain,"Test fixture only","fixture","fixture","fixture-scene",utc.AddYears(-2),"fixture datum",[new("elevation","terrain.tif","geotiff",Unit:"m")]);
        source=science.ImportSourceAsync(manifest,input).GetAwaiter().GetResult();
        var f=SimulatedTelemetryProvider.Generate(utc,0) with{Latitude=34.9999,Longitude=-109.9999,AltitudeAglFeet=500};
        var id=missions.Create("Workspace",f,utc,ScienceMode.Realistic);mission=missions.Detail(id)!.Mission;missions.Append(id,f);missions.Finish(id,MissionStatus.Completed,utc.AddSeconds(1),"Fixture end");
        dataset=science.EnsureDataset(mission,new("terrain-scanner",true),missions.Research(id));
    }
    private RemoteProduct Add(int second,double? first=101,double? secondValue=null,bool withEvidence=false)
    {
        var id=Guid.NewGuid();var boundary=ScientificRaster.GridCoverage(grid);var footprint=new ObservationFootprint(ScientificRaster.GridPoint(grid,2,2),boundary,100,"EPSG:4326","fixture datum","fixture",[]);
        var p=new RemoteProduct(id,dataset.Id,science.LibraryId,mission.Id,"terrain-scanner",source.Id,utc.AddSeconds(second),utc,source.Manifest.ObservationUtc,footprint,$"Products/{mission.Id:N}/{id:N}.json",0,"",2,1,["elevation"],"numeric fixture","fixture",[]);
        var values=new double?[]{first,secondValue};return science.Publish(new(1,p,source,[ScientificRaster.GridPoint(grid,.5,.5),ScientificRaster.GridPoint(grid,1.5,.5)],[new("elevation","m",null,values,"nearest native pixel",grid)],new(){["elevation"]=values},["fixture"],withEvidence?new(1,SimulatedTelemetryProvider.Generate(utc.AddSeconds(second),0),null,new("terrain-scanner",true),null,ScienceMode.Realistic,"Synthetic fixture time"):null));
    }
    [Fact] public void CoverageUsesOnlySavedValidNativePixelsAndDuplicateAcquisitionsDoNotInflateArea()
    {
        Add(0);var plan=new MissionPlan(Guid.NewGuid(),"Survey",SurveyAreas:[new("Area",ScientificRaster.GridCoverage(grid))]);
        var before=ScientificCoverage.Calculate(science,plan,mission.Id,new("terrain-scanner"));
        Assert.Single(before.Sources);Assert.InRange(before.Sources[0].SurveyPercent!.Value,6.24,6.26);
        Assert.InRange(before.GeometricSurveyPercent!.Value,99.99,100.00001);
        Add(1);var after=ScientificCoverage.Calculate(science,plan,mission.Id,new("terrain-scanner"));
        Assert.Equal(before.Sources[0].ValidSupportSquareMetres,after.Sources[0].ValidSupportSquareMetres,6);
        Assert.Equal(2,after.Sources[0].SavedValidSamples);
        Add(2,secondValue:102);var extra=ScientificCoverage.Calculate(science,plan,mission.Id,new("terrain-scanner"));Assert.InRange(extra.Sources[0].SurveyPercent!.Value,12.49,12.51);
        Assert.Null(ScientificCoverage.Calculate(science,null,mission.Id,new("terrain-scanner")).SurveySquareMetres);
    }
    [Fact] public void SourceCapabilitiesDescribeRetainedValuesIndependentlyOfModeledHardware()
    {
        var terrain=ScientificCapabilities.Describe(source);Assert.Contains(ScientificCapability.Elevation,terrain.Capabilities);Assert.DoesNotContain(ScientificCapability.LidarPoints,terrain.Capabilities);
        var scanner=InstrumentProfiles.Catalog.Single(p=>p.Id=="riegl.vq-1560-ii-s.nominal");var thermal=InstrumentProfiles.Catalog.Single(p=>p.Id=="flir.a655sc.lens-25");
        Assert.True(ScientificCapabilities.Supports(new(scanner),source));Assert.False(ScientificCapabilities.Supports(new(thermal),source));
        var dsm=source with{Manifest=source.Manifest with{Provider="Microsoft Planetary Computer / Copernicus DEM"}};
        Assert.Contains(ScientificCapability.SurfaceModel,ScientificCapabilities.Describe(dsm).Capabilities);Assert.DoesNotContain(ScientificCapability.LidarPoints,ScientificCapabilities.Describe(dsm).Capabilities);
    }
    [Fact] public void CompositeDistinguishesSparseMissionSamplesFromRetainedSourceAndReusesVerifiedOutputs()
    {
        Add(0,999);Add(1,888);
        var sample=Assert.Single(ScientificComposites.Build(science,missions,mission.Id,new(source.Id,"elevation")));
        using(var raster=new ScientificRaster(Path.GetDirectoryName(science.VerifiedArtifactPath(sample))!,new("value",Path.GetFileName(sample.RelativePath),"geotiff")))
        {Assert.Equal(888,raster.Read(ScientificRaster.GridPoint(grid,.5,.5)));Assert.Null(raster.Read(ScientificRaster.GridPoint(grid,1.5,.5)));}
        var reused=Assert.Single(ScientificComposites.Build(science,missions,mission.Id,new(source.Id,"elevation")));Assert.Equal(sample.Id,reused.Id);
        var first=Assert.Single(ScientificComposites.Build(science,missions,mission.Id,new(source.Id,"elevation",Selection:"first")));
        using(var raster=new ScientificRaster(Path.GetDirectoryName(science.VerifiedArtifactPath(first))!,new("value",Path.GetFileName(first.RelativePath),"geotiff")))Assert.Equal(999,raster.Read(ScientificRaster.GridPoint(grid,.5,.5)));
        var mosaic=Assert.Single(ScientificComposites.Build(science,missions,mission.Id,new(source.Id,"elevation",Mode:"retained-source")));
        using(var raster=new ScientificRaster(Path.GetDirectoryName(science.VerifiedArtifactPath(mosaic))!,new("value",Path.GetFileName(mosaic.RelativePath),"geotiff")))Assert.Equal(101,raster.Read(ScientificRaster.GridPoint(grid,1.5,.5)));
        Assert.Equal("mission-samples",sample.Kind);Assert.Equal("retained-source",mosaic.Kind);
        Assert.Contains(source.ManifestSha256,sample.MetadataJson);
    }
    [Fact] public async Task CompatibleRegionalSourcesFormOneNativeMosaicAndIncompatibleDatesStaySeparate()
    {
        Add(0);var nextGrid=grid with{OriginX=grid.OriginX+grid.Width*grid.PixelWidth};var input=Path.Combine(root,"adjacent");Directory.CreateDirectory(input);
        ScientificRaster.WriteGeoTiff(Path.Combine(input,"terrain.tif"),nextGrid,[Enumerable.Range(0,16).Select(i=>(double?)(200+i)).ToArray()],["m"],new{fixture=true});
        var next=await science.ImportSourceAsync(source.Manifest,input);Assert.True(ScientificComposites.Compatible(source,next));
        var id=Guid.NewGuid();var footprint=new ObservationFootprint(ScientificRaster.GridPoint(nextGrid,2,2),ScientificRaster.GridCoverage(nextGrid),100,"EPSG:4326","fixture datum","fixture",[]);
        var p=new RemoteProduct(id,dataset.Id,science.LibraryId,mission.Id,"terrain-scanner",next.Id,utc.AddSeconds(1),utc,next.Manifest.ObservationUtc,footprint,$"Products/{mission.Id:N}/{id:N}.json",0,"",1,1,["elevation"],"fixture","fixture",[]);
        science.Publish(new(1,p,next,[ScientificRaster.GridPoint(nextGrid,.5,.5)],[new("elevation","m",null,[200],"native pixel",nextGrid)],new(){["elevation"]=[200]},["fixture"]));
        var artifact=Assert.Single(ScientificComposites.Build(science,missions,mission.Id,new(source.Id,"elevation",Mode:"retained-source")));
        var metadata=JsonSerializer.Deserialize<RasterProductMetadata>(artifact.MetadataJson,WireJson.Options)!;Assert.Equal(2,metadata.SourceIds!.Length);Assert.Equal(8,metadata.CompositeGrid!.Width);
        using var raster=new ScientificRaster(Path.GetDirectoryName(science.VerifiedArtifactPath(artifact))!,new("elevation",Path.GetFileName(artifact.RelativePath),"geotiff"));
        Assert.Equal(100,raster.Read(ScientificRaster.GridPoint(grid,.5,.5)));Assert.Equal(200,raster.Read(ScientificRaster.GridPoint(nextGrid,.5,.5)));
        Assert.False(ScientificComposites.Compatible(source,next with{Manifest=next.Manifest with{ObservationUtc=utc.AddYears(-1)}}));
        Assert.False(ScientificComposites.Compatible(source,next with{Manifest=next.Manifest with{VerticalDatum="different datum"}}));
        var sparse=Assert.Single(ScientificComposites.Build(science,missions,mission.Id,new(source.Id,"elevation")));using var samples=new ScientificRaster(Path.GetDirectoryName(science.VerifiedArtifactPath(sparse))!,new("elevation",Path.GetFileName(sparse.RelativePath),"geotiff"));
        Assert.Equal(101,samples.Read(ScientificRaster.GridPoint(grid,.5,.5)));Assert.Equal(200,samples.Read(ScientificRaster.GridPoint(nextGrid,.5,.5)));Assert.Null(samples.Read(ScientificRaster.GridPoint(grid,2.5,2.5)));
    }
    [Fact] public void AffineMultichannelGeoTiffPreservesRotatedGridValuesChannelsAndNodata()
    {
        var rotated=grid with{RowX=.00002,ColumnY=.00001};var path=Path.Combine(root,"rotated.tif");var values=Enumerable.Range(0,16).Select(i=>(double?)i).ToArray();values[3]=null;
        ScientificRaster.WriteGeoTiff(path,rotated,[values,values.Select(v=>v+100).ToArray()],["K","m"],new{test="rotated"});
        using var a=new ScientificRaster(root,new("a","rotated.tif","geotiff",Channel:0));using var b=new ScientificRaster(root,new("b","rotated.tif","geotiff",Channel:1));
        Assert.Equal(rotated,a.Grid);Assert.Equal(5,a.Read(ScientificRaster.GridPoint(rotated,1.5,1.5)));Assert.Equal(105,b.Read(ScientificRaster.GridPoint(rotated,1.5,1.5)));Assert.Null(a.Read(ScientificRaster.GridPoint(rotated,3.5,.5)));
    }
    [Fact] public async Task ResearchPackageIncludesNumericRastersIndependentTimestampsAndVerifiableHashes()
    {
        Add(0);_=ScientificComposites.Build(science,missions,mission.Id,new(source.Id,"elevation"));
        using var output=new MemoryStream();await ResearchPackage.WriteAsync(missions,science,mission.Id,new AsyncOnlyOutput(output));output.Position=0;using var zip=new ZipArchive(output,ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("environment.csv"));Assert.NotNull(zip.GetEntry("mission.json"));Assert.Contains(zip.Entries,e=>e.FullName.EndsWith(".tif"));
        using var manifestStream=zip.GetEntry("manifest.json")!.Open();using var manifest=await JsonDocument.ParseAsync(manifestStream);
        foreach(var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {using var stream=zip.GetEntry(file.GetProperty("path").GetString()!)!.Open();Assert.Equal(file.GetProperty("sha256").GetString(),Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());}
    }
    [Fact] public void AnalysisCancellationAndClearCoordinateAndPreserveOriginals()
    {
        Add(0);using(var lease=science.BeginAnalysis())Assert.Throws<InvalidOperationException>(()=>science.Clear("DELETE SCIENCE DATA",false));
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>ScientificComposites.Build(science,missions,mission.Id,new(source.Id,"elevation"),cancel.Token));
        Assert.Empty(science.Artifacts());_=ScientificComposites.Build(science,missions,mission.Id,new(source.Id,"elevation"));
        science.Clear("DELETE SCIENCE DATA",false);Assert.Empty(science.Artifacts());Assert.Single(science.Sources());
        Assert.All(source.Files,f=>Assert.True(File.Exists(ScientificRaster.SafeFile(Path.Combine(science.DirectoryPath,source.RelativeDirectory),f.RelativePath))));
    }
    [Fact] public void PreviousMissionAndScienceSchemasUpgradeWithVerifiedBackupsWithoutRewritingSavedPayloads()
    {
        Add(0);
        object? Sql(string path,string sql)
        {using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());db.Open();using var command=db.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
        EvidenceTestDatabase.RemoveSchemaEight(missions.DatabasePath);
        Sql(missions.DatabasePath,"PRAGMA user_version=5; DROP INDEX telemetry_receipt_time; DROP INDEX events_receipt_time");
        Sql(science.DatabasePath,"PRAGMA user_version=1; DROP TABLE scientific_artifacts; DROP INDEX observations_receipt_time");
        var telemetry=Sql(missions.DatabasePath,"SELECT payload_json FROM telemetry LIMIT 1");var product=Sql(science.DatabasePath,"SELECT payload_json FROM remote_products LIMIT 1");
        missions.Initialize();science.Initialize();
        Assert.Equal((long)MissionStore.CurrentSchemaVersion,Sql(missions.DatabasePath,"PRAGMA user_version"));Assert.Equal(2L,Sql(science.DatabasePath,"PRAGMA user_version"));
        Assert.Equal(telemetry,Sql(missions.DatabasePath,"SELECT payload_json FROM telemetry LIMIT 1"));Assert.Equal(product,Sql(science.DatabasePath,"SELECT payload_json FROM remote_products LIMIT 1"));
        Assert.Equal(ScienceMode.Realistic,missions.Detail(mission.Id)!.Mission.Mode);Assert.Equal(TelemetrySource.Simulated,missions.StreamSamples(mission.Id).Single().Source);
        var missionBackup=Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(missions.DatabasePath)!,"Backups"),"missions-schema5-*.db"));
        Assert.Equal(5L,Sql(missionBackup,"PRAGMA user_version"));Assert.Equal(telemetry,Sql(missionBackup,"SELECT payload_json FROM telemetry LIMIT 1"));
        var backups=Directory.GetFiles(Path.Combine(science.DirectoryPath,"Metadata","Backups"),"*.sqlite");
        Assert.All(backups,b=>Assert.Equal("ok",Sql(b,"PRAGMA integrity_check")));
        // Earlier schema-1 backups legitimately precede the remote-source extension.
        // Select the actual pre-upgrade scientific archive, without querying a nonexistent table.
        Assert.Contains(backups,b=>Equals(1L,Sql(b,"PRAGMA user_version"))
            && Equals(1L,Sql(b,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='remote_products'"))
            && Equals(product,Sql(b,"SELECT payload_json FROM remote_products LIMIT 1")));
        Assert.Single(science.AllProducts(mission.Id));Assert.Single(missions.Replay(mission.Id).Samples);
    }
    [Fact] public void RegionalCoverageExportsSplitAntimeridianAndPreserveConcaveSurveyGeometry()
    {
        var local=new ScientificLocalGeometry(new(10,179.999));
        var polygon=local.Polygon([new(10,179.99),new(10,-179.99),new(10.01,-179.99),new(10.005,179.999),new(10.01,179.99)]);
        using var data=JsonDocument.Parse(JsonSerializer.Serialize(local.GeoJson(polygon),WireJson.Options));
        var parts=data.RootElement.GetProperty("coordinates");Assert.Equal(2,parts.GetArrayLength());
        foreach(var part in parts.EnumerateArray())foreach(var ring in part.EnumerateArray())
        {double? before=null;foreach(var p in ring.EnumerateArray()){var longitude=p[0].GetDouble();Assert.InRange(longitude,-180,180);if(before is {} last)Assert.InRange(Math.Abs(longitude-last),0,1);before=longitude;}}
    }
    [Fact] public void CompleteMapOverviewTraversesBeyondBothOldPageLimitsWithoutChangingOriginals()
    {
        var first=Add(0);var originalHash=first.Sha256;
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=science.DatabasePath,Pooling=false}.ToString()))
        {
            db.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            // Catalog-query load fixture. Every row references the same unchanged
            // small numeric file; this is not acquisition/durability acceptance.
            for(var i=1;i<143;i++)
            {
                var product=first with{Id=Guid.NewGuid(),FlightPacketUtc=utc.AddSeconds(i)};
                cmd.CommandText="INSERT INTO remote_products VALUES($id,$dataset,$mission,$instrument,$utc,$json)";cmd.Parameters.Clear();
                foreach(var (key,value) in new[]{("$id",product.Id.ToString()),("$dataset",product.DatasetId.ToString()),("$mission",product.MissionId.ToString()),("$instrument",product.InstrumentId),("$utc",product.FlightPacketUtc.ToString("O")),("$json",JsonSerializer.Serialize(product,WireJson.Options))})cmd.Parameters.AddWithValue(key,value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var temperature=science.EnsureDataset(mission,new("ambient-temperature",true),missions.Research(mission.Id));
        science.Append(Enumerable.Range(0,721).Select(i=>new ScienceObservation(0,temperature.Id,mission.Id,"ambient-temperature",utc.AddSeconds(i),"temperature",10+i*.01,"degC",ScienceProvenance.SimulatorReported,"synthetic catalog fixture",35+i*.000001,-110,1000,i%17==0?ObservationQuality.Stale:ObservationQuality.Valid)).ToArray());
        var overview=science.MapOverview(new(Missions:[mission.Id]));
        Assert.Equal(143,overview.Acquisitions);Assert.Equal(721,overview.AtmosphericValues);
        Assert.Equal(143,overview.Bins.Where(b=>b.Measurement is null).Sum(b=>b.Records));
        Assert.Equal(721,overview.Bins.Where(b=>b.Measurement is not null).Sum(b=>b.Records));
        Assert.InRange(overview.Bins.Length,1,96*overview.Categories.Length);
        Assert.Equal(721-Enumerable.Range(0,721).Count(i=>i%17==0),overview.Categories.Single(c=>c.Measurement=="temperature").ValidAtmosphericValues);
        Assert.Equal(10,science.MapPage(new(Missions:[mission.Id],Limit:10,Offset:130)).Products.Length);
        Assert.Equal(21,science.AtmosphereMap(new(Missions:[mission.Id],Limit:30,Offset:700,Measurement:"temperature")).Items.Length);
        Assert.Empty(science.AtmosphereMap(new(Missions:[mission.Id],Measurement:"pressure")).Items);
        Assert.Equal(Enumerable.Range(0,721).Count(i=>i%17==0),science.MapOverview(new(Missions:[mission.Id],Search:"stale")).AtmosphericValues);
        Assert.Equal(0,science.MapOverview(new(Missions:[mission.Id],Search:"' OR 1=1 --")).AtmosphericValues);
        Assert.Empty(science.MapOverview(new(Missions:[Guid.NewGuid()])).Categories);
        Assert.Equal(0,science.MapOverview(new(Missions:[mission.Id],Area:new(0,0,1,1))).Acquisitions);
        Assert.Equal(originalHash,science.ReadProduct(first.Id).Product.Sha256);Assert.Equal(2,science.ReadProduct(first.Id).SampleLocations.Length);
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>science.MapOverview(new(),cancel.Token));
    }
    [Fact] public void OverviewClusterCentroidStaysAtTheDateLineRatherThanAveragingToGreenwich()
    {
        var temperature=science.EnsureDataset(mission,new("ambient-temperature",true),missions.Research(mission.Id));
        science.Append(new[]{179.999,-179.999}.Select((longitude,index)=>new ScienceObservation(0,temperature.Id,mission.Id,"ambient-temperature",utc.AddSeconds(index),"temperature",10+index,"degC",ScienceProvenance.SimulatorReported,"synthetic date-line fixture",10,longitude,1000,ObservationQuality.Valid)).ToArray());
        var overview=science.MapOverview(new(Missions:[mission.Id],Area:new(179.94,9,-179.9,11)));
        Assert.Equal(2,overview.AtmosphericValues);Assert.All(overview.Bins,bin=>Assert.True(Math.Abs(bin.Center.Longitude)>179));
    }
    [Fact] public void DifferenceMapContainsExactMatchedNativeCellsAndPreservesOriginalAcquisitions()
    {
        var first=Add(0,100,200,true);var second=Add(1,102,195,true);
        var comparison=science.Compare(new([first.Id,second.Id],"elevation"));
        Assert.True(comparison.Comparable);Assert.Equal(2,comparison.MatchedNativeCells);Assert.Equal(-1.5,comparison.MeanDelta);
        Assert.Equal(new[]{-5d,2d},comparison.DifferenceCells!.Select(cell=>cell.Delta).Order().ToArray());
        Assert.All(comparison.DifferenceCells!,cell=>{Assert.Equal(first.Id,cell.BaselineProductId);Assert.Equal(second.Id,cell.ProductId);Assert.Equal(cell.Compared-cell.Baseline,cell.Delta);Assert.Equal(5,cell.Boundary.Length);});
        Assert.Empty(science.Compare(new([first.Id,second.Id],"temperature")).DifferenceCells??[]);
        Assert.Equal(first.Sha256,science.ReadProduct(first.Id).Product.Sha256);Assert.Equal(second.Sha256,science.ReadProduct(second.Id).Product.Sha256);
    }
    [Fact] public void ConstantPreviewUsesOneColorWithTransparentMissingCellsAndUnchangedNumericRaster()
    {
        Add(0,222);var artifact=Assert.Single(ScientificComposites.Build(science,missions,mission.Id,new(source.Id,"elevation")));
        var path=science.VerifiedArtifactPath(artifact);var original=SHA256.HashData(File.ReadAllBytes(path));
        var preview=ScientificArtifactPreview.Create(science,artifact);Assert.Equal(222,preview.Minimum);Assert.Equal(222,preview.Maximum);
        using var compressed=new MemoryStream();var index=8;
        while(index<preview.Png.Length){var length=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(preview.Png.AsSpan(index,4));var type=System.Text.Encoding.ASCII.GetString(preview.Png,index+4,4);if(type=="IDAT")compressed.Write(preview.Png,index+8,length);index+=length+12;}
        compressed.Position=0;using var zlib=new ZLibStream(compressed,CompressionMode.Decompress);using var pixels=new MemoryStream();zlib.CopyTo(pixels);var raw=pixels.ToArray();var visible=0;var transparent=0;
        for(var y=0;y<384;y++)for(var x=0;x<384;x++){var offset=y*(384*4+1)+1+x*4;if(raw[offset+3]==0){transparent++;continue;}visible++;Assert.Equal(new byte[]{33,145,140,255},raw.AsSpan(offset,4).ToArray());}
        Assert.True(visible>0&&transparent>0);Assert.Equal(original,SHA256.HashData(File.ReadAllBytes(path)));using var raster=new ScientificRaster(Path.GetDirectoryName(path)!,new("value",Path.GetFileName(path),"geotiff"));Assert.Equal(222,raster.Read(ScientificRaster.GridPoint(grid,.5,.5)));Assert.Null(raster.Read(ScientificRaster.GridPoint(grid,1.5,.5)));
    }
    public void Dispose(){science.Dispose();Directory.Delete(root,true);}
    private sealed class AsyncOnlyOutput(Stream output):Stream
    {
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush()=>throw new InvalidOperationException("Synchronous HTTP flush is forbidden");public override Task FlushAsync(CancellationToken token)=>output.FlushAsync(token);
        public override void Write(byte[] data,int offset,int count)=>throw new InvalidOperationException("Synchronous HTTP write is forbidden");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data,CancellationToken token=default)=>output.WriteAsync(data,token);
        public override Task WriteAsync(byte[] data,int offset,int count,CancellationToken token)=>output.WriteAsync(data,offset,count,token);
        public override int Read(byte[] data,int offset,int count)=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
    }
}
