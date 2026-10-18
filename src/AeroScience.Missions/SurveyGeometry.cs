using AeroScience.Shared;
namespace AeroScience.Missions;

public static class SurveyGeometry
{
    // Local unwrapped WGS84 polygon validation. This does not claim cadastral accuracy.
    public static void Validate(Coordinates[] vertices)
    {
        if (vertices.Length < 3 || vertices.Any(p => !MissionPlanning.Position(p.Latitude, p.Longitude)))
            throw new ArgumentException("Survey polygons require at least three valid WGS84 vertices.");
        var points = vertices.ToList();
        if (points.Count > 3 && points[0] == points[^1]) points.RemoveAt(points.Count - 1); // legacy explicitly closed ring
        if (points.Distinct().Count() != points.Count) throw new ArgumentException("Survey vertices must be distinct.");
        var origin = points[0].Longitude;
        var xy = points.Select(p => (X:origin + (p.Longitude - origin + 540) % 360 - 180, Y:p.Latitude)).ToArray();
        static double Cross((double X,double Y) a,(double X,double Y) b,(double X,double Y) c) => (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        for(var i=0;i<xy.Length;i++) for(var j=i+1;j<xy.Length;j++)
        {
            if(j==i+1 || i==0 && j==xy.Length-1) continue;
            var a=xy[i];var b=xy[(i+1)%xy.Length];var c=xy[j];var d=xy[(j+1)%xy.Length];
            if(Cross(a,b,c)*Cross(a,b,d)<=0 && Cross(c,d,a)*Cross(c,d,b)<=0
                && Math.Max(Math.Min(a.X,b.X),Math.Min(c.X,d.X))<=Math.Min(Math.Max(a.X,b.X),Math.Max(c.X,d.X))
                && Math.Max(Math.Min(a.Y,b.Y),Math.Min(c.Y,d.Y))<=Math.Min(Math.Max(a.Y,b.Y),Math.Max(c.Y,d.Y)))
                throw new ArgumentException("Survey polygon crosses or touches itself.");
        }
        var area=xy.Select((a,i)=>{var b=xy[(i+1)%xy.Length];return (b.X-a.X)*Math.PI/180*(2+Math.Sin(a.Y*Math.PI/180)+Math.Sin(b.Y*Math.PI/180));}).Sum();
        if(Math.Abs(area)*6371008.8*6371008.8/2 < 1) throw new ArgumentException("Survey polygon has no usable area.");
    }
}
