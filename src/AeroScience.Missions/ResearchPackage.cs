using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public static class ResearchPackage
{
    public static async Task WriteAsync(MissionStore missions,ScienceStore science,Guid mission,Stream output,CancellationToken token=default)
    {
        ResearchExports.RequireCompleted(missions,mission);
        if(science.AvailabilityError() is {} unavailable)throw new IOException(unavailable);
        using var analysis=science.BeginAnalysis();
        // .NET's entry wrapper still disposes DeflateStream synchronously, even
        // after OpenAsync. Bridge those bounded final writes to asynchronous HTTP
        // I/O; do not enable synchronous Kestrel I/O or buffer the archive in RAM.
        using var transport=new AsyncTransport(output,token);
        await using var zip=await ZipArchive.CreateAsync(transport,ZipArchiveMode.Create,true,null,token);
        var manifest=new List<object>();
        async Task Entry(string name,Func<Stream,Task> write)
        {
            token.ThrowIfCancellationRequested();var entry=zip.CreateEntry(name,CompressionLevel.Fastest);
            await using var stream=await entry.OpenAsync(token);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);await using var tracked=new HashingWriteStream(stream,hash);
            await write(tracked);await tracked.FlushAsync(token);manifest.Add(new{path=name,bytes=tracked.Bytes,sha256=Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()});
        }
        await Entry("scientific-review.json",stream=>JsonSerializer.SerializeAsync(stream,missions.ScientificReview(mission),WireJson.Options,token));
        await Entry("mission.json",stream=>missions.ExportJsonAsync(mission,stream,token));
        if(missions.LatestPerformanceReport(mission) is not null)await Entry("performance-yield-v1.csv",stream=>stream.WriteAsync(Encoding.UTF8.GetBytes(ResearchExports.PerformanceCsv(missions,mission)),token).AsTask());
        foreach(var kind in new[]{"flight","science","events"})await Entry(kind+".csv",stream=>ResearchExports.CsvAsync(missions,mission,kind,stream,token));
        await Entry("track.gpx",stream=>ResearchExports.GpxAsync(missions,mission,stream,token));
        await Entry("track.kml",stream=>ResearchExports.KmlAsync(missions,mission,stream,token));
        await Entry("summary.txt",stream=>ResearchExports.ReportAsync(missions,mission,stream,token));
        await Entry("environment.csv",async stream=>
        {
            await using var writer=new StreamWriter(stream,new UTF8Encoding(false),16384,true);await writer.WriteLineAsync("aircraft_packet_utc,observation_utc,latitude,longitude,altitude_msl_ft,variable,value,unit,quality,source");
            static string Q(string s)=>"\""+s.Replace("\"","\"\"")+"\"";
            foreach(var f in missions.StreamSamples(mission))foreach(var r in f.Environment?.Readings??[])
            {token.ThrowIfCancellationRequested();await writer.WriteLineAsync(string.Join(',',Q(f.TimestampUtc.ToString("O")),Q(r.TimestampUtc.ToString("O")),f.Latitude.ToString("R",System.Globalization.CultureInfo.InvariantCulture),f.Longitude.ToString("R",System.Globalization.CultureInfo.InvariantCulture),f.AltitudeMslFeet.ToString("R",System.Globalization.CultureInfo.InvariantCulture),Q(r.SimulatorVariable),r.Value?.ToString("R",System.Globalization.CultureInfo.InvariantCulture)??"",Q(r.Unit),Q(r.Quality.ToString()),"SimulatorReported").AsMemory(),token);}
        });
        var used=new Dictionary<Guid,ScientificSource>();
        foreach(var p in science.AllProducts(mission))
        {
            token.ThrowIfCancellationRequested();var saved=science.ReadProduct(p.Id);used.TryAdd(p.SourceId,saved.Source);
            var path=StorageManager.ResolveDataset(science.DirectoryPath,p.LibraryId,p.RelativePath);
            await Entry($"acquisitions/{p.Id:N}.json",async stream=>{await using var input=File.OpenRead(path);await input.CopyToAsync(stream,token);});
        }
        foreach(var a in science.Artifacts(mission))
        {
            using(var metadata=JsonDocument.Parse(a.MetadataJson))if(metadata.RootElement.ValueKind==JsonValueKind.Object&&metadata.RootElement.TryGetProperty("sourceIds",out var ids)&&ids.ValueKind==JsonValueKind.Array)
                foreach(var id in ids.EnumerateArray())if(id.TryGetGuid(out var sourceId))if(science.Source(sourceId) is {} source)used.TryAdd(sourceId,source);
            var path=science.VerifiedArtifactPath(a);
            await Entry($"analysis/{a.Id:N}.{a.Format}",async stream=>{await using var input=File.OpenRead(path);await input.CopyToAsync(stream,token);});
            await Entry($"analysis/{a.Id:N}.metadata.json",stream=>JsonSerializer.SerializeAsync(stream,a,WireJson.Options,token));
            if(a.Kind=="coverage")
            {
                using var content=JsonDocument.Parse(await File.ReadAllTextAsync(path,token));var result=content.RootElement;
                var features=new List<object>();
                foreach(var (name,property) in new[]{("modeled-footprints","geometricGeoJson"),("modeled-gaps","gapsGeoJson"),("valid-scientific-support","validDataGeoJson"),("valid-data-gaps","validDataGapsGeoJson")})
                    if(result.TryGetProperty(property,out var geometry)&&geometry.ValueKind!=JsonValueKind.Null)features.Add(new{type="Feature",properties=new{layer=name,method="scientific-coverage-1; see the complete coverage analysis for denominator, source dates and uncertainty"},geometry=geometry.Clone()});
                foreach(var source in result.GetProperty("sources").EnumerateArray())features.Add(new{type="Feature",properties=new{layer="source-valid-support",sourceId=source.GetProperty("sourceId").GetString(),band=source.GetProperty("band").GetString()},geometry=source.GetProperty("validSupportGeoJson").Clone()});
                await Entry($"coverage/{a.Id:N}.geojson",stream=>JsonSerializer.SerializeAsync(stream,new{type="FeatureCollection",features},WireJson.Options,token));
            }
        }
        await Entry("sources.json",stream=>JsonSerializer.SerializeAsync(stream,used.Values,WireJson.Options,token));
        await Entry("methods.txt",stream=>stream.WriteAsync(Encoding.UTF8.GetBytes("AeroScience research package 1. Recorded aircraft and auxiliary times are host packet-receipt UTC; source observation and processing times are separate. Missing values remain missing. Profile snapshots model geometry, not external-source calibration. Coverage artifacts contain exact processing settings, projection, denominator and limitations. Mission sample composites contain saved valid native pixels; retained-source mosaics contain historical originals selected for analysis and do not establish aircraft collection. GeoTIFF tiles retain units, nodata, CRS and source resolution. Originals are referenced with hashes and relative library identifiers, not bundled. Saved acquisition samples can be reviewed without originals; new source-pixel processing requires the referenced originals. No data was uploaded. Explicitly labeled post-flight derived reconstruction may be present; scientific-review.json identifies unresolved gaps and provisional state.\n"),token).AsTask());
        var final=zip.CreateEntry("manifest.json",CompressionLevel.Fastest);await using var finalStream=await final.OpenAsync(token);
        await JsonSerializer.SerializeAsync(finalStream,new{formatVersion=1,missionId=mission,createdUtc=DateTimeOffset.UtcNow,files=manifest,manifestHash="The manifest excludes itself; verify the complete exported ZIP hash separately.",sourcePolicy="Original retained-source references and SHA-256 hashes included; original datasets are not duplicated."},WireJson.Options,token);
    }
    private sealed class AsyncTransport(Stream inner,CancellationToken cancellation):Stream
    {
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush()=>inner.FlushAsync(cancellation).GetAwaiter().GetResult();
        public override Task FlushAsync(CancellationToken token)=>inner.FlushAsync(token);
        public override void Write(byte[] bytes,int offset,int count)=>inner.WriteAsync(bytes.AsMemory(offset,count),cancellation).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,CancellationToken token=default)=>inner.WriteAsync(bytes,token);
        public override Task WriteAsync(byte[] bytes,int offset,int count,CancellationToken token)=>inner.WriteAsync(bytes.AsMemory(offset,count),token).AsTask();
        public override int Read(byte[] bytes,int offset,int count)=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){} // The response/file is owned by the caller.
    }
    private sealed class HashingWriteStream(Stream inner,IncrementalHash hash):Stream
    {
        public long Bytes{get;private set;}
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;public override long Length=>Bytes;public override long Position{get=>Bytes;set=>throw new NotSupportedException();}
        public override void Flush()=>inner.FlushAsync().GetAwaiter().GetResult();public override Task FlushAsync(CancellationToken token)=>inner.FlushAsync(token);
        public override void Write(byte[] buffer,int offset,int count){hash.AppendData(buffer,offset,count);Bytes+=count;inner.WriteAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();}
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default){await inner.WriteAsync(buffer,token);hash.AppendData(buffer.Span);Bytes+=buffer.Length;}
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken token)=>WriteAsync(buffer.AsMemory(offset,count),token).AsTask();
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        // Entry ownership stays with the caller; hashes cover the uncompressed file bytes.
        protected override void Dispose(bool disposing){}
        public override ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
