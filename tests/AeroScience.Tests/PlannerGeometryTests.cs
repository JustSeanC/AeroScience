using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
public class PlannerGeometryTests
{
    [Theory]
    [InlineData(0)] [InlineData(37)] [InlineData(90)] [InlineData(218)]
    public void LawnmowerLegsFollowReferenceAndCreepingLegsCrossIt(double heading)
    {
        var definition=new PatternDefinition(Guid.NewGuid(),FlightPatternKind.Lawnmower,new(0,0),HeadingDegrees:heading,LengthMetres:10000,SpacingMetres:1000,Legs:4);
        var lawn=FlightPatterns.Generate(definition).Waypoints;
        var creep=FlightPatterns.Generate(definition with { Kind=FlightPatternKind.CreepingLine }).Waypoints;
        static double Error(double actual,double expected) => Math.Abs((actual-expected+540)%360-180);
        Assert.InRange(Error(Geodesy.Bearing(Geodesy.Point(lawn[0]),Geodesy.Point(lawn[1])),heading),0,.02);
        Assert.InRange(Error(Geodesy.Bearing(Geodesy.Point(creep[0]),Geodesy.Point(creep[1])),heading+90),0,.02);
        Assert.InRange(Geodesy.Distance(Geodesy.Point(lawn[0]),Geodesy.Point(lawn[1])),9999,10001);
        Assert.InRange(Geodesy.Distance(Geodesy.Point(creep[0]),Geodesy.Point(creep[1])),9999,10001);
        Assert.InRange(Geodesy.Distance(Geodesy.Point(lawn[1]),Geodesy.Point(lawn[2])),999,1001);
    }
    [Fact]
    public void SurveyValidationRejectsCrossingsCollinearAndDuplicateButAcceptsDatelineAndLegacyClosure()
    {
        Assert.Throws<ArgumentException>(()=>SurveyGeometry.Validate([new(0,0),new(1,1),new(0,1),new(1,0)]));
        Assert.Throws<ArgumentException>(()=>SurveyGeometry.Validate([new(0,0),new(0,1),new(0,2)]));
        Assert.Throws<ArgumentException>(()=>SurveyGeometry.Validate([new(0,0),new(1,1),new(1,0),new(1,1)]));
        SurveyGeometry.Validate([new(10,179.9),new(10,-179.9),new(10.2,-179.9),new(10.2,179.9)]);
        SurveyGeometry.Validate([new(0,0),new(0,1),new(1,1),new(0,0)]);
    }
}

public class PlanningUnitTests
{
    [Fact]
    public void AviationQuarterNauticalMileAndMetricAltitudeUseCanonicalConversions()
    {
        var aviation=ScienceUnits.Conversion("distance",ScienceUnits.Aviation);
        Assert.Equal("NM",aviation.Label);Assert.Equal(463,.25/aviation.Scale,10);
        var metric=ScienceUnits.Conversion("altitude",ScienceUnits.Scientific);
        Assert.Equal(3048,10000*metric.Scale,10);
        Assert.Equal(10000,3048/metric.Scale,10);
        Assert.Equal("° true",ScienceUnits.Conversion("wind-direction",ScienceUnits.Aviation).Label);
    }
    [Theory]
    [InlineData(FlightPatternKind.Lawnmower)] [InlineData(FlightPatternKind.CreepingLine)]
    public void FitUsesActualSurveyHeadingEnvelopeAndEnteredSpacingWithoutInventingSensorCoverage(FlightPatternKind kind)
    {
        var origin=new Coordinates(0,0);var area=new SurveyArea("Known rectangle",[origin,Geodesy.Direct(origin,90,1852),Geodesy.Direct(Geodesy.Direct(origin,90,1852),0,3704),Geodesy.Direct(origin,0,3704)]);
        var definition=new PatternDefinition(Guid.NewGuid(),kind,origin,SpacingMetres:463);
        var fitted=FlightPatterns.FitSurvey(definition,area,"envelope");
        Assert.Equal(463,fitted.SpacingMetres);
        Assert.InRange(fitted.LengthMetres,kind==FlightPatternKind.Lawnmower?3703:1851,kind==FlightPatternKind.Lawnmower?3705:1853);
        Assert.InRange((fitted.Legs-1)*fitted.SpacingMetres,fitted.WidthMetres,fitted.WidthMetres+463.001);
        Assert.Equal(fitted.Legs*2,FlightPatterns.Generate(fitted).Waypoints.Length);
        Assert.Throws<ArgumentException>(()=>FlightPatterns.FitSurvey(definition with { SpacingMetres=10 },area));
    }
    [Fact]
    public void HistoricalMeterPatternRoundTripsThroughSavedPlanAndUnitSettingsWithoutChangingGeometry()
    {
        var root=Path.Combine(Path.GetTempPath(),"aeroscience-planning-units-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();
            var definition=new PatternDefinition(Guid.NewGuid(),FlightPatternKind.Orbit,new(40,-75),LengthMetres:10000.123456789,SpacingMetres:463,RadiusMetres:1234.567891234,TargetAltitudeFeet:10000.125);
            var geometry=FlightPatterns.Generate(definition);
            var saved=store.SavePlan(new(Guid.NewGuid(),"Meter-based historical plan",Waypoints:geometry.Waypoints,Patterns:[definition]));
            var settings=new ScienceUnitSettings(root);
            for(var i=0;i<20;i++)settings.Set(i%2==0?ScienceUnits.Scientific:ScienceUnits.Aviation);
            var loaded=store.Plan(saved.Id)!;
            Assert.Equal(definition,Assert.Single(loaded.Patterns!));
            Assert.Equal(geometry.Waypoints,loaded.Waypoints);
            Assert.Equal(geometry.DistanceMetres,FlightPatterns.Generate(loaded.Patterns![0]).DistanceMetres);
            Assert.Equal(ScienceUnits.Aviation,new ScienceUnitSettings(Path.Combine(root,"fresh")).Get());
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
}
