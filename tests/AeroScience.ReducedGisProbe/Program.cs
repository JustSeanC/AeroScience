using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

// Test-only C ABI; no MaxRev package and no production native dependency or Python requirement.
if (args.Length != 4) throw new ArgumentException("Use native-library, original-TIFF, independent-reference-JSON and result-JSON.");
var native = Path.GetFullPath(args[0]); var original = Path.GetFullPath(args[1]);
var handle = NativeLibrary.Load(native, Assembly.GetExecutingAssembly(), DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) => name == G.Name ? handle : IntPtr.Zero);
Environment.SetEnvironmentVariable("PROJ_NETWORK", "OFF");
using var reference = JsonDocument.Parse(File.ReadAllText(args[2])); var r = reference.RootElement;
using (var stream = File.OpenRead(original))
    if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != r.GetProperty("sha256").GetString()) throw new IOException("Genuine FAA original differs; no restamping.");
G.GDALAllRegister(); G.GDALSetCacheMax64(64L * 1024 * 1024);
var version = Marshal.PtrToStringUTF8(G.GDALVersionInfo("RELEASE_NAME"));
if (version != "3.12.4") throw new IOException("Unexpected native GDAL version.");
var drivers = Enumerable.Range(0, G.GDALGetDriverCount()).Select(i => Marshal.PtrToStringUTF8(G.GDALGetDriverShortName(G.GDALGetDriver(i)))).ToArray();
if (!drivers.Contains("GTiff") || drivers.Any(d => d is "PDF" or "MySQL" or "MSSQLSpatial" or "ODBC")) throw new IOException("Required reduced-driver contract failed.");
var clock = Stopwatch.StartNew(); var dataset = G.GDALOpen(original, 0);
if (dataset == IntPtr.Zero) throw new IOException("Native original opening failed.");
var transformed = new List<double[]>(); var maximumDifference = 0d; var bytes = new byte[64]; var paletteCount = 0;
try
{
    var dimensions = r.GetProperty("dimensions").EnumerateArray().Select(v => v.GetInt32()).ToArray();
    if (G.GDALGetRasterXSize(dataset) != dimensions[0] || G.GDALGetRasterYSize(dataset) != dimensions[1]) throw new IOException("Original raster dimensions differ.");
    var band = G.GDALGetRasterBand(dataset, 1); var offset = r.GetProperty("windowOffset").EnumerateArray().Select(x => x.GetInt32()).ToArray();
    if (G.GDALRasterIO(band, 0, offset[0], offset[1], 8, 8, bytes, 8, 8, 1, 0, 0) != 0 ||
        !bytes.SequenceEqual(r.GetProperty("window").EnumerateArray().Select(v => v.GetByte()))) throw new IOException("Original palette-index window differs.");
    var colors = G.GDALGetRasterColorTable(band); paletteCount = colors == IntPtr.Zero ? 0 : G.GDALGetColorEntryCount(colors);
    if (paletteCount < 1) throw new IOException("Original chart palette missing.");
    var transform = new double[6]; if (G.GDALGetGeoTransform(dataset, transform) != 0) throw new IOException("Original affine missing.");
    var source = G.OSRNewSpatialReference(Marshal.PtrToStringUTF8(G.GDALGetProjectionRef(dataset))); var target = G.OSRNewSpatialReference(null); IntPtr conversion = IntPtr.Zero;
    try
    {
        if (source == IntPtr.Zero || target == IntPtr.Zero || G.OSRImportFromEPSG(target,4326) != 0) throw new IOException("Original CRS initialization failed.");
        G.OSRSetAxisMappingStrategy(source,0); G.OSRSetAxisMappingStrategy(target,0); conversion = G.OCTNewCoordinateTransformation(source,target);
        if (conversion == IntPtr.Zero) throw new IOException("Geographic transformation unavailable; no guessed projection.");
        var pixels = r.GetProperty("pixelCoordinates").EnumerateArray().ToArray(); var expected = r.GetProperty("wgs84").EnumerateArray().ToArray();
        for (var i=0;i<pixels.Length;i++)
        {
            var p = pixels[i].EnumerateArray().Select(v=>v.GetDouble()).ToArray();
            double[] x=[transform[0]+p[0]*transform[1]+p[1]*transform[2]], y=[transform[3]+p[0]*transform[4]+p[1]*transform[5]], z=[0];
            if (G.OCTTransform(conversion,1,x,y,z) == 0 || !double.IsFinite(x[0]) || !double.IsFinite(y[0])) throw new IOException("Native coordinate transform failed.");
            var e=expected[i].EnumerateArray().Select(v=>v.GetDouble()).ToArray();maximumDifference=Math.Max(maximumDifference,Math.Max(Math.Abs(x[0]-e[0]),Math.Abs(y[0]-e[1])));transformed.Add([x[0],y[0]]);
        }
        if(maximumDifference>1e-9)throw new IOException("Independent original-coordinate comparison failed.");
    }
    finally { if(conversion!=IntPtr.Zero)G.OCTDestroyCoordinateTransformation(conversion);if(source!=IntPtr.Zero)G.OSRDestroySpatialReference(source);if(target!=IntPtr.Zero)G.OSRDestroySpatialReference(target); }
}
finally { G.GDALClose(dataset); }
File.WriteAllText(args[3],JsonSerializer.Serialize(new { evidenceClass="Actual native research process and limited genuine FAA original window/CRS comparison; no chart neatline, application, EMIT or owner acceptance",version,drivers,paletteCount,window=bytes.Select(b=>(int)b),transformed,maximumDifference,elapsedSeconds=clock.Elapsed.TotalSeconds,peakResidentBytes=Process.GetCurrentProcess().PeakWorkingSet64,productionDependencyAdded=false },new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("PASS: reduced GDAL actual loading, original palette window and independent CRS values.");
static class G
{
    public const string Name="aeroscience-reduced-gdal";
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern void GDALAllRegister();
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern void GDALSetCacheMax64(long bytes);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern IntPtr GDALVersionInfo([MarshalAs(UnmanagedType.LPUTF8Str)]string key);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern int GDALGetDriverCount();
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern IntPtr GDALGetDriver(int i);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern IntPtr GDALGetDriverShortName(IntPtr driver);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern IntPtr GDALOpen([MarshalAs(UnmanagedType.LPUTF8Str)]string file,int access);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern void GDALClose(IntPtr dataset);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern int GDALGetRasterXSize(IntPtr dataset);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern int GDALGetRasterYSize(IntPtr dataset);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern IntPtr GDALGetRasterBand(IntPtr dataset,int band);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern int GDALRasterIO(IntPtr band,int read,int x,int y,int width,int height,[Out]byte[] buffer,int bw,int bh,int type,int px,int line);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern IntPtr GDALGetRasterColorTable(IntPtr band);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern int GDALGetColorEntryCount(IntPtr colors);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern int GDALGetGeoTransform(IntPtr dataset,[Out]double[] transform);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern IntPtr GDALGetProjectionRef(IntPtr dataset);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern IntPtr OSRNewSpatialReference([MarshalAs(UnmanagedType.LPUTF8Str)]string? wkt);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern int OSRImportFromEPSG(IntPtr srs,int code);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern void OSRSetAxisMappingStrategy(IntPtr srs,int strategy);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern void OSRDestroySpatialReference(IntPtr srs);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern IntPtr OCTNewCoordinateTransformation(IntPtr source,IntPtr target);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern int OCTTransform(IntPtr transform,int count,[In,Out]double[] x,[In,Out]double[] y,[In,Out]double[] z);
    [DllImport(Name,CallingConvention=CallingConvention.Cdecl)]public static extern void OCTDestroyCoordinateTransformation(IntPtr transform);
}
