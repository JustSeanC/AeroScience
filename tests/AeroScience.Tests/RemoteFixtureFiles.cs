using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using BitMiracle.LibTiff.Classic;

namespace AeroScience.Tests;

// Synthetic numeric fixtures ONLY in test projects; never included in production publishing.
public static class RemoteFixtureFiles
{
    public static string Create(string root,RemoteKind kind,bool cloudy=false)
    {
        Directory.CreateDirectory(root);var bands=new List<SourceBand>();
        void Add(string id,float value,string unit="1",double scale=1,double offset=0,bool coarse=false)
        {
            var file=id+".tif";GeoTiff(Path.Combine(root,file),value,coarse?32:64,coarse?.002:.001,byteSamples:id=="SCL");
            bands.Add(new(id,file,"geotiff",Unit:unit,Scale:scale,Offset:offset,NoData:-9999));
        }
        switch(kind)
        {
            case RemoteKind.Thermal:Add("ST_B10",40000,"K",.00341802,149);Add("QA_PIXEL",cloudy?8:0);break;
            case RemoteKind.Multispectral:Add("B02",1000,"reflectance",.0001);Add("B03",3000,"reflectance",.0001);Add("B04",2000,"reflectance",.0001);Add("B08",6000,"reflectance",.0001);Add("SCL",cloudy?9:4,coarse:true);break;
            case RemoteKind.Terrain:Add("elevation",123.25f,"m");break;
            case RemoteKind.Hyperspectral:
                File.WriteAllText(Path.Combine(root,"cube.hdr"),"ENVI\nsamples = 64\nlines = 64\nbands = 16\nheader offset = 0\ndata type = 4\ninterleave = bsq\nbyte order = 0\nmap info = {UTM, 1, 1, 499680, 4428000, 10, 10, 18, North, WGS-84}\nwavelength units = Nanometers\nwavelength = {"+string.Join(",",Enumerable.Range(0,16).Select(i=>400+10*i))+"}\nbbl = {1,1,1,1,1,0,1,1,1,1,1,1,1,1,1,1}\ndata ignore value = -9999\n");
                using(var file=File.Create(Path.Combine(root,"cube.img")))using(var writer=new BinaryWriter(file))
                    for(var band=0;band<16;band++)for(var pixel=0;pixel<64*64;pixel++)writer.Write(.1f+band*.01f);
                for(var i=0;i<16;i++)bands.Add(new("wavelength-"+i,"cube.img","envi",i,"reflectance",BadBand:i==5,HeaderFile:"cube.hdr"));break;
        }
        var manifest=new ScientificSourceManifest(1,"SYNTHETIC TEST FIXTURE — "+kind,kind,"Isolated automated test data","Test-only numeric fixture","Not a genuine external observation; never distributed as scientific source data","fixture-scene",new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero),kind==RemoteKind.Terrain?"NAVD88 (fixture label, no conversion)":"not applicable",bands.ToArray());
        var path=Path.Combine(root,"source.json");File.WriteAllText(path,JsonSerializer.Serialize(manifest,WireJson.Options));return path;
    }
    public static void GeoTiff(string file,float value,int size=64,double pixel=.001,bool byteSamples=false,bool gradient=false,bool pixelPoint=false,double west=-75.032,double north=40.032)
    {
        using var tif=Tiff.Open(file,"w");
        tif.MergeFieldInfo([
            new(TiffTag.GEOTIFF_MODELPIXELSCALETAG,-1,-1,TiffType.DOUBLE,FieldBit.Custom,true,true,"ModelPixelScale"),
            new(TiffTag.GEOTIFF_MODELTIEPOINTTAG,-1,-1,TiffType.DOUBLE,FieldBit.Custom,true,true,"ModelTiepoint"),
            new(TiffTag.GEOTIFF_GEOKEYDIRECTORYTAG,-1,-1,TiffType.SHORT,FieldBit.Custom,true,true,"GeoKeys"),
            new(TiffTag.GDAL_NODATA,-1,-1,TiffType.ASCII,FieldBit.Custom,true,false,"GDAL_NODATA")],4);
        tif.SetField(TiffTag.IMAGEWIDTH,size);tif.SetField(TiffTag.IMAGELENGTH,size);tif.SetField(TiffTag.SAMPLESPERPIXEL,1);tif.SetField(TiffTag.BITSPERSAMPLE,byteSamples?8:32);tif.SetField(TiffTag.SAMPLEFORMAT,byteSamples?SampleFormat.UINT:SampleFormat.IEEEFP);tif.SetField(TiffTag.ROWSPERSTRIP,8);tif.SetField(TiffTag.PHOTOMETRIC,Photometric.MINISBLACK);tif.SetField(TiffTag.PLANARCONFIG,PlanarConfig.CONTIG);tif.SetField(TiffTag.COMPRESSION,Compression.DEFLATE);
        tif.SetField(TiffTag.GEOTIFF_MODELPIXELSCALETAG,3,new[]{pixel,pixel,0});tif.SetField(TiffTag.GEOTIFF_MODELTIEPOINTTAG,6,new[]{0d,0,0,west,north,0});tif.SetField(TiffTag.GEOTIFF_GEOKEYDIRECTORYTAG,16,new short[]{1,1,0,3,1024,0,1,2,1025,0,1,(short)(pixelPoint?2:1),2048,0,1,4326});tif.SetField(TiffTag.GDAL_NODATA,"-9999");
        var bytes=new byte[size*(byteSamples?1:4)];for(var x=0;x<size;x++){if(byteSamples)bytes[x]=(byte)value;else BitConverter.GetBytes(value).CopyTo(bytes,x*4);}for(var row=0;row<size;row++){if(gradient)for(var x=0;x<size;x++)BitConverter.GetBytes((float)(row*size+x)).CopyTo(bytes,x*4);if(!tif.WriteScanline(bytes,row))throw new IOException("Fixture TIFF write failed.");}tif.WriteDirectory();
    }
}
