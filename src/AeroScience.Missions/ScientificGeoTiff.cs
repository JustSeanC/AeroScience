using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;
using BitMiracle.LibTiff.Classic;
namespace AeroScience.Missions;

public sealed partial class ScientificRaster
{
    // Float64 preserves saved numeric values. NaN nodata and affine corner transform are interoperable with GDAL/rasterio.
    public static void WriteGeoTiff(string path,RasterGrid grid,double?[][] values,string[] units,object metadata,CancellationToken token=default)
    {
        if(grid.Width is <1 or >1024||grid.Height is <1 or >1024||values.Length is <1 or >4||values.Any(v=>v.Length!=(long)grid.Width*grid.Height)||units.Length!=values.Length)
            throw new ArgumentException("GeoTIFF tiles support 1–4 numeric channels and at most 1024×1024 cells.");
        _=ValidateGrid(grid);
        using var output=Tiff.Open(path,"w")??throw new IOException("Cannot create numeric GeoTIFF.");
        output.SetField(TiffTag.IMAGEWIDTH,grid.Width);output.SetField(TiffTag.IMAGELENGTH,grid.Height);output.SetField(TiffTag.BITSPERSAMPLE,64);
        output.SetField(TiffTag.SAMPLESPERPIXEL,values.Length);output.SetField(TiffTag.SAMPLEFORMAT,SampleFormat.IEEEFP);output.SetField(TiffTag.PLANARCONFIG,PlanarConfig.CONTIG);
        output.SetField(TiffTag.PHOTOMETRIC,Photometric.MINISBLACK);output.SetField(TiffTag.COMPRESSION,Compression.DEFLATE);output.SetField(TiffTag.ROWSPERSTRIP,16);
        if(values.Length>1)output.SetField(TiffTag.EXTRASAMPLES,values.Length-1,Enumerable.Repeat(ExtraSample.UNSPECIFIED,values.Length-1).ToArray());
        if(grid.RowX==0&&grid.ColumnY==0)
        {
            output.SetField(TiffTag.GEOTIFF_MODELPIXELSCALETAG,3,new[]{grid.PixelWidth,-grid.PixelHeight,0d});
            output.SetField(TiffTag.GEOTIFF_MODELTIEPOINTTAG,6,new[]{0d,0d,0d,grid.OriginX,grid.OriginY,0d});
        }
        else output.SetField(TiffTag.GEOTIFF_MODELTRANSFORMATIONTAG,16,new[]{grid.PixelWidth,grid.RowX,0,grid.OriginX,grid.ColumnY,grid.PixelHeight,0,grid.OriginY,0,0,1d,0,0,0,0,1d});
        var code=short.Parse(grid.Crs[5..],CultureInfo.InvariantCulture);short[] keys=[1,1,0,3,1024,0,1,(short)(code==4326?2:1),1025,0,1,1,(short)(code==4326?2048:3072),0,1,code];
        output.SetField(TiffTag.GEOTIFF_GEOKEYDIRECTORYTAG,keys.Length,keys);output.SetField(TiffTag.GDAL_NODATA,"nan");
        output.SetField(TiffTag.IMAGEDESCRIPTION,JsonSerializer.Serialize(metadata,WireJson.Options));
        var xml="<GDALMetadata>"+string.Join("",units.Select((u,i)=>$"<Item name=\"UNITTYPE\" sample=\"{i}\">{System.Security.SecurityElement.Escape(u)}</Item>"))+"</GDALMetadata>";
        output.SetField((TiffTag)42112,xml);
        var row=new byte[checked(grid.Width*values.Length*8)];
        for(var y=0;y<grid.Height;y++)
        {
            token.ThrowIfCancellationRequested();for(var x=0;x<grid.Width;x++)for(var b=0;b<values.Length;b++)
            {var v=values[b][y*grid.Width+x];BitConverter.TryWriteBytes(row.AsSpan((x*values.Length+b)*8,8),v is {} n&&double.IsFinite(n)?n:double.NaN);}
            if(!output.WriteScanline(row,y))throw new IOException("Numeric GeoTIFF scanline write failed.");
        }
        if(!output.WriteDirectory())throw new IOException("Numeric GeoTIFF directory write failed.");
    }
}
