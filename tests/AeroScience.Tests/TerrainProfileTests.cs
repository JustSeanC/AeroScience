using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;
public sealed class TerrainProfileTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"as-terrain-profile-"+Guid.NewGuid());
    readonly MissionStore missions;readonly ScienceStore science;
    public TerrainProfileTests(){var paths=new StorageManager(new PreferenceStore(root)).Initialize();missions=new(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;}
    async Task<ScientificSource> Source(bool gradient=false,string? datum=null){var folder=Path.Combine(root,Guid.NewGuid().ToString());var manifest=RemoteFixtureFiles.Create(folder,RemoteKind.Terrain);if(gradient)RemoteFixtureFiles.GeoTiff(Path.Combine(folder,"elevation.tif"),100,sample:(x,y)=>x==31?-9999:(float)(100+50*Math.Sin(x*Math.PI/16)));var definition=ScienceStore.ReadSourceManifest(manifest);if(datum is not null)definition=definition with{VerticalDatum=datum};return await science.ImportSourceAsync(definition,folder);}
    TerrainProfileResult Profile(ScientificSource? source,Coordinates[] line,int count=129)=>TerrainProfiles.Create(new(SourceId:source?.Id,Path:"line",Line:line,MaximumPoints:count),missions,science,_=>true);
    [Fact]public async Task ProfileUsesGeographicDistanceOriginalElevationAndNeverWritesScientificObservations(){
        var source=await Source(true);var line=new[]{new Coordinates(40.01,-75.02),new Coordinates(40.01,-74.98)};var before=source.ManifestSha256;
        var p=Profile(source,line);Assert.Equal(Geodesy.Distance(line[0],line[1]),p.DistanceMetres,5);Assert.InRange(p.DistanceMetres,3000,4000);Assert.True(p.MaximumMetres>p.MinimumMetres);Assert.Contains(p.Points,x=>x.ElevationMetres is null);Assert.Contains(p.Points,x=>x.ElevationMetres>140);Assert.Contains(p.Points,x=>x.ElevationMetres<60);Assert.Equal(0,p.Points[0].DistanceMetres);Assert.Equal(p.DistanceMetres,p.Points[^1].DistanceMetres);Assert.Equal(source.Manifest.VerticalDatum,p.Source!.VerticalDatum);Assert.Empty(science.Products());Assert.Equal(before,science.Source(source.Id)!.ManifestSha256);
    }
    [Fact]public async Task NoDataAndOutsideCoverageAreGapsAndUnknownDatumNeverBecomesMsl(){
        var source=await Source(datum:"Unknown reference");var p=Profile(source,[new(40,-75),new(40,-74.9)]);Assert.Contains(p.Points,x=>x.ElevationMetres is null);Assert.Null(p.NetChangeMetres);Assert.Contains(p.Warnings,w=>w.Contains("unknown",StringComparison.OrdinalIgnoreCase));Assert.DoesNotContain(p.Source!.VerticalDatum,"MSL");
        var outside=Profile(source,[new(1,1),new(1,1.01)]);Assert.All(outside.Points,x=>Assert.Null(x.ElevationMetres));Assert.Null(outside.MinimumMetres);
    }
    [Fact]public async Task SavedPlanLegAndDatelineDistanceAreActualGeodesics(){
        var source=await Source();var plan=missions.SavePlan(new(Guid.NewGuid(),"Profile route",Waypoints:[new("A",40,-75.01),new("B",40,-75),new("C",40,-74.99)]));
        var p=TerrainProfiles.Create(new(SourceId:source.Id,Path:"plan",PlanId:plan.Id,Leg:1),missions,science,_=>true);Assert.Equal(Geodesy.Distance(new(40,-75),new(40,-74.99)),p.DistanceMetres,5);
        var dateline=Profile(null,[new(0,179.99),new(0,-179.99)]);Assert.InRange(dateline.DistanceMetres,2200,2250);Assert.All(dateline.Points,x=>Assert.Null(x.ElevationMetres));
        Assert.Throws<ArgumentException>(()=>TerrainProfiles.Create(new(SourceId:source.Id,Path:"plan",PlanId:plan.Id,Leg:8),missions,science,_=>true));
    }
    [Fact]public async Task SimulatorContextUsesOnlyRecordedSimulatorValuesAndCannotSubstituteForDem(){
        var source=await Source();var now=DateTimeOffset.UtcNow;var id=missions.Create("Profile simulator fixture",null,now);
        for(var i=0;i<10;i++)missions.Append(id,new(now.AddSeconds(i),TelemetrySource.Simulator,"Explicit simulated test input",40,-75+i*.001,1000,100,80,80,80,90,90,0,0,0));
        missions.Finish(id,MissionStatus.Completed,now.AddSeconds(10),"fixture end");
        var absent=TerrainProfiles.Create(new(MissionId:id,SimulatorContext:true),missions,science,_=>true);Assert.All(absent.Points,p=>Assert.Null(p.ElevationMetres));Assert.Equal(10,absent.SimulatorContext.Length);Assert.All(absent.SimulatorContext,p=>Assert.Equal(274.32,p.ApproximateElevationMetres,5));Assert.Empty(science.Products());
        var real=TerrainProfiles.Create(new(MissionId:id,SourceId:source.Id),missions,science,_=>true);Assert.Empty(real.SimulatorContext);Assert.All(real.Points,p=>Assert.Equal(123.25,p.ElevationMetres));
    }
    [Theory][InlineData("feet",328.0839895,"ft")][InlineData("metres",100,"m")]
    public void ElevationPreferenceUsesScienceAltitudeIndependentlyOfPlanningPreset(string altitude,double value,string unit){var preferences=new ScienceUnitPreferences(Altitude:altitude,PlanningProfile:altitude=="feet"?"metric":"aviation");var c=ScienceUnits.Conversion("elevation",preferences);Assert.Equal(unit,c.Label);Assert.Equal(value,100*c.Scale,5);Assert.DoesNotContain("MSL",c.Label);}
    [Fact]public async Task CorruptOrUnavailableOriginalsFailClosed(){var source=await Source();Assert.Throws<IOException>(()=>TerrainProfiles.Create(new(SourceId:source.Id,Path:"line",Line:[new(40,-75),new(40.01,-75)]),missions,science,_=>false));Assert.Throws<IOException>(()=>TerrainProfiles.Create(new(SourceId:source.Id,Path:"line",Line:[new(40,-75),new(40.01,-75)]),missions,science,_=>throw new IOException("Original hash mismatch")));Assert.Throws<ArgumentException>(()=>TerrainProfiles.Create(new(SourceId:Guid.NewGuid(),Path:"line",Line:[new(40,-75),new(40.01,-75)]),missions,science,_=>true));}
    public void Dispose(){science.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
