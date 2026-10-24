using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed record EmitImportReview(Guid Id,string SelectionHash,DateTimeOffset ExpiresUtc,long OriginalBytes,EmitDefinition Definition,string[] FileNames,string Message);
public sealed record EmitImportState(string Stage,string Message,EmitImportReview? Review=null,Guid? SourceId=null);
public sealed record EmitImportApproval(Guid Id,string SelectionHash,bool ValidationAcknowledged);
public sealed record EmitSampleRequest(int Row,int Column,int FirstBand=0,int BandCount=285,bool ExcludeWater=false,bool ExcludeInterpolatedBands=true);
public sealed record EmitMapRequest(int Row,int Column,int Rows=1,int Columns=4,int Band=0,bool ExcludeWater=false,bool ExcludeInterpolatedBands=true);
public sealed record EmitComparisonRequest(EmitSampleRequest First,EmitSampleRequest Second);

// A small source adapter over the existing Library. No new database, job engine,
// authentication, hidden originals or persistent derived cache. User-facing reads
// are serialized, cancellable and rejected during active flight recording.
public sealed class EmitLibrary(ScienceStore science,EmitProductDecoder decoder,MissionCoordinator coordinator):IScientificSwathImport,IDisposable
{
    private readonly SemaphoreSlim work=new(1);private readonly object stateGate=new();
    private EmitImportState state=new("Choose","Development validation: genuine NASA original numerical acceptance is pending. Choose matching RFL002, RFLUNCERT002 and MASK003 originals on the companion PC.");
    private EmitProduct? preview;private string? previewFolder,previewLedger;private CancellationTokenSource? cancellation;
    public EmitImportState Snapshot(){lock(stateGate)return state;}
    private void Set(EmitImportState value){lock(stateGate)state=value;}
    private void RequireIdle(){if(coordinator.Snapshot().ActiveMission is not null)throw new InvalidOperationException("EMIT source exploration/import waits while flight recording is active. Flight-critical collection retains priority; no HDF processing runs on telemetry.");}
    public async Task SelectLocal(string[] selected,CancellationToken token=default)
    {
        RequireIdle();if(!await work.WaitAsync(0,token))throw new InvalidOperationException("An EMIT verification/read is active. Wait or cancel it first.");
        try
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token);cancellation=linked;token=linked.Token;
            using var operation=science.BeginAnalysis();
            if(previewFolder is not null)throw new InvalidOperationException("Cancel or import the current reviewed files before choosing others.");
            if(selected.Length!=3||selected.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=3)throw new ArgumentException("Select exactly three matching original NetCDF files.");
            foreach(var path in selected){StorageManager.ValidateLocalPath(path);if(!Path.IsPathFullyQualified(path)||Path.GetFileName(path).Length>150||!Path.GetExtension(path).Equals(".nc",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Use original local NetCDF files, not archives or images.");}
            var count=selected.Sum(f=>new FileInfo(f).Length);
            if(count<=0||count>64L*1024*1024*1024||selected.Any(f=>new FileInfo(f).Length is <=0 or >16L*1024*1024*1024))throw new IOException("This bounded import supports at most 16 GiB per original / 64 GiB per scene; no Library collection-count limit applies.");
            using var reservation=science.Management?.ReserveScience(checked(count*2+2097152));
            var id=Guid.NewGuid();var relative=$"Downloads/Emit/{id:N}";
            var folder=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,relative);
            var paths=selected.Select(p=>relative+"/"+Path.GetFileName(p)).Append(relative+"/source.json").ToArray();
            var ledger=science.BeginManagedWrite("Owned EMIT local-original staging",paths);
            Directory.CreateDirectory(folder);previewFolder=folder;previewLedger=ledger;
            Set(new("Verifying","Copying original files into bounded owned staging, hashing and checking isolated product compatibility. No source is available until approved publication."));
            foreach(var input in selected)
            {
                token.ThrowIfCancellationRequested();var output=Path.Combine(folder,Path.GetFileName(input));
                await using var from=new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.Asynchronous);
                await using var to=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,FileOptions.Asynchronous);
                var originalLength=from.Length;var originalHash=Convert.ToHexStringLower(await SHA256.HashDataAsync(from,token));from.Position=0;
                using var copied=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long written=0;
                var buffer=new byte[131072];int n;while((n=await from.ReadAsync(buffer,token))>0){written+=n;if(written>originalLength)throw new IOException("Selected EMIT original changed during copying; no source was published.");copied.AppendData(buffer,0,n);science.Management?.RequireScienceCapacity();if(new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace<n+StorageManager.MinimumFreeBytes)throw new IOException("Scientific Library disk space exhausted during EMIT staging.");await to.WriteAsync(buffer.AsMemory(0,n),token);}to.Flush(true);if(written!=originalLength||Convert.ToHexStringLower(copied.GetHashAndReset())!=originalHash)throw new IOException("Selected EMIT original changed while copying; staged bytes were not published.");
                from.Position=0;if(from.Length!=originalLength||Convert.ToHexStringLower(await SHA256.HashDataAsync(from,token))!=originalHash)throw new IOException("Selected EMIT source identity changed during staging.");
            }
            var product=await OpenFolder(folder,token);
            preview=product;var review=new EmitImportReview(id,ScientificCatalog.Hash(new{science.LibraryId,files=product.Definition.Sources.Select(s=>new{s.OriginalFileName,s.Bytes,s.Sha256}),policy=EmitProductDecoder.Policy}),DateTimeOffset.UtcNow.AddMinutes(30),count,product.Definition,product.Definition.Sources.Select(s=>s.OriginalFileName).ToArray(),"Original files are preserved. Import creates a managed immutable copy; historical NASA science is not an aircraft observation. Genuine-original and installed acceptance remain separate.");
            PreferenceStore.WriteAtomic(Path.Combine(folder,"source.json"),Manifest(product));
            Set(new("Review","Review this exact three-file source and explicitly approve experimental import. No scientific Library source has been published.",review));
        }
        catch(Exception e){Set(new("Failed",e is OperationCanceledException?"Verification cancelled; owned interrupted staging is retained for explicit cleanup.":e.Message));throw;}
        finally{cancellation=null;work.Release();}
    }
    public void Import(EmitImportApproval approval)
    {
        RequireIdle();lock(stateGate)
        {
            if(state.Stage!="Review"||state.Review is not {} r||r.Id!=approval.Id||r.SelectionHash!=approval.SelectionHash||r.ExpiresUtc<DateTimeOffset.UtcNow||!approval.ValidationAcknowledged||preview is null)throw new InvalidOperationException("EMIT review expired, changed or was not acknowledged. Review the exact originals again; no source was published.");
            state=state with{Stage="Importing",Message="Verifying originals again and atomically registering the source. Closing this window does not cancel the operation."};
            cancellation=new();_ = RunImport(cancellation.Token);
        }
    }
    private async Task RunImport(CancellationToken token)
    {
        var acquired=false;
        try
        {
            await work.WaitAsync(token);acquired=true;RequireIdle();var product=preview??throw new IOException("No verified originals.");
            var imported=await Publish(product,previewFolder!,null,token);
            CleanupPreview();Set(new("Ready","Verified local source retained for offline EMIT exploration. Product compatibility is checked; genuine NASA numeric acceptance remains pending.",SourceId:imported.Id));
        }
        catch(Exception e){Set(new("Failed",e is OperationCanceledException?"Import cancelled. No partial source is advertised; owned staging remains for explicit retry/cleanup.":e.Message));}
        finally{cancellation?.Dispose();cancellation=null;if(acquired)work.Release();}
    }
    public void Cancel()
    {
        lock(stateGate){if(state.Stage is "Importing" or "Verifying"){cancellation?.Cancel();return;}}
        if(!work.Wait(0))throw new InvalidOperationException("Verification is still active; wait for it to finish before clearing the review.");
        try{CleanupPreview();Set(new("Choose","Select another exact original triple. Saved sources and background jobs were not changed."));}finally{work.Release();}
    }
    private void CleanupPreview()
    {
        if(previewFolder is {} folder)
        {
            StorageManager.ValidateLocalPath(folder);if(Directory.Exists(folder))
            {
                var allowed=preview?.Definition.Sources.Select(s=>s.OriginalFileName).Append("source.json").ToHashSet(StringComparer.Ordinal)??Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(n=>n is not null&&n.EndsWith(".nc",StringComparison.Ordinal)).Append("source.json").ToHashSet(StringComparer.Ordinal)!;
                foreach(var file in Directory.EnumerateFiles(folder)){StorageManager.ValidateLocalPath(file);if(!allowed.Contains(Path.GetFileName(file)))throw new IOException("Unexpected staging ownership; cleanup stopped without deleting unrelated files.");}
                foreach(var file in Directory.EnumerateFiles(folder))File.Delete(file);Directory.Delete(folder);
            }
            if(previewLedger is {} ledger){StorageManager.ValidateLocalPath(ledger);File.Delete(ledger);}
        }
        preview=null;previewFolder=null;previewLedger=null;science.Management?.RefreshScience();
    }
    private async Task<EmitProduct> OpenFolder(string folder,CancellationToken token)
    {
        StorageManager.ValidateLocalPath(folder);
        var names=Directory.EnumerateFiles(folder,"*.nc").Take(4).ToArray();
        var originals=new List<HdfOriginal>();
        foreach(var path in names){StorageManager.ValidateLocalPath(path);await using var stream=File.OpenRead(path);var relative=Path.GetRelativePath(science.DirectoryPath,path).Replace('\\','/');originals.Add(new(science.DirectoryPath,science.LibraryId,relative,stream.Length,Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,token))));}
        return await decoder.OpenAsync(EmitProductDecoder.ResolveOriginals(originals),token);
    }
    private static ScientificSourceManifest Manifest(EmitProduct product)
    {
        var d=product.Definition;var files=d.Sources.Select(s=>s.OriginalFileName).ToArray();
        // Stable identity excludes transient staging paths, retains original attributes/hashes.
        var stable=d with{Sources=d.Sources.Select(s=>s with{RelativePath=s.OriginalFileName}).ToImmutableArray()};
        return new(1,"NASA EMIT L2A reflectance · "+d.Acquisition,RemoteKind.Hyperspectral,"NASA / LP DAAC EMIT","NASA Earthdata collection terms; https://doi.org/10.5067/EMIT/EMITL2ARFL.002","https://doi.org/10.5067/EMIT/EMITL2ARFL.002",d.Sources[0].Granule,d.StartUtc,"not applicable",
            Enumerable.Range(0,d.Bands).Select(i=>new SourceBand("EMIT_"+i,files[0],"emit-netcdf",i,"reflectance",WavelengthNm:d.WavelengthNm[i],BadBand:d.GoodWavelengths[i]==0)).ToArray(),
            Notes:"Historical NASA swath. GLT domain is a candidate envelope, not verified QA coverage. Use bounded EMIT exploration; automatic aircraft acquisition and affine raster composites are not supported by this adapter.",OriginalMetadataFiles:files,
            Swath:new(1,"emit-netcdf",EmitProductDecoder.Policy,d.DerivedGltDomain,JsonSerializer.Serialize(stable,WireJson.Options)));
    }
    private static void CheckBundle(EmitProduct product,NasaDownloadBundle bundle)
    {
            var d=product.Definition;var item=bundle.Selection;var domain=d.DerivedGltDomain;
            if(item.Provider!="nasa-emit"||item.Nasa?.Flightline!=d.Sources[0].Granule||item.ObservationUtc!=d.StartUtc||item.Nasa.ObservationEndUtc!=d.EndUtc||bundle.Companions.Length!=1||bundle.Companions[0].Nasa?.Flightline!=d.Sources[2].Granule||bundle.Companions[0].ObservationUtc!=d.StartUtc||
                domain.East<=item.Bounds.West||domain.West>=item.Bounds.East||domain.North<=item.Bounds.South||domain.South>=item.Bounds.North||
                !bundle.Files.Select(f=>(f.Name,f.Bytes)).OrderBy(f=>f.Name).SequenceEqual(d.Sources.Select(f=>(f.OriginalFileName,f.Bytes)).OrderBy(f=>f.OriginalFileName)))
                throw new EmitProductException("CatalogMismatch","Original EMIT identity, time, geographic domain or required files disagree with the approved CMR selection; no source was published or substituted.");
    }
    private async Task<ScientificSource> Publish(EmitProduct product,string folder,NasaDownloadBundle? bundle,CancellationToken token)
    {
        var manifest=Manifest(product);
        if(bundle is not null)CheckBundle(product,bundle);
        using var lease=science.BeginAnalysis();
        // Exact original hashes/policy only. Nearby acquisitions are never interchangeable.
        foreach(var source in science.StreamSources(new(Scene:manifest.SceneId)).Where(s=>s.Manifest.Swath?.DecoderPolicy==EmitProductDecoder.Policy))
            if(source.Files.Select(f=>(f.RelativePath,f.Bytes,f.Sha256)).OrderBy(f=>f.RelativePath).SequenceEqual(product.Definition.Sources.Select(f=>(f.OriginalFileName,f.Bytes,f.Sha256)).OrderBy(f=>f.OriginalFileName))){await OpenSource(source,token);return source;}
        if(bundle is not null)manifest=manifest with{Acquisition=new("nasa-earthdata-original-emit","NASA CMR / LP DAAC",bundle.Selection.Scene,bundle.Files.Select(f=>f.Url).ToArray(),bundle.Selection.Bounds,DateTimeOffset.UtcNow,"Original NetCDF bytes retained, isolated EMIT decoder; no affine conversion or flight observation.",bundle.Selection.Quality,bundle.Selection.Resolution,0,["Genuine NASA numeric acceptance is separate and pending."],JsonSerializer.Serialize(bundle,WireJson.Options),CatalogRevision:bundle.Revision)};
        return await science.ImportSourceAsync(manifest,folder,token,reuseExisting:true,verifySwath:async(stage,ct)=>
        {
            var copied=await OpenFolder(stage,ct);
            if(ScientificCatalog.Hash(Manifest(copied))!=ScientificCatalog.Hash(Manifest(product)))throw new IOException("Staged EMIT product changed; no source was published.");
            // Verify bounded actual numeric / spatial correspondence before publication.
            await decoder.ReadNativeAsync(copied,new(0,0,1,1,0,1),token:ct);
            await decoder.ReadGeographicAsync(copied,new(0,0,1,1,0,1),token:ct);
        });
    }
    public async Task<ScientificSource?> FindReusable(NasaDownloadBundle bundle,CancellationToken token)
    {
        // Names/nearby coverage alone never prove exact content identity. Public CMR
        // original SHA-512 checksums permit reuse before requesting protected bytes.
        if(bundle.Files.Length!=3||bundle.Files.Any(f=>f.Sha512 is null))return null;
        RequireIdle();await work.WaitAsync(token);
        try
        {
            using var lease=science.BeginAnalysis();var scene=bundle.Selection.Nasa!.Flightline;
            foreach(var source in science.StreamSources(new(Kind:RemoteKind.Hyperspectral,Scene:scene)).Concat(scene==bundle.Selection.Scene?[]:science.StreamSources(new(Kind:RemoteKind.Hyperspectral,Scene:bundle.Selection.Scene))).DistinctBy(s=>s.Id).Where(s=>s.Manifest.Swath?.DecoderPolicy==EmitProductDecoder.Policy&&(s.Manifest.SceneId==scene||s.Manifest.SceneId==bundle.Selection.Scene)))
            {
                if(source.Files.Length!=3||!source.Files.Select(f=>(f.RelativePath,f.Bytes)).OrderBy(f=>f.RelativePath).SequenceEqual(bundle.Files.Select(f=>(f.Name,f.Bytes)).OrderBy(f=>f.Name)))continue;
                var matches=true;foreach(var file in bundle.Files){var path=ScientificRaster.SafeFile(Path.Combine(science.DirectoryPath,source.RelativeDirectory),file.Name);await using var input=File.OpenRead(path);if(Convert.ToHexStringLower(await SHA512.HashDataAsync(input,token))!=file.Sha512){matches=false;break;}}
                if(matches){CheckBundle(await OpenSource(source,token),bundle);return source;}
            }
            return null;
        }finally{work.Release();}
    }
    public async Task<ScientificSource> ImportNasa(string folder,NasaDownloadBundle bundle,CancellationToken token)
    {RequireIdle();await work.WaitAsync(token);try{
        foreach(var file in bundle.Files.Where(f=>f.Sha512 is not null)){await using var input=File.OpenRead(ScientificRaster.SafeFile(folder,file.Name));if(file.Sha512!.Length!=128||Convert.ToHexStringLower(await SHA512.HashDataAsync(input,token))!=file.Sha512)throw new EmitProductException("Integrity","Downloaded original failed NASA CMR SHA-512 verification; no source was published.");}
        return await Publish(await OpenFolder(folder,token),folder,bundle,token);
    }finally{work.Release();}}
    // Compare parsed scientific metadata, not process-dependent dictionary order in
    // an opaque JSON string. The global Library hash algorithm remains unchanged.
    private static string SwathIdentity(ScientificSwathSource swath)=>ScientificCatalog.Hash(new{swath.Version,swath.Format,swath.DecoderPolicy,swath.Domain,definition=JsonSerializer.Deserialize<JsonElement>(swath.DefinitionJson,WireJson.Options)});
    private async Task<EmitProduct> OpenSource(ScientificSource source,CancellationToken token)
    {
        if(source.Manifest.Swath is not{Version:1,Format:"emit-netcdf",DecoderPolicy:EmitProductDecoder.Policy})throw new InvalidOperationException("This source has no supported versioned EMIT decoder.");
        var originals=new List<HdfOriginal>();
        foreach(var file in source.Files)originals.Add(new(science.DirectoryPath,science.LibraryId,source.RelativeDirectory+"/"+file.RelativePath,file.Bytes,file.Sha256));
        var product=await decoder.OpenAsync(EmitProductDecoder.ResolveOriginals(originals),token);
        if(SwathIdentity(Manifest(product).Swath!)!=SwathIdentity(source.Manifest.Swath))throw new IOException("Retained EMIT metadata differs from verified originals; no decoded result was returned.");
        return product;
    }
    private async Task<T> Read<T>(Guid id,Func<EmitProduct,CancellationToken,Task<T>> action,CancellationToken token)
    {
        RequireIdle();if(!await work.WaitAsync(0,token))throw new InvalidOperationException("An EMIT read/import is active. Retry after it finishes.");
        try{RequireIdle();using var lease=science.BeginAnalysis();var source=science.Source(id)??throw new ArgumentException("EMIT source was deleted or is unavailable; no hidden original is reused.");return await action(await OpenSource(source,token),token);}finally{work.Release();}
    }
    public Task<object> Export(Guid id,EmitSampleRequest q,CancellationToken token)=>Read<object>(id,async(p,ct)=>new{formatVersion=1,kind="Decoded historical EMIT sample; not an original NASA file or aircraft observation",definition=p.Definition,result=await decoder.ReadNativeAsync(p,new(q.Row,q.Column,1,1,q.FirstBand,q.BandCount),new(q.ExcludeWater,q.ExcludeInterpolatedBands),ct)},token);
    public Task<EmitDefinition> Details(Guid id,CancellationToken token)=>Read(id,(p,_)=>Task.FromResult(p.Definition),token);
    public Task<EmitNativeResult> Spectrum(Guid id,EmitSampleRequest q,CancellationToken token)=>Read(id,(p,ct)=>decoder.ReadNativeAsync(p,new(q.Row,q.Column,1,1,q.FirstBand,q.BandCount),new(q.ExcludeWater,q.ExcludeInterpolatedBands),ct),token);
    public Task<EmitGeographicResult> Map(Guid id,EmitMapRequest q,CancellationToken token)=>Read(id,(p,ct)=>decoder.ReadGeographicAsync(p,new(q.Row,q.Column,q.Rows,q.Columns,q.Band,1),new(q.ExcludeWater,q.ExcludeInterpolatedBands),ct),token);
    public Task<object> Compare(Guid id,EmitComparisonRequest q,CancellationToken token)=>Read<object>(id,async(p,ct)=>
    {
        if(q.First.FirstBand!=q.Second.FirstBand||q.First.BandCount!=q.Second.BandCount||q.First.ExcludeWater!=q.Second.ExcludeWater||q.First.ExcludeInterpolatedBands!=q.Second.ExcludeInterpolatedBands)throw new ArgumentException("Compare the same channels and QA policy within one verified scene.");
        async Task<EmitNativeResult> Sample(EmitSampleRequest s)=>await decoder.ReadNativeAsync(p,new(s.Row,s.Column,1,1,s.FirstBand,s.BandCount),new(s.ExcludeWater,s.ExcludeInterpolatedBands),ct);
        var first=await Sample(q.First);var second=await Sample(q.Second);
        return new{first,second,wavelengthNm=p.Definition.WavelengthNm.Skip(q.First.FirstBand).Take(q.First.BandCount),difference=first.Pixels[0].UsableReflectance.Zip(second.Pixels[0].UsableReflectance,(a,b)=>a.HasValue&&b.HasValue?a-b:null),method="First minus second; QA-usable reflectance only, same historical scene/channels/policy. No cross-sensor equivalence or flight credit."};
    },token);
    public void Dispose(){cancellation?.Cancel();}
}
