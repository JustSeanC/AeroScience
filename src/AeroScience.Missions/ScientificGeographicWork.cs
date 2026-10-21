using AeroScience.Shared;
namespace AeroScience.Missions;

// Shared by route preparation and user-directed Library work; original geodesic traversal retained.
public static class ScientificGeographicWork
{
    public static IEnumerable<GeographicBounds> Cells(Coordinates[] route,Coordinates[][] polygons,double step=.02,bool strict=false)
    {
        var seen=new HashSet<GeographicBounds>();var inspected=0;
        GeographicBounds? Cell(Coordinates p){try{var a=new GeographicBounds(Math.Floor(p.Longitude/step)*step,Math.Floor(p.Latitude/step)*step,Math.Floor(p.Longitude/step)*step+step,Math.Floor(p.Latitude/step)*step+step);AutomaticScientificData.ValidateArea(a);return a;}catch(ArgumentException){return null;}}
        var points=route;
        foreach(var (a,b) in points.Zip(points.Skip(1)))
        {
            var distance=Geodesy.Distance(a,b);var bearing=Geodesy.Bearing(a,b);var steps=(int)Math.Min(20000,Math.Ceiling(distance/700));
            for(var i=0;i<=steps;i++)
            {if(++inspected>100000||seen.Count>=4096){if(strict)throw new ArgumentException("Geographic selection exceeds 4,096 work units or 100,000 geometry candidates. Split it into smaller explicit regions.");yield break;}var cell=Cell(Geodesy.Direct(a,bearing,steps==0?0:distance*i/steps));if(cell is not null&&seen.Add(cell))yield return cell;}
        }
        foreach(var p in points){var cell=Cell(p);if(cell is not null&&seen.Add(cell))yield return cell;}
        foreach(var vertices in polygons)
        {
            if(vertices.Length<3)continue;
            foreach(var (a,b) in vertices.Zip(vertices.Skip(1).Append(vertices[0])))
            {var length=Geodesy.Distance(a,b);var bearing=Geodesy.Bearing(a,b);var count=(int)Math.Min(20000,Math.Ceiling(length/700));for(var n=0;n<=count;n++){if(++inspected>100000||seen.Count>=4096){if(strict)throw new ArgumentException("Geographic selection exceeds 4,096 work units or 100,000 geometry candidates. Split it into smaller explicit regions.");yield break;}var edgeCell=Cell(Geodesy.Direct(a,bearing,count==0?0:length*n/count));if(edgeCell is not null&&seen.Add(edgeCell))yield return edgeCell;}}
            var south=vertices.Min(p=>p.Latitude);var north=vertices.Max(p=>p.Latitude);var west=vertices.Min(p=>p.Longitude);var east=vertices.Max(p=>p.Longitude);
            for(var lat=Math.Floor(south/step)*step;lat<=north;lat+=step)
            for(var lon=Math.Floor(west/step)*step;lon<=east;lon+=step)
            {if(++inspected>100000||seen.Count>=4096){if(strict)throw new ArgumentException("Geographic selection exceeds 4,096 work units or 100,000 geometry candidates. Split it into smaller explicit regions.");yield break;}var center=new Coordinates(lat+step/2,lon+step/2);var cell=Cell(center);if(cell is not null&&(MissionAnalysis.Inside(center,vertices)||cell.Corners.Any(c=>MissionAnalysis.Inside(c,vertices))||vertices.Any(cell.Contains))&&seen.Add(cell))yield return cell;}
        }
    }
}
