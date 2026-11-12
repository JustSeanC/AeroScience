using System.Diagnostics;
using System.Security.Cryptography;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed record LidarDecodedPoint(long Record,int RawX,int RawY,int RawZ,double X,double Y,double Z,
    ushort Intensity,byte ReturnNumber,byte NumberOfReturns,byte Classification,byte ClassificationFlags,
    byte ScanDirectionFlag,byte EdgeOfFlightLine,byte UserData,ushort PointSourceId,short RawScanAngle,double ScanAngleDegrees,
    byte? ScannerChannel,double? GpsTime,long? RawGpsTimeBits,ushort[]? Rgb,ushort? Infrared,byte[] OriginalExtraBytes);

public static class LidarDecoder
{
    private static readonly SemaphoreSlim Reader=new(1);
    public static string VerifiedExecutable()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"LidarRuntime",OperatingSystem.IsWindows()?"AeroScience.LidarReader.exe":"AeroScience.LidarReader");
        StorageManager.ValidateLocalPath(path);using var f=File.OpenRead(path);
        if(f.Length is <65536 or >16777216||Convert.ToHexStringLower(SHA256.HashData(f))!=LidarRuntimeIdentity.Sha256)throw new IOException("The bundled LiDAR decoder is missing or its compiled identity differs. Rebuild/repair this application; no external executable is downloaded or selected from PATH.");return path;
    }
    public static string SourceIdentity=>LidarRuntimeIdentity.Source;
    public static async Task<LidarDecodedPoint[]> Read(string original,LidarLasHeader header,long first,int count,CancellationToken token=default)
    {
        if(first<0||count is <1 or >4096||first>header.PointCount-count)throw new ArgumentException("Read 1–4096 original points inside the verified original record count.");
        StorageManager.ValidateLocalPath(original);var path=Path.GetFullPath(original);
        if(!Path.IsPathFullyQualified(path)||new FileInfo(path).Length>LidarLas.MaximumOriginalBytes)throw new IOException("Only a verified bounded local original can be decoded.");
        await Reader.WaitAsync(token);
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var start=new ProcessStartInfo(VerifiedExecutable()){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=AppContext.BaseDirectory};
            foreach(var arg in new[]{"--read-v1",path,first.ToString(System.Globalization.CultureInfo.InvariantCulture),count.ToString(System.Globalization.CultureInfo.InvariantCulture)})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)??throw new IOException("The bundled LiDAR reader could not start.");
            void Stop(){try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}}
            using var cancellation=deadline.Token.Register(Stop);
            try
            {
                async Task<byte[]> Bounded(Stream stream,int limit)
                {
                    using var output=new MemoryStream();var buffer=new byte[16384];int n;
                    while((n=await stream.ReadAsync(buffer,deadline.Token))>0){if(output.Length+n>limit){Stop();throw new IOException("LiDAR decoder output exceeded its protocol bound.");}output.Write(buffer,0,n);}return output.ToArray();
                }
                var output=Bounded(process.StandardOutput.BaseStream,checked(12+count*(44+2048)));var error=Bounded(process.StandardError.BaseStream,8192);
                await Task.WhenAll(output,error,process.WaitForExitAsync(deadline.Token));
                if(process.ExitCode!=0)throw new IOException("The bounded native LAS/LAZ decoder refused this original. Inspect supported format, corruption and resource limits; no points were substituted.");
                using var data=new MemoryStream(output.Result);using var r=new BinaryReader(data);
                if(System.Text.Encoding.ASCII.GetString(r.ReadBytes(8))!="ASLIDAR1"||r.ReadUInt32()!=count)throw new IOException("Native LiDAR response identity/count differs.");
                int[] sizes=[20,28,26,34,57,63,30,36,38];var f=header.PointFormat;var points=new List<LidarDecodedPoint>(count);
                for(var i=0;i<count;i++)
                {
                    var x=r.ReadInt32();var y=r.ReadInt32();var z=r.ReadInt32();var intensity=r.ReadUInt16();var ret=r.ReadByte();var returns=r.ReadByte();var classification=r.ReadByte();var flags=r.ReadByte();var direction=r.ReadByte();var edge=r.ReadByte();var user=r.ReadByte();var source=r.ReadUInt16();var angle=r.ReadInt16();var channel=r.ReadByte();var gpsBits=r.ReadInt64();var gps=BitConverter.Int64BitsToDouble(gpsBits);ushort[] rgb=[r.ReadUInt16(),r.ReadUInt16(),r.ReadUInt16()];var nir=r.ReadUInt16();var extras=r.ReadUInt16();
                    if(extras!=header.RecordLength-sizes[f])throw new IOException("Decoded extra dimensions differ from the original point-record declaration.");var extra=r.ReadBytes(extras);if(extra.Length!=extras)throw new EndOfStreamException();
                    var hasGps=f is 1 or 3 or >=6;var hasRgb=f is 2 or 3 or 7 or 8;
                    points.Add(new(first+i,x,y,z,x*header.Scale[0]+header.Offset[0],y*header.Scale[1]+header.Offset[1],z*header.Scale[2]+header.Offset[2],intensity,ret,returns,classification,flags,direction,edge,user,source,angle,f>=6?angle*.006:angle,f>=6?channel:null,hasGps&&double.IsFinite(gps)?gps:null,hasGps?gpsBits:null,hasRgb?rgb:null,f==8?nir:null,extra));
                }
                if(data.Position!=data.Length)throw new IOException("Unexpected bytes after the bounded LiDAR reply.");return points.ToArray();
            }
            finally{Stop();await process.WaitForExitAsync(CancellationToken.None);}
        }
        finally{Reader.Release();}
    }
}
