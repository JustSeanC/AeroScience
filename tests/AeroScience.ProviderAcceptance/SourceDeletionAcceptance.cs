using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

// Genuine public retained raster; all flight inputs are explicitly synthetic.
// Only fresh copies under the acceptance output are deleted. Inputs are read-only.
internal static class SourceDeletionAcceptance
{
    internal static async Task Run(string originalLibrary,string output,ScienceStore science,MissionStore missions)
    {
        ScientificSource? original=null;string? input=null;
        foreach(var directory in Directory.EnumerateDirectories(Path.Combine(originalLibrary,"Sources")))
        {
            var path=Path.Combine(directory,"source.json");StorageManager.ValidateLocalPath(path);if(new FileInfo(path).Length>16777216)throw new IOException("Retained source metadata is oversized.");
            var candidate=JsonSerializer.Deserialize<ScientificSource>(File.ReadAllText(path),WireJson.Options)!;
            if(candidate.Manifest.Acquisition?.Workflow!="usgs-3dep-numeric")continue;original=candidate;input=directory;break;
        }
        if(original is null||input is null)throw new IOException("This check requires an already genuinely verified USGS 3DEP numeric source; no fixture is substituted.");
        foreach(var file in original.Files){using var stream=File.OpenRead(Path.Combine(input,file.RelativePath));if(Convert.ToHexStringLower(SHA256.HashData(stream))!=file.Sha256)throw new IOException("Genuine input original changed; no deletion test was run.");}
        var source=await science.ImportSourceAsync(original.Manifest,input);missions.ScienceLibrary=science;
        var point=source.Manifest.Acquisition!.RequestedArea.Center;Mission mission;RemoteProduct product;MissionCoordinator recorder;
        using(var research=new ResearchOperations(missions,science))
        {
            recorder=new(missions,1000,research:research);mission=recorder.Start(new("Genuine 3DEP / synthetic deletion test flight",ScienceMode.Realistic,Instruments:[new("terrain-scanner",true)]));
            var at=DateTimeOffset.UtcNow;var frame=new TelemetryFrame(at,TelemetrySource.Simulated,"Synthetic acceptance position — no MSFS",point.Latitude,point.Longitude,1500,500,100,100,100,0,0,0,0,0);
            recorder.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Genuine public source; synthetic aircraft input",at),frame));
            var deadline=DateTimeOffset.UtcNow.AddSeconds(10);while(science.Products(mission.Id).Length==0&&DateTimeOffset.UtcNow<deadline)await Task.Delay(20);
            product=science.Products(mission.Id).Single();recorder.End(mission.Id);
        }
        var data=science.ReadProduct(product.Id);if(!data.Bands.Any(b=>b.Values.Any(v=>v.HasValue)))throw new IOException("No genuine original numeric values were acquired.");
        var before=MissionEvaluationArchive.Build(missions,science,mission.Id);var report=missions.SavePerformanceReport(before,"Genuine source with synthetic flight-input independence check; not owner acceptance");
        var flightHash=PreflightReadiness.Hash(missions.StreamSamples(mission.Id).ToArray());
        var recoveryParent=Path.Combine(output,"controlled-managed-recovery");Directory.CreateDirectory(recoveryParent);var recovery=science.CreateRecoveryBackup(recoveryParent);
        var deletion=science.PreviewSourceDeletion(new([source.Id]),missions);if(deletion.EligibleSources!=1)throw new IOException("Original deletion blocked: "+string.Join("; ",deletion.Sources.SelectMany(s=>s.Blockers)));
        var removed=science.DeleteExternalSources(new(deletion.Id,deletion.SelectionHash),missions,recorder.EnterMaintenance);if(removed.State!="Completed")throw new IOException("Original deletion incomplete: "+removed.Warning);
        if(source.Files.Any(f=>File.Exists(Path.Combine(science.DirectoryPath,source.RelativeDirectory,f.RelativePath))))throw new IOException("A managed original remains after Completed.");
        var after=MissionEvaluationArchive.Build(missions,science,mission.Id);if(PreflightReadiness.Hash(before)!=PreflightReadiness.Hash(after)||missions.LatestPerformanceReport(mission.Id)?.Sha256!=report.Sha256)throw new IOException("Source deletion changed retained grading evidence.");
        var library=science.DirectoryPath;science.Dispose();
        using(var reopened=new ScienceStore(library))
        {
            reopened.Initialize();missions.ScienceLibrary=reopened;var saved=reopened.ReadProduct(product.Id);
            if(PreflightReadiness.Hash(data)!=PreflightReadiness.Hash(saved))throw new IOException("Offline restart changed saved scientific results.");
            _=ScientificMapLayer.Create(saved,"elevation");_=ScientificResearchSummary.Build(missions,reopened,mission.Id);
            using var export=new MemoryStream();await ResearchPackage.WriteAsync(missions,reopened,mission.Id,export);
            if(ScientificSourceSelection.Select(reopened.Availability(point,RemoteKind.Terrain),RemoteKind.Terrain) is not null)throw new IOException("Removed source can still be selected for new sampling.");
            var preview=reopened.PreviewCollectedDeletion(new([mission.Id]),missions);var result=reopened.DeleteCollectedScience(new(preview.Id,preview.SelectionHash),missions,recorder.EnterMaintenance);
            if(result.State!="Completed"||reopened.AllProducts(mission.Id).Any()||File.Exists(Path.Combine(library,product.RelativePath))||File.Exists(Path.Combine(recovery,product.RelativePath))||missions.LatestPerformanceReport(mission.Id) is not null||!missions.ScienceDeleted(mission.Id)||flightHash!=PreflightReadiness.Hash(missions.StreamSamples(mission.Id).ToArray()))throw new IOException("Collected science/backup purge or flight preservation failed.");
            await File.WriteAllTextAsync(Path.Combine(output,"source-deletion.json"),JsonSerializer.Serialize(new{
                formatVersion=1,checkedUtc=DateTimeOffset.UtcNow,evidenceType="genuine public retained 3DEP original + synthetic flight-input integration",windowsMsfs="not tested",networkDuringThisCheck="none; retained original input read-only",original=source.Manifest,originalSource=original,originalFiles=original.Files,source.Id,
                locations=data.SampleLocations,bands=data.Bands,derived=data.Derived,originalsRemoved=true,offlineSavedValuesIdentical=true,savedMapAndPackageOpened=true,gradingInputSha256=PreflightReadiness.Hash(before),reportSha256=report.Sha256,gradeStatus=report.Grade.Status,
                removedSourceNotSelectableForNewSampling=true,collectedSciencePurged=true,controlledRecoveryCopyPurged=true,rawFlightSha256=flightHash,inputOriginalsPreserved=true
            },WireJson.Options));
        }
        foreach(var file in original.Files){using var stream=File.OpenRead(Path.Combine(input,file.RelativePath));if(Convert.ToHexStringLower(SHA256.HashData(stream))!=file.Sha256)throw new IOException("Read-only genuine inputs changed.");}
        Console.WriteLine("PASS: genuine 3DEP values survive original deletion/offline reopen; selected science/recovery copies then purged, raw synthetic-input flight preserved. Not installed-device acceptance.");
    }
}
