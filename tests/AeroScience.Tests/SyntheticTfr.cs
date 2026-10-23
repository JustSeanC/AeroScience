using System.Text.Json;
namespace AeroScience.Tests;

// Deliberately synthetic NOTAM identities/coordinates; never genuine FAA acceptance.
public static class SyntheticTfr
{
    public static byte[] Notices()=>JsonSerializer.SerializeToUtf8Bytes(new[]{new{notam_id="6/99991",facility="ZZZ",state="XX",type="SYNTHETIC",description="Synthetic restriction with Unknown exact activation"},new{notam_id="6/99992",facility="ZZZ",state="XX",type="SYNTHETIC",description="Synthetic notice without geometry"}});
    public static byte[] NoShape()=>JsonSerializer.SerializeToUtf8Bytes(new[]{new{notam_id="6/99992"}});
    public static byte[] Geometry(string? fault=null)
    {
        double[] Point(double x,double y)=>[6378137*x*Math.PI/180,6378137*Math.Log(Math.Tan(Math.PI/4+y*Math.PI/360))];
        double[][] Ring(double w,double s,double e,double n)=>[Point(w,s),Point(e,s),Point(e,n),Point(w,n),Point(w,s)];
        return JsonSerializer.SerializeToUtf8Bytes(new{type="FeatureCollection",totalFeatures=fault=="truncated"?2:1,numberReturned=1,crs=new{type="name",properties=new{name=fault=="crs"?"EPSG:4326":"urn:ogc:def:crs:EPSG::3857"}},features=new[]{new{type="Feature",id="V_TFR_LOC.synthetic",geometry=new{type="Polygon",coordinates=new[]{Ring(-76,39,-74,41),Ring(-75.5,39.5,-74.5,40.5)}},properties=new{GID=123L,NOTAM_KEY="6/99991-1-FDC-F",TITLE=fault=="mismatch"?"Different identity":"Synthetic restriction with Unknown exact activation",CNS_LOCATION_ID="ZZZ",STATE="XX",LEGAL="SYNTHETIC",LAST_MODIFICATION_DATETIME="Synthetic timestamp; UTC unknown"}}}});
    }
}
