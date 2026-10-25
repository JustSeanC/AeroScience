using AeroScience.Shared;
using NetTopologySuite.Geometries;
using Coordinates=AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

public static partial class AviationAirspace
{
    // Pure three-valued set algebra. Source-native datum and unknown boundary
    // inclusion are never replaced by guessed terrain, pressure or legal rules.
    public static AviationVolumeAssessment AssessVolumes(AviationAirspaceVolume[] volumes,Coordinates point,double altitude,string unit,string reference)
    {
        if(volumes.Length is <1 or >32||!double.IsFinite(point.Latitude)||!double.IsFinite(point.Longitude)||point.Latitude is < -85 or >85||point.Longitude is < -180 or >180||!double.IsFinite(altitude)||unit is not("FT" or "M" or "FL")||reference is not("MSL" or "Above surface" or "Standard pressure")||unit=="FL"&&reference!="Standard pressure")
            throw new ArgumentException("Supply a finite point, altitude, units and explicit altitude reference.");
        var ordered=volumes.OrderBy(v=>v.Sequence).ToArray();
        if(ordered[0].Operation!="BASE"||ordered.Count(v=>v.Operation=="BASE")!=1||ordered.Select(v=>v.Sequence).Distinct().Count()!=ordered.Length)throw new InvalidDataException("Ambiguous ordered volume model.");
        double Metres(double value,string u)
        {
            var metres=u switch{"M"=>value,"FT"=>value*.3048,"FL"=>value*100*.3048,_=>throw new ArgumentException("Unsupported altitude unit.")};
            return double.IsFinite(metres)?metres:throw new ArgumentException("Altitude conversion must remain finite.");
        }
        bool? And(bool? a,bool? b)=>a==false||b==false?false:a==true&&b==true?true:null;
        bool? Or(bool? a,bool? b)=>a==true||b==true?true:a==false&&b==false?false:null;
        bool? Limit(AviationAltitudeLimit l,bool lower,bool? included)
        {
            if(!lower&&l.Status=="Published unlimited")return true;
            if(l.Status!="Known"||l.Reference!=reference||l.Value is null||l.Unit is null||l.Unit=="FL"&&reference!="Standard pressure")return null;
            var d=Metres(altitude,unit)-Metres(l.Value.Value,l.Unit);
            return d==0?included:lower?d>0:d<0;
        }
        var findings=new List<AviationVolumeFinding>();bool? result=null;
        foreach(var v in ordered)
        {
            if(v.Version!=1||v.Polygons is null||v.Operation is not("BASE" or "UNION" or "SUBTR"))throw new InvalidDataException("Unsupported native volume definition.");
            var geometry=new GeometryFactory().CreateMultiPolygon(v.Polygons.Select(p=>new Polygon(new LinearRing(p[0].Select(c=>new Coordinate(c.Longitude,c.Latitude)).ToArray()),p.Skip(1).Select(r=>new LinearRing(r.Select(c=>new Coordinate(c.Longitude,c.Latitude)).ToArray())).ToArray())).ToArray());
            var inside=geometry.Covers(new Point(point.Longitude,point.Latitude));
            var vertical=And(Limit(v.Lower,true,v.LowerIncluded),Limit(v.Upper,false,v.UpperIncluded));
            var membership=inside?vertical:false;
            result=v.Operation=="BASE"?membership:v.Operation=="UNION"?Or(result,membership):And(result,membership is null?null:!membership.Value);
            findings.Add(new(v.SourceComponent,v.Operation,inside?"Inside published component":"Outside published component",vertical is null?"Unknown":vertical.Value?"Inside comparable altitude bounds":"Outside comparable altitude bounds",membership is null?"Unknown":membership.Value?"Included":"Excluded",
                "Native limits retained; missing datum conversion or boundary inclusivity remains Unknown. Published validity/hours do not establish activation."));
        }
        return new(1,result is null?"Unable to determine assessed volume membership":result.Value?"Inside assessed volume dimensions":"Outside assessed volume dimensions",findings.ToArray(),
            ["Simulation reference only; not a legal clearance or confirmed activation.","Only the selected point and comparable altitude dimensions are assessed; route arrival times are not inferred.","Unknown terrain/pressure conversions, inclusivity and operational activation remain Unknown."]);
    }
}
