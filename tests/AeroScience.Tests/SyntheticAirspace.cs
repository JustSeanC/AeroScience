using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using System.Globalization;

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
        if(fault?.StartsWith("circle",StringComparison.Ordinal)==true)
        {
            var start=xml.IndexOf("<gml:LinearRing>",StringComparison.Ordinal);var end=xml.IndexOf("</gml:LinearRing>",StringComparison.Ordinal)+"</gml:LinearRing>".Length;
            var radius=fault=="circle-negative"?"-1":"1";var unit=fault=="circle-unit"?"DEGREES":"NM";
            var circle=$"<gml:Ring><gml:curveMember><gml:Curve><gml:segments><gml:CircleByCenterPoint numArc=\"1\"><gml:pointProperty><gml:Point><gml:pos>{(fault=="circle-dateline"?"179.999 40":"-75 40")}</gml:pos></gml:Point></gml:pointProperty><gml:radius uom=\"{unit}\">{radius}</gml:radius></gml:CircleByCenterPoint></gml:segments></gml:Curve></gml:curveMember></gml:Ring>";
            if(fault=="circle-mixed")circle=circle.Replace("</gml:segments>","<gml:LineStringSegment><gml:pos>-75 40</gml:pos></gml:LineStringSegment></gml:segments>");
            xml=xml[..start]+circle+xml[end..];
        }
        if(fault=="dtd")xml="<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///must-not-be-read'>]>"+xml;
        if(fault=="axis")xml=xml.Replace("urn:ogc:def:crs:OGC:1.3:CRS84","EPSG:4326");
        var members=new Dictionary<string,byte[]>{["Synthetic, SAA.xml"]=Encoding.UTF8.GetBytes(xml)};
        if(fault is "composite" or "curve")members["Unsupported.xml"]=Encoding.UTF8.GetBytes(fault=="composite"?xml.Replace("<operation>BASE</operation>","<operation>SUBTR</operation>"):xml.Replace("<gml:LinearRing>","<gml:LinearRing><gml:ArcByCenterPoint/>"));
        if (fault?.StartsWith("arc", StringComparison.Ordinal) == true)
        {
            var ringStart = xml.IndexOf("<gml:LinearRing>", StringComparison.Ordinal); var ringEnd = xml.IndexOf("</gml:LinearRing>", StringComparison.Ordinal) + "</gml:LinearRing>".Length;
            // Independent pyproj WGS84 endpoints rounded to the genuine FAA's six decimal places.
            var curve = """
                <gml:Ring><gml:curveMember><gml:Curve><gml:segments><gml:LineStringSegment><gml:pos>-75 40.016679</gml:pos><gml:pos>-75 40</gml:pos><gml:pos>-74.978312 39.999998</gml:pos></gml:LineStringSegment></gml:segments></gml:Curve></gml:curveMember><gml:curveMember><gml:Curve><gml:segments><gml:ArcByCenterPoint numArc="1"><gml:pointProperty><gml:Point><gml:pos>-75 40</gml:pos></gml:Point></gml:pointProperty><gml:radius uom="NM">1</gml:radius><gml:startAngle uom="deg">0</gml:startAngle><gml:endAngle uom="deg">90</gml:endAngle></gml:ArcByCenterPoint></gml:segments></gml:Curve></gml:curveMember></gml:Ring>
                """;
            curve = fault switch
            {
                "arc-unwrapped" => curve.Replace(">0</gml:startAngle>", ">360</gml:startAngle>").Replace(">90</gml:endAngle>", ">450</gml:endAngle>"),
                "arc-adjacent" => curve.Replace(">90</gml:endAngle>", ">45</gml:endAngle>").Replace("</gml:segments></gml:Curve></gml:curveMember></gml:Ring>", "<gml:ArcByCenterPoint numArc=\"1\"><gml:pointProperty><gml:Point><gml:pos>-75 40</gml:pos></gml:Point></gml:pointProperty><gml:radius uom=\"NM\">1</gml:radius><gml:startAngle uom=\"deg\">45</gml:startAngle><gml:endAngle uom=\"deg\">90</gml:endAngle></gml:ArcByCenterPoint></gml:segments></gml:Curve></gml:curveMember></gml:Ring>"),
                "arc-unit" => curve.Replace("uom=\"NM\"", "uom=\"DEGREES\""),
                "arc-wrap" => curve.Replace(">90</gml:endAngle>", ">-270</gml:endAngle>"),
                "arc-gap" => curve.Replace("-74.978312 39.999998", "-74.9 40"),
                "arc-link" => curve.Replace("<gml:Point>", "<gml:Point xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:href=\"file:///untrusted\">"),
                "arc-axis" => curve.Replace("<gml:Curve>", "<gml:Curve srsName=\"EPSG:4326\">"),
                "arc-major" => curve.Replace(">90</gml:endAngle>", ">270</gml:endAngle>"),
                "arc-reverse" => curve.Replace("<gml:Curve>", "<gml:Curve orientation=\"-\">"),
                _ => curve
            };
            members["Curved.xml"] = Encoding.UTF8.GetBytes(xml[..ringStart] + curve + xml[ringEnd..]);
        }
        if(fault?.StartsWith("composite-",StringComparison.Ordinal)==true)
        {
            XNamespace a="http://www.aixm.aero/schema/5.0",g="http://www.opengis.net/gml/3.2",x="http://www.w3.org/1999/xlink";
            var doc=XDocument.Parse(xml);var slice=doc.Descendants(a+"AirspaceTimeSlice").Single();slice.Element(a+"name")!.Value="Synthetic composite "+fault;var original=slice.Element(a+"geometryComponent")!;
            XElement Component(string operation,int sequence,double west,double south,double east,double north)
            {
                var component=new XElement(original);var c=component.Element(a+"AirspaceGeometryComponent")!;
                c.Element(a+"operation")!.Value=operation;c.AddFirst(new XElement(a+"operationSequence",sequence));
                var surface=c.Descendants(a+"Surface").Single();surface.SetAttributeValue(g+"id","surface-"+sequence);
                var ring=surface.Descendants(g+"LinearRing").Single();ring.ReplaceNodes(new[]{(west,south),(east,south),(east,north),(west,north),(west,south)}.Select(p=>new XElement(g+"pos",p.Item1.ToString(CultureInfo.InvariantCulture)+" "+p.Item2.ToString(CultureInfo.InvariantCulture))));
                return component;
            }
            var first=Component("BASE",10,-76,39,-74,41);
            var second=Component(fault=="composite-union"?"UNION":"SUBTR",20,fault=="composite-union"?-72:-75.5,39.5,fault=="composite-union"?-71:-74.5,40.5);
            if(fault=="composite-split")second=Component("SUBTR",20,-75.2,38,-74.8,42);
            if(fault=="composite-empty")second=Component("SUBTR",20,-77,38,-73,42);
            var c2=second.Element(a+"AirspaceGeometryComponent")!;
            if(fault=="composite-vertical")c2.Descendants(a+"upperLimit").Single().Value="9000";
            if(fault=="composite-unknown")c2.Descendants(a+"lowerLimitReference").Single().Remove();
            if(fault=="composite-duplicate")c2.Element(a+"operationSequence")!.Value="10";
            if(fault=="composite-base-order")first.Descendants(a+"operationSequence").Single().Value="30";
            if(fault=="composite-axis")c2.Descendants(a+"Surface").Single().SetAttributeValue("srsName","EPSG:4326");
            if(fault is "composite-missing" or "composite-cycle")
            {var v=c2.Element(a+"theAirspaceVolume")!;v.RemoveNodes();v.SetAttributeValue(x+"href",fault=="composite-cycle"?"#surface-20":"file:///never-resolve");}
            if(fault=="composite-self-intersect")c2.Descendants(g+"LinearRing").Single().ReplaceNodes(new[]{"-75.5 39.5","-74.5 40.5","-75.5 40.5","-74.5 39.5","-75.5 39.5"}.Select(v=>new XElement(g+"pos",v)));
            if(fault=="composite-dateline")second=Component("SUBTR",20,-179,39,179,41);
            if(fault=="composite-polar")second=Component("SUBTR",20,-75.5,84,-74.5,86);
            if(fault=="composite-work")
            {
                foreach(var (component,radius) in new[]{(first,500000),(second,400000)})
                    component.Descendants(g+"LinearRing").Single().ReplaceWith(new XElement(g+"Ring",new XElement(g+"curveMember",new XElement(g+"Curve",new XElement(g+"segments",new XElement(g+"CircleByCenterPoint",new XAttribute("numArc","1"),new XElement(g+"pointProperty",new XElement(g+"Point",new XElement(g+"pos","-75 40"))),new XElement(g+"radius",new XAttribute("uom","M"),radius)))))));
            }
            original.Remove();slice.Add(second,first); // Deliberately not operation order.
            if(fault=="composite-nested")slice.Add(Component("UNION",30,-75.25,39.75,-74.75,40.25));
            if(fault=="composite-excess")for(var i=0;i<31;i++)slice.Add(Component("UNION",30+i,-72,39,-71,40));
            members["Composite.xml"]=Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
        }
        var data=Zip(members);
        var middle=Zip(new(){[fault=="nested"?"unexpected.zip":"Saa_Sub_File.zip"]=data});
        return Zip(new(){[fault=="traversal"?"../SaaSubscriberFile.zip":"SAA-AIXM_5_Schema/SaaSubscriberFile.zip"]=middle});
    }
    private static byte[] Zip(Dictionary<string,byte[]> files)
    { using var m=new MemoryStream();using(var z=new ZipArchive(m,ZipArchiveMode.Create,true))foreach(var f in files){using var s=z.CreateEntry(f.Key).Open();s.Write(f.Value);}return m.ToArray(); }
}
