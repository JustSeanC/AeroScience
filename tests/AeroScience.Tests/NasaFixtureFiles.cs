using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Tests;
// Synthetic numeric files; recorded NASA metadata is not genuine numeric acceptance.
internal static class NasaFixtureFiles
{
    public static ScientificCatalogItem Item(string family)
    {
        var c=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata",$"nasa-{family}-collections.json"))).RootElement.GetProperty("feed").GetProperty("entry")[0];var e=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata",$"nasa-{family}-granules.json"))).RootElement.GetProperty("feed").GetProperty("entry")[0];
        return ScientificCatalog.DescribeNasa("nasa-"+family,JsonSerializer.SerializeToElement(c),JsonSerializer.SerializeToElement(e));
    }
    public static string Cube(string root,ScientificCatalogItem item,int bands=224,string interleave="bil",bool bigEndian=false,bool float64=false)
    {
        Directory.CreateDirectory(root);var stem=Path.GetFileNameWithoutExtension(item.Nasa!.Assets.First(a=>a.Role=="Original numeric binary"&&a.Name.Contains("_corr_",StringComparison.Ordinal)).Name);
        var binary=Path.Combine(root,stem+".bin");
        using(var stream=File.Create(binary))using(var writer=new BinaryWriter(stream))
        {
            void Write(int band,int pixel){var value=band==5?-9999:band*.001+pixel*.01;var b=float64?BitConverter.GetBytes(value):BitConverter.GetBytes((float)value);if(bigEndian)Array.Reverse(b);writer.Write(b);}
            if(interleave=="bsq")for(int band=0;band<bands;band++)for(int pixel=0;pixel<12;pixel++)Write(band,pixel);
            else if(interleave=="bil")for(int y=0;y<3;y++)for(int band=0;band<bands;band++)for(int x=0;x<4;x++)Write(band,y*4+x);
            else for(int pixel=0;pixel<12;pixel++)for(int band=0;band<bands;band++)Write(band,pixel);
        }
        var center=item.Bounds.Center;var (originX,originY)=ScientificRaster.Project(center,"EPSG:32611");
        var header=Path.Combine(root,stem+".hdr");
        File.WriteAllText(header,FormattableString.Invariant($"ENVI\nsamples = 4\nlines = 3\nbands = {bands}\ndata type = {(float64?5:4)}\ninterleave = {interleave}\nbyte order = {(bigEndian?1:0)}\nmap info = {{UTM, 1, 1, {originX}, {originY}, 10, 10, 11, North, WGS-84, rotation=10}}\nwavelength units = Nanometers\ndata units = reflectance\ndata ignore value = -9999\nwavelength = {{")+string.Join(",",Enumerable.Range(0,bands).Select(b=>400+5*b))+"}\nbbl = {"+string.Join(",",Enumerable.Range(0,bands).Select(b=>b==5?0:1))+"}\n");
        foreach(var asset in item.Nasa.Assets.Where(a=>a.Role=="Provider SHA-256"))
        {var original=Path.Combine(root,asset.Name[..^7]);if(File.Exists(original))File.WriteAllText(Path.Combine(root,asset.Name),Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original))).ToLowerInvariant()+"  "+Path.GetFileName(original));}
        return header;
    }
}
