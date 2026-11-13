using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.TestHost;

// Explicit isolated development fixture. Never called by opening an app viewer.
internal static class SurveyViewProbe
{
    public static async Task Run(string retainedSource,string terrainOriginal,string destination)
    {
        destination=Path.GetFullPath(destination);retainedSource=Path.GetFullPath(retainedSource);
        if(Directory.Exists(destination))throw new IOException("Preserve earlier evidence: use a new isolated validation directory.");
        var parent=JsonSerializer.Deserialize<ScientificSource>(File.ReadAllBytes(Path.Combine(retainedSource,"source.json")))??throw new IOException("A retained genuine source descriptor is required.");
        if(parent.Manifest.Provider!="USGS public EPT"||parent.Manifest.PointCloud?.EptProject!="IA_FullState")throw new IOException("This explicit probe is limited to previously approved IA_FullState originals.");
        var paths=new StorageManager(new PreferenceStore(destination)).Initialize();var missions=new MissionStore(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();using var science=new ScienceStore(paths.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;
        new ScientificAccessSettings(destination,()=>true).Save(new(false,false));
        var original=parent.Manifest.PointCloud.Originals.Single();var input=Path.Combine(retainedSource,original.File);var header=LidarLas.Inspect(input);if(header.PointCount>100000)throw new IOException("Use one bounded genuine original with at most 100,000 records.");
        var source=await LidarImports.Import(science,input,new("Genuine IA_FullState historical delivered EPT node — offline viewer validation",parent.Manifest.SceneId!,parent.Manifest.License,parent.Manifest.Citation+" ; imported offline from retained source "+parent.Id+" / "+parent.ManifestSha256,parent.Manifest.ObservationUtc,PublishedUtc:parent.Manifest.PublicationUtc));
        var b=header.NativeBounds;var center=LidarCoordinates.Geographic(header.HorizontalCrs!,(b[0]+b[3])/2,(b[1]+b[4])/2);
        var geometry=LidarWorkflowFixture.Flight(missions,science,center,100,6000,11);
        var before=ScientificCatalog.Hash(MissionEvaluationArchive.Build(missions,science,geometry.MissionId));
        var job=LidarProcessing.Create(science,missions,geometry.MissionId,new(geometry.Id,[new(source.Id,source.ManifestSha256,true)],new(RecordsPerTile:1024,MaximumCandidatePoints:100000,MaximumScratchBytes:16777216),Guid.NewGuid()));science.ControlLidarJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));var index=await LidarProcessing.Process(science,missions,job.Id);
        var tiles=Path.Combine(destination,"tiles");Directory.CreateDirectory(tiles);foreach(var tile in science.LidarCheckpoints(job.Id))File.Copy(science.VerifiedArtifactPath(science.Artifact(tile.ArtifactId)),Path.Combine(tiles,tile.ArtifactId.ToString("N")+".json"));
        File.Copy(science.VerifiedArtifactPath(geometry),Path.Combine(destination,"geometry.json"));File.Copy(science.VerifiedArtifactPath(index),Path.Combine(destination,"index-"+job.Id.ToString("N")+".json"));PreferenceStore.WriteAtomic(Path.Combine(destination,"sources.json"),new[]{source});
        // Reuse the accepted genuine DEM fixture and original worker, same isolated Library.
        var terrain=await TerrainCompletionFixture.Create(missions,science,destination,terrainOriginal);var terrainJob=science.TerrainJob(terrain.JobId);science.ControlTerrainJob(terrainJob.Id,new(Guid.NewGuid(),terrainJob.Revision,"start"));var terrainIndex=TerrainElevations.Process(science,missions,terrainJob.Id);
        var profile=SurveyProfiles.Create(science,missions,terrainIndex.Id,new(terrainIndex.Sha256,terrain.SourceId,MaximumSamples:257));
        PreferenceStore.WriteAtomic(Path.Combine(destination,"profile.json"),profile);PreferenceStore.WriteAtomic(Path.Combine(destination,"terrain-manifest.json"),SurveyResults.Manifest(science,missions,terrainIndex.Id));
        var pages=new List<SurveyViewValue>();long? cursor=0;do{var page=SurveyResults.Page(science,missions,terrainIndex.Id,new(terrainIndex.Sha256,cursor.Value,512,terrain.SourceId));pages.AddRange(page.Values);cursor=page.NextCursor;}while(cursor is not null);PreferenceStore.WriteAtomic(Path.Combine(destination,"terrain-view-values.json"),pages);
        PreferenceStore.WriteAtomic(Path.Combine(destination,"probe.json"),new{syntheticFlight=true,genuineHistoricalSources=true,mode="offline-genuine-viewer",geometry.MissionId,geometry.Sha256,geometry.InputIdentity,algorithm=LidarProcessing.Algorithm,sourceLibrary=Path.GetRelativePath(destination,science.DirectoryPath),parentSourceId=parent.Id,parentManifestSha256=parent.ManifestSha256,parentOriginalSha256=parent.Files.Single(f=>f.RelativePath==original.File).Sha256,sourceId=source.Id,lidarIndexId=index.Id,lidarPoints=science.LidarJob(job.Id).RetainedPoints,terrainMissionId=terrain.MissionId,terrainIndexId=terrainIndex.Id,terrainSourceId=terrain.SourceId,originalScienceUnchanged=before==ScientificCatalog.Hash(MissionEvaluationArchive.Build(missions,science,geometry.MissionId)),originalScienceObservations=science.StreamObservations(geometry.MissionId).Count(),originalScienceProducts=science.Products(geometry.MissionId).Length,networkDownloads=0,nativeAcceptance="Not Tested",note="Visible tile/profile exports are declared isolated validation evidence, not application caches. The managed genuine original is an explicit validation import; no owner Library is modified."});
        Console.WriteLine(JsonSerializer.Serialize(new{lidarPoints=science.LidarJob(job.Id).RetainedPoints,terrainCells=pages.Count(v=>v.Value is not null),profileValues=profile.Samples.Count(v=>v.Value is not null),state="Awaiting independent numeric and browser validation"}));
    }
}
