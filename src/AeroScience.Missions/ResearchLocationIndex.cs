using AeroScience.Shared;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Quadtree;
using Coordinates=AeroScience.Shared.Coordinates;
namespace AeroScience.Missions;

// Shared scientific sampling-location index, not a coverage-area estimator.
internal sealed class ResearchLocationIndex(double spacing)
{
    readonly List<Coordinates> points=[];readonly Quadtree<int> index=new();
    public int Count=>points.Count;
    public int Locate(Coordinates point)
    {
        var dy=spacing/110000d;var dx=Math.Min(180,dy/Math.Max(1e-12,Math.Cos(Math.Min(90,Math.Abs(point.Latitude)+dy)*Math.PI/180)));
        var found=index.Query(new Envelope(point.Longitude-dx,point.Longitude+dx,point.Latitude-dy,point.Latitude+dy)).Distinct()
            .Select(i=>(Index:i,Distance:Geodesy.Distance(point,points[i]))).Where(x=>x.Distance<spacing).OrderBy(x=>x.Distance).ThenBy(x=>x.Index).Select(x=>(int?)x.Index).FirstOrDefault();
        if(found is {} previous)return previous;
        if(points.Count>=20000)throw new InvalidOperationException("This analysis window exceeds 20,000 distinct sampling locations. Choose a shorter interval or wider spacing; retained Library and flight history are not limited.");
        var next=points.Count;points.Add(point);foreach(var offset in new[]{-360,0,360})index.Insert(new Envelope(point.Longitude+offset,point.Longitude+offset,point.Latitude,point.Latitude),next);return next;
    }
}
