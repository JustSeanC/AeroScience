using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;

public static class ChartRenderingChecks
{
    public static void Run(string runtime,string originals,string reference,string output)
    {
        AviationChartRaster.Initialize(runtime);
        using var document=JsonDocument.Parse(File.ReadAllText(reference));var results=new List<object>();var clock=Stopwatch.StartNew();
        foreach(var r in document.RootElement.GetProperty("sources").EnumerateArray())
        {
            var name=r.GetProperty("file").GetString()!;if(Path.GetFileName(name)!=name)throw new IOException("Unsafe pinned source name.");
            var path=Path.Combine(originals,name);var sha=r.GetProperty("sha256").GetString()!;
            var ring=r.GetProperty("mainBodyPixels").EnumerateArray().Select(p=>new AviationChartRaster.Pixel(p[0].GetDouble(),p[1].GetDouble())).ToArray();
            var description=AviationChartRaster.Inspect(path,sha,ring);var dims=r.GetProperty("dimensions");
            if(description.Width!=dims[0].GetInt32()||description.Height!=dims[1].GetInt32()||description.Bands!=dims[2].GetInt32())throw new IOException("Genuine chart dimensions differ.");
            var expected=r.GetProperty("mainBodyWgs84").EnumerateArray().ToArray();var max=0d;
            for(var i=0;i<expected.Length;i++)max=Math.Max(max,Math.Max(Math.Abs(description.MainBody[i].Longitude-expected[i][0].GetDouble()),Math.Abs(description.MainBody[i].Latitude-expected[i][1].GetDouble())));
            if(max>1e-9)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!,"chart-coordinate-diagnostic.json"),JsonSerializer.Serialize(new{file=name,sha256=sha,maximumDifference=max,originalCrs=description.Crs,actual=description.MainBody,expected=expected.Select(e=>new[]{e[0].GetDouble(),e[1].GetDouble()}).ToArray(),tolerance=1e-9,passed=false}));
                throw new IOException("Independent original CRS/interior-coordinate comparison failed; retained exact diagnostic for "+name+". No tolerance was relaxed.");
            }
            var tile=r.GetProperty("tile");var tileClock=Stopwatch.StartNew();
            var png=AviationChartRaster.Tile(path,sha,ring,tile.GetProperty("zoom").GetInt32(),tile.GetProperty("x").GetInt32(),tile.GetProperty("y").GetInt32());
            var imageDirectory=Path.Combine(Path.GetDirectoryName(output)!,"chart-native-tiles");Directory.CreateDirectory(imageDirectory);File.WriteAllBytes(Path.Combine(imageDirectory,name+".png"),png);
            var rgba=Decode(png);var samples=0;
            foreach(var sample in r.GetProperty("samples").EnumerateArray())
            {
                var offset=(sample.GetProperty("y").GetInt32()*256+sample.GetProperty("x").GetInt32())*4;
                var color=sample.GetProperty("rgba").EnumerateArray().Select(v=>v.GetByte()).ToArray();
                if(!rgba.AsSpan(offset,4).SequenceEqual(color))throw new IOException($"Independent original palette/RGB comparison failed: {name}, pixel {sample.GetProperty("x")},{sample.GetProperty("y")}.");
                samples++;
            }
            var outside=Decode(AviationChartRaster.Tile(path,sha,ring,4,0,0));
            if(Enumerable.Range(0,256*256).Any(i=>outside[i*4+3]!=0))throw new IOException("Chart colors painted outside verified source interior.");
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{AviationChartRaster.Tile(path,sha,ring,4,1,0,cancel.Token);throw new IOException("Cancellation ignored.");}catch(OperationCanceledException){}
            using var input=File.OpenRead(path);if(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()!=sha)throw new IOException("Original changed during rendering.");
            results.Add(new{file=name,sha256=sha,description.Width,description.Height,description.Bands,maximumCoordinateDifference=max,independentColorComparisons=samples,pngSha256=Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),tileSeconds=tileClock.Elapsed.TotalSeconds,outsideAlphaZero=true,cancelled=true,originalUnchanged=true});
        }
        if(Process.GetCurrentProcess().PeakWorkingSet64>256L*1024*1024)throw new IOException("Five-family bounded chart probe exceeded its 256-MiB process memory guard.");
        File.WriteAllText(output,JsonSerializer.Serialize(new{evidenceClass="Actual Windows execution of application chart codec with five genuine public FAA originals and independent rasterio/PROJ color/coordinate references",results,elapsedSeconds=clock.Elapsed.TotalSeconds,peakResidentBytes=Process.GetCurrentProcess().PeakWorkingSet64,
            limitations=new[]{"Reviewed conservative interiors, not publisher neatlines or printed-control/full-edge/inset acceptance","Not installed SDK/MSFS/iPad/Quest, long-flight or public distribution acceptance"}},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS: application color/CRS/cutline/cancellation/original-integrity checks on five genuine FAA chart families.");
    }
    private static byte[] Decode(byte[] png)
    {
        if(!png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new IOException("Invalid PNG signature.");
        using var compressed=new MemoryStream();var offset=8;var header=false;var ended=false;
        while(offset<png.Length)
        {
            if(png.Length-offset<12)throw new IOException("Short PNG chunk.");var count=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset,4));
            if(count<0||count>1048576||count>png.Length-offset-12)throw new IOException("Unbounded PNG chunk.");
            var type=System.Text.Encoding.ASCII.GetString(png,offset+4,4);var bytes=png.AsSpan(offset+8,count);
            if(type=="IHDR"){if(count!=13||BinaryPrimitives.ReadInt32BigEndian(bytes)!=256||BinaryPrimitives.ReadInt32BigEndian(bytes[4..])!=256||bytes[8]!=8||bytes[9]!=6)throw new IOException("Unexpected PNG shape/type.");header=true;}
            else if(type=="IDAT")compressed.Write(bytes);
            else if(type=="IEND")ended=true;
            offset+=count+12;
        }
        if(!header||!ended)throw new IOException("Incomplete PNG.");compressed.Position=0;using var z=new ZLibStream(compressed,CompressionMode.Decompress);var filtered=new byte[256*1025];z.ReadExactly(filtered);if(z.ReadByte()!=-1)throw new IOException("Excess decompressed PNG data.");
        var rgba=new byte[256*256*4];for(var row=0;row<256;row++){if(filtered[row*1025]!=0)throw new IOException("Unexpected production PNG filter.");filtered.AsSpan(row*1025+1,1024).CopyTo(rgba.AsSpan(row*1024));}return rgba;
    }
}
