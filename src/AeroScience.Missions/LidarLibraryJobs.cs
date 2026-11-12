using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed partial class ScientificLibraryJobs
{
    private ScientificSource? RetainedLidar(LidarEptProject source,string node)=>science.StreamSources(new(Scene:source.Project+"/"+node)).FirstOrDefault(s=>s.Manifest.PointCloud is {} c&&c.EptProject==source.Project&&c.EptMetadataSha256==source.MetadataSha256&&c.Originals.Length==1&&c.Originals[0].EptNode==node);
    public async Task<LibraryDownloadJob> PreviewLidar(LidarDownloadRequest request,CancellationToken token=default)
    {
        EnsureRecovered();using var lease=science.BeginAnalysis();var p=settings.Get().Preferences;
        if(request.MaximumTransferBytes>p.MaximumDownloadBytes||request.MaximumTransferBytes>p.PreflightTransferBytes)throw new ArgumentException("LiDAR job cap must fit the existing per-download and preparation transfer policies. No policy is raised automatically.");
        var bundle=await new LidarCatalog(automatic,science,settings).Plan(request,token);var work=new List<LibraryWorkUnit>();long reused=0;
        foreach(var node in bundle.Nodes)
        {
            var original=RetainedLidar(bundle.Source,node.Key);if(original is not null){if(!Verify(original))throw new IOException("Retained EPT node changed. Restore/reimport it; no replacement download is authorized.");reused+=original.Files.Sum(f=>f.Bytes);}
            work.Add(new(work.Count,new("usgs-ept",bundle.Source.Project+"/"+node.Key,bundle.Source.MetadataSha256,["historical-lidar"]),bundle.Area,original?.Id,request.MaximumTransferBytes*2+2097152));
        }
        var now=DateTimeOffset.UtcNow;var selection=new ScientificLibrarySelection(1,[request.Area],[new("usgs-ept",bundle.Source.Project,bundle.Source.MetadataSha256,["historical-lidar"])],MaximumTransferBytes:request.MaximumTransferBytes);
        var hash=ScientificCatalog.Hash(new{bundle,selection});
        var item=new ScientificCatalogItem("usgs-ept","Historical LiDAR points",bundle.Source.Project,bundle.Source.MetadataSha256,RemoteKind.Terrain,bundle.Source.Bounds,null,null,null,[],bundle.Source.HorizontalCrs,bundle.Source.VerticalReference,"Original class/return attributes retained; not QA-certified","Original point records; density unknown","US Government Public Domain","https://registry.opendata.aws/usgs-lidar/","Explicit public EPT LAZ nodes",bundle.Limitations);
        var storage=science.Management?.Snapshot();var reserve=request.MaximumTransferBytes*2+2097152;
        return science.SaveLibraryPreview(new(1,Guid.NewGuid(),selection,hash,SettingsHash(),now,now.AddMinutes(15),work.ToArray(),[item],request.MaximumTransferBytes,request.MaximumTransferBytes,reserve,reused,work.All(w=>w.ReuseSourceId is not null)?0:null,null,bundle.Limitations,
            AvailableDriveBytes:storage?.Science.FreeBytes,ScientificStorageLimitBytes:storage?.Science.LimitBytes,ScientificStorageUsedBytes:storage?.Science.UsedBytes,PreparationBudgetRemainingBytes:automatic.QueueSummary().PreflightAvailableBytes,Lidar:bundle));
    }
    private async Task ExecuteLidar(LibraryDownloadJob job,LibraryWorkUnit work,LibraryDownloadItem previous,LidarDownloadBundle bundle,CancellationToken token)
    {
        ScientificDownloadBudget? budget=null;ScientificTransferLedger.Lease? transfer=null;var initial=previous.DownloadedBytes;
        progress.TryGetValue(job.Id,out var measured);
        try
        {
            using var operation=science.BeginAnalysis();var current=science.LibraryJob(job.Id);
            if(current.State!="Queued"||current.ApprovalId is null)return;
            if(current.Preview.SettingsHash!=SettingsHash()||bundle.Revision!=ScientificCatalog.Hash(new{bundle.Source.MetadataSha256,bundle.Area,nodes=bundle.Nodes,hierarchy=bundle.HierarchyJson}))throw new InvalidOperationException("The reviewed LiDAR settings/recipe changed. Preview and approve again.");
            var node=bundle.Nodes[work.Index];if(node.Key!=work.Scene.Scene[(bundle.Source.Project.Length+1)..])throw new IOException("Approved EPT work identity differs.");
            science.SetLibraryState(job.Id,"Preparing",runningOnly:true);science.SetLibraryItem(job.Id,work.Index,"Preparing");
            var source=work.ReuseSourceId is {} id?science.Source(id):RetainedLidar(bundle.Source,node.Key);
            if(work.ReuseSourceId is not null&&source is null)throw new IOException("Reviewed retained EPT node is unavailable. No replacement transfer was authorized.");
            if(source is not null){if(!Verify(source)||source.Manifest.PointCloud?.EptMetadataSha256!=bundle.Source.MetadataSha256)throw new IOException("Retained EPT node integrity/revision differs.");}
            else
            {
                var p=settings.Get().Preferences;if(!p.OnlineEnabled||!p.PrivacyAccepted||!p.BackgroundRetrieval||p.UnmeteredOnly&&settings.IsUnmetered()!=true)throw new InvalidOperationException("Existing privacy, background or metered-network policy blocks LiDAR retrieval.");
                transfer=automatic.AcquireLibraryTransfer();budget=new(job.Preview.PerItemTransferLimit,bytes=>{measured?.Network(bytes);transfer.Charge(bytes);science.LibraryBudget(job.Id,work.Index,initial+bytes,initial+bytes);},total=>{transfer.EnsureTotal(total);science.LibraryBudget(job.Id,work.Index,initial+(budget?.Bytes??0),initial+total);});
                using var reserve=science.Management?.ReserveScience(work.StorageAllowanceBytes);
                // Fresh source identity check is part of the approved bounded transfer.
                var fresh=await new LidarCatalog(automatic,science,settings).Verify(bundle.Source.Project,budget,token);if(fresh.MetadataSha256!=bundle.Source.MetadataSha256)throw new IOException("EPT metadata changed after approval. No numeric source was substituted.");
                var relative=$"Downloads/Library/{job.Id:N}/{work.Index}";var folder=Path.Combine(science.DirectoryPath,relative);Directory.CreateDirectory(folder);StorageManager.ValidateLocalPath(folder);
                var path=Path.Combine(folder,"original.laz");var part=path+".partial";
                var receiptPath=Path.Combine(folder,"download-receipt.json");
                var ledger=science.BeginManagedWrite("EPT Library "+job.Id,[relative+"/original.laz",relative+"/original.laz.partial",relative+"/source.json",relative+"/download-receipt.json"]);
                try
                {
                    science.SetLibraryState(job.Id,"Downloading",runningOnly:true);science.SetLibraryItem(job.Id,work.Index,"Downloading");measured?.Stage("Downloading");
                    // Retained completed bytes from an interrupted import are reused.
                    // An incomplete file is never accepted as a completed node.
                    if(!File.Exists(path))
                    {
                        if(File.Exists(part))File.Delete(part);var bytes=await automatic.Http.LidarBytes(new(LidarCatalog.Bucket+bundle.Source.Project+"/ept-data/"+node.Key+".laz"),8388608,budget,token);
                        await File.WriteAllBytesAsync(part,bytes,token);using(var sync=new FileStream(part,FileMode.Open,FileAccess.ReadWrite,FileShare.None))sync.Flush(true);File.Move(part,path);
                        PreferenceStore.WriteAtomic(receiptPath,new EptStageReceipt(bundle.Source.Project,bundle.Source.MetadataSha256,node.Key,bytes.LongLength,Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))));
                    }
                    if(!File.Exists(receiptPath)||new FileInfo(receiptPath).Length>65536)throw new IOException("Interrupted EPT bytes lack their verified transfer receipt. Review/remove this owned staging operation explicitly; they were not accepted or downloaded again.");
                    var receipt=JsonSerializer.Deserialize<EptStageReceipt>(File.ReadAllText(receiptPath),WireJson.Options)!;
                    using(var input=File.OpenRead(path))if(receipt.Project!=bundle.Source.Project||receipt.MetadataSha256!=bundle.Source.MetadataSha256||receipt.Node!=node.Key||input.Length!=receipt.Bytes||Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input))!=receipt.Sha256)throw new IOException("Interrupted EPT bytes no longer match their original download receipt.");
                    var h=LidarLas.Inspect(path);if(!h.Compressed||h.PointCount!=node.Points||h.HorizontalCrs!=bundle.Source.HorizontalCrs)throw new IOException("Downloaded EPT original count/CRS/compression differs from its approved hierarchy.");
                    var metadata=new LidarImportMetadata("Historical LiDAR: "+bundle.Source.Project+" / "+node.Key,bundle.Source.Project+"/"+node.Key,"US Government Public Domain","USGS 3DEP public EPT; https://registry.opendata.aws/usgs-lidar/ ; original node "+node.Key);
                    var manifest=LidarImports.Inspect(path,metadata,bundle.Source,node.Key);PreferenceStore.WriteAtomic(Path.Combine(folder,"source.json"),manifest);
                    science.SetLibraryState(job.Id,"Verifying",runningOnly:true);science.SetLibraryItem(job.Id,work.Index,"Verifying");measured?.Stage("Verifying");
                    source=await science.ImportSourceAsync(manifest,folder,token,reuseExisting:true,verifyPointCloud:(root,ct)=>LidarImports.Verify(root,manifest,ct));
                    File.Delete(path);File.Delete(Path.Combine(folder,"source.json"));File.Delete(receiptPath);if(File.Exists(part))File.Delete(part);File.Delete(ledger);if(!Directory.EnumerateFileSystemEntries(folder).Any())Directory.Delete(folder);
                }
                catch{science.Management?.RefreshScience();throw;}
            }
            science.SetLibraryItem(job.Id,work.Index,"Completed",source:source.Id,verifiedBytes:source.Files.Sum(f=>f.Bytes));var done=science.LibraryJob(job.Id);science.SetLibraryState(job.Id,done.Items.All(i=>i.State=="Completed")?"Completed":"Queued",runningOnly:true);
        }
        catch(ScientificAllowanceWaitException e){science.SetLibraryItem(job.Id,work.Index,"Interrupted",e.Message);science.SetLibraryState(job.Id,"Interrupted",e.Message,runningOnly:true);}
        catch(OperationCanceledException){science.SetLibraryItem(job.Id,work.Index,"Interrupted","LiDAR retrieval/import stopped; resume explicitly. Completed retained originals remain separate.");science.SetLibraryState(job.Id,"Interrupted","Resume explicitly; no automatic restart or download authorization.",runningOnly:true);}
        catch(Exception e)when(e is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException){science.SetLibraryItem(job.Id,work.Index,"Failed",e is HttpRequestException?"Public EPT network unavailable.":e.Message);science.SetLibraryState(job.Id,"Failed",e is HttpRequestException?"Public EPT network unavailable.":e.Message,runningOnly:true);}
        finally{transfer?.Dispose();if(budget is not null)science.LibraryBudget(job.Id,work.Index,initial+budget.Bytes,initial+budget.Bytes,finish:true);science.Management?.RefreshScience();}
    }
    private sealed record EptStageReceipt(string Project,string MetadataSha256,string Node,long Bytes,string Sha256);
}
