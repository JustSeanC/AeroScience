using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.TestHost;
internal static class TerrainElevationProbe
{
    public static async Task Run(string input,string destination)
    {
        input=Path.GetFullPath(input);destination=Path.GetFullPath(destination);
        if(Directory.Exists(destination))throw new IOException("Choose a new isolated probe directory; earlier results and owner data are preserved.");
        var paths=new StorageManager(new PreferenceStore(destination)).Initialize();var missions=new MissionStore(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();
        using var science=new ScienceStore(paths.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;
        var fixture=await TerrainCompletionFixture.Create(missions,science,destination,input);var job=science.TerrainJob(fixture.JobId);science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));
        var index=TerrainElevations.Process(science,missions,job.Id);var original=science.Source(fixture.SourceId)!;
        var grid=original.Grids["elevation"];if((long)grid.Width*grid.Height>4096)throw new InvalidDataException("Genuine probe is capped at 4096 native source cells.");
        using var raster=new ScientificRaster(Path.Combine(science.DirectoryPath,original.RelativeDirectory),original.Manifest.Bands[0]);
        var cells=new List<object>();for(var row=0;row<grid.Height;row++)for(var col=0;col<grid.Width;col++){var value=raster.ReadNativeCell(col,row);var point=ScientificRaster.InspectionGridPoint(grid,col+.5,row+.5);cells.Add(new{column=col,row,value=value?.Value,raw=value?.DecodedSourceValue,point});}
        PreferenceStore.WriteAtomic(Path.Combine(destination,"native-grid.json"),new{grid,cells});PreferenceStore.WriteAtomic(Path.Combine(destination,"source.json"),original);
        File.Copy(science.VerifiedArtifactPath(science.Artifact(fixture.GeometryId)),Path.Combine(destination,"geometry.json"));File.Copy(science.VerifiedArtifactPath(index),Path.Combine(destination,"index.json"));
        var tiles=Path.Combine(destination,"tiles");Directory.CreateDirectory(tiles);foreach(var tile in science.TerrainCheckpoints(job.Id))File.Copy(science.VerifiedArtifactPath(science.Artifact(tile.ArtifactId)),Path.Combine(tiles,tile.ArtifactId.ToString("N")+".json"));
        PreferenceStore.WriteAtomic(Path.Combine(destination,"probe.json"),new{fixture,index.Sha256,algorithm=TerrainElevations.Algorithm,originalSource=Path.GetRelativePath(destination,Path.Combine(science.DirectoryPath,original.RelativeDirectory,original.Manifest.Bands[0].File)),originalScienceObservations=science.StreamObservations(fixture.MissionId).Count(),originalScienceProducts=science.Products(fixture.MissionId).Length,nativeSimulatorAcceptance="Not Tested",independentReferenceAcceptance="Pending GDAL/OGR comparison"});
        Console.WriteLine("Genuine retained composite source processed offline with synthetic archived flight geometry. Independent GDAL/OGR comparison is still required.");
    }
}
