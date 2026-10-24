using System.Security.Cryptography;
using AeroScience.Shared;
namespace AeroScience.Missions;

public static class TerrainPlanning
{
    // Pure bounded relative-height model. All heights come from one selected source plus the stated planning AGL.
    // It deliberately never treats unspecified MSFS MSL as an external geodetic datum.
    public static ObservationFootprint Intersect(Coordinates centre,double agl,double heading,SensorMount mount,Func<Coordinates,double?> elevation,string sourceReference,CancellationToken token=default)
    {
        if(!double.IsFinite(agl)||agl is <1 or >60000||!double.IsFinite(heading))throw new ArgumentException("Terrain planning requires positive finite AGL and true heading.");
        var baseHeight=elevation(centre)??throw new InvalidOperationException("Selected terrain has no value beneath the planning centre.");
        var aircraftHeight=baseHeight+agl;var h=Math.Tan(mount.HorizontalFovDegrees*Math.PI/360);var v=Math.Tan(mount.VerticalFovDegrees*Math.PI/360);
        Coordinates Ray(double forward,double right)
        {
            var d=SensorGeometry.Direction(mount,0,0,forward,right);var norm=Math.Sqrt(d.X*d.X+d.Y*d.Y+d.Z*d.Z);d=(d.X/norm,d.Y/norm,d.Z/norm);
            if(d.Z<.15)throw new InvalidOperationException("Terrain ray approaches the horizon; terrain-aware planning is unavailable.");
            var bearing=heading+Math.Atan2(d.Y,d.X)*180/Math.PI;var horizontal=Math.Sqrt(d.X*d.X+d.Y*d.Y);
            Coordinates Position(double metres)=>Geodesy.Direct(centre,bearing,metres*horizontal);
            double Clearance(double metres){token.ThrowIfCancellationRequested();var terrain=elevation(Position(metres))??throw new InvalidOperationException("Terrain ray leaves verified DEM coverage or meets nodata.");return aircraftHeight-metres*d.Z-terrain;}
            double prior=0;
            for(var step=1;step<=2048;step++)
            {
                var distance=step*25d;if(Clearance(distance)>0){prior=distance;continue;}
                var low=prior;var high=distance;for(var i=0;i<20;i++){var mid=(low+high)/2;if(Clearance(mid)>0)low=mid;else high=mid;}return Position((low+high)/2);
            }
            throw new InvalidOperationException("Terrain ray exceeds the bounded 51.2 km slant range; no extrapolated intersection was generated.");
        }
        return new(Ray(0,0),[Ray(v,-h),Ray(v,h),Ray(-v,h),Ray(-v,-h)],agl,"EPSG:4326",sourceReference,
            "terrain-planning-1: relative AGL anchored to one retained DEM at the planned centre; normalized body/mount rays, WGS84 displacement, bounded terrain march and binary crossing refinement",
            ["Planning estimate only. This does not transform or correct simulator MSL altitude. All terrain heights share the selected original source reference.",
             "Level flight; 25 m slant steps (2048 maximum) and 20 crossing refinements. Narrower terrain features/occlusions can be missed; native DEM resolution and surface-model identity still limit accuracy.",
             "No terrain or source values are extrapolated across nodata. Survey-wide spacing/swath strips remain the explicitly labeled nadir flat-ground baseline."]);
    }
    public static SensorPlanningResult Apply(SensorPlanningRequest request,SensorPlanningResult result,ScienceStore science,CancellationToken token=default)
    {
        if(request.TerrainSourceId is not {} id)return result;
        try
        {
            var centre=request.Pattern?.Center??throw new InvalidOperationException("Choose a pattern centre to inspect terrain-aware planning.");
            var source=(science.Source(id) is {Manifest.Kind:RemoteKind.Terrain} terrain?terrain:null)??throw new InvalidOperationException("Selected retained numeric terrain source is unavailable.");
            var band=source.Manifest.Bands.FirstOrDefault(b=>!b.BadBand&&b.Unit=="m")??throw new InvalidOperationException("Selected terrain source has no supported metre elevation band.");
            var root=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,source.RelativeDirectory);
            foreach(var file in source.Files){using var stream=File.OpenRead(ScientificRaster.SafeFile(root,file.RelativePath));if(stream.Length!=file.Bytes||Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()!=file.Sha256)throw new IOException("Selected terrain source failed its original-file hash check.");}
            using var raster=new ScientificRaster(root,band);
            var footprint=Intersect(centre,request.AltitudeAglMetres,request.Pattern!.HeadingDegrees,InstrumentProfiles.Mount(request.Instrument),raster.Read,
                $"Selected source {source.Id}; {source.Manifest.VerticalDatum}; planned relative AGL anchored to its centre elevation",token);
            return result with{TerrainPlanningFootprint=footprint,TerrainStatus="Terrain-aware centre footprint ready (relative-AGL planning only)",Advisories=result.Advisories.Concat(footprint.Limitations).ToArray()};
        }
        catch(Exception e)when(e is IOException or InvalidOperationException or ArgumentException)
        {return result with{TerrainStatus="Flat-ground fallback: "+e.Message,Advisories=result.Advisories.Concat(["Terrain-aware centre footprint unavailable; flat-ground planning retained. No simulator/external datum correction was applied."]).ToArray()};}
    }
}
