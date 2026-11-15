using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.TestHost;

// Read-only, bounded inspection of explicitly selected RETAINED originals.
// No ScienceStore, mission, download, import, authorization or source mutation.
internal static class RetainedRasterProbe
{
    public static void Run(string folder,string destination)
    {
        folder=Path.GetFullPath(folder);destination=Path.GetFullPath(destination);
        if(destination.Equals(folder,StringComparison.OrdinalIgnoreCase)||destination.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Keep evidence outside the original source folder.");
        if(Directory.Exists(destination)||File.Exists(destination))throw new IOException("Preserve prior evidence; use a fresh output directory.");
        static void NoLinks(string path){for(var p=new DirectoryInfo(Path.GetDirectoryName(path)!);p is not null;p=p.Parent)if(p.LinkTarget is not null||(p.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked source/output directories are refused.");if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked files are refused.");}
        NoLinks(Path.Combine(folder,"source.json"));NoLinks(destination);
        var descriptor=Path.Combine(folder,"source.json");if(new FileInfo(descriptor).Length>1048576)throw new IOException("Source descriptor exceeds one MiB.");
        var source=JsonSerializer.Deserialize<ScientificSource>(File.ReadAllBytes(descriptor))??throw new IOException("Retained source descriptor required.");
        if(source.Manifest.Bands.Length is <1 or >16||source.Files.Length is <1 or >64||source.Files.Sum(f=>f.Bytes)>268435456)throw new IOException("Bounded raster source required (256 MiB total).");
        string Original(string relative){if(Path.IsPathRooted(relative)||relative.Contains(':')||relative.Contains('\\')||relative.Split('/').Contains(".."))throw new IOException("Original escapes source folder.");var path=Path.GetFullPath(Path.Combine(folder,relative));NoLinks(path);return path;}
        void Verify(){foreach(var file in source.Files){var path=Original(file.RelativePath);var size=new FileInfo(path).Length;if(size!=file.Bytes||size>268435456)throw new IOException("Original size changed/exceeds 256 MiB.");using var stream=File.OpenRead(path);if(Convert.ToHexStringLower(SHA256.HashData(stream))!=file.Sha256)throw new IOException("Original SHA-256 changed.");}}
        Verify();var bands=new List<object>();
        foreach(var band in source.Manifest.Bands)
        {
            if(band.Format!="geotiff"||!source.Files.Any(f=>f.RelativePath==band.File))throw new IOException("This probe requires retained GeoTIFF bands.");
            Original(band.File);using var raster=new ScientificRaster(folder,band);var grid=raster.Grid;
            if(!source.Grids.TryGetValue(band.Id,out var recorded)||recorded!=grid)throw new IOException("Inspected grid differs from retained metadata.");
            var total=checked((long)grid.Width*grid.Height);var count=(int)Math.Min(256,total);var samples=new List<object>();
            for(var i=0;i<count;i++){var index=count==1?0:(long)i*(total-1)/(count-1);var row=(int)(index/grid.Width);var column=(int)(index%grid.Width);var value=raster.ReadNativeCell(column,row);samples.Add(new{row,column,raw=value?.DecodedSourceValue,value=value?.Value,position=ScientificRaster.InspectionGridPoint(grid,column+.5,row+.5)});}
            bands.Add(new{band,grid,raster.NativeNoData,samples});
        }
        Verify();Directory.CreateDirectory(destination);File.Copy(descriptor,Path.Combine(destination,"source.json"));
        PreferenceStore.WriteAtomic(Path.Combine(destination,"raster-values.json"),new{schema="aeroscience-p08c-retained-raster-1",source.Id,source.ManifestSha256,bands,networkDownloads=0,originalsUnchanged=true,recordingOrScienceCommands=0,scope="Read-only genuine retained source samples; no new flight observations or terrain product acceptance"});
        Console.WriteLine($"Read-only retained raster probe completed: {bands.Count} bands, at most 256 original cells per band.");
    }
}
