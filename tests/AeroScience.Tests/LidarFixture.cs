using System.Text;
namespace AeroScience.Tests;

// SYNTHETIC LAS records for deterministic format/attribute/geometry edge cases.
internal static class LidarFixture
{
    internal static string Modern(string root,int format)
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,Guid.NewGuid().ToString("N")+".las");var wkt=Encoding.UTF8.GetBytes("GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433],AUTHORITY[\"EPSG\",\"4326\"]]\0");
        var h=new byte[375];using(var w=new BinaryWriter(new MemoryStream(h))){w.Write(Encoding.ASCII.GetBytes("LASF"));w.BaseStream.Position=6;w.Write((ushort)16);w.BaseStream.Position=24;w.Write((byte)1);w.Write((byte)4);w.BaseStream.Position=94;w.Write((ushort)375);w.Write((uint)(375+54+wkt.Length));w.Write((uint)1);w.Write((byte)format);w.Write((ushort)((format==6?30:format==7?36:38)+4));w.BaseStream.Position=131;foreach(var x in new[]{.000001,.000001,.01,-75d,40d,0d,-74.99998,-75d,40.00002,40d,123.45,123.45})w.Write(x);w.BaseStream.Position=247;w.Write(3UL);w.BaseStream.Position=319;w.Write(3UL);}
        using var file=File.Create(path);using var writer=new BinaryWriter(file);writer.Write(h);writer.Write((ushort)0);writer.Write(Encoding.ASCII.GetBytes("LASF_Projection".PadRight(16,'\0')));writer.Write((ushort)2112);writer.Write((ushort)wkt.Length);writer.Write(new byte[32]);writer.Write(wkt);
        for(var i=0;i<3;i++){writer.Write(i*10);writer.Write(i*10);writer.Write(12345);writer.Write((ushort)40000);writer.Write((byte)(9|(12<<4)));writer.Write((byte)(13|(3<<4)|64|128));writer.Write((byte)200);writer.Write((byte)50);writer.Write((short)-1234);writer.Write((ushort)50000);writer.Write(1234.567);if(format>=7)foreach(ushort c in new ushort[]{1000,2000,3000})writer.Write(c);if(format==8)writer.Write((ushort)4000);writer.Write(12345U);}
        return path;
    }
    internal static string Write(string root,int format=1,int epsg=4326,int unit=9001,(int X,int Y,int Z)[]? points=null,bool copc=false)
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,Guid.NewGuid().ToString("N")+".las");
        points??=[(0,0,12345),(10,10,23456),(20,20,-4321)];
        var geo=new MemoryStream();using(var b=new BinaryWriter(geo,Encoding.UTF8,true)){foreach(ushort v in new ushort[]{1,1,0,2,2048,0,1,(ushort)epsg,3076,0,1,(ushort)unit})b.Write(v);}
        var vlrs=new List<(string User,ushort Id,byte[] Bytes)>{("LASF_Projection",34735,geo.ToArray())};
        if(copc)vlrs.Add(("copc",1,new byte[160]));
        var header=227;var offset=header+vlrs.Sum(v=>54+v.Bytes.Length);int[] lengths=[20,28,26,34];var recordLength=lengths[format];
        var h=new byte[header];using(var w=new BinaryWriter(new MemoryStream(h))){w.Write(Encoding.ASCII.GetBytes("LASF"));w.BaseStream.Position=24;w.Write((byte)1);w.Write((byte)2);w.BaseStream.Position=94;w.Write((ushort)header);w.Write((uint)offset);w.Write((uint)vlrs.Count);w.Write((byte)format);w.Write((ushort)recordLength);w.Write((uint)points.Length);w.BaseStream.Position=131;foreach(var x in new[]{.000001,.000001,.01,-75d,40d,0d})w.Write(x);foreach(var x in new[]{-75+points.Max(p=>p.X)*.000001,-75+points.Min(p=>p.X)*.000001,40+points.Max(p=>p.Y)*.000001,40+points.Min(p=>p.Y)*.000001,points.Max(p=>p.Z)*.01,points.Min(p=>p.Z)*.01})w.Write(x);}
        using var file=File.Create(path);using var writer=new BinaryWriter(file);writer.Write(h);
        foreach(var v in vlrs){writer.Write((ushort)0);writer.Write(Encoding.ASCII.GetBytes(v.User.PadRight(16,'\0')));writer.Write(v.Id);writer.Write((ushort)v.Bytes.Length);writer.Write(new byte[32]);writer.Write(v.Bytes);}
        foreach(var p in points){writer.Write(p.X);writer.Write(p.Y);writer.Write(p.Z);writer.Write((ushort)54321);writer.Write((byte)(2|(3<<3)|(1<<6)));writer.Write((byte)(6|128));writer.Write((sbyte)-7);writer.Write((byte)42);writer.Write((ushort)321);if(format is 1 or 3)writer.Write(123456.789);if(format is 2 or 3)foreach(ushort c in new ushort[]{100,200,300})writer.Write(c);}
        return path;
    }
}
