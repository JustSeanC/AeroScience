using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using AeroScience.Shared;
namespace AeroScience.Missions;

public static class LidarLas
{
    public const long MaximumOriginalBytes=536870912,MaximumPoints=20000000;
    public static LidarLasHeader Inspect(string path)
    {
        StorageManager.ValidateLocalPath(path);using var f=File.OpenRead(path);
        if(f.Length is <227 or >MaximumOriginalBytes)throw new InvalidDataException("LAS/LAZ original must be 227 bytes–512 MiB. Import bounded tiles; no whole-project fallback.");
        using var r=new BinaryReader(f,Encoding.UTF8,true);var h=r.ReadBytes((int)Math.Min(375,f.Length));
        ushort U16(int i)=>BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(i));
        uint U32(int i)=>BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(i));
        ulong U64(int i)=>BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(i));
        double D(int i)=>BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(i)));
        if(Encoding.ASCII.GetString(h,0,4)!="LASF"||h[24]!=1||h[25] is <1 or >4)throw new InvalidDataException("Supported LAS versions are 1.1–1.4; this is not a verified LAS/LAZ original.");
        var minor=h[25];var headerSize=U16(94);var offset=U32(96);var vlrCount=U32(100);var format=h[104]&63;var compressed=(h[104]&128)!=0;
        int[] sizes=[20,28,26,34,57,63,30,36,38];var recordLength=U16(105);
        if(headerSize<(minor==4?375:minor==3?235:227)||headerSize>f.Length||offset<headerSize||offset>f.Length||vlrCount>128||format>8||format is 4 or 5||recordLength<sizes[format]||recordLength>2048||(h[104]&64)!=0||minor<4&&format>=6)
            throw new InvalidDataException("Unsupported LAS header, waveform point format, record size or metadata bounds.");
        var count=minor==4&&U64(247)>0?checked((long)U64(247)):U32(107);
        if(count is <1 or >MaximumPoints||!compressed&&checked(count*recordLength)>f.Length-offset)throw new InvalidDataException("Original point count/record bounds exceed the verified local-reader limits.");
        double[] scale=[D(131),D(139),D(147)],shift=[D(155),D(163),D(171)],bounds=[D(187),D(203),D(219),D(179),D(195),D(211)];
        if(scale.Any(x=>!double.IsFinite(x)||x<=0)||shift.Concat(bounds).Any(x=>!double.IsFinite(x))||Enumerable.Range(0,3).Any(i=>bounds[i]>bounds[i+3]))throw new InvalidDataException("Original coordinate scale, offset or bounds are invalid.");
        var vlrs=new List<LidarVlr>();long metadata=0;
        string Text(byte[] b)=>Encoding.UTF8.GetString(b).TrimEnd('\0',' ');
        void Vlr(bool extended)
        {
            var fixedSize=extended?60:54;if(f.Position>f.Length-fixedSize)throw new InvalidDataException("Truncated LAS metadata.");
            r.ReadUInt16();var user=Text(r.ReadBytes(16));var id=r.ReadUInt16();var n=extended?checked((long)r.ReadUInt64()):r.ReadUInt16();var desc=Text(r.ReadBytes(32));
            metadata=checked(metadata+n+fixedSize);if(metadata>1048576||n>f.Length-f.Position)throw new InvalidDataException("LAS VLR/EVLR metadata exceeds 1 MiB or original file bounds.");
            var data=r.ReadBytes((int)n);vlrs.Add(new(user,id,desc,Convert.ToBase64String(data),extended));
        }
        f.Position=headerSize;for(var i=0;i<vlrCount;i++){Vlr(false);if(f.Position>offset)throw new InvalidDataException("LAS metadata overlaps point records.");}
        if(minor==4&&U32(243)>0){var evlr=U64(235);if(U32(243)>128||evlr<offset||evlr>(ulong)f.Length)throw new InvalidDataException("Unsupported LAS EVLR offset/count.");f.Position=(long)evlr;for(var i=0;i<U32(243);i++)Vlr(true);}
        var laz=vlrs.Where(v=>v.UserId=="laszip encoded"&&v.RecordId==22204).ToArray();
        if(compressed&&laz.Length!=1||!compressed&&laz.Length!=0)throw new InvalidDataException("LAS/LAZ compression declaration differs from its original VLR.");
        var copc=vlrs.Any(v=>v.UserId=="copc");
        if(copc)throw new InvalidDataException("COPC marker detected. Indexed COPC access is not validated by this LAS/LAZ reader; no ordinary LAZ or sequential fallback is claimed as COPC support.");
        var wktVlr=vlrs.SingleOrDefault(v=>v.UserId=="LASF_Projection"&&v.RecordId==2112);var wkt=wktVlr is null?null:Text(Convert.FromBase64String(wktVlr.Base64));
        string? crs=null;var vertical="Unknown (not established by delivered original metadata)";var zunit="unknown";
        var keys=vlrs.SingleOrDefault(v=>v.UserId=="LASF_Projection"&&v.RecordId==34735);
        if(keys is not null)
        {
            var b=Convert.FromBase64String(keys.Base64);if(b.Length<8||b.Length%2!=0)throw new InvalidDataException("Corrupt LAS GeoKey directory.");
            int Key(int key){var n=BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6));if(n>128||8+n*8>b.Length)throw new InvalidDataException("LAS GeoKeys exceed original metadata.");for(var k=0;k<n;k++){var i=8+k*8;if(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i))==key){if(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i+2))!=0||BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i+4))!=1)return 0;return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i+6));}}return 0;}
            var projected=Key(3072);var geographic=Key(2048);if(projected is >0 and <32767){if(projected==4326)throw new InvalidDataException("Geographic authority cannot be declared as projected coordinates.");crs="EPSG:"+projected;}else if(geographic is >0 and <32767){if(geographic!=4326)throw new InvalidDataException("Only original WGS84 geographic coordinates are supported.");crs="EPSG:"+geographic;}
            var unit=Key(3076);if(unit!=0&&unit!=9001)throw new InvalidDataException("Projected horizontal units are unsupported; no feet/metres conversion is inferred.");
            if(crs=="EPSG:4326"&&Key(2054) is not(0 or 9102))throw new InvalidDataException("Original geographic angular units must be degrees; no radians/degrees substitution.");
            var v=Key(4096);if(v is >0 and <32767)vertical="EPSG:"+v;zunit=Key(4099) switch{9001=>"m",9002=>"ft",9003=>"US survey ft",_=>"unknown"};
        }
        if(wkt is not null)
        {
            var matches=Regex.Matches(wkt,"(?:AUTHORITY|ID)\\[\\s*\"EPSG\"\\s*,\\s*\"?(\\d+)\"?\\s*\\]",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
            var known=matches.Select(m=>"EPSG:"+m.Groups[1].Value).Where(LidarCoordinates.Supported).Distinct().ToArray();
            // A nested geographic base CRS cannot replace the outer projected CRS.
            if(wkt.Contains("PROJCS[",StringComparison.Ordinal)||wkt.Contains("PROJCRS[",StringComparison.Ordinal))known=known.Where(c=>c!="EPSG:4326").ToArray();
            if(known.Length!=1)throw new InvalidDataException("WKT horizontal reference is missing, ambiguous or unsupported; no transformation was guessed.");
            if(crs is not null&&crs!=known[0])throw new InvalidDataException("LAS WKT/GeoKey horizontal references disagree.");crs=known[0];
            var start=wkt.IndexOf("PROJCS[",StringComparison.Ordinal);if(start<0)start=wkt.IndexOf("PROJCRS[",StringComparison.Ordinal);if(start<0)start=wkt.IndexOf("GEOGCS[",StringComparison.Ordinal);if(start<0)start=wkt.IndexOf("GEOGCRS[",StringComparison.Ordinal);
            if(start<0)throw new InvalidDataException("Original horizontal WKT structure is unsupported.");var depth=0;var quote=false;var end=-1;
            for(var k=start;k<wkt.Length;k++){var c=wkt[k];if(c=='\"'){if(quote&&k+1<wkt.Length&&wkt[k+1]=='\"'){k++;continue;}quote=!quote;}if(quote)continue;if(c=='[')depth++;if(c==']'&&--depth==0){end=k+1;break;}}
            if(end<0)throw new InvalidDataException("Original WKT brackets are incomplete.");
            var units=Regex.Matches(wkt[start..end],"(?:LENGTHUNIT|ANGLEUNIT|UNIT)\\[\\s*\"[^\"]*\"\\s*,\\s*([-+0-9.eE]+)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
            if(units.Count==0||!double.TryParse(units[^1].Groups[1].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var factor)||Math.Abs(factor-(crs=="EPSG:4326"?Math.PI/180:1))>1e-14)throw new InvalidDataException("Original WKT horizontal units conflict with the supported authority; no unit conversion is guessed.");
            // Preserve compound vertical WKT without guessing units or a conversion.
            if(wkt.Contains("VERT_CS[",StringComparison.Ordinal)||wkt.Contains("VERTCRS[",StringComparison.Ordinal))vertical="Original compound vertical WKT retained; transformations unverified";
        }
        if(crs is null||!LidarCoordinates.Supported(crs))throw new InvalidDataException("A supported original horizontal CRS is required: WGS84 geographic, Web Mercator or WGS84 UTM. No NAD83/state-plane substitution or datum guessing.");
        string[] dimensions=["X","Y","Z","Intensity","ReturnNumber","NumberOfReturns","Classification","ClassificationFlags","ScanDirectionFlag","EdgeOfFlightLine","ScanAngle","UserData","PointSourceId",..(format is 1 or 3 or >=6?new[]{"GpsTime"}:[]),..(format is 2 or 3 or 7 or 8?new[]{"Red","Green","Blue"}:[]),..(format==8?new[]{"Infrared"}:[]),..(format>=6?new[]{"ScannerChannel"}:[]),..(recordLength>sizes[format]?new[]{"OriginalExtraBytes (descriptors retained in VLRs)"}:[])];
        return new(minor,format,recordLength,count,compressed,scale,shift,bounds,crs,vertical,zunit,wkt,vlrs.ToArray(),false,dimensions,["Delivered historical source XYZ and raw integer coordinates retained. No vertical/unit transformation or simulator MSL equivalence.","LAS header creation date is not a survey acquisition date. Missing survey dates remain unknown.","Local unindexed LAS/LAZ reads are bounded sequential/chunk-seek work; no COPC spatial-index claim."]);
    }
}

public static class LidarCoordinates
{
    public static void ValidateDomain(GeographicBounds b)
    {ArgumentNullException.ThrowIfNull(b);if(new[]{b.West,b.East,b.South,b.North}.Any(x=>!double.IsFinite(x))||b.West< -180||b.East>180||b.South< -90||b.North>90||b.West>b.East||b.South>b.North)throw new InvalidDataException("Historical source index bounds must be finite and ordered; split the dateline. Bounds are candidates, not measured corridor coverage.");}
    public static bool Supported(string value)=>value is "EPSG:4326" or "EPSG:3857"||int.TryParse(value.Replace("EPSG:",""),out var e)&&(e is >=32601 and <=32660 or >=32701 and <=32760);
    public static Coordinates Geographic(string crs,double x,double y)
    {
        if(!double.IsFinite(x)||!double.IsFinite(y)||!Supported(crs))throw new InvalidDataException("Unsupported or nonfinite original horizontal coordinates.");
        Coordinates p;
        if(crs=="EPSG:4326")p=new(y,x);
        else if(crs=="EPSG:3857"){
            if(Math.Abs(x)>20037508.34278925||Math.Abs(y)>20037508.34278925)throw new InvalidDataException("Source point is outside the supported Web Mercator domain.");
            p=new(Math.Atan(Math.Sinh(y/6378137))*180/Math.PI,x/6378137*180/Math.PI);
        }else{
            var e=int.Parse(crs.AsSpan(5));if(x is <100000 or >900000||y is <0 or >10000000)throw new InvalidDataException("Source point is outside the verified UTM domain.");
            var q=GeographicLib.Projections.TransverseMercator.UTM.Reverse((e%100)*6-183,x-500000,y-(e>=32700?10000000:0));p=new(q.Item1,q.Item2);
            if(p.Latitude is <-80 or >84||e<32700&&p.Latitude<0||e>=32700&&p.Latitude>0)throw new InvalidDataException("Source point is outside its declared UTM hemisphere.");
        }
        if(!MissionPlanning.Position(p.Latitude,p.Longitude))throw new InvalidDataException("Source point has invalid geographic position.");return p;
    }
    public static GeographicBounds Domain(LidarLasHeader h)
    {
        if(h.HorizontalCrs is not("EPSG:4326" or "EPSG:3857")){
            var e=int.Parse(h.HorizontalCrs!.AsSpan(5));var center=(e%100)*6-183;return new(Math.Max(-180,center-12),e>=32700?-80:0,Math.Min(180,center+12),e>=32700?0:84);
        }
        var lo=Geographic(h.HorizontalCrs!,h.NativeBounds[0],h.NativeBounds[1]);var hi=Geographic(h.HorizontalCrs!,h.NativeBounds[3],h.NativeBounds[4]);return new(lo.Longitude,lo.Latitude,hi.Longitude,hi.Latitude);
    }
}
