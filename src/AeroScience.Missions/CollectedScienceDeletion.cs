using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed record CollectedScienceDeletionRequest(Guid[]? MissionIds=null,bool All=false);
public sealed record CollectedScienceDeletionPreview(Guid Id,Guid LibraryId,DateTimeOffset CreatedUtc,string SelectionHash,bool All,long Missions,long Datasets,long Observations,long Products,long ManagedBytes,string[] Blockers,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] long PilotObservations=0);
public sealed record CollectedScienceDeletionResult(Guid Id,string State,long RemovedFiles,long? PendingFiles,string? Warning,bool DatabaseCleanupPending=false);
public sealed partial class ScienceStore
{
    internal Action<string>? CollectedDeletionFault;
    private CollectedScienceDeletionPreview CollectedReview(Guid id)
    {
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT payload_json FROM science_delete_reviews WHERE id=$id";command.Parameters.AddWithValue("$id",id.ToString());
        var review=ReadCatalog<CollectedScienceDeletionPreview>(command.ExecuteScalar() as string??throw new InvalidOperationException("Review collected-science deletion again."));
        if(review.Id!=id||review.LibraryId!=LibraryId)throw new IOException("Collected-science deletion identity changed.");return review;
    }
    private IEnumerable<Guid> CollectedMissionIds(Guid id)
    {
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT mission_id FROM science_delete_missions WHERE review_id=$id ORDER BY mission_id";command.Parameters.AddWithValue("$id",id.ToString());
        using var rows=command.ExecuteReader();while(rows.Read())yield return Guid.Parse(rows.GetString(0));
    }
    private IEnumerable<ManagedSourceFile> CollectedFiles(Guid id)
    {
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT path,bytes,sha FROM science_delete_files WHERE review_id=$id ORDER BY path";command.Parameters.AddWithValue("$id",id.ToString());
        using var rows=command.ExecuteReader();while(rows.Read())yield return new(rows.GetString(0),rows.GetInt64(1),rows.GetString(2));
    }
    private string CollectedHash(Guid id,MissionStore missions)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value){var bytes=Encoding.UTF8.GetBytes(value);hash.AppendData(BitConverter.GetBytes(bytes.Length));hash.AppendData(bytes);}
        Append(LibraryId.ToString());foreach(var mission in CollectedMissionIds(id))
        {
            Append(mission.ToString());Append(missions.ScienceDeletionFingerprint(mission));
            using var db=Open();using var command=db.CreateCommand();command.Parameters.AddWithValue("$id",mission.ToString());
            foreach(var table in new[]{"datasets","observations","remote_products","scientific_artifacts"})
            {command.CommandText=$"SELECT payload_json FROM {table} WHERE mission_id=$id ORDER BY id";using var rows=command.ExecuteReader();while(rows.Read())Append(rows.GetString(0));}
        }
        foreach(var file in CollectedFiles(id)){var path=ResolveCollectedPath(file.RelativePath);StorageManager.ValidateLocalPath(path);if(!File.Exists(path)||new FileInfo(path).Length!=file.Bytes||Digest(path)!=file.Sha256)throw new IOException("Selected scientific product changed; review again.");Append(JsonSerializer.Serialize(file,WireJson.Options));}
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public CollectedScienceDeletionPreview PreviewCollectedDeletion(CollectedScienceDeletionRequest request,MissionStore missions)
    {
        if(request.All==(request.MissionIds is{Length:>0})||request.MissionIds?.Length>100||request.MissionIds is {} ids&&ids.Distinct().Count()!=ids.Length)throw new ArgumentException("Choose specific missions (up to 100 per request) or all collected science.");
        using var analysis=BeginAnalysis();var id=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        using(var db=Open(true))using(var command=db.CreateCommand())
        {
            command.CommandText="INSERT INTO science_delete_reviews VALUES($id,'{}','Building')";command.Parameters.AddWithValue("$id",id.ToString());command.ExecuteNonQuery();
            command.CommandText="INSERT INTO science_delete_missions VALUES($id,$mission)";command.Parameters.Add("$mission",SqliteType.Text);
            foreach(var mission in request.All?missions.DeletionMissionIds():request.MissionIds!.Order())
            {if(missions.Detail(mission) is null)throw new ArgumentException("A selected mission no longer exists.");command.Parameters["$mission"].Value=mission.ToString();command.ExecuteNonQuery();}
        }
        var blockers=new List<string>();long missionCount=0,datasets=0,observations=0,products=0,bytes=0,pilotObservations=0;
        void FileItem(string relative,long length,string sha)
        {
            var path=ResolveCollectedPath(relative);StorageManager.ValidateLocalPath(path);
            if(!File.Exists(path)||new FileInfo(path).Length!=length||Digest(path)!=sha)throw new IOException("A selected scientific file is missing or changed: "+relative);
            using var db=Open(true);using var command=db.CreateCommand();command.CommandText="INSERT OR IGNORE INTO science_delete_files VALUES($id,$path,$bytes,$sha)";command.Parameters.AddWithValue("$id",id.ToString());command.Parameters.AddWithValue("$path",relative);command.Parameters.AddWithValue("$bytes",length);command.Parameters.AddWithValue("$sha",sha);if(command.ExecuteNonQuery()>0)bytes=checked(bytes+length);
        }
        foreach(var mission in CollectedMissionIds(id))
        {
            missionCount++;pilotObservations+=missions.PilotObservationCount(mission);if(missions.Detail(mission)?.Mission.Status==MissionStatus.Recording)blockers.Add("End active flight recording before deleting collected science.");
            using(var db=Open())using(var command=db.CreateCommand()){command.CommandText="SELECT (SELECT COUNT(*) FROM datasets WHERE mission_id=$id),(SELECT COUNT(*) FROM observations WHERE mission_id=$id)";command.Parameters.AddWithValue("$id",mission.ToString());using var row=command.ExecuteReader();row.Read();datasets+=row.GetInt64(0);observations+=row.GetInt64(1);}
            foreach(var product in AllProducts(mission)){FileItem(product.RelativePath,product.Bytes,product.Sha256);products++;}
            foreach(var artifact in Artifacts(mission)){FileItem(artifact.RelativePath,artifact.Bytes,artifact.Sha256);products++;}
            foreach(var cache in CacheEntries().Where(c=>c.MissionId==mission))FileItem(cache.RelativePath,cache.Bytes,cache.Sha256);
        }
        var selected=CollectedMissionIds(id).Select(m=>"Products/"+m.ToString("N")+"/").ToArray();
        foreach(var path in PendingArtifacts().Artifacts)
        {
            var relative=Path.GetRelativePath(DirectoryPath,path).Replace('\\','/');if(!selected.Any(prefix=>relative.StartsWith(prefix,StringComparison.Ordinal))||!File.Exists(path))continue;
            FileItem(relative,new FileInfo(path).Length,Digest(path));
        }
        var clears=Path.Combine(DirectoryPath,"Metadata","Clears");
        if(Directory.Exists(clears))foreach(var path in Directory.EnumerateFiles(clears,"*.json")){
            StorageManager.ValidateLocalPath(path);if(new FileInfo(path).Length>33554432)throw new IOException("An old clear journal is too large to verify safely; permanent deletion is blocked.");
            using var json=JsonDocument.Parse(File.ReadAllText(path));var references=new HashSet<Guid>();
            foreach(var key in new[]{"products","artifacts"})if(json.RootElement.TryGetProperty(key,out var items)&&items.ValueKind==JsonValueKind.Array)foreach(var item in items.EnumerateArray())if(item.TryGetProperty("missionId",out var identity)&&identity.TryGetGuid(out var mission))references.Add(mission);
            bool chosen(Guid mission)=>CollectedMissionIds(id).Contains(mission);
            if(references.Any(chosen)){
                if(references.Any(m=>!chosen(m)))blockers.Add("A historical clear journal contains selected and unrelated analysis metadata. Select all affected missions to purge that shared copy explicitly.");
                else FileItem(Path.GetRelativePath(DirectoryPath,path).Replace('\\','/'),new FileInfo(path).Length,Digest(path));
            }
        }
        foreach(var copy in RegisteredCopies().Where(c=>c.Kind=="recovery"))
        {
            var database=RegisteredCopies().FirstOrDefault(c=>c.Kind=="database"&&Path.GetDirectoryName(c.Path)==copy.Path)??throw new IOException("Managed recovery database is unregistered; review this shared copy before deletion.");
            using var db=OpenDeletionBackup(database.Path,false);using var command=db.CreateCommand();command.Parameters.Add("$id",SqliteType.Text);
            foreach(var mission in CollectedMissionIds(id))
            {
                command.Parameters["$id"].Value=mission.ToString();
                foreach(var table in new[]{"remote_products","scientific_artifacts"}){
                    command.CommandText=$"SELECT payload_json FROM {table} WHERE mission_id=$id ORDER BY id";using var rows=command.ExecuteReader();while(rows.Read()){
                        using var payload=JsonDocument.Parse(rows.GetString(0));var item=payload.RootElement;var relative=item.GetProperty("relativePath").GetString()!;
                        if(!relative.StartsWith($"Products/{mission:N}/",StringComparison.Ordinal))throw new IOException("Shared recovery file ownership is unsupported; purge cannot be claimed complete.");
                        FileItem($"ManagedCopies/{copy.Id:N}/{relative}",item.GetProperty("bytes").GetInt64(),item.GetProperty("sha256").GetString()!);
                    }
                }
            }
        }
        foreach(var root in ManagedReplicaRoots?.Invoke()??[])blockers.Add("A retained relocation copy needs an explicitly reconciled science purge before deletion: "+root);
        CheckSourceBackupSafety(Guid.Empty);
        var review=new CollectedScienceDeletionPreview(id,LibraryId,now,CollectedHash(id,missions),request.All,missionCount,datasets,observations,products,bytes,blockers.Distinct().ToArray(),pilotObservations);
        using(var db=Open(true))using(var command=db.CreateCommand()){command.CommandText="UPDATE science_delete_reviews SET payload_json=$json,state='Previewed' WHERE id=$id";command.Parameters.AddWithValue("$id",id.ToString());command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(review,WireJson.Options));command.ExecuteNonQuery();}return review;
    }
    public CollectedScienceDeletionResult DeleteCollectedScience(SourceDeletionExecution request,MissionStore missions,Func<IDisposable> missionMaintenance)
    {
        var review=CollectedReview(request.PreviewId);if(review.SelectionHash!=request.SelectionHash)throw new InvalidOperationException("Approve the exact collected-science selection.");
        using var held=missionMaintenance();using var maintenance=EnterSourceMaintenance();
        using(var db=Open(true))using(var command=db.CreateCommand())
        {
            command.CommandText="SELECT state FROM science_delete_reviews WHERE id=$id";command.Parameters.AddWithValue("$id",review.Id.ToString());var state=command.ExecuteScalar() as string;
            if(state=="Completed")return new(review.Id,"Completed",0,0,null);
            if(state=="Previewed")
            {
                if(review.CreatedUtc<DateTimeOffset.UtcNow.AddMinutes(-10)||review.Blockers.Length>0||CollectedHash(review.Id,missions)!=review.SelectionHash)throw new InvalidOperationException("Collected-science review expired, is blocked, or changed. Nothing was deleted.");
                command.CommandText="UPDATE science_delete_reviews SET state='Approved' WHERE id=$id";command.ExecuteNonQuery();
            }
        }
        CollectedDeletionFault?.Invoke("approved");return CompleteCollectedDeletion(review,missions);
    }
    public void RecoverCollectedDeletion(MissionStore missions)
    {
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT id FROM science_delete_reviews WHERE state IN ('Approved','Cleanup pending') ORDER BY id";
        var ids=new List<Guid>();using(var rows=command.ExecuteReader())while(rows.Read())ids.Add(Guid.Parse(rows.GetString(0)));
        foreach(var id in ids){var result=CompleteCollectedDeletion(CollectedReview(id),missions);if(result.State!="Completed")throw new IOException("Collected-science deletion remains pending: "+result.Warning);}
    }
    private CollectedScienceDeletionResult CompleteCollectedDeletion(CollectedScienceDeletionPreview review,MissionStore missions)
    {
        long removed=0;long? pending=0;bool metadataPending=false;string? warning=null;
        try
        {
            foreach(var mission in CollectedMissionIds(review.Id))
            {
                missions.DeleteCollectedScience(mission);
                using(var db=Open(true))PurgeCollectedRows(db,mission);
                foreach(var path in ManagedScienceBackups()){using var backup=OpenDeletionBackup(path,true);PurgeCollectedRows(backup,mission);CompactDeletedRows(backup);}
            }
            CollectedDeletionFault?.Invoke("records-removed");
            foreach(var file in CollectedFiles(review.Id))
            {
                var path=ResolveCollectedPath(file.RelativePath);StorageManager.ValidateLocalPath(path);
                try{if(File.Exists(path)){if(new FileInfo(path).Length!=file.Bytes||Digest(path)!=file.Sha256)throw new IOException("Selected scientific file changed; permanent deletion remains incomplete.");CollectedDeletionFault?.Invoke("before-file-remove");File.Delete(path);}removed++;}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException){pending++;warning=error.Message;}
            }
            CheckpointDeletedContent();
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or SqliteException){metadataPending=true;pending=null;warning=error.Message;}
        var state=pending==0&&!metadataPending?"Completed":"Cleanup pending";
        using(var db=Open(true))using(var command=db.CreateCommand())
        {
            command.CommandText="UPDATE science_delete_reviews SET state=$state WHERE id=$id";command.Parameters.AddWithValue("$id",review.Id.ToString());command.Parameters.AddWithValue("$state",state);command.ExecuteNonQuery();
            if(pending==0){command.CommandText="DELETE FROM science_delete_files WHERE review_id=$id";command.ExecuteNonQuery();}
        }
        return new(review.Id,state,removed,pending,warning,metadataPending);
    }
    private static void PurgeCollectedRows(SqliteConnection db,Guid mission)
    {
        using var command=db.CreateCommand();command.CommandText="PRAGMA secure_delete=ON; PRAGMA foreign_keys=ON";command.ExecuteNonQuery();
        using var tx=db.BeginTransaction();command.Transaction=tx;
        foreach(var table in new[]{"scientific_statistics","remote_cache","remote_products","scientific_artifacts","observations","datasets"})
        {
            command.Parameters.Clear();command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table";command.Parameters.AddWithValue("$table",table);if(Convert.ToInt64(command.ExecuteScalar())==0)continue;
            command.Parameters.Clear();command.Parameters.AddWithValue("$id",mission.ToString());
            command.CommandText=table is "scientific_statistics" or "remote_cache"?$"DELETE FROM {table} WHERE product_id IN (SELECT id FROM remote_products WHERE mission_id=$id)":$"DELETE FROM {table} WHERE mission_id=$id";command.ExecuteNonQuery();
        }
        tx.Commit();
    }
    private static void CompactDeletedRows(SqliteConnection db)
    {
        using var command=db.CreateCommand();command.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";using(var rows=command.ExecuteReader())if(!rows.Read()||rows.GetInt32(0)!=0)throw new IOException("Backup checkpoint busy; deletion is pending.");
        command.CommandText="VACUUM";command.ExecuteNonQuery();command.CommandText="PRAGMA integrity_check";if(command.ExecuteScalar() as string!="ok")throw new IOException("Backup purge integrity verification failed.");
    }
}
