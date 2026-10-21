using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed partial class ScientificLibraryJobs
{
    private ScientificSource? ReusableNasa(NasaDownloadBundle bundle)=>science.Sources().FirstOrDefault(s=>s.Manifest.Acquisition is {Workflow:"nasa-earthdata-original-envi"} a&&a.CatalogRevision==bundle.Revision&&s.Manifest.SceneId==bundle.Selection.Scene);
    private NasaDownloadBundle? RetainedNasa(NasaSelectionRequest request)
    {
        foreach(var source in science.Sources().Where(s=>s.Manifest.SceneId==request.GranuleId&&s.Manifest.Acquisition?.Workflow=="nasa-earthdata-original-envi"))
        {
            var bundle=JsonSerializer.Deserialize<NasaDownloadBundle>(source.Manifest.Acquisition!.ProviderMetadataJson!,WireJson.Options);
            if(bundle?.Selection.Provider==request.Provider&&bundle.Selection.Revision==request.Revision)return bundle;
        }
        return null;
    }
    public async Task<LibraryDownloadJob> PreviewNasa(NasaDownloadRequest request,CancellationToken token=default)
    {
        if(request.Selection is null||request.Selection.Provider is not("nasa-classic" or "nasa-ng"))throw new ArgumentException("Choose an exact AVIRIS-Classic or AVIRIS-NG reflectance scene. EMIT is discovery-only.");
        var p=settings.Get().Preferences;var limit=request.MaximumTransferBytes;
        if(limit<1048576||limit>p.NasaOriginalMaximumBytes||limit>(p.PreflightTransferBytes??21474836480))throw new ArgumentException("The NASA original-job budget must fit the configured NASA scene cap and remaining preflight policy (1 MiB–20 GiB). Review Scientific access settings; automatic subset caps stay unchanged.");
        var bundle=RetainedNasa(request.Selection);
        if(bundle is null)
        {
            if(!p.OnlineEnabled||!p.PrivacyAccepted||p.UnmeteredOnly&&settings.IsUnmetered()!=true)throw new IOException("Enable scientific retrieval consent and the required network type before NASA discovery/access.");
            if(earthdata.ReadToken() is null)throw new IOException("Connect Earthdata on the companion PC, then preview the exact original files.");
            bundle=await DiscoverNasaBundle(request.Selection,token);
        }
        var retained=ReusableNasa(bundle);if(retained is not null&&!Verify(retained))throw new IOException("Retained NASA originals changed. Restore/reimport them; no original was replaced.");
        if(bundle.Version!=1)throw new IOException("Unsupported NASA acquisition contract version. Preserve these records and use a compatible application.");
        var bytes=bundle.Files.Sum(f=>f.Bytes);if(retained is null&&bytes>limit)throw new IOException($"The selected NASA originals require {bytes} bytes before metadata/retries, above the {limit}-byte approval cap. Choose a smaller flightline or explicitly raise NASA/preflight/job limits. No subset was invented.");
        var temp=retained is null?checked(bytes*2+2097152):0;
        // Exact read-only reuse allocates no new source bytes. Storage reservations
        // deliberately reject zero and remain mandatory for every new acquisition.
        using(var space=temp>0?science.Management?.ReserveScience(temp):null){}
        var selected=new ScientificSceneSelection(request.Selection.Provider,request.Selection.GranuleId,request.Selection.Revision,["native-reflectance-and-companions"]);
        var selection=new ScientificLibrarySelection(1,[bundle.Selection.Bounds],[selected],MaximumTransferBytes:limit);
        var work=new[]{new LibraryWorkUnit(0,selected,bundle.Selection.Bounds,retained?.Id,temp)};
        var now=DateTimeOffset.UtcNow;var config=SettingsHash(true);var hash=ScientificCatalog.Hash(new{selection,bundle,work,config});var storage=science.Management?.Snapshot();
        return science.SaveLibraryPreview(new(1,Guid.NewGuid(),selection,hash,config,now,now.AddMinutes(30),work,[bundle.Selection,..bundle.Companions],limit,limit,temp,
            retained?.Files.Sum(f=>f.Bytes)??0,retained is not null?0:bytes,bytes,
            ["NASA direct acquisition downloads these complete original ENVI companions, not just the map selection. All native spectral channels are preserved; there is no validated NASA regional subset service here.","Exact file bytes are obtained from provider headers. Metadata, retries and interrupted-read allowances also consume the explicitly approved job cap; allow headroom.","The independent NASA original-scene cap does not change the existing automatic numeric-subset limit. Active flights preempt Library downloads.","Historical NASA reflectance is not a new aircraft observation. Successful file transfer does not establish QA-valid pixels or certify scientific accuracy.","Earthdata User Tokens expire (NASA documents 60 days). Reconnect when NASA denies access. Browser/manual import remains available."],
            AvailableDriveBytes:storage?.Science.FreeBytes,ScientificStorageLimitBytes:storage?.Science.LimitBytes,ScientificStorageUsedBytes:storage?.Science.UsedBytes,PreparationBudgetRemainingBytes:automatic.QueueSummary().PreflightAvailableBytes,Nasa:bundle));
    }
    private async Task<NasaDownloadBundle> DiscoverNasaBundle(NasaSelectionRequest request,CancellationToken token)
    {
        var selected=await catalog.ResolveNasa(request,token);var originals=selected.Nasa!.Assets.Where(a=>a.Role is "ENVI header" or "Original numeric binary"&&Regex.IsMatch(a.Name,@"_(corr|rfl|refl)(_|$)")).ToArray();
        var stems=originals.Select(a=>Path.GetFileNameWithoutExtension(a.Name)).Distinct().ToArray();
        if(stems.Length!=1)throw new IOException("This granule does not identify exactly one supported original reflectance pair. Use the guided importer for ambiguous/unsupported distributions.");
        var stem=stems[0];var items=new List<ScientificCatalogItem>{selected};
        if(!originals.Any(a=>a.Role=="ENVI header")||!originals.Any(a=>a.Role=="Original numeric binary"))
        {
            var page=await catalog.Search(new(request.Provider,selected.Bounds,PageSize:20,Refresh:true,SceneFilter:stem),token);
            if(page.Status!="Available"||page.NextCursor is not null)throw new IOException("The bounded matching-companion search is unavailable or ambiguous. Use official browser acquisition; no filename or companion URL was guessed.");
            items.AddRange(page.Items.Where(i=>i.Nasa?.CollectionId==selected.Nasa.CollectionId&&i.Scene!=selected.Scene&&i.Nasa.Assets.Any(a=>Path.GetFileNameWithoutExtension(a.Name)==stem)));
        }
        var assets=items.SelectMany(i=>i.Nasa!.Assets).Where(a=>a.Name==stem+".hdr"||a.Name==stem+".bin"||a.Name==stem+".hdr.sha256"||a.Name==stem+".bin.sha256").DistinctBy(a=>a.Url).OrderBy(a=>a.Name,StringComparer.Ordinal).ToArray();
        if(assets.Length is <2 or >4||assets.Select(a=>a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=assets.Length||!assets.Any(a=>a.Name==stem+".hdr")||!assets.Any(a=>a.Name==stem+".bin"))throw new IOException("NASA's exact original header/binary pairing could not be verified. Use the guided importer; no companion was inferred.");
        var files=new List<NasaDownloadFile>();foreach(var asset in assets)files.Add(await automatic.Http.DescribeNasaFile(asset,earthdata,token));
        var companions=items.Skip(1).OrderBy(i=>i.Scene,StringComparer.Ordinal).ToArray();return new(selected,companions,files.ToArray(),ScientificCatalog.Hash(new{selected,companions,files}));
    }
    private async Task<ScientificSource> AcquireNasa(LibraryDownloadJob job,NasaDownloadBundle bundle,ScientificDownloadBudget budget,CancellationToken token)
    {
        if(bundle.Version!=1)throw new IOException("Unsupported NASA acquisition contract version. No network or import operation was started.");
        if(bundle.Files.Sum(f=>f.Bytes)>settings.Get().Preferences.NasaOriginalMaximumBytes)throw new IOException("NASA scene allowance changed. Preview and approve the exact originals again.");
        foreach(var item in new[]{bundle.Selection}.Concat(bundle.Companions))await catalog.ResolveNasa(new(item.Provider,item.Scene,item.Revision),token,budget);
        var relative="Downloads/Library/"+job.Id.ToString("N")+"/0/NASA";
        var folder=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,relative);Directory.CreateDirectory(folder);
        var paths=bundle.Files.SelectMany(f=>new[]{relative+"/"+f.Name,relative+"/"+f.Name+".part",relative+"/"+f.Name+".part.json"}).ToArray();
        var marker=Path.Combine(folder,"ownership.json");StorageManager.ValidateLocalPath(marker);
        if(!File.Exists(marker)){var ledger=science.BeginManagedWrite("Approved recoverable NASA original acquisition",paths.Append(relative+"/ownership.json"));PreferenceStore.WriteAtomic(marker,new{ledger=Path.GetFileName(ledger)});}
        string OwnedLedger()
        {
            StorageManager.ValidateLocalPath(marker);if(new FileInfo(marker).Length>4096)throw new IOException("NASA download ownership record is oversized; preserved for review.");
            using var owned=JsonDocument.Parse(File.ReadAllText(marker));
            if(!owned.RootElement.TryGetProperty("ledger",out var value)||value.ValueKind!=JsonValueKind.String||value.GetString() is not {} name)throw new IOException("NASA download ownership is missing; retained work needs review.");
            if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(name),"N",out var id)||Path.GetFileName(name)!=name)throw new IOException("Invalid NASA download ownership identity.");
            var file=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,"Metadata/Work/"+name);
            if(!File.Exists(file)||new FileInfo(file).Length>65536)throw new IOException("NASA download ownership ledger is unavailable; retained work needs review.");
            var work=JsonSerializer.Deserialize<ManagedScientificWork>(File.ReadAllText(file),WireJson.Options)??throw new IOException("NASA download ownership is invalid.");
            if(work.Id!=id||work.Paths is null||work.Operation!="Approved recoverable NASA original acquisition"||!work.Paths.Order().SequenceEqual(paths.Append(relative+"/ownership.json").Order()))throw new IOException("NASA download ownership changed; no unrelated work was removed.");
            return file;
        }
        _=OwnedLedger();
        var merged=bundle.Selection with{Nasa=bundle.Selection.Nasa! with{Assets=bundle.Files.Select(f=>new NasaAsset(f.Name,f.Url,f.Role)).ToArray()}};
        var header=Path.Combine(folder,bundle.Files.Single(f=>f.Role=="ENVI header").Name);
        science.SetLibraryState(job.Id,"Downloading");science.SetLibraryItem(job.Id,0,"Downloading");
        using(var reservation=science.Management?.ReserveScience(bundle.Files.Sum(f=>f.Bytes)))
            foreach(var file in bundle.Files.OrderBy(f=>f.Role=="ENVI header"?0:f.Role=="Provider SHA-256"?1:2))
            {
                // Reject unsupported spectral/header metadata before requesting a large cube.
                if(file.Role=="Original numeric binary")_=NasaGuidedImport.Inspect(header,merged,token:token,direct:true);
                await automatic.Http.DownloadNasaFile(file,folder,earthdata,budget,token);
            }
        science.SetLibraryState(job.Id,"Verifying");science.SetLibraryItem(job.Id,0,"Verifying");
        var inspected=NasaGuidedImport.Inspect(header,merged,token:token,direct:true);
        if(inspected.Manifest is null)throw new IOException("NASA original metadata is incomplete or incompatible. Browser/manual import remains available; no metadata was guessed.");
        var provenance=new AutomaticSourceProvenance("nasa-earthdata-original-envi","NASA CMR / ORNL DAAC",bundle.Selection.Scene,bundle.Files.Select(f=>f.Url).ToArray(),bundle.Selection.Bounds,DateTimeOffset.UtcNow,
            "Original ENVI files streamed, identity-checked and verified by the existing NASA/ENVI importer; no numerical conversion.",bundle.Selection.Quality,bundle.Selection.Resolution,budget.Bytes,
            ["Historical source; no flight observation is created.","Provider/catalog footprints do not guarantee QA-valid pixels.","Retained hashes establish local integrity; only an authoritative advertised checksum verifies provider file identity."],JsonSerializer.Serialize(bundle,WireJson.Options),CatalogRevision:bundle.Revision);
        var manifest=inspected.Manifest with{Acquisition=provenance};
        var imported=await science.ImportSourceAsync(manifest,folder,token,reuseExisting:true,verifyStaged:stage=>
        {
            var copy=NasaGuidedImport.Inspect(Path.Combine(stage,Path.GetFileName(header)),merged,token:token,direct:true);
            if(ScientificCatalog.Hash(copy.Manifest)!=ScientificCatalog.Hash(inspected.Manifest))throw new IOException("NASA metadata changed during staged verification; no source was published.");
        });
        automatic.RefreshLocalReadiness();
        // Atomic publication succeeded. Remove ONLY this job's owned download duplicates.
        var completedLedger=OwnedLedger();
        foreach(var path in paths){var absolute=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,path);if(File.Exists(absolute))File.Delete(absolute);}
        File.Delete(completedLedger);
        File.Delete(marker);Directory.Delete(folder);return imported;
    }
    public async Task DisconnectEarthdata(CancellationToken token=default)
    {
        await commands.WaitAsync(token);
        try
        {
            // Clear first, so concurrent approvals cannot obtain another bearer.
            // Active transfers can be cancelled even with the Library drive offline.
            earthdata.Clear();var active=activeNasa.Keys.ToArray();
            foreach(var id in active)automatic.CancelScope("library:"+id);
            if(science.AvailabilityError() is null)
                foreach(var id in science.UnfinishedNasaJobs())
                {
                    science.SetLibraryState(id,"Paused");automatic.CancelScope("library:"+id);
                }
            foreach(var id in active)await automatic.WaitForLibraryScope("library:"+id,token);
        }finally{commands.Release();}
    }
}
