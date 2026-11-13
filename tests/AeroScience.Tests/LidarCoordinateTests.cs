using AeroScience.Missions;
using Xunit;
namespace AeroScience.Tests;

public sealed class LidarCoordinateTests
{
    // Independent PROJ 9.5.1 / pyproj 3.7.2 always_xy values, not source surveys.
    [Theory]
    [InlineData("EPSG:32601",166021.44308054057,0,-180,0)]
    [InlineData("EPSG:32660",833978.5569194623,0,180,0)]
    [InlineData("EPSG:32760",756099.6479702791,5567930.943101483,180,-40)]
    [InlineData("EPSG:32701",243900.3520297231,5567930.943101483,-180,-40)]
    [InlineData("EPSG:32618",500000,4427757.218738374,-75,40)]
    public void SupportedUtmZoneSeamsAndHemispheresMatchIndependentProj(string crs,double x,double y,double longitude,double latitude)
    {
        var p=LidarCoordinates.Geographic(crs,x,y);Assert.InRange(Math.Abs(p.Latitude-latitude),0,1e-9);
        var angular=((p.Longitude-longitude+540)%360)-180;Assert.InRange(Math.Abs(angular),0,1e-9);
    }
    [Theory]
    [InlineData("EPSG:32601",99999,0)]
    [InlineData("EPSG:32660",900001,0)]
    [InlineData("EPSG:32618",500000,-1)]
    [InlineData("EPSG:32718",500000,10000001)]
    [InlineData("EPSG:32618",500000,9999999)]
    [InlineData("EPSG:32718",500000,0)]
    public void UnsupportedUtmExtentOrHemisphereIsRefused(string crs,double x,double y)=>Assert.Throws<InvalidDataException>(()=>LidarCoordinates.Geographic(crs,x,y));
}
