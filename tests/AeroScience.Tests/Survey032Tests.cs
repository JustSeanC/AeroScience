using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
public sealed class Survey032Tests
{
    [Theory][InlineData(FlightPatternKind.Lawnmower)][InlineData(FlightPatternKind.CreepingLine)]
    public void ConcaveRotatedPolygonProducesSeparateInBoundsScansAndExplicitTransitions(FlightPatternKind kind)
    {
        var origin=new Coordinates(40,-75);const double heading=37;
        Coordinates Point(double x,double y)=>Geodesy.Direct(origin,heading+Math.Atan2(y,x)*180/Math.PI,Math.Sqrt(x*x+y*y));
        var vertices=new[]{Point(-2000,-1800),Point(2000,-1800),Point(2000,-600),Point(-200,-600),Point(-200,600),Point(2000,600),Point(2000,1800),Point(-2000,1800)};
        var d=FlightPatterns.FitSurvey(new(Guid.NewGuid(),kind,origin,heading,SpacingMetres:400),new("Concave",vertices));
        var preview=FlightPatterns.Generate(d);Assert.Equal("polygon",d.FitMode);Assert.Contains(preview.Segments!,s=>s.Role=="transition");Assert.Contains(preview.Segments!,s=>s.Role=="scan");Assert.Equal(400,d.SpacingMetres);
        Assert.True(preview.ScanDistanceMetres>0);Assert.True(preview.TransitionDistanceMetres>0);
        Assert.Equal(preview.DistanceMetres,preview.ScanDistanceMetres!.Value+preview.TransitionDistanceMetres!.Value,6);
        Assert.All(preview.Segments!.Where(s=>s.Role=="scan"),s=>
        {
            var a=preview.Waypoints[s.Start];var b=preview.Waypoints[s.End];
            var length=Geodesy.Distance(new(a.Latitude,a.Longitude),new(b.Latitude,b.Longitude));var bearing=Geodesy.Bearing(new(a.Latitude,a.Longitude),new(b.Latitude,b.Longitude));
            for(var i=1;i<10;i++){var p=Geodesy.Direct(new(a.Latitude,a.Longitude),bearing,length*i/10);Assert.True(Inside(p,vertices),$"Scan point {p} outside polygon");}
        });
        var reopened=JsonSerializer.Deserialize<PatternDefinition>(JsonSerializer.Serialize(d,WireJson.Options),WireJson.Options)!;
        Assert.Equal(preview.Waypoints,FlightPatterns.Generate(reopened).Waypoints);
        var legacy=FlightPatterns.FitSurvey(d,new("Concave",vertices),"envelope");Assert.Null(legacy.ClippingPolygon);Assert.Null(FlightPatterns.Generate(legacy).Segments);
    }
    private static bool Inside(Coordinates p,Coordinates[] ring){var result=false;for(int i=0,j=ring.Length-1;i<ring.Length;j=i++)if((ring[i].Latitude>p.Latitude)!=(ring[j].Latitude>p.Latitude)&&p.Longitude<(ring[j].Longitude-ring[i].Longitude)*(p.Latitude-ring[i].Latitude)/(ring[j].Latitude-ring[i].Latitude)+ring[i].Longitude)result=!result;return result;}
    [Fact]public void ExistingFinitePreferencesAndExplicitUnlimitedRoundTripWithoutBypassingPerDownloadLimits()
    {
        var path=Path.Combine(Path.GetTempPath(),"aeroscience-032-prefs-"+Guid.NewGuid());Directory.CreateDirectory(path);
        try{var settings=new ScientificAccessSettings(path);Assert.Equal(268435456,settings.Get().Preferences.PrefetchHourlyBytes);settings.Save(new(PrefetchHourlyBytes:800*1048576));Assert.Equal(800*1048576,new ScientificAccessSettings(path).Get().Preferences.PrefetchHourlyBytes);settings.Save(settings.Get().Preferences with{PrefetchHourlyBytes=null,PreflightTransferBytes=null});var p=new ScientificAccessSettings(path).Get().Preferences;Assert.Null(p.PrefetchHourlyBytes);Assert.Null(p.PreflightTransferBytes);Assert.Equal(67108864,p.MaximumDownloadBytes);Assert.Throws<ArgumentException>(()=>settings.Save(p with{MaximumDownloadBytes=long.MaxValue}));}finally{Directory.Delete(path,true);}
    }
}
