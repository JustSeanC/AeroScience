using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using AeroScience.Shared;
using BitMiracle.LibTiff.Classic;

namespace AeroScience.Missions;

// Narrow, explicit readers. No file extension is treated as proof of format.
public sealed class ScientificRaster : IDisposable
{
    private static readonly Tiff.TiffExtendProc? previousExtender;
    static ScientificRaster()
    {
        previousExtender=Tiff.SetTagExtender(tif=>
        {
            previousExtender?.Invoke(tif);
            tif.MergeFieldInfo([
                new(TiffTag.GEOTIFF_MODELPIXELSCALETAG,-1,-1,TiffType.DOUBLE,FieldBit.Custom,true,true,"ModelPixelScale"),
                new(TiffTag.GEOTIFF_MODELTIEPOINTTAG,-1,-1,TiffType.DOUBLE,FieldBit.Custom,true,true,"ModelTiepoint"),
                new(TiffTag.GEOTIFF_MODELTRANSFORMATIONTAG,-1,-1,TiffType.DOUBLE,FieldBit.Custom,true,true,"ModelTransformation"),
                new(TiffTag.GEOTIFF_GEOKEYDIRECTORYTAG,-1,-1,TiffType.SHORT,FieldBit.Custom,true,true,"GeoKeys"),
                new(TiffTag.GEOTIFF_GEODOUBLEPARAMSTAG,-1,-1,TiffType.DOUBLE,FieldBit.Custom,true,true,"GeoDoubleParams"),
                new(TiffTag.GEOTIFF_GEOASCIIPARAMSTAG,-1,-1,TiffType.ASCII,FieldBit.Custom,true,false,"GeoAsciiParams"),
                new((TiffTag)42112,-1,-1,TiffType.ASCII,FieldBit.Custom,true,false,"GDAL_METADATA"),
                new(TiffTag.GDAL_NODATA,-1,-1,TiffType.ASCII,FieldBit.Custom,true,false,"GDAL_NODATA")],8);
        });
    }
    private readonly Tiff? tiff;
    private readonly FileStream? envi;
    private readonly SourceBand band;
    private readonly int channels, bits, sampleFormat, byteOrder;
    private readonly long headerOffset;
    private readonly string interleave = "bsq";
    private byte[]? block;
    private int blockIndex = -1;
    private readonly int tileWidth, tileHeight, rowsPerStrip;
    public RasterGrid Grid { get; }
    public double? NativeNoData { get; }
    public double? NativeWavelengthNm { get; }
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public ScientificRaster(string root, SourceBand definition)
    {
        band = definition;
        var file = SafeFile(root, band.File);
        try
        {
            if (band.Format == "geotiff")
            {
                using (var signature = File.OpenRead(file))
                {
                    Span<byte> header = stackalloc byte[4]; signature.ReadExactly(header);
                    if (!(header[0] == 73 && header[1] == 73 && header[2] == 42 && header[3] == 0
                        || header[0] == 77 && header[1] == 77 && header[2] == 0 && header[3] == 42))
                        throw new InvalidDataException("Only classic TIFF/COG with a valid TIFF header is supported; BigTIFF is not supported.");
                }
                ValidateTiffDirectory(file);
                tiff = Tiff.Open(file, "r") ?? throw new InvalidDataException("TIFF could not be decoded.");
                var width = Int(TiffTag.IMAGEWIDTH); var height = Int(TiffTag.IMAGELENGTH);
                channels = Int(TiffTag.SAMPLESPERPIXEL, 1); bits = Int(TiffTag.BITSPERSAMPLE);
                sampleFormat = Int(TiffTag.SAMPLEFORMAT, 1);
                if (channels != 1 || band.Channel != 0 || bits is not (8 or 16 or 32 or 64) || sampleFormat is < 1 or > 3
                    || sampleFormat == 3 && bits is 8 or 16 || sampleFormat != 3 && bits == 64 || Int(TiffTag.PLANARCONFIG, 1) != 1)
                    throw new InvalidDataException("GeoTIFF supports single-band UInt8/Int8/UInt16/Int16/UInt32/Int32/Float32/Float64 rasters, with channel 0.");
                var scale = Doubles(TiffTag.GEOTIFF_MODELPIXELSCALETAG); var tie = Doubles(TiffTag.GEOTIFF_MODELTIEPOINTTAG);
                if (scale.Length < 2 || tie.Length < 6 || tiff.GetField(TiffTag.GEOTIFF_MODELTRANSFORMATIONTAG) is not null)
                    throw new InvalidDataException("A north-up GeoTIFF scale/tiepoint transform is required; rotated grids must be reprojected first.");
                var keys = tiff.GetField(TiffTag.GEOTIFF_GEOKEYDIRECTORYTAG);
                var shorts = keys?[^1].ToShortArray() ?? throw new InvalidDataException("GeoTIFF CRS keys are required.");
                var crs = 0;var model=0;var geographic=0;var projected=0; var pixelPoint = false;
                if (shorts.Length < 4 || shorts.Length != 4 + shorts[3] * 4) throw new InvalidDataException("Invalid GeoTIFF key directory.");
                for (var i = 4; i < shorts.Length; i += 4)
                {
                    if (shorts[i + 1] != 0 || shorts[i + 2] != 1) continue;
                    if(shorts[i]==1024)model=shorts[i+3];
                    if(shorts[i]==2048)geographic=(ushort)shorts[i+3];if(shorts[i]==3072)projected=(ushort)shorts[i+3];
                    if (shorts[i] == 1025) pixelPoint = shorts[i + 3] == 2;
                }
                crs=model switch{1=>projected,2=>geographic,_=>throw new InvalidDataException("An explicit supported projected/geographic GeoTIFF model is required.")};
                if(model==1&&projected is 0 or 32767||model==2&&projected is >0 and not 32767)throw new InvalidDataException("Contradictory or user-defined GeoTIFF projection is unsupported.");
                var dx = scale[0]; var dy = -scale[1]; var shift = pixelPoint ? .5 : 0;
                Grid = ValidateGrid(new(width, height, "EPSG:" + crs, tie[3] - (tie[0] + shift) * dx, tie[4] - (tie[1] + shift) * dy, dx, dy));
                if (double.TryParse(tiff.GetField(TiffTag.GDAL_NODATA)?[0].ToString(), NumberStyles.Float, Inv, out var nodata)) NativeNoData = nodata;
                if (tiff.IsTiled()) { tileWidth = Int(TiffTag.TILEWIDTH); tileHeight = Int(TiffTag.TILELENGTH); }
                else rowsPerStrip = Math.Min(height, Int(TiffTag.ROWSPERSTRIP, height));
                var blockBytes = tiff.IsTiled() ? tiff.TileSize() : tiff.StripSize();
                if (blockBytes is <= 0 or > 33554432) throw new InvalidDataException("Decoded TIFF blocks must be at most 32 MiB. Retile the source for bounded reading.");
                block = new byte[blockBytes];
            }
            else if (band.Format == "envi")
            {
                var header = SafeFile(root, band.HeaderFile ?? throw new InvalidDataException("ENVI requires its original .hdr file."));
                if (new FileInfo(header).Length > 1048576) throw new InvalidDataException("ENVI header is too large.");
                var text = File.ReadAllText(header);
                if (!text.TrimStart().StartsWith("ENVI", StringComparison.Ordinal)) throw new InvalidDataException("Invalid ENVI header signature.");
                var fields = Regex.Matches(text, @"(?m)^\s*([\w ]+)\s*=\s*(\{[^}]*\}|[^\r\n]+)",RegexOptions.None,TimeSpan.FromSeconds(2))
                    .ToDictionary(m => m.Groups[1].Value.Trim().ToLowerInvariant(), m => m.Groups[2].Value.Trim().Trim('{', '}'));
                string Field(string key) => fields.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Missing ENVI field: " + key);
                int Number(string key) => int.Parse(Field(key), Inv);
                var width = Number("samples"); var height = Number("lines"); channels = Number("bands");
                var type = Number("data type"); bits = type switch { 1 => 8, 2 or 12 => 16, 3 or 4 or 13 => 32, 5 => 64, _ => throw new InvalidDataException("Unsupported ENVI numeric type.") };
                sampleFormat = type is 4 or 5 ? 3 : type is 1 or 12 or 13 ? 1 : 2;
                byteOrder = Number("byte order"); headerOffset = fields.TryGetValue("header offset", out var offset) ? long.Parse(offset, Inv) : 0;
                interleave = Field("interleave").Trim().ToLowerInvariant();
                if (interleave is not ("bsq" or "bil" or "bip") || byteOrder is < 0 or > 1 || channels is < 1 or > 512 || band.Channel < 0 || band.Channel >= channels || headerOffset < 0)
                    throw new InvalidDataException("Invalid ENVI layout, channel count, byte order or band index.");
                var info = Field("map info").Split(',').Select(s => s.Trim()).ToArray();
                if(info.Skip(10).Any(s=>s.StartsWith("rotation",StringComparison.OrdinalIgnoreCase)&&(!double.TryParse(s.Split('=').Last(),NumberStyles.Float,Inv,out var angle)||angle!=0)))throw new InvalidDataException("Rotated ENVI grids require reprojection before import.");
                double Parse(int i) => double.Parse(info[i], Inv);
                if (info.Length < 10 || !info[0].Equals("UTM", StringComparison.OrdinalIgnoreCase) || !info[9].Replace(" ", "").Equals("WGS-84", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ENVI import currently requires UTM WGS-84 map info. Reproject other CRSs first.");
                if (!int.TryParse(info[7], out var zone) || zone is < 1 or > 60 || !(info[8].Equals("North", StringComparison.OrdinalIgnoreCase) || info[8].Equals("South", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Invalid UTM zone/hemisphere.");
                Grid = ValidateGrid(new(width, height, "EPSG:" + ((info[8].Equals("North", StringComparison.OrdinalIgnoreCase) ? 32600 : 32700) + zone), Parse(3) - (Parse(1) - .5) * Parse(5), Parse(4) + (Parse(2) - .5) * Parse(6), Parse(5), -Parse(6)));
                if (fields.TryGetValue("data ignore value", out var ignore)) NativeNoData = double.Parse(ignore, Inv);
                if (fields.TryGetValue("wavelength", out var wavelengths))
                {
                    var list = wavelengths.Split(',').Select(v => double.Parse(v.Trim(), Inv)).ToArray();
                    var unit = Field("wavelength units").Trim().ToLowerInvariant();
                    if (list.Length != channels || unit is not ("nanometers" or "micrometers" or "nm" or "um")) throw new InvalidDataException("ENVI wavelengths must match all bands and state nanometers or micrometers.");
                    NativeWavelengthNm = list[band.Channel] * (unit is "micrometers" or "um" ? 1000 : 1);
                }
                if (fields.TryGetValue("bbl", out var bad))
                {
                    var values = bad.Split(',').Select(v => int.Parse(v.Trim(), Inv)).ToArray();
                    if (values.Length != channels || values.Any(v=>v is not (0 or 1)) || values[band.Channel] == 0 && !band.BadBand) throw new InvalidDataException("Honor the ENVI bad-band list in the source manifest.");
                }
                if (fields.TryGetValue("reflectance scale factor", out var factor) && Math.Abs(band.Scale - 1 / double.Parse(factor, Inv)) > 1e-10)
                    throw new InvalidDataException("Reflectance scale must match the ENVI header's scale factor.");
                var expected = checked(headerOffset + (long)width * height * channels * bits / 8);
                if (new FileInfo(file).Length != expected) throw new InvalidDataException("ENVI file size does not match its dimensions/header.");
                envi = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            }
            else throw new InvalidDataException("Supported inputs are geotiff and ENVI. NetCDF, HDF, LAS/LAZ and BigTIFF are not implemented.");
        }
        catch { tiff?.Dispose(); envi?.Dispose(); throw; }
    }
    private int Int(TiffTag tag, int fallback = 0) => tiff!.GetField(tag)?[0].ToInt() ?? fallback;
    private static void ValidateTiffDirectory(string file)
    {
        using var stream=File.OpenRead(file);Span<byte> header=stackalloc byte[8];stream.ReadExactly(header);var little=header[0]==73;
        uint U32(ReadOnlySpan<byte> bytes)=>little?BinaryPrimitives.ReadUInt32LittleEndian(bytes):BinaryPrimitives.ReadUInt32BigEndian(bytes);
        ushort U16(ReadOnlySpan<byte> bytes)=>little?BinaryPrimitives.ReadUInt16LittleEndian(bytes):BinaryPrimitives.ReadUInt16BigEndian(bytes);
        var offset=U32(header[4..]);if(offset<8||offset>stream.Length-2)throw new InvalidDataException("Invalid TIFF directory offset.");
        stream.Position=offset;stream.ReadExactly(header[..2]);var count=U16(header[..2]);if(count>512||offset+2L+count*12L+4>stream.Length)throw new InvalidDataException("TIFF directory exceeds supported metadata bounds.");
        long total=0;Span<byte> entry=stackalloc byte[12];
        for(var i=0;i<count;i++)
        {
            stream.ReadExactly(entry);var size=U16(entry[2..]) switch{1 or 2 or 6 or 7=>1,3 or 8=>2,4 or 9 or 11 or 13=>4,5 or 10 or 12=>8,_=>throw new InvalidDataException("Unsupported TIFF metadata type.")};
            var bytes=(long)U32(entry[4..])*size;total+=bytes;
            if(bytes>1048576||total>4194304||bytes>4&&U32(entry[8..])+(long)bytes>stream.Length)throw new InvalidDataException("TIFF metadata is oversized or truncated; import a bounded numeric subset.");
        }
    }
    private double[] Doubles(TiffTag tag) => tiff!.GetField(tag)?[^1].ToDoubleArray() ?? [];
    public static string SafeFile(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is "." or ".." or "")) throw new InvalidDataException("Source files require safe relative paths.");
        var full = Path.GetFullPath(Path.Combine(root, relative)); var parent = new FileInfo(full) as FileSystemInfo;
        for (; parent is not null; parent = parent is FileInfo f ? f.Directory : ((DirectoryInfo)parent).Parent)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Source imports cannot follow links/junctions.");
            if (parent.FullName.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) break;
        }
        if (!File.Exists(full)) throw new IOException("A required source file is missing: " + Path.GetFileName(relative));
        return full;
    }
    private static RasterGrid ValidateGrid(RasterGrid grid)
    {
        if (grid.Width is < 1 or > 1000000 || grid.Height is < 1 or > 1000000 || (long)grid.Width * grid.Height > 100000000000
            || new[] { grid.OriginX, grid.OriginY, grid.PixelWidth, grid.PixelHeight }.Any(v => !double.IsFinite(v)) || grid.PixelWidth <= 0 || grid.PixelHeight >= 0)
            throw new InvalidDataException("Invalid/unsupported north-up raster dimensions or transform.");
        var referenceLongitude=int.TryParse(grid.Crs.Replace("EPSG:",""),out var code)&&code is >=32601 and <=32760?(code%100)*6-183:0;
        _ = Project(new(0, referenceLongitude), grid.Crs); return grid;
    }
    public static (double X, double Y) Project(Coordinates point, string crs)
    {
        if (!MissionPlanning.Position(point.Latitude, point.Longitude)) throw new ArgumentException("Invalid WGS84 point.");
        if (crs == "EPSG:4326") return (point.Longitude, point.Latitude);
        if (!int.TryParse(crs.Replace("EPSG:", ""), out var code) || code is < 32601 or > 32760 || code is > 32660 and < 32701)
            throw new InvalidDataException("Only WGS84 geographic and WGS84 UTM EPSG:32601–32660/32701–32760 are supported; no datum is silently converted.");
        if (point.Latitude is < -80 or > 84) throw new InvalidDataException("UTM does not support polar coordinates; use geographic EPSG:4326.");
        // Snyder transverse Mercator series, WGS84; metre units, k0=.9996.
        var zone = code % 100; var south = code >= 32700;
        const double a = 6378137, e2 = .0066943799901413165, k = .9996;
        var lat = point.Latitude * Math.PI / 180; var lon = point.Longitude * Math.PI / 180;
        var central = (zone * 6 - 183) * Math.PI / 180;
        var delta = Math.Atan2(Math.Sin(lon - central), Math.Cos(lon - central));
        if (Math.Abs(delta) > 12 * Math.PI / 180) throw new InvalidDataException("Point is too far from the source UTM zone for a supported transformation.");
        var n = a / Math.Sqrt(1 - e2 * Math.Pow(Math.Sin(lat), 2)); var t = Math.Pow(Math.Tan(lat), 2); var c = e2 / (1 - e2) * Math.Pow(Math.Cos(lat), 2); var A = Math.Cos(lat) * delta;
        var m = a * ((1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256) * lat
            - (3 * e2 / 8 + 3 * e2 * e2 / 32 + 45 * e2 * e2 * e2 / 1024) * Math.Sin(2 * lat)
            + (15 * e2 * e2 / 256 + 45 * e2 * e2 * e2 / 1024) * Math.Sin(4 * lat) - 35 * e2 * e2 * e2 / 3072 * Math.Sin(6 * lat));
        return (500000 + k * n * (A + (1 - t + c) * Math.Pow(A, 3) / 6 + (5 - 18 * t + t * t + 72 * c - 58 * e2 / (1 - e2)) * Math.Pow(A, 5) / 120),
            (south ? 10000000 : 0) + k * (m + n * Math.Tan(lat) * (A * A / 2 + (5 - t + 9 * c + 4 * c * c) * Math.Pow(A, 4) / 24 + (61 - 58 * t + t * t + 600 * c - 330 * e2 / (1 - e2)) * Math.Pow(A, 6) / 720)));
    }
    public bool Covers(Coordinates p)
    {
        try { var (x, y) = Project(p, Grid.Crs); return x >= Grid.OriginX && x < Grid.OriginX + Grid.Width * Grid.PixelWidth && y <= Grid.OriginY && y > Grid.OriginY + Grid.Height * Grid.PixelHeight; }
        catch (InvalidDataException) { return false; }
    }
    public double? Read(Coordinates point)
    {
        if (band.BadBand || !Covers(point)) return null;
        var (x, y) = Project(point, Grid.Crs);
        var col = (int)Math.Floor((x - Grid.OriginX) / Grid.PixelWidth); var row = (int)Math.Floor((y - Grid.OriginY) / Grid.PixelHeight);
        var size = bits / 8; Span<byte> bytes = stackalloc byte[8];
        if (envi is not null)
        {
            var sample = interleave == "bsq" ? ((long)band.Channel * Grid.Height + row) * Grid.Width + col
                : interleave == "bil" ? ((long)row * channels + band.Channel) * Grid.Width + col : ((long)row * Grid.Width + col) * channels + band.Channel;
            envi.Position = checked(headerOffset + sample * size); envi.ReadExactly(bytes[..size]);
            if (byteOrder == 1) bytes[..size].Reverse();
        }
        else
        {
            var index = tiff!.IsTiled() ? tiff.ComputeTile(col, row, 0, 0) : tiff.ComputeStrip(row, 0);
            if (blockIndex != index)
            {
                var read = tiff.IsTiled() ? tiff.ReadEncodedTile(index, block, 0, -1) : tiff.ReadEncodedStrip(index, block, 0, -1);
                var required = tiff.IsTiled() ? block!.Length : Math.Min(rowsPerStrip, Grid.Height - index * rowsPerStrip) * Grid.Width * size;
                if (read < required) throw new InvalidDataException("TIFF block was truncated or could not be decoded.");
                blockIndex = index;
            }
            var offset = tiff.IsTiled() ? ((row % tileHeight) * tileWidth + col % tileWidth) * size : ((row % rowsPerStrip) * Grid.Width + col) * size;
            block!.AsSpan(offset, size).CopyTo(bytes);
            if (!BitConverter.IsLittleEndian) bytes[..size].Reverse(); // LibTiff decodes into host order.
        }
        var value = sampleFormat == 3 ? bits == 32 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)) : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes))
            : sampleFormat == 2 ? bits == 8 ? (sbyte)bytes[0] : bits == 16 ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt32LittleEndian(bytes)
            : bits == 8 ? bytes[0] : bits == 16 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        bool Missing(double? declared) => declared is {} n && (value == n || sampleFormat == 3 && bits == 32 && value == (double)(float)n);
        if (!double.IsFinite(value) || Missing(band.NoData) || Missing(NativeNoData)) return null;
        var scaled = value * band.Scale + band.Offset; return double.IsFinite(scaled) ? scaled : null;
    }
    public void Dispose() { tiff?.Dispose(); envi?.Dispose(); }
}
