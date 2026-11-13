using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class LidarDecoderTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"lidar-decoder-"+Guid.NewGuid().ToString("N"));
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task SupportedLasPreservesNativeCoordinatesReturnsFlagsAndOptionalAttributes(int format)
    {
        var path=LidarFixture.Write(root,format);var h=LidarLas.Inspect(path);Assert.Equal(3,h.PointCount);Assert.False(h.Compressed);Assert.Equal("EPSG:4326",h.HorizontalCrs);Assert.Contains("Unknown",h.VerticalReference);
        var p=(await LidarDecoder.Read(path,h,1,1))[0];Assert.Equal(1,p.Record);Assert.Equal(10,p.RawX);Assert.Equal(-74.99999,p.X,8);Assert.Equal(234.56,p.Z,8);Assert.Equal(54321,p.Intensity);Assert.Equal(2,p.ReturnNumber);Assert.Equal(3,p.NumberOfReturns);Assert.Equal(6,p.Classification);Assert.Equal(4,p.ClassificationFlags);Assert.Equal(-7,p.RawScanAngle);Assert.Equal(321,p.PointSourceId);Assert.Empty(p.OriginalExtraBytes);
        if(format is 1 or 3){Assert.Equal(123456.789,p.GpsTime);Assert.NotNull(p.RawGpsTimeBits);}else{Assert.Null(p.GpsTime);Assert.Null(p.RawGpsTimeBits);}
        if(format is 2 or 3)Assert.Equal(new ushort[]{100,200,300},p.Rgb);else Assert.Null(p.Rgb);Assert.Null(p.Infrared);Assert.Null(p.ScannerChannel);
        await Assert.ThrowsAsync<ArgumentException>(()=>LidarDecoder.Read(path,h,0,4097));
    }
    [Fact]public void UnsupportedDatumUnitsAndCopcAreNeverGuessed()
    {
        Assert.Throws<InvalidDataException>(()=>LidarLas.Inspect(LidarFixture.Write(root,epsg:4269)));
        Assert.Throws<InvalidDataException>(()=>LidarLas.Inspect(LidarFixture.Write(root,unit:9002)));
        Assert.Contains("COPC",Assert.Throws<InvalidDataException>(()=>LidarLas.Inspect(LidarFixture.Write(root,copc:true))).Message);
        var path=LidarFixture.Write(root);using(var s=new FileStream(path,FileMode.Open,FileAccess.Write)){s.Position=131;s.Write(new byte[8]);}Assert.Throws<InvalidDataException>(()=>LidarLas.Inspect(path));
    }
    [Theory][InlineData(6)][InlineData(7)][InlineData(8)]public async Task Las14PreservesExtendedClassesReturnsFlagsAngleAndExtraDimensions(int format)
    {
        var path=LidarFixture.Modern(root,format);var h=LidarLas.Inspect(path);Assert.Equal(4,h.VersionMinor);Assert.Equal(3,h.PointCount);var p=(await LidarDecoder.Read(path,h,0,1))[0];Assert.Equal(200,p.Classification);Assert.Equal(9,p.ReturnNumber);Assert.Equal(12,p.NumberOfReturns);Assert.Equal(13,p.ClassificationFlags);Assert.Equal((byte)3,p.ScannerChannel);Assert.Equal(-1234,p.RawScanAngle);Assert.Equal(-7.404,p.ScanAngleDegrees,10);Assert.Equal(50000,p.PointSourceId);Assert.Equal(1234.567,p.GpsTime);Assert.Equal(BitConverter.GetBytes(12345U),p.OriginalExtraBytes);
        if(format>=7)Assert.Equal(new ushort[]{1000,2000,3000},p.Rgb);else Assert.Null(p.Rgb);if(format==8)Assert.Equal((ushort)4000,p.Infrared);else Assert.Null(p.Infrared);
    }
    [Fact]public void ExplicitUnsupportedAxisOrderIsRefusedRatherThanSwappingSourceCoordinates()
    {
        Assert.Throws<InvalidDataException>(()=>LidarLas.Inspect(LidarFixture.Modern(root,6,"AXIS[\"Latitude\",NORTH],AXIS[\"Longitude\",EAST],")));
        Assert.Equal("EPSG:4326",LidarLas.Inspect(LidarFixture.Modern(root,6,"AXIS[\"Longitude\",EAST],AXIS[\"Latitude\",NORTH],")).HorizontalCrs);
    }
    [Theory][InlineData("https://usgs-lidar.s3.amazonaws.com/project/file.laz")][InlineData("https://usgs-lidar-public.s3-us-west-2.amazonaws.com/IA_FullState/ept.json?x-amz-request-payer=requester")][InlineData("https://127.0.0.1/ept.json")][InlineData("https://usgs-lidar-public.s3-us-west-2.amazonaws.com/IA_FullState/../secret")]
    public void UnsafeOrPaidEndpointsAreRejected(string uri)=>Assert.Throws<IOException>(()=>LidarCatalog.ValidateUrl(new(uri)));
    [Fact]public async Task GenuineDeliveredEptLazDecodesAgainstIndependentRecordedReference()
    {
        var folder=Path.Combine(AppContext.BaseDirectory,"LidarOriginals");var path=Path.Combine(folder,"ia-fullstate-root.laz");var h=LidarLas.Inspect(path);
        using var reference=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"independent-reference.json")));var e=reference.RootElement;
        Assert.Equal(e.GetProperty("originalSha256").GetString(),Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));Assert.Equal(34923,h.PointCount);Assert.Equal("EPSG:3857",h.HorizontalCrs);Assert.True(h.Compressed);Assert.Equal(1,h.PointFormat);Assert.Contains("OriginalExtraBytes (descriptors retained in VLRs)",h.Dimensions);
        using var canonical=new MemoryStream();using var writer=new BinaryWriter(canonical);
        for(var first=0L;first<h.PointCount;first+=4096){foreach(var p in await LidarDecoder.Read(path,h,first,(int)Math.Min(4096,h.PointCount-first))){writer.Write(p.RawX);writer.Write(p.RawY);writer.Write(p.RawZ);writer.Write(p.Intensity);writer.Write(p.ReturnNumber);writer.Write(p.NumberOfReturns);writer.Write(p.Classification);writer.Write(p.ClassificationFlags);writer.Write(p.ScanDirectionFlag);writer.Write(p.EdgeOfFlightLine);writer.Write(p.UserData);writer.Write(p.PointSourceId);writer.Write(p.RawScanAngle);writer.Write(p.ScannerChannel??0);writer.Write(p.RawGpsTimeBits??0);foreach(var c in p.Rgb??new ushort[3])writer.Write(c);writer.Write(p.Infrared??0);writer.Write((ushort)p.OriginalExtraBytes.Length);writer.Write(p.OriginalExtraBytes);}}
        Assert.Equal(e.GetProperty("nativeProtocolSha256").GetString(),Convert.ToHexStringLower(SHA256.HashData(canonical.ToArray())));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
