using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AeroScience.Tests;

// Synthetic file-format fixtures only. Genuine FAA acceptance uses retained official originals.
public static class SyntheticAirspace
{
    public const string ClassUrl = "https://nfdc.faa.gov/webContent/28DaySub/2026-10-01/class_airspace_shape_files.zip";
    public const string SaaUrl = "https://nfdc.faa.gov/webContent/28DaySub/2026-10-01/aixm5.0.zip";
    public const string Prj = "GEOGCS[\"GCS_North_American_1983\",DATUM[\"D_North_American_1983\",SPHEROID[\"GRS_1980\",6378137.0,298.257222101]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";
    public static byte[] ClassArchive(string? fault = null)
    {
        double[][][] rings = [
            [[-76,39],[-76,41],[-74,41],[-74,39],[-76,39]],
            [[-75.5,39.5],[-74.5,39.5],[-74.5,40.5],[-75.5,40.5],[-75.5,39.5]]
        ];
        var bodies = new[] { Shape(rings), Shape([[[ -123,36],[-123,38],[-121,38],[-121,36],[-123,36]], [[-120,36],[-120,37],[-119,37],[-119,36],[-120,36]]]) };
        if (fault == "huge-count") BinaryPrimitives.WriteInt32LittleEndian(bodies[0].AsSpan(40), int.MaxValue);
        using var shp = new MemoryStream(); using var shx = new MemoryStream();
        using (var w = new BinaryWriter(shp, Encoding.UTF8, true))
        using (var x = new BinaryWriter(shx, Encoding.UTF8, true))
        {
            Header(w, 100 + bodies.Sum(b => b.Length + 8)); Header(x, 100 + 8 * bodies.Length);
            for (var i=0;i<bodies.Length;i++) { Big(x,(int)shp.Position/2);Big(x,bodies[i].Length/2);Big(w,i+1);Big(w,bodies[i].Length/2);w.Write(bodies[i]); }
        }
        var index = shx.ToArray(); if (fault == "index") index[^1] ^= 1;
        var fields = new[] { "NAME","CLASS","LOWER_VAL","LOWER_UOM","LOWER_CODE","UPPER_VAL","UPPER_UOM","UPPER_CODE","WKHR_CODE","WKHR_RMK","DST","GMTOFFSET" };
        string[][] rows = [["Synthetic Class B","B","0","FT","SFC","10000","FT","MSL","H24","","",""],
            ["Synthetic Class D","D","700","FT","SFC","-9998","","","HX","By published notice","Y","-5"]];
        var files = new Dictionary<string,byte[]> { ["Class_Airspace.shp"]=shp.ToArray(),["Class_Airspace.shx"]=index,["Class_Airspace.dbf"]=Dbf(fields,rows),["Class_Airspace.prj"]=Encoding.ASCII.GetBytes(fault=="crs"?"PROJCS[unsupported]":Prj) };
        return Zip(files);
    }
    private static byte[] Shape(double[][][] rings)
    {
        using var stream = new MemoryStream(); using var w=new BinaryWriter(stream);var points=rings.SelectMany(r=>r).ToArray();
        w.Write(5);w.Write(points.Min(p=>p[0]));w.Write(points.Min(p=>p[1]));w.Write(points.Max(p=>p[0]));w.Write(points.Max(p=>p[1]));w.Write(rings.Length);w.Write(points.Length);
        var offset=0;foreach(var ring in rings){w.Write(offset);offset+=ring.Length;}foreach(var p in points){w.Write(p[0]);w.Write(p[1]);}return stream.ToArray();
    }
    private static void Header(BinaryWriter writer,int bytes)
    { Big(writer,9994);writer.Write(new byte[20]);Big(writer,bytes/2);writer.Write(1000);writer.Write(5);writer.Write(new byte[64]); }
    private static void Big(BinaryWriter writer,int value){Span<byte> b=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(b,value);writer.Write(b);}
    private static byte[] Dbf(string[] fields,string[][] rows)
    {
        using var s=new MemoryStream();using var w=new BinaryWriter(s);w.Write((byte)3);w.Write(new byte[3]);w.Write(rows.Length);w.Write((ushort)(33+32*fields.Length));w.Write((ushort)(1+64*fields.Length));w.Write(new byte[20]);
        foreach(var field in fields){var b=new byte[32];Encoding.ASCII.GetBytes(field).CopyTo(b,0);b[11]=(byte)'C';b[16]=64;w.Write(b);}w.Write((byte)13);
        foreach(var row in rows){w.Write((byte)32);foreach(var value in row)w.Write(Encoding.ASCII.GetBytes(value.PadRight(64)));}w.Write((byte)26);return s.ToArray();
    }
    public static byte[] SaaArchive(string? fault = null)
    {
        var xml = """
            <SaaMessage xmlns="urn:us:gov:dot:faa:aim:saa" xmlns:gml="http://www.opengis.net/gml/3.2"><hasMember><Airspace xmlns="http://www.aixm.aero/schema/5.0" gml:id="Airspace1"><gml:identifier>synthetic-id</gml:identifier><timeSlice><AirspaceTimeSlice><gml:validTime><gml:TimePeriod><gml:beginPosition>2020-01-01T09:01:00Z</gml:beginPosition><gml:endPosition indeterminatePosition="unknown"/></gml:TimePeriod></gml:validTime><interpretation>BASELINE</interpretation><designator>TEST</designator><name>Synthetic Special Use</name><geometryComponent><AirspaceGeometryComponent><operation>BASE</operation><theAirspaceVolume><AirspaceVolume><upperLimit uom="FT">10000</upperLimit><upperLimitReference>MSL</upperLimitReference><lowerLimit uom="FT">GND</lowerLimit><lowerLimitReference>SFC</lowerLimitReference><horizontalProjection><Surface srsName="urn:ogc:def:crs:OGC:1.3:CRS84" srsDimension="2"><gml:patches><gml:PolygonPatch><gml:exterior><gml:LinearRing><gml:pos>-76 39</gml:pos><gml:pos>-74 39</gml:pos><gml:pos>-74 41</gml:pos><gml:pos>-76 41</gml:pos><gml:pos>-76 39</gml:pos></gml:LinearRing></gml:exterior></gml:PolygonPatch></gml:patches></Surface></horizontalProjection></AirspaceVolume></theAirspaceVolume></AirspaceGeometryComponent></geometryComponent></AirspaceTimeSlice></timeSlice></Airspace></hasMember><hasMember><AirspaceUsageTimeSlice xmlns="http://www.aixm.aero/schema/5.0"><statusActivation>AVBL_FOR_ACTIVATION</statusActivation><workingHours>NOTAM</workingHours><Timesheet><timeReference>UTC</timeReference><startDate>01-01</startDate><endDate>31-12</endDate></Timesheet></AirspaceUsageTimeSlice></hasMember></SaaMessage>
            """;
        if(fault=="dtd")xml="<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///must-not-be-read'>]>"+xml;
        if(fault=="axis")xml=xml.Replace("urn:ogc:def:crs:OGC:1.3:CRS84","EPSG:4326");
        var members=new Dictionary<string,byte[]>{["Synthetic, SAA.xml"]=Encoding.UTF8.GetBytes(xml)};
        if(fault is "composite" or "curve")members["Unsupported.xml"]=Encoding.UTF8.GetBytes(fault=="composite"?xml.Replace("<operation>BASE</operation>","<operation>SUBTR</operation>"):xml.Replace("<gml:LinearRing>","<gml:LinearRing><gml:ArcByCenterPoint/>"));
        var data=Zip(members);
        var middle=Zip(new(){[fault=="nested"?"unexpected.zip":"Saa_Sub_File.zip"]=data});
        return Zip(new(){[fault=="traversal"?"../SaaSubscriberFile.zip":"SAA-AIXM_5_Schema/SaaSubscriberFile.zip"]=middle});
    }
    private static byte[] Zip(Dictionary<string,byte[]> files)
    { using var m=new MemoryStream();using(var z=new ZipArchive(m,ZipArchiveMode.Create,true))foreach(var f in files){using var s=z.CreateEntry(f.Key).Open();s.Write(f.Value);}return m.ToArray(); }
}
