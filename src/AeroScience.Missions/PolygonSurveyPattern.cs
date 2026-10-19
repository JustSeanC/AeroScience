using AeroScience.Shared;
namespace AeroScience.Missions;

// Intersect reference-heading scan lines with a validated local survey ring.
// Intersections stay separate: no convex hull, date mixing or coverage scoring.
public static class PolygonSurveyPattern
{
    public static PatternPreview Generate(PatternDefinition d)
    {
        var polygon=d.ClippingPolygon!;var heading=d.HeadingDegrees+(d.Kind==FlightPatternKind.CreepingLine?90:0);
        (double X,double Y) Project(Coordinates p){var distance=Geodesy.Distance(d.Center,p);var angle=(Geodesy.Bearing(d.Center,p)-heading)*Math.PI/180;return(distance*Math.Cos(angle),distance*Math.Sin(angle));}
        Coordinates Position(double x,double y)=>Geodesy.Direct(d.Center,heading+Math.Atan2(y,x)*180/Math.PI,Math.Sqrt(x*x+y*y));
        var ring=polygon.Select(Project).ToArray();var min=ring.Min(p=>p.Y);var max=ring.Max(p=>p.Y);var rows=(int)Math.Floor((max-min)/d.SpacingMetres)+1;
        if(rows>100)throw new ArgumentException("This polygon needs more than 100 scan tracks. Increase Track Spacing or split the area.");
        var positions=new List<Coordinates>();var segments=new List<PatternRouteSegment>();
        var inset=Math.Min(d.SpacingMetres*.001,.01); // Avoid vertex/tangent ambiguity, not an overscan buffer.
        for(var row=0;row<rows;row++)
        {
            var y=min+(max-min-(rows-1)*d.SpacingMetres)/2+row*d.SpacingMetres;
            y=Math.Clamp(y,min+inset,max-inset);var xs=new List<double>();
            for(var i=0;i<ring.Length;i++){var a=ring[i];var b=ring[(i+1)%ring.Length];if((a.Y>y)==(b.Y>y))continue;xs.Add(a.X+(b.X-a.X)*(y-a.Y)/(b.Y-a.Y));}
            xs.Sort();if(xs.Count%2!=0)throw new ArgumentException("Ambiguous polygon intersection. Adjust heading or split the boundary.");
            var pairs=Enumerable.Range(0,xs.Count/2).Select(i=>(A:xs[i*2]+inset,B:xs[i*2+1]-inset)).Where(p=>p.B-p.A>=1).ToArray();
            if(row%2==1)Array.Reverse(pairs);
            foreach(var pair in pairs)
            {
                var a=Position(row%2==0?pair.A:pair.B,y);var b=Position(row%2==0?pair.B:pair.A,y);
                foreach(var (start,end) in Clip(a,b,polygon)){
                if(positions.Count>0)segments.Add(new(positions.Count-1,positions.Count,"transition"));
                positions.Add(start);positions.Add(end);segments.Add(new(positions.Count-2,positions.Count-1,"scan"));
                if(positions.Count>1000)throw new ArgumentException("Clipped polygon exceeds 1,000 route points. Increase spacing or split the survey.");
                }
            }
        }
        if(positions.Count<2)throw new ArgumentException("No usable scan intersections. Reduce track spacing, change heading or choose a wider survey area.");
        var waypoints=positions.Select((p,i)=>new ResearchWaypoint($"Survey {i+1}",p.Latitude,p.Longitude,d.TargetAltitudeFeet,Instructions:i%2==0?"Begin clipped scan leg":"End clipped scan leg; following transition may leave polygon",PatternId:d.Id,Id:MissionPlanning.StableWaypointId(d.Id,i))).ToArray();
        return new(d,waypoints,positions.Zip(positions.Skip(1),(a,b)=>Geodesy.Distance(a,b)).Sum(),"WGS84 local geodesic polygon intersections; scan segments and transitions are separate. Straight connectors can leave concave boundaries. Aircraft turn radius, terrain clearance and sensor coverage are not validated.",segments.ToArray(),segments.Where(s=>s.Role=="scan").Sum(s=>Geodesy.Distance(positions[s.Start],positions[s.End])),segments.Where(s=>s.Role=="transition").Sum(s=>Geodesy.Distance(positions[s.Start],positions[s.End])));
    }
    private static IEnumerable<(Coordinates Start,Coordinates End)> Clip(Coordinates start,Coordinates end,Coordinates[] ring)
    {
        var length=Geodesy.Distance(start,end);if(length<1)yield break;var bearing=Geodesy.Bearing(start,end);
        Coordinates At(double metres)=>Geodesy.Direct(start,bearing,metres);
        var cuts=new List<double>{0,length};var steps=Math.Max(16,(int)Math.Ceiling(length/500));
        var samples=Enumerable.Range(0,steps+1).Select(i=>At(length*i/steps)).ToArray();
        foreach(var (a,b) in ring.Zip(ring.Skip(1).Append(ring[0])))
        {
            double Cross(Coordinates p)=>(b.Longitude-a.Longitude)*(p.Latitude-a.Latitude)-(b.Latitude-a.Latitude)*(p.Longitude-a.Longitude);
            for(var i=0;i<steps;i++)
            {
                var ca=Cross(samples[i]);var cb=Cross(samples[i+1]);if(ca*cb>0||ca==0&&cb==0)continue;
                var low=length*i/steps;var high=length*(i+1)/steps;
                for(var pass=0;pass<40;pass++){var middle=(low+high)/2;var cm=Cross(At(middle));if(ca*cm<=0)high=middle;else{low=middle;ca=cm;}}
                var distance=(low+high)/2;var p=At(distance);var dx=b.Longitude-a.Longitude;var dy=b.Latitude-a.Latitude;var t=((p.Longitude-a.Longitude)*dx+(p.Latitude-a.Latitude)*dy)/(dx*dx+dy*dy);
                if(t>=-1e-9&&t<=1+1e-9)cuts.Add(distance);
            }
        }
        var sorted=cuts.Order().DistinctBy(v=>Math.Round(v,2)).ToArray();
        bool Inside(Coordinates p){var result=false;for(int i=0,j=ring.Length-1;i<ring.Length;j=i++)if((ring[i].Latitude>p.Latitude)!=(ring[j].Latitude>p.Latitude)&&p.Longitude<(ring[j].Longitude-ring[i].Longitude)*(p.Latitude-ring[i].Latitude)/(ring[j].Latitude-ring[i].Latitude)+ring[i].Longitude)result=!result;return result;}
        for(var i=0;i<sorted.Length-1;i++)
        {
            var low=sorted[i]+.25;var high=sorted[i+1]-.25;if(high-low<1||!Inside(At((low+high)/2)))continue;
            // Check the actual navigation geodesic, not only the projected bounding envelope.
            if(Enumerable.Range(0,17).All(n=>Inside(At(low+(high-low)*n/16))))yield return(At(low),At(high));
        }
    }

}
