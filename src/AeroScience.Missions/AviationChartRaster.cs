using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AeroScience.Missions;

// Cartographic color rasters only. This never enters a scientific source/sample pipeline.
// Callers supply an already verified retained original and a source-hash-bound main-body cutline.
public static class AviationChartRaster
{
    public sealed record Pixel(double X, double Y);
    public sealed record Location(double Longitude, double Latitude);
    public sealed record Description(int Width, int Height, int Bands, string Crs, Location[] MainBody);
    private const string Library = "aeroscience-chart-gdal";
    private static readonly object Gate = new();
    private static readonly List<FileStream> RuntimeLocks = [];
    private static readonly Dictionary<string, Input> Originals = new(StringComparer.OrdinalIgnoreCase);
    private static string? loadedDirectory;
    private static IntPtr nativeHandle;
    private static readonly N.Error QuietErrors = (_, _, _) => { };
    private static readonly string[] Dlls = ["gdal.dll", "geotiff.dll", "json-c.dll", "libcurl.dll", "liblzma.dll", "proj_9.dll", "sqlite3.dll", "tiff.dll", "z.dll"];
    static AviationChartRaster() => NativeLibrary.SetDllImportResolver(typeof(AviationChartRaster).Assembly, (name, _, _) => name == Library ? nativeHandle : IntPtr.Zero);

    public static void Initialize(string directory)
    {
        lock (Gate)
        {
            directory = Path.GetFullPath(directory);
            if (loadedDirectory is not null)
            {
                if (!string.Equals(loadedDirectory, directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A different chart runtime cannot replace the loaded runtime.");
                return;
            }
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("The reviewed chart runtime requires Windows x64.");
            var locks = new List<FileStream>();
            try
            {
                var manifestPath = Path.Combine(directory, "manifest.json");
                EnsureRegular(manifestPath, 4 * 1024 * 1024);
                using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath), new() { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("runtime").GetString() != "win-x64" ||
                    root.GetProperty("gdalVersion").GetString() != "3.12.4" || root.GetProperty("networkGridsAllowed").GetBoolean() ||
                    !root.GetProperty("nativeAudit").GetProperty("importsAccountedFor").GetBoolean())
                    throw new InvalidDataException("Unsupported chart runtime identity or native audit.");
                var files = root.GetProperty("files").EnumerateArray().ToArray();
                if (files.Length is < 12 or > 256) throw new InvalidDataException("Chart runtime inventory exceeds its bound.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                foreach (var file in files)
                {
                    var relative = file.GetProperty("path").GetString() ?? "";
                    if (relative.Length is < 1 or > 240 || relative.StartsWith('/') || relative.Contains('\\') || relative.Contains(':') ||
                        relative.Split('/').Any(p => p is "" or "." or "..") || !names.Add(relative))
                        throw new InvalidDataException("Unsafe or duplicate chart runtime member.");
                    var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                    var length = EnsureRegular(path, 32 * 1024 * 1024);
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    locks.Add(stream);
                    if (length != file.GetProperty("bytes").GetInt64() || Hash(stream) != file.GetProperty("sha256").GetString())
                        throw new InvalidDataException("Chart runtime/data content changed.");
                    total = checked(total + length);
                }
                if (total > 192L * 1024 * 1024 || total != root.GetProperty("totalBytes").GetInt64() ||
                    !names.Contains("share/proj/proj.db") || Dlls.Any(n => !names.Contains("bin/" + n)))
                    throw new InvalidDataException("Incomplete chart runtime/data inventory.");
                foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked chart runtime paths are unsupported.");
                    if (File.Exists(path) && Path.GetRelativePath(directory, path).Replace('\\', '/') is var relative &&
                        relative != "manifest.json" && !names.Contains(relative)) throw new InvalidDataException("Unlisted chart runtime member.");
                }
                // Disable dynamic plugins, sidecars and network/grid discovery before loading any raster.
                Environment.SetEnvironmentVariable("PROJ_NETWORK", "OFF");
                Environment.SetEnvironmentVariable("PROJ_DATA", Path.Combine(directory, "share", "proj"));
                Environment.SetEnvironmentVariable("GDAL_DATA", Path.Combine(directory, "share", "gdal"));
                Environment.SetEnvironmentVariable("GDAL_DRIVER_PATH", "disable");
                nativeHandle = NativeLibrary.Load(Path.Combine(directory, "bin", "gdal.dll"), typeof(AviationChartRaster).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
                N.CPLSetConfigOption("GDAL_PAM_ENABLED", "NO"); N.CPLSetConfigOption("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR");
                N.CPLSetConfigOption("PROJ_NETWORK", "OFF"); N.CPLSetConfigOption("GDAL_NUM_THREADS", "1");
                // Native diagnostic strings can contain local paths. API errors remain explicit
                // and sanitized; source filenames/URLs never leak through native stderr.
                N.CPLSetErrorHandler(QuietErrors);
                N.GDALAllRegister(); N.GDALSetCacheMax64(32L * 1024 * 1024);
                if (Text(N.GDALVersionInfo("RELEASE_NAME")) != "3.12.4") throw new InvalidDataException("Loaded chart codec differs from the reviewed version.");
                RuntimeLocks.AddRange(locks); locks.Clear(); loadedDirectory = directory;
            }
            finally { foreach (var stream in locks) stream.Dispose(); }
        }
    }

    public static Description Inspect(string original, string originalSha256, Pixel[] mainBody, CancellationToken token = default)
    {
        lock (Gate)
        {
            var input = Open(original, originalSha256, mainBody, token);
            var source = N.OSRNewSpatialReference(input.Crs); var target = N.OSRNewSpatialReference(null); IntPtr transform = IntPtr.Zero;
            try
            {
                if (source == IntPtr.Zero || target == IntPtr.Zero || N.OSRImportFromEPSG(target, 4326) != 0) throw new InvalidDataException("Chart geographic CRS unavailable.");
                N.OSRSetAxisMappingStrategy(source, 0); N.OSRSetAxisMappingStrategy(target, 0); transform = N.OCTNewCoordinateTransformation(source, target);
                if (transform == IntPtr.Zero) throw new InvalidDataException("Chart geographic transformation unavailable.");
                var points = new List<Location>();
                foreach (var pixel in mainBody)
                {
                    token.ThrowIfCancellationRequested(); var world = input.World(pixel); double[] x = [world.X], y = [world.Y], z = [0];
                    if (N.OCTTransform(transform, 1, x, y, z) == 0 || !double.IsFinite(x[0]) || !double.IsFinite(y[0]) || x[0] is < -180 or > 180 || y[0] is < -85 or > 85)
                        throw new NotSupportedException("Chart crosses unsupported geographic display bounds.");
                    points.Add(new(x[0], y[0]));
                }
                return new(input.Width, input.Height, input.Bands, input.Crs, points.ToArray());
            }
            finally { if (transform != IntPtr.Zero) N.OCTDestroyCoordinateTransformation(transform); if (source != IntPtr.Zero) N.OSRDestroySpatialReference(source); if (target != IntPtr.Zero) N.OSRDestroySpatialReference(target); }
        }
    }

    public static byte[] Tile(string original, string originalSha256, Pixel[] mainBody, int zoom, int column, int row, CancellationToken token = default)
    {
        if (zoom is < 3 or > 15 || column < 0 || row < 0 || column >= 1 << zoom || row >= 1 << zoom) throw new ArgumentOutOfRangeException(nameof(zoom));
        lock (Gate)
        {
            var input = Open(original, originalSha256, mainBody, token);
            const double half = 20037508.342789244; var span = 2 * half / (1 << zoom);
            var west = -half + column * span; var north = half - row * span;
            string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
            var cutline = "POLYGON ((" + string.Join(",", mainBody.Select(p => { var w = input.World(p); return Number(w.X) + " " + Number(w.Y); })) + "))";
            // Native GDAL owns projection/resampling; no ad-hoc Lambert or satellite numeric reader.
            string[] args = ["-of", "MEM", "-t_srs", "EPSG:3857", "-te", Number(west), Number(north - span), Number(west + span), Number(north),
                "-ts", "256", "256", "-r", "near", "-ot", "Byte", "-dstalpha", "-wm", "16", "-wo", "NUM_THREADS=1",
                "-cutline", cutline, "-cutline_srs", input.Crs];
            using var strings = new Strings(args); var options = N.GDALWarpAppOptionsNew(strings.Pointer, IntPtr.Zero);
            if (options == IntPtr.Zero) throw new InvalidDataException("Chart warp options unavailable.");
            IntPtr output = IntPtr.Zero;
            N.Progress progress = (_, _, _) => token.IsCancellationRequested ? 0 : 1;
            try
            {
                N.GDALWarpAppOptionsSetProgress(options, progress, IntPtr.Zero);
                output = N.GDALWarp("", IntPtr.Zero, 1, [input.Handle], options, out var error);
                token.ThrowIfCancellationRequested();
                if (output == IntPtr.Zero || error != 0 || N.GDALGetRasterXSize(output) != 256 || N.GDALGetRasterYSize(output) != 256)
                    throw new InvalidDataException("Bounded geographic chart rendering failed.");
                var count = N.GDALGetRasterCount(output); var rgba = new byte[256 * 256 * 4];
                if (input.Bands == 1 && count == 2)
                {
                    var band = N.GDALGetRasterBand(output, 1); var indices = Read(band); var alpha = Read(N.GDALGetRasterBand(output, 2));
                    var table = N.GDALGetRasterColorTable(N.GDALGetRasterBand(input.Handle, 1));
                    if (table == IntPtr.Zero || N.GDALGetColorEntryCount(table) != 256) throw new InvalidDataException("Unsupported original chart palette.");
                    var palette = new short[256][];
                    for (var i = 0; i < 256; i++) { palette[i] = new short[4]; var entry = N.GDALGetColorEntry(table, i); if (entry == IntPtr.Zero) throw new InvalidDataException("Missing palette entry."); Marshal.Copy(entry, palette[i], 0, 4); }
                    for (var i = 0; i < indices.Length; i++)
                    { token.ThrowIfCancellationRequested(); var color = palette[indices[i]]; for (var c = 0; c < 3; c++) rgba[i * 4 + c] = checked((byte)color[c]); rgba[i * 4 + 3] = checked((byte)((alpha[i] * color[3] + 127) / 255)); }
                }
                else if (input.Bands == 3 && count == 4)
                    for (var c = 0; c < 4; c++) { var values = Read(N.GDALGetRasterBand(output, c + 1)); for (var i = 0; i < values.Length; i++) rgba[i * 4 + c] = values[i]; token.ThrowIfCancellationRequested(); }
                else throw new InvalidDataException("Unsupported cartographic raster/alpha bands.");
                return Png(rgba);
            }
            finally { if (output != IntPtr.Zero) N.GDALClose(output); N.GDALWarpAppOptionsFree(options); GC.KeepAlive(progress); }
        }
    }

    private static byte[] Read(IntPtr band)
    {
        var bytes = new byte[256 * 256];
        if (band == IntPtr.Zero || N.GDALRasterIO(band, 0, 0, 0, 256, 256, bytes, 256, 256, 1, 0, 0) != 0) throw new InvalidDataException("Chart color window unavailable.");
        return bytes;
    }
    private static long EnsureRegular(string path, long maximum)
    {
        path = Path.GetFullPath(path);
        for (var parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked chart paths are unsupported.");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 || file.Length > maximum) throw new InvalidDataException("Chart file exceeds its bounded size or is unavailable.");
        return file.Length;
    }
    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    private static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var bytes = new byte[65536]; int read;
        while ((read = stream.Read(bytes)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(bytes.AsSpan(0, read)); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static Input Open(string original, string sha256, Pixel[] ring, CancellationToken token)
    {
        if (loadedDirectory is null) throw new InvalidOperationException("Verified chart runtime is unavailable.");
        if (ring.Length is < 4 or > 512 || ring[0] != ring[^1]) throw new InvalidDataException("Invalid source-pixel main-body cutline.");
        token.ThrowIfCancellationRequested(); original = Path.GetFullPath(original);
        // Windows FileShare.Read holds byte identity while cached; no full-file hash or raster
        // scan runs for each map tile. At most eight originals remain open; GDAL cache is 32 MiB.
        var key = original + "|" + sha256 + "|" + JsonSerializer.Serialize(ring);
        if (Originals.TryGetValue(key, out var retained)) return retained;
        EnsureRegular(original, 512L * 1024 * 1024);
        // Hold the original against Windows modification/replacement for the complete native read.
        var file = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
        IntPtr dataset = IntPtr.Zero;
        try
        {
            if (sha256.Length != 64 || Hash(file, token) != sha256) throw new InvalidDataException("Retained FAA chart changed; original provenance was not restamped.");
            token.ThrowIfCancellationRequested();
            using var drivers = new Strings(["GTiff"]);
            dataset = N.GDALOpenEx(Path.GetFullPath(original), 2, drivers.Pointer, IntPtr.Zero, IntPtr.Zero);
            if (dataset == IntPtr.Zero) throw new InvalidDataException("Verified original GeoTIFF cannot be decoded.");
            var width = N.GDALGetRasterXSize(dataset); var height = N.GDALGetRasterYSize(dataset); var bands = N.GDALGetRasterCount(dataset);
            if (width is < 1 or > 100000 || height is < 1 or > 100000 || (long)width * height > 2_000_000_000 || bands is not (1 or 3)) throw new NotSupportedException("Unsupported bounded chart dimensions/color model.");
            for (var b = 1; b <= bands; b++) if (N.GDALGetRasterDataType(N.GDALGetRasterBand(dataset, b)) != 1) throw new NotSupportedException("Only original byte-valued cartographic colors are supported.");
            if (ring.Length is < 4 or > 512 || ring[0] != ring[^1] || ring.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0 || p.X > width || p.Y > height)) throw new InvalidDataException("Invalid source-pixel main-body cutline.");
            var transform = new double[6]; var crs = Text(N.GDALGetProjectionRef(dataset));
            if (crs.Length is < 1 or > 16384 || N.GDALGetGeoTransform(dataset, transform) != 0 || transform.Any(v => !double.IsFinite(v)) || Math.Abs(transform[1] * transform[5] - transform[2] * transform[4]) < 1e-20) throw new InvalidDataException("Original CRS/affine metadata unavailable.");
            var result = new Input(dataset, file, width, height, bands, crs, transform);
            if (Originals.Count >= 8) { var old = Originals.First(); Originals.Remove(old.Key); old.Value.Dispose(); }
            Originals.Add(key, result); return result;
        }
        catch { if (dataset != IntPtr.Zero) N.GDALClose(dataset); file.Dispose(); throw; }
    }
    private sealed record Input(IntPtr Handle, FileStream File, int Width, int Height, int Bands, string Crs, double[] Transform) : IDisposable
    {
        public Pixel World(Pixel p) => new(Transform[0] + p.X * Transform[1] + p.Y * Transform[2], Transform[3] + p.X * Transform[4] + p.Y * Transform[5]);
        public void Dispose() { N.GDALClose(Handle); File.Dispose(); }
    }
    private sealed class Strings : IDisposable
    {
        private readonly IntPtr[] values; public IntPtr Pointer { get; }
        public Strings(string[] args) { values = args.Select(Marshal.StringToCoTaskMemUTF8).ToArray(); Pointer = Marshal.AllocCoTaskMem((args.Length + 1) * IntPtr.Size); Marshal.Copy([.. values, IntPtr.Zero], 0, Pointer, args.Length + 1); }
        public void Dispose() { foreach (var p in values) Marshal.FreeCoTaskMem(p); Marshal.FreeCoTaskMem(Pointer); }
    }
    private static string Text(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? "";
    private static byte[] Png(byte[] pixels)
    {
        using var output = new MemoryStream(); output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string kind, byte[] bytes)
        {
            var type = Encoding.ASCII.GetBytes(kind); var length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            output.Write(length); output.Write(type); output.Write(bytes); uint crc = 0xffffffff;
            foreach (var b in type.Concat(bytes)) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); output.Write(length);
        }
        Chunk("IHDR", [0, 0, 1, 0, 0, 0, 1, 0, 8, 6, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true)) for (var row = 0; row < 256; row++) { z.WriteByte(0); z.Write(pixels, row * 1024, 1024); }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return output.ToArray();
    }

    private static class N
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Progress(double complete, IntPtr message, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void Error(int kind, int number, IntPtr message);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void GDALAllRegister();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void GDALSetCacheMax64(long bytes);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr GDALVersionInfo([MarshalAs(UnmanagedType.LPUTF8Str)] string key);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void CPLSetConfigOption([MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr CPLSetErrorHandler(Error handler);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr GDALOpenEx([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint flags, IntPtr drivers, IntPtr options, IntPtr siblings);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void GDALClose(IntPtr dataset);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int GDALGetRasterXSize(IntPtr dataset);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int GDALGetRasterYSize(IntPtr dataset);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int GDALGetRasterCount(IntPtr dataset);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr GDALGetRasterBand(IntPtr dataset, int band);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int GDALGetRasterDataType(IntPtr band);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int GDALRasterIO(IntPtr band, int read, int x, int y, int width, int height, [Out] byte[] buffer, int bw, int bh, int type, int px, int line);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr GDALGetRasterColorTable(IntPtr band);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int GDALGetColorEntryCount(IntPtr colors);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr GDALGetColorEntry(IntPtr colors, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int GDALGetGeoTransform(IntPtr dataset, [Out] double[] transform);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr GDALGetProjectionRef(IntPtr dataset);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr OSRNewSpatialReference([MarshalAs(UnmanagedType.LPUTF8Str)] string? wkt);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int OSRImportFromEPSG(IntPtr srs, int code);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void OSRSetAxisMappingStrategy(IntPtr srs, int strategy);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void OSRDestroySpatialReference(IntPtr srs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr OCTNewCoordinateTransformation(IntPtr source, IntPtr target);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int OCTTransform(IntPtr transform, int count, [In, Out] double[] x, [In, Out] double[] y, [In, Out] double[] z);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void OCTDestroyCoordinateTransformation(IntPtr transform);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr GDALWarpAppOptionsNew(IntPtr args, IntPtr binaryOptions);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void GDALWarpAppOptionsSetProgress(IntPtr options, Progress progress, IntPtr data);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr GDALWarp([MarshalAs(UnmanagedType.LPUTF8Str)] string destination, IntPtr dataset, int count, [In] IntPtr[] sources, IntPtr options, out int error);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void GDALWarpAppOptionsFree(IntPtr options);
    }
}
