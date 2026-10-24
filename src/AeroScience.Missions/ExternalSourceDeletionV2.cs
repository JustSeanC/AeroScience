using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed record SourceDeletionPage(SourceDeletionItem[] Items,long? Next);
internal sealed record SourceDeletionJournalV2(int Version,Guid PreviewId,Guid LibraryId,string SelectionHash,long? FreeBefore,string State,string? Warning=null);
public sealed partial class ScienceStore
{
    private const string DeletionPreserved="Flight recordings, independently saved mission/science observations, grades with retained evidence, reports, calibration history, settings, unrelated files and user originals outside managed storage remain untouched. Selected originals and their managed copies are permanently removed; active operations remain protected.";
    private SourceDeletionPreview ReadDeletionPreview(Guid id)
    {
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT CASE WHEN length(payload_json)<=65536 THEN payload_json END FROM source_delete_previews WHERE id=$id";command.Parameters.AddWithValue("$id",id.ToString());
        var json=command.ExecuteScalar() as string??throw new InvalidOperationException("Deletion review is unavailable or expired. Review the exact selection again.");
        var preview=ReadCatalog<SourceDeletionPreview>(json);
        if(preview.Version!=2||preview.Id!=id||preview.LibraryId!=LibraryId)throw new IOException("Deletion preview ownership/version is invalid; no source was changed.");
        return preview;
    }
    private IEnumerable<SourceDeletionItem> DeletionItems(Guid id)
    {
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT CASE WHEN length(payload_json)<=16777216 THEN payload_json END FROM source_delete_items WHERE preview_id=$id ORDER BY ordinal";command.Parameters.AddWithValue("$id",id.ToString());
        using var rows=command.ExecuteReader();while(rows.Read())
        {
            if(rows.IsDBNull(0))throw new IOException("Deletion source metadata exceeds its per-collection verification bound.");
            var item=ReadCatalog<SourceDeletionItem>(rows.GetString(0));
            if(item.RelativeDirectory!=$"Sources/{item.Id:N}"||item.Files.Length>10001||(item.AdditionalFiles??[]).Any(f=>!(f.RelativePath.StartsWith("Downloads/",StringComparison.Ordinal)||f.RelativePath.StartsWith("Products/",StringComparison.Ordinal)||f.RelativePath.StartsWith("ManagedCopies/",StringComparison.Ordinal)))||item.Files.Any(f=>f.Bytes<0||f.Sha256.Length!=64||f.Sha256.Any(c=>!char.IsAsciiHexDigit(c))))throw new IOException("Deletion source ownership is invalid; original files preserved.");
            yield return item;
        }
    }
    public SourceDeletionPage DeletionPage(Guid id,long after=0)
    {
        _=ReadDeletionPreview(id);if(after<0)throw new ArgumentException("Use a nonnegative deletion review cursor.");
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT ordinal,CASE WHEN length(payload_json)<=16777216 THEN payload_json END FROM source_delete_items WHERE preview_id=$id AND ordinal>=$after ORDER BY ordinal LIMIT 21";command.Parameters.AddWithValue("$id",id.ToString());command.Parameters.AddWithValue("$after",after);
        var items=new List<SourceDeletionItem>();long? next=null;using var rows=command.ExecuteReader();while(rows.Read())
        {if(items.Count==20){next=rows.GetInt64(0);break;}if(rows.IsDBNull(1))throw new IOException("Deletion metadata exceeds its verified per-collection bound.");var item=ReadCatalog<SourceDeletionItem>(rows.GetString(1));items.Add(item with{Files=[],AdditionalFiles=[],FileCount=item.ManagedFileCount});}
        return new(items.ToArray(),next);
    }
    private static string DeletionHashV2(Guid library,bool all,string? collection,IEnumerable<SourceDeletionItem> items)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append<T>(T value){var data=JsonSerializer.SerializeToUtf8Bytes(value,WireJson.Options);Span<byte> count=stackalloc byte[4];BinaryPrimitives.WriteInt32LittleEndian(count,data.Length);hash.AppendData(count);hash.AppendData(data);}
        Append(new{version=2,library,all,collection});foreach(var item in items)Append(new{item.Id,item.ManifestSha256,item.Files,item.AdditionalFiles,item.Blockers});return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private SourceDeletionItem ReviewDeletionSource(ScientificSource source,MissionStore missions,string? global,CancellationToken token,Func<ScientificSource,string[]>? planning,Func<Guid,bool>? selected=null)
    {
        var blockers=new List<string>();ManagedSourceFile[] files=[],copies=[];if(global is not null)blockers.Add(global);
        try{if(planning is not null)blockers.AddRange(planning(source));blockers.AddRange(missions.ExternalSourceDependencies(source.Id).Select(reason=>"Mission evidence: "+reason));blockers.AddRange(ScientificSourceDependencies(source));}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or SqliteException or JsonException){blockers.Add("Dependency verification unavailable; deletion is blocked: "+error.Message);}
        if(blockers.Count==0)try{files=InspectOwnedSource(source,token);copies=InspectSourceCopies(source,token,selected??(id=>id==source.Id));CheckSourceBackupSafety(source.Id);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException){blockers.Add(error.Message);}
        return new(source.Id,source.Manifest.Name,source.Manifest.Provider,source.Manifest.Kind,source.RelativeDirectory,source.ManifestSha256,files,files.Sum(f=>f.Bytes)+copies.Sum(f=>f.Bytes),blockers.ToArray(),AdditionalFiles:copies);
    }
    public SourceDeletionPreview PreviewSourceDeletion(SourceDeletionRequest request,MissionStore missions,Func<string?>? busy=null,CancellationToken token=default,Func<ScientificSource,string[]>? planningDependencies=null)
    {
        var modes=(request.All?1:0)+(request.CollectionId is not null?1:0)+(request.SourceIds is{Length:>0}?1:0);
        if(modes!=1||request.SourceIds is {} ids&&ids.Distinct().Count()!=ids.Length||request.CollectionId is {} key&&(key.Length!=64||key.Any(c=>!char.IsAsciiHexDigit(c))))throw new ArgumentException("Review one exact source set, one collection, or all retained external sources.");
        EnsureSourceIndex();using var analysis=BeginAnalysis();var id=Guid.NewGuid();var library=LibraryId;var now=DateTimeOffset.UtcNow;
        var preview=new SourceDeletionPreview(id,library,request.All,now,"",[],DeletionPreserved,Version:2,CollectionId:request.CollectionId);
        using(var db=Open(true))using(var command=db.CreateCommand())
        {
            command.CommandText="INSERT INTO source_delete_previews VALUES($id,$json,'Building',$utc)";command.Parameters.AddWithValue("$id",id.ToString());command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(preview,WireJson.Options));command.Parameters.AddWithValue("$utc",now.ToString("O"));command.ExecuteNonQuery();
        }
        var global=busy?.Invoke();lock(gate)if(sourceMaintenance||activeAnalyses>1)global="Scientific acquisition/import/analysis or source maintenance is active. Finish or cancel it before deletion.";
        IEnumerable<ScientificSource> selected=request.SourceIds is{Length:>0}?request.SourceIds.Order().Select(source=>Source(source)??throw new InvalidOperationException("Selected source changed. Refresh and review again.")):StreamSources(new(Collection:request.CollectionId));
        long total=0,blocked=0,bytes=0;
        foreach(var source in selected)
        {
            token.ThrowIfCancellationRequested();var item=ReviewDeletionSource(source,missions,global,token,planningDependencies,id=>request.All||request.SourceIds?.Contains(id)==true||request.CollectionId is {} collection&&SourceInCollection(id,collection));var json=JsonSerializer.Serialize(item,WireJson.Options);
            if(disk.FreeBytes(DirectoryPath)<System.Text.Encoding.UTF8.GetByteCount(json)*2L+65536)throw new StorageLimitException("Insufficient physical space for the bounded deletion journal. Free temporary working space, then review again; no source was deleted.");
            using var db=Open(true);using var command=db.CreateCommand();command.CommandText="INSERT INTO source_delete_items VALUES($preview,$ordinal,$source,$json)";command.Parameters.AddWithValue("$preview",id.ToString());command.Parameters.AddWithValue("$ordinal",total);command.Parameters.AddWithValue("$source",source.Id.ToString());command.Parameters.AddWithValue("$json",json);command.ExecuteNonQuery();
            total++;if(item.Blockers.Length>0)blocked++;else bytes=checked(bytes+item.ManagedBytes);
        }
        if(total==0)throw new InvalidOperationException("There are no retained sources in this exact selection.");
        if(library!=LibraryId)throw new IOException("Library changed during source review; nothing was authorized.");
        var hash=DeletionHashV2(library,preview.All,preview.CollectionId,DeletionItems(id));preview=preview with{SelectionHash=hash,TotalSources=total,BlockedSources=blocked,ManagedBytes=bytes};
        SaveDeletionPreview(preview,"Previewed");var page=DeletionPage(id);return preview with{Sources=page.Items,Next=page.Next};
    }
    private void SaveDeletionPreview(SourceDeletionPreview preview,string state)
    {
        using var db=Open(true);using var command=db.CreateCommand();command.CommandText="UPDATE source_delete_previews SET payload_json=$json,state=$state WHERE id=$id";command.Parameters.AddWithValue("$id",preview.Id.ToString());command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(preview with{Sources=[],Next=null},WireJson.Options));command.Parameters.AddWithValue("$state",state);command.ExecuteNonQuery();
    }
    public SourceDeletionResult DeleteExternalSources(SourceDeletionExecution request,MissionStore missions,Func<IDisposable> missionMaintenance,Func<string?>? busy=null,CancellationToken token=default,Func<ScientificSource,string[]>? planningDependencies=null)
    {
        var path=Path.Combine(SourceDeletionFolder(request.PreviewId),"journal.json");
        if(File.Exists(path))
        {
            using var document=JsonDocument.Parse(File.ReadAllText(path));
            if((document.RootElement.TryGetProperty("version",out var version)||document.RootElement.TryGetProperty("Version",out version))&&version.GetInt32()==1)return DeleteExternalSourcesLegacy(request,missions,missionMaintenance,busy,token,planningDependencies);
            var prior=ReadDeletionPreview(request.PreviewId);AuthorizeDeletion(prior,request);using var held=missionMaintenance();using var maintenance=EnterSourceMaintenance();return RecoverSourceDeletionV2(path);
        }
        var preview=ReadDeletionPreview(request.PreviewId);AuthorizeDeletion(preview,request);
        if(preview.CreatedUtc<DateTimeOffset.UtcNow.AddMinutes(-10))throw new InvalidOperationException("Deletion review expired. Review the exact selection again.");
        if(preview.BlockedSources>0)throw new InvalidOperationException("This selection contains protected/unavailable sources. Nothing was deleted; review a new explicit selection.");
        using var missionLock=missionMaintenance();
        var global=busy?.Invoke();lock(gate)if(activeAnalyses>0||sourceMaintenance)global="Scientific acquisition/import/analysis or source maintenance is active.";
        var current=DeletionItems(preview.Id).Select(item=>
        {
            var reviewed=ReviewDeletionSource(Source(item.Id)??throw new InvalidOperationException("Selected source is no longer retained."),missions,global,token,planningDependencies,id=>DeletionContains(preview.Id,id));
            if(reviewed.Blockers.Length>0)throw new InvalidOperationException("Selected source is protected or cannot be safely verified. Nothing was removed: "+string.Join("; ",reviewed.Blockers));
            return reviewed;
        });
        if(DeletionHashV2(preview.LibraryId,preview.All,preview.CollectionId,current)!=preview.SelectionHash)throw new InvalidOperationException("Original files, dependencies or selection changed. Review again; no reduced batch was deleted.");
        using var sourceLock=EnterSourceMaintenance();
        if((preview.All||preview.CollectionId is not null)&&!StreamSources(new(Collection:preview.CollectionId)).Select(source=>source.Id).SequenceEqual(DeletionItems(preview.Id).Select(item=>item.Id)))throw new InvalidOperationException("Retained source inventory changed. Review the full selection again.");
        foreach(var item in DeletionItems(preview.Id))if(missions.ExternalSourceDependencies(item.Id).Length>0||ScientificSourceDependencies(Source(item.Id)??throw new IOException("Source identity changed.")).Length>0||planningDependencies?.Invoke(Source(item.Id)!).Length>0)throw new InvalidOperationException("A new research dependency blocks deletion. Nothing was removed.");
        var directory=SourceDeletionFolder(preview.Id);Directory.CreateDirectory(directory);
        long? free=null;try{free=disk.FreeBytes(DirectoryPath);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        var journal=new SourceDeletionJournalV2(2,preview.Id,preview.LibraryId,preview.SelectionHash,free,"Prepared");PreferenceStore.WriteAtomic(path,journal);SaveDeletionPreview(preview,"Prepared");SourceDeletionFault?.Invoke("prepared");
        try
        {
            using(var db=Open(true))using(var transaction=db.BeginTransaction())using(var command=db.CreateCommand())
            {
                command.Transaction=transaction;command.CommandText="DELETE FROM remote_sources WHERE id=$id AND manifest_sha=$sha";command.Parameters.Add("$id",SqliteType.Text);command.Parameters.Add("$sha",SqliteType.Text);
                foreach(var item in DeletionItems(preview.Id)){token.ThrowIfCancellationRequested();command.Parameters["$id"].Value=item.Id.ToString();command.Parameters["$sha"].Value=item.ManifestSha256;if(command.ExecuteNonQuery()!=1)throw new IOException("Source identity changed before atomic deletion commit.");}
                command.Parameters.Clear();command.CommandText="UPDATE library_job_items SET state='Source deleted',verified=0,error='Verified source permanently removed by the user; a new approved job is required to acquire it again.' WHERE source_id=$id";command.Parameters.Add("$id",SqliteType.Text);
                foreach(var item in DeletionItems(preview.Id)){command.Parameters["$id"].Value=item.Id.ToString();command.ExecuteNonQuery();}
                command.Parameters.Clear();command.CommandText="DELETE FROM scientific_artifacts WHERE json_extract(payload_json,'$.kind')='retained-source' AND json_extract(payload_json,'$.relativePath')=$path";command.Parameters.Add("$path",SqliteType.Text);
                foreach(var file in DeletionItems(preview.Id).SelectMany(item=>item.AdditionalFiles??[]).Where(f=>f.RelativePath.StartsWith("Products/",StringComparison.Ordinal)||f.RelativePath.StartsWith("ManagedCopies/",StringComparison.Ordinal))){command.Parameters["$path"].Value=file.RelativePath;command.ExecuteNonQuery();}
                command.Parameters.Clear();command.CommandText="INSERT INTO metadata VALUES($key,$hash)";command.Parameters.AddWithValue("$key","source_delete:"+preview.Id.ToString("N"));command.Parameters.AddWithValue("$hash",preview.SelectionHash);command.ExecuteNonQuery();SourceDeletionFault?.Invoke("before-commit");transaction.Commit();
            }
            SourceDeletionFault?.Invoke("committed");SourceDeletionFault?.Invoke("quarantined");return RecoverSourceDeletionV2(path);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or SqliteException or OperationCanceledException)
        {
            var result=RecoverSourceDeletionV2(path);if(result.State=="Rolled back")throw new IOException("Deletion stopped before commitment. Original data and catalog were preserved. "+error.Message,error);return result;
        }
    }
    private SourceDeletionResult RecoverSourceDeletionV2(string path)
    {
        StorageManager.ValidateLocalPath(path);if(new FileInfo(path).Length>65536)throw new IOException("Invalid source deletion journal; preserve recovery evidence.");
        var journal=ReadCatalog<SourceDeletionJournalV2>(File.ReadAllText(path));var preview=ReadDeletionPreview(journal.PreviewId);
        if(journal.Version!=2||journal.LibraryId!=LibraryId||path!=Path.Combine(SourceDeletionFolder(preview.Id),"journal.json")||journal.SelectionHash!=preview.SelectionHash||preview.BlockedSources!=0||journal.State!="Completed"&&DeletionHashV2(preview.LibraryId,preview.All,preview.CollectionId,DeletionItems(preview.Id))!=preview.SelectionHash)throw new IOException("Deletion recovery ownership/hash is invalid; no files were changed.");
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT value FROM metadata WHERE key=$key";command.Parameters.AddWithValue("$key","source_delete:"+preview.Id.ToString("N"));var receipt=command.ExecuteScalar() as string;
        if(receipt is not null&&receipt!=preview.SelectionHash)throw new IOException("Deletion receipt identity differs; preserve original data.");
        var committed=receipt is not null;
        if(journal.State=="Completed"&&committed)return new(preview.Id,preview.TotalSources,preview.ManagedBytes,0,null,"Completed",null);
        long pending=0,removed=0;bool metadataPending=false;string? warning=null;
        if(committed)try{PurgeSourceBackups(DeletionItems(preview.Id).Select(item=>item.Id));}catch(Exception error)when(error is IOException or UnauthorizedAccessException or SqliteException){metadataPending=true;warning="Managed backup cleanup incomplete: "+error.Message;}
        foreach(var item in DeletionItems(preview.Id))
        {
            if(!committed)continue; // No scientific file has been removed before the atomic receipt.
            var files=item.Files.Select(f=>f with{RelativePath=item.RelativeDirectory+"/"+f.RelativePath}).Concat(item.AdditionalFiles??[]);
            foreach(var file in files)
            {
                var target=ResolveCollectedPath(file.RelativePath);StorageManager.ValidateLocalPath(target);
                try{if(File.Exists(target)){if(new FileInfo(target).Length!=file.Bytes||Digest(target)!=file.Sha256)throw new IOException("A selected managed file changed; deletion needs review and is not complete.");SourceDeletionFault?.Invoke("before-file-remove");File.Delete(target);}removed=checked(removed+file.Bytes);}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException){pending=checked(pending+file.Bytes);warning=error.Message;}
            }
        }
        if(committed&&pending==0)try{CheckpointDeletedContent();}catch(Exception error)when(error is IOException or SqliteException){metadataPending=true;warning=error.Message;}
        var state=committed?(pending==0&&!metadataPending?"Completed":"Cleanup pending"):"Rolled back";
        PreferenceStore.WriteAtomic(path,journal with{State=state,Warning=warning});SaveDeletionPreview(preview,state);
        if(pending==0)
        {
            command.Parameters.Clear();command.CommandText="SELECT name FROM source_delete_ledgers WHERE preview_id=$id";command.Parameters.AddWithValue("$id",preview.Id.ToString());using var rows=command.ExecuteReader();while(rows.Read())
            {var name=rows.GetString(0);if(name!=Path.GetFileName(name)||!Guid.TryParseExact(Path.GetFileNameWithoutExtension(name),"N",out _))throw new IOException("Invalid owned deletion ledger name.");var ledger=StorageManager.ResolveDataset(DirectoryPath,LibraryId,"Metadata/Work/"+name);if(File.Exists(ledger))File.Delete(ledger);}
        }
        long? change=null;try{if(journal.FreeBefore is {} before)change=disk.FreeBytes(DirectoryPath)-before;}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        if(state=="Completed"){
            // Keep source IDs for invalidation, not source file payloads or recoverable copies.
            using var compact=Open(true);using var clean=compact.CreateCommand();clean.CommandText="UPDATE source_delete_items SET payload_json=json_set(payload_json,'$.files',json('[]'),'$.additionalFiles',json('[]')) WHERE preview_id=$id";clean.Parameters.AddWithValue("$id",preview.Id.ToString());clean.ExecuteNonQuery();
        }
        SourceDeletionWarning=warning;return new(preview.Id,committed?preview.TotalSources:0,removed,pending,change,state,warning,MetadataCleanupPending:metadataPending);
    }
    private bool DeletionContains(Guid preview,Guid source)
    {using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT EXISTS(SELECT 1 FROM source_delete_items WHERE preview_id=$preview AND source_id=$source)";command.Parameters.AddWithValue("$preview",preview.ToString());command.Parameters.AddWithValue("$source",source.ToString());return Convert.ToInt64(command.ExecuteScalar())!=0;}
    public bool SourceRemovedBy(Guid deletionId,Guid sourceId)
    {
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT EXISTS(SELECT 1 FROM source_delete_items i JOIN metadata m ON m.key=$key WHERE i.preview_id=$id AND i.source_id=$source)";command.Parameters.AddWithValue("$id",deletionId.ToString());command.Parameters.AddWithValue("$source",sourceId.ToString());command.Parameters.AddWithValue("$key","source_delete:"+deletionId.ToString("N"));return (long)command.ExecuteScalar()!!=0;
    }
}
