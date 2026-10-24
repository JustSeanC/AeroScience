using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class ScienceStore
{
    // Server-supplied relocation history, never paths accepted from a browser.
    public Func<IEnumerable<string>>? ManagedReplicaRoots { get; set; }

    private ManagedSourceFile[] InspectSourceCopies(ScientificSource source,CancellationToken token,Func<Guid,bool> selected)
    {
        foreach(var root in AllReplicaRoots())
        {
            StorageManager.ValidateLocalPath(root);
            if(!Directory.Exists(root))throw new IOException("A managed prior Library location is unavailable. Reconnect it before permanent deletion: "+root);
            if(File.Exists(Path.Combine(root,"recovery.json")))continue;
            if(StorageManager.LibraryId(root)!=LibraryId)throw new IOException("A prior Library copy has a different identity; review it before deletion.");
            var original=Path.Combine(root,source.RelativeDirectory);
            if(Directory.Exists(original))throw new IOException("A managed relocation copy contains this source. Remove this source from the retained prior Library before confirming complete deletion: "+original);
        }
        // A successful import normally clears these owned paths. Interrupted imports
        // and completed range jobs can leave duplicates. Never sweep unrelated files.
        var expected=source.Files.ToLookup(f=>(f.Bytes,f.Sha256));var copies=new Dictionary<string,ManagedSourceFile>(StringComparer.Ordinal);
        var pending=PendingArtifacts();
        foreach(var path in pending.Artifacts)
        {
            token.ThrowIfCancellationRequested();
            var relative=Path.GetRelativePath(DirectoryPath,path).Replace('\\','/');
            if(!relative.StartsWith("Downloads/",StringComparison.Ordinal)||!File.Exists(path))continue;
            StorageManager.ValidateLocalPath(path);var bytes=new FileInfo(path).Length;
            if(!source.Files.Any(f=>f.Bytes==bytes))continue;
            var sha=Digest(path);if(!expected.Contains((bytes,sha)))continue;
            var owner=Path.Combine(Path.GetDirectoryName(path)!,"source.json");bool recognized=false;
            if(File.Exists(owner)){
                StorageManager.ValidateLocalPath(owner);if(new FileInfo(owner).Length>16777216)throw new IOException("Staging ownership metadata is oversized.");using var document=JsonDocument.Parse(File.ReadAllText(owner));var node=document.RootElement.TryGetProperty("manifest",out var manifest)?manifest:document.RootElement;
                var candidate=node.Deserialize<ScientificSourceManifest>(WireJson.Options);if(candidate is null||ScientificCatalog.Hash(candidate)!=ScientificCatalog.Hash(source.Manifest))continue;recognized=true;
            }
            if(!recognized)foreach(var ledger in pending.Ledgers){var work=ReadCatalog<ManagedScientificWork>(File.ReadAllText(ledger));if(ContainsIdentity(work.Operation,source.Id)&&work.Paths.Contains(relative,StringComparer.Ordinal)){recognized=true;break;}}
            if(!recognized)throw new IOException("An interrupted managed copy matches selected bytes but its exact source ownership cannot be established. Resolve that shared staging operation before permanent deletion.");
            copies.TryAdd(relative,new(relative,bytes,sha));
        }
        // Completed automatic staging has a manifest binding its other owned files.
        foreach(var path in pending.Artifacts.Where(p=>Path.GetFileName(p)=="source.json"&&File.Exists(p)))
        {
            token.ThrowIfCancellationRequested();StorageManager.ValidateLocalPath(path);
            if(new FileInfo(path).Length>16777216)throw new IOException("Staged source metadata is oversized; deletion review is incomplete.");
            using var document=JsonDocument.Parse(File.ReadAllText(path));
            var node=document.RootElement.TryGetProperty("manifest",out var manifest)?manifest:document.RootElement;
            var candidate=node.Deserialize<ScientificSourceManifest>(WireJson.Options);
            if(candidate is null||ScientificCatalog.Hash(candidate)!=ScientificCatalog.Hash(source.Manifest))continue;
            var relative=Path.GetRelativePath(DirectoryPath,path).Replace('\\','/');
            if(relative.StartsWith("Downloads/",StringComparison.Ordinal))copies.TryAdd(relative,new(relative,new FileInfo(path).Length,Digest(path)));
        }
        foreach(var artifact in Artifacts().Where(a=>a.Kind=="retained-source")){
            using var metadata=JsonDocument.Parse(artifact.MetadataJson);var node=metadata.RootElement;
            var sourceIds=node.TryGetProperty("sourceIds",out var set)&&set.ValueKind==JsonValueKind.Array?set.EnumerateArray().Select(v=>v.GetGuid()).ToArray():node.TryGetProperty("sourceId",out var one)?[one.GetGuid()]:[];
            if(!sourceIds.Contains(source.Id))continue;
            if(sourceIds.Any(id=>!selected(id)))throw new IOException("A retained-source mosaic shares selected and unselected original data. Review all contributing sources together; no partial batch will be silently deleted.");
            if(sourceIds.Order().First()!=source.Id)continue;
            var path=VerifiedArtifactPath(artifact);copies.TryAdd(artifact.RelativePath,new(artifact.RelativePath,new FileInfo(path).Length,Digest(path)));
        }
        foreach(var copy in RegisteredCopies().Where(c=>c.Kind=="recovery")){
            var database=RegisteredCopies().FirstOrDefault(c=>c.Kind=="database"&&Path.GetDirectoryName(c.Path)==copy.Path)??throw new IOException("Managed recovery copy ownership is incomplete.");
            using var backup=OpenDeletionBackup(database.Path,false);using var read=backup.CreateCommand();read.CommandText="SELECT payload_json FROM scientific_artifacts WHERE json_extract(payload_json,'$.kind')='retained-source'";using var artifacts=read.ExecuteReader();while(artifacts.Read()){
                var artifact=ReadCatalog<ScientificArtifact>(artifacts.GetString(0));using var metadata=JsonDocument.Parse(artifact.MetadataJson);var node=metadata.RootElement;
                var ids=node.TryGetProperty("sourceIds",out var set)&&set.ValueKind==JsonValueKind.Array?set.EnumerateArray().Select(v=>v.GetGuid()).ToArray():node.TryGetProperty("sourceId",out var one)?[one.GetGuid()]:[];
                if(!ids.Contains(source.Id))continue;if(ids.Any(id=>!selected(id)))throw new IOException("A shared recovery mosaic includes unselected sources. Review all contributors before permanent deletion.");if(ids.Order().First()!=source.Id)continue;
                var relative=$"ManagedCopies/{copy.Id:N}/{artifact.RelativePath}";var path=ResolveCollectedPath(relative);if(new FileInfo(path).Length!=artifact.Bytes||Digest(path)!=artifact.Sha256)throw new IOException("A managed recovery mosaic changed; deletion review is incomplete.");copies.TryAdd(relative,new(relative,artifact.Bytes,artifact.Sha256));
            }
        }
        // A recovery block is not necessarily byte-identical to an imported crop.
        // Its source identity is the durable job item, rather than guessed from pixels.
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT i.job_id,i.ordinal FROM library_job_items i JOIN library_jobs j ON j.id=i.job_id WHERE i.source_id=$id OR (i.source_id IS NULL AND json_extract(j.preview_json,'$.work['||i.ordinal||'].scene.revision')=$revision AND json_extract(j.preview_json,'$.work['||i.ordinal||'].scene.scene')=$scene)";command.Parameters.AddWithValue("$id",source.Id.ToString());command.Parameters.AddWithValue("$revision",source.Manifest.Acquisition?.CatalogRevision??"no-compatible-revision");command.Parameters.AddWithValue("$scene",source.Manifest.SceneId);
        using var rows=command.ExecuteReader();while(rows.Read())
        {
            var prefix=$"Downloads/Library/{Guid.Parse(rows.GetString(0)):N}/{rows.GetInt32(1)}/";
            foreach(var path in pending.Artifacts)
            {
                var relative=Path.GetRelativePath(DirectoryPath,path).Replace('\\','/');if(!relative.StartsWith(prefix,StringComparison.Ordinal)||!File.Exists(path))continue;
                StorageManager.ValidateLocalPath(path);copies.TryAdd(relative,new(relative,new FileInfo(path).Length,Digest(path)));
            }
        }
        return copies.Values.OrderBy(f=>f.RelativePath,StringComparer.Ordinal).ToArray();
    }

    private IEnumerable<string> ManagedScienceBackups()
    {
        var directory=Path.Combine(DirectoryPath,"Metadata","Backups");
        if(Directory.Exists(directory))foreach(var file in Directory.EnumerateFiles(directory,"*.sqlite",SearchOption.TopDirectoryOnly))
        {StorageManager.ValidateLocalPath(file);yield return file;}
        foreach(var copy in RegisteredCopies().Where(c=>c.Kind=="database")){
            if(!File.Exists(copy.Path))throw new IOException("A registered managed backup is unavailable. Reconnect or explicitly resolve it before permanent deletion: "+copy.Path);
            yield return copy.Path;
        }
    }
    private static SqliteConnection OpenDeletionBackup(string path,bool write)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=write?SqliteOpenMode.ReadWrite:SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        try{db.Open();return db;}catch{db.Dispose();throw;}
    }
    private void CheckSourceBackupSafety(Guid source)
    {
        foreach(var path in ManagedScienceBackups())
        {
            using var db=OpenDeletionBackup(path,false);using var command=db.CreateCommand();command.CommandText="SELECT value FROM metadata WHERE key='library_id'";
            if(command.ExecuteScalar() as string!=LibraryId.ToString())throw new IOException("A shared backup has another Library identity; cannot safely purge selected content: "+path);
            command.CommandText="PRAGMA integrity_check";if(command.ExecuteScalar() as string!="ok")throw new IOException("A managed backup cannot be verified; permanent deletion is blocked: "+path);
        }
    }
    private void PurgeSourceBackups(IEnumerable<Guid> sources)
    {
        foreach(var path in ManagedScienceBackups())
        {
            using var db=OpenDeletionBackup(path,true);using var command=db.CreateCommand();command.CommandText="PRAGMA secure_delete=ON; PRAGMA foreign_keys=ON";command.ExecuteNonQuery();command.CommandText="SELECT count(*) FROM sqlite_master WHERE name='remote_sources'";
            if(Convert.ToInt64(command.ExecuteScalar())==0)continue;
            var affected=0;using(var transaction=db.BeginTransaction()){
                command.Transaction=transaction;command.CommandText="DELETE FROM remote_sources WHERE id=$id";command.Parameters.Add("$id",SqliteType.Text);
                foreach(var source in sources){command.CommandText="DELETE FROM remote_sources WHERE id=$id";command.Parameters["$id"].Value=source.ToString();affected+=command.ExecuteNonQuery();
                    using var table= db.CreateCommand();table.Transaction=transaction;table.CommandText="SELECT count(*) FROM sqlite_master WHERE name='scientific_artifacts'";if(Convert.ToInt64(table.ExecuteScalar())>0){command.CommandText="DELETE FROM scientific_artifacts WHERE json_extract(payload_json,'$.kind')='retained-source' AND (json_extract(json_extract(payload_json,'$.metadataJson'),'$.sourceId')=$id OR EXISTS(SELECT 1 FROM json_each(json_extract(payload_json,'$.metadataJson'),'$.sourceIds') WHERE value=$id))";affected+=command.ExecuteNonQuery();}
                }
                transaction.Commit();command.Transaction=null;
            }
            command.Parameters.Clear();command.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";
            using(var rows=command.ExecuteReader())if(!rows.Read()||rows.GetInt32(0)!=0)throw new IOException("Managed backup checkpoint is busy; purge is pending.");
            command.CommandText="VACUUM";command.ExecuteNonQuery();command.CommandText="PRAGMA integrity_check";
            if(command.ExecuteScalar() as string!="ok")throw new IOException("Managed backup purge could not be verified.");
        }
    }
    private void CheckpointDeletedContent()
    {
        using var db=Open(true);using var command=db.CreateCommand();command.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";
        using(var rows=command.ExecuteReader()){if(!rows.Read()||rows.GetInt32(0)!=0)throw new IOException("Deletion checkpoint is busy; managed database cleanup remains pending.");}
        command.CommandText="VACUUM";command.ExecuteNonQuery();command.CommandText="PRAGMA integrity_check";
        if(command.ExecuteScalar() as string!="ok")throw new IOException("Deleted-content database verification failed; cleanup is not complete.");
    }
}
