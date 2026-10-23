using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;
using OSGeo.OSR;

// Research executable only. No application project references this package graph.
// Synthetic compressed NetCDF is not original EMIT, QA, swath or GLT acceptance.
if (args.Length != 2) throw new ArgumentException("Supply retained FAA TIFF and output JSON paths.");
var stopwatch = Stopwatch.StartNew();
GdalBase.ConfigureAll();
Gdal.SetConfigOption("GDAL_CACHEMAX", "32");
Gdal.SetConfigOption("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR");
var results = new List<object>();
var directory = Path.Combine(Path.GetTempPath(), "aeroscience-gis-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    foreach (var name in new[] { "GTiff", "netCDF" })
    {
        using var driver = Gdal.GetDriverByName(name) ?? throw new Exception("Missing " + name);
        var path = Path.Combine(directory, name + (name == "GTiff" ? ".tif" : ".nc"));
        using (var output = driver.Create(path, 3, 2, 1, DataType.GDT_Float32,
            name == "netCDF" ? ["FORMAT=NC4C", "COMPRESS=DEFLATE"] : ["COMPRESS=DEFLATE"]))
        {
            Check(output.SetGeoTransform([-75d, .01, 0, 40d, 0, -.01]));
            using var reference = new SpatialReference("");
            CheckCode(reference.ImportFromEPSG(4326));
            CheckCode(reference.ExportToWkt(out var wkt, null));
            Check(output.SetProjection(wkt));
            using var band = output.GetRasterBand(1);
            Check(band.SetNoDataValue(-9999));
            Check(band.WriteRaster(0, 0, 3, 2, new float[] { 1, 2, 3, 4, -9999, 6 }, 3, 2, 0, 0));
            output.FlushCache();
        }
        using (var source = Gdal.Open(path, Access.GA_ReadOnly))
        {
            if (source.RasterXSize != 3 || source.RasterYSize != 2) throw new Exception("Dimensions changed.");
            using var band = source.GetRasterBand(1);
            var values = new float[2];
            Check(band.ReadRaster(1, 1, 2, 1, values, 2, 1, 0, 0));
            if (values[0] != -9999 || values[1] != 6) throw new Exception("Bounded numeric window differs.");
            band.GetNoDataValue(out var fill, out var supported);
            if (supported == 0 || fill != -9999) throw new Exception("Nodata differs.");
            results.Add(new { driver = name, dimensions = new[] { 3, 2 }, window = new[] { 1, 1, 2, 1 }, values, fill, bytes = new FileInfo(path).Length });
        }
        // On Windows this also establishes that native dataset disposal releases file locks.
        File.Delete(path);
    }
    if (Gdal.GetDriverByName("HDF5") is not { } hdf) throw new Exception("HDF5 driver missing.");
    hdf.Dispose();
    using var expectedDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "faa-window-reference.json")));
    var expected = expectedDocument.RootElement;
    using (var input = File.OpenRead(args[0]))
        if (Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() != expected.GetProperty("sha256").GetString())
            throw new Exception("Genuine FAA TIFF hash differs from the independent reference.");
    using (var source = Gdal.Open(args[0], Access.GA_ReadOnly))
    {
        var dimensions = expected.GetProperty("dimensions").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        if (source.RasterXSize != dimensions[0] || source.RasterYSize != dimensions[1]) throw new Exception("FAA dimensions differ.");
        using var band = source.GetRasterBand(1);
        var offset = expected.GetProperty("windowOffset").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var values = new byte[64];
        Check(band.ReadRaster(offset[0], offset[1], 8, 8, values, 8, 8, 0, 0));
        if (!values.SequenceEqual(expected.GetProperty("window").EnumerateArray().Select(v => v.GetByte())))
            throw new Exception("Genuine FAA palette window differs from rasterio.");
        var transform = new double[6];
        source.GetGeoTransform(transform);
        using var crs = new SpatialReference(source.GetProjection());
        using var target = new SpatialReference("");
        CheckCode(target.ImportFromEPSG(4326));
        crs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        target.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        using var projection = new CoordinateTransformation(crs, target);
        var coordinates = new List<double[]>();
        var pixels = expected.GetProperty("pixelCoordinates");
        var geographic = expected.GetProperty("wgs84");
        for (var i = 0; i < pixels.GetArrayLength(); i++)
        {
            var x = pixels[i][0].GetDouble(); var y = pixels[i][1].GetDouble();
            var actual = new double[3];
            projection.TransformPoint(actual, transform[0] + x * transform[1] + y * transform[2], transform[3] + x * transform[4] + y * transform[5], 0);
            if (!double.IsFinite(actual[0]) || !double.IsFinite(actual[1]) ||
                Math.Abs(actual[0] - geographic[i][0].GetDouble()) > 1e-9 || Math.Abs(actual[1] - geographic[i][1].GetDouble()) > 1e-9)
                throw new Exception("FAA coordinate transform differs from independent PROJ by more than 1e-9 degrees.");
            coordinates.Add(actual);
        }
        results.Add(new { driver = "genuine-FAA-GTiff", source.RasterXSize, source.RasterYSize, windowOffset = offset, values, transform, wgs84 = coordinates });
    }
    var process = Process.GetCurrentProcess();
    var modules = process.Modules.Cast<ProcessModule>().Where(m => File.Exists(m.FileName)).Select(m => new { name = m.ModuleName, fileVersion = m.FileVersionInfo.FileVersion }).OrderBy(m => m.name).ToArray();
    var report = new { evidence = "Research native process, synthetic NetCDF/HDF compression, genuine FAA bytes/coordinate transforms; not EMIT or chart neatline/control-point acceptance", platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        gdal = Gdal.VersionInfo("--version"), elapsedSeconds = stopwatch.Elapsed.TotalSeconds, peakResidentBytes = process.PeakWorkingSet64, modules, results,
        productionDistributionApproved = false };
    File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine("PASS: actual native load, compressed GeoTIFF/NetCDF windows/fill, HDF5 driver, disposal, genuine FAA hash/values and independent CRS transforms.");
}
finally { Directory.Delete(directory, true); }
static void Check(CPLErr error) { if (error != CPLErr.CE_None) throw new Exception("Native operation failed: " + error); }
static void CheckCode(int error) { if (error != 0) throw new Exception("Spatial reference operation failed: " + error); }
