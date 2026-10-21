using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed record SourceDeletionRequest(Guid[]? SourceIds=null,bool All=false);
public sealed record SourceDeletionExecution(Guid PreviewId,string SelectionHash,string Confirmation);
public sealed record SourceDeletionItem(Guid Id,string Name,string Provider,RemoteKind Kind,string RelativeDirectory,
    string ManifestSha256,ManagedSourceFile[] Files,long ManagedBytes,string[] Blockers);
public sealed record SourceDeletionPreview(Guid Id,Guid LibraryId,bool All,DateTimeOffset CreatedUtc,string SelectionHash,
    SourceDeletionItem[] Sources,string Confirmation,string Preserved)
{
    public int EligibleSources=>Sources.Count(s=>s.Blockers.Length==0);
    public long EligibleManagedBytes=>Sources.Where(s=>s.Blockers.Length==0).Sum(s=>s.ManagedBytes);
}
public sealed record SourceDeletionResult(Guid Id,int RemovedSources,long RemovedManagedBytes,long PendingManagedBytes,
    long? PhysicalFreeSpaceChangeBytes,string State,string? Warning,Guid[]? RemovedSourceIds=null);
internal sealed record SourceDeletionJournal(SourceDeletionPreview Preview,string State,string[] Ledgers,long? FreeBefore,string? Warning=null,int Version=1);

public sealed partial class ScienceStore
{
    private bool sourceMaintenance,sourceDeletionRecoveryDone;
    private readonly Dictionary<Guid,SourceDeletionPreview> sourceDeletionPreviews=[];
    internal Action<string>? SourceDeletionFault;
    public string? SourceDeletionWarning {get;private set;}
    public IDisposable EnterSourceMaintenance()
    {
        Initialize();lock(gate){if(sourceMaintenance||activeAnalyses>0)throw new InvalidOperationException("Finish active scientific acquisition, import or analysis before deleting external sources.");sourceMaintenance=true;return new AnalysisLease(()=>{lock(gate)sourceMaintenance=false;});}
    }
    private string SourceDeletionFolder(Guid id)=>StorageManager.ResolveDataset(DirectoryPath,LibraryId,$"Metadata/SourceDeletions/{id:N}");
    private static bool ContainsIdentity(string json,Guid id)=>json.Contains(id.ToString(),StringComparison.OrdinalIgnoreCase)||json.Contains(id.ToString("N"),StringComparison.OrdinalIgnoreCase);
    private string[] ScientificSourceDependencies(ScientificSource source)
    {
        using var db=Open();var reasons=SourceDependencies.ArchivedReferences(db,source.Id,"remote_sources","library_jobs","library_job_items","metadata").ToList();
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,payload_json FROM remote_sources WHERE id<>$id";cmd.Parameters.AddWithValue("$id",source.Id.ToString());
        using(var rows=cmd.ExecuteReader())while(rows.Read())if(ContainsIdentity(rows.GetString(1),source.Id))reasons.Add("Another retained source references this identity: "+rows.GetString(0));
        cmd.CommandText="SELECT id,preview_json FROM library_jobs WHERE state NOT IN ('Completed','Cancelled','Failed')";cmd.Parameters.Clear();
        using(var rows=cmd.ExecuteReader())while(rows.Read()){
            var json=rows.GetString(1);using var parsed=JsonDocument.Parse(json);
            if(ContainsIdentity(json,source.Id)||JsonStrings(parsed.RootElement).Contains(source.Manifest.SceneId,StringComparer.Ordinal))reasons.Add("Unfinished download/approval references this source or scene: "+rows.GetString(0));
        }
        return reasons.ToArray();
    }
    private static IEnumerable<string> JsonStrings(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.String)yield return e.GetString()!;
        else if(e.ValueKind==JsonValueKind.Object)foreach(var p in e.EnumerateObject())foreach(var v in JsonStrings(p.Value))yield return v;
        else if(e.ValueKind==JsonValueKind.Array)foreach(var n in e.EnumerateArray())foreach(var v in JsonStrings(n))yield return v;
    }
    private ManagedSourceFile[] InspectOwnedSource(ScientificSource source,CancellationToken token)
    {
        if(source.RelativeDirectory!=$"Sources/{source.Id:N}"||source.Files is null||source.Files.Length is <1 or >10000)throw new IOException("Source ownership is unsupported. No files can be removed.");
        var root=StorageManager.ResolveDataset(DirectoryPath,LibraryId,source.RelativeDirectory);StorageManager.ValidateLocalPath(root);
        var metadata=ScientificRaster.SafeFile(root,"source.json");if(new FileInfo(metadata).Length>16777216)throw new IOException("Source ownership metadata is oversized.");
        var owned=JsonSerializer.Deserialize<ScientificSource>(File.ReadAllText(metadata),WireJson.Options)??throw new IOException("Source ownership metadata is missing.");
        if(ScientificCatalog.Hash(owned)!=ScientificCatalog.Hash(source)||ScientificComposites.Identity(new{manifest=source.Manifest,files=source.Files})!=source.ManifestSha256)throw new IOException("Original source metadata differs from its catalog identity.");
        var files=new List<ManagedSourceFile>();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in source.Files){token.ThrowIfCancellationRequested();if(!names.Add(file.RelativePath)||file.RelativePath=="source.json")throw new IOException("Conflicting managed source paths.");var path=ScientificRaster.SafeFile(root,file.RelativePath);StorageManager.ValidateLocalPath(path);
            if(new FileInfo(path).Length!=file.Bytes||Digest(path)!=file.Sha256)throw new IOException("An original source file is missing or changed; restore or review it before deletion.");files.Add(file);}
        files.Add(new("source.json",new FileInfo(metadata).Length,Digest(metadata)));names.Add("source.json");
        var pending=new Stack<string>();pending.Push(root);int entries=0;
        while(pending.TryPop(out var directory))foreach(var path in Directory.EnumerateFileSystemEntries(directory)){
            if(++entries>20000)throw new IOException("Source tree exceeds bounded review limits; review a smaller collection.");StorageManager.ValidateLocalPath(path);
            if(Directory.Exists(path))pending.Push(path);else if(!names.Contains(Path.GetRelativePath(root,path).Replace('\\','/')))throw new IOException("Uncatalogued files exist in this source folder; preserve them outside the managed source before deletion.");
        }
        return files.ToArray();
    }
    public SourceDeletionPreview PreviewSourceDeletion(SourceDeletionRequest request,MissionStore missions,Func<string?>? busy=null,CancellationToken token=default,Func<ScientificSource,string[]>? planningDependencies=null)
    {
        if(request.All&&request.SourceIds is {Length:>0}||!request.All&&(request.SourceIds is not {Length:>0 and <=500}||request.SourceIds.Distinct().Count()!=request.SourceIds.Length))throw new ArgumentException("Choose an exact nonempty source set, or explicitly request all downloaded sources.");
        if(AvailabilityError() is {} error)throw new IOException(error);
        var library=LibraryId;var all=Sources();var ids=request.All?all.Select(s=>s.Id).Order().ToArray():request.SourceIds!.Order().ToArray();
        if(ids.Length==0)throw new InvalidOperationException("There are no retained external sources to delete.");
        var selected=ids.Select(id=>all.SingleOrDefault(s=>s.Id==id)??throw new InvalidOperationException("Source selection changed; refresh the inventory and review again.")).ToArray();
        string? global=busy?.Invoke();lock(gate)if(activeAnalyses>0||sourceMaintenance)global="Scientific acquisition/import/analysis or source maintenance is active. Finish or cancel it before deletion.";
        var items=new List<SourceDeletionItem>();int fileCount=0;
        foreach(var source in selected){token.ThrowIfCancellationRequested();var reasons=new List<string>();var files=Array.Empty<ManagedSourceFile>();
            if(global is not null)reasons.Add(global);
            try{if(planningDependencies is not null)reasons.AddRange(planningDependencies(source));reasons.AddRange(missions.ExternalSourceDependencies(source.Id).Select(r=>"Mission evidence: "+r));reasons.AddRange(ScientificSourceDependencies(source));}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or SqliteException or JsonException){reasons.Add("Dependency check unavailable; deletion is blocked: "+e.Message);}
            if(reasons.Count==0)try{files=InspectOwnedSource(source,token);fileCount+=files.Length;if(fileCount>20000)reasons.Add("This reviewed operation exceeds 20,000 managed files. Select smaller explicit batches.");}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException){reasons.Add(e.Message);}
            items.Add(new(source.Id,source.Manifest.Name,source.Manifest.Provider,source.Manifest.Kind,source.RelativeDirectory,source.ManifestSha256,files,files.Sum(f=>f.Bytes),reasons.ToArray()));
        }
        if(LibraryId!=library)throw new IOException("Library identity changed during review; no deletion was authorized.");
        var hash=SourceDeleteHash(library,request.All,items);
        var preview=new SourceDeletionPreview(Guid.NewGuid(),library,request.All,DateTimeOffset.UtcNow,hash,items.ToArray(),request.All?"DELETE ALL EXTERNAL DATA":"DELETE EXTERNAL DATA",
            "Flight recordings, telemetry, mission/science observations, grades, reports, calibration history, settings, prior backups, other sources and user originals outside this Library remain untouched. Referenced/protected sources cannot be deleted.");
        lock(gate){foreach(var old in sourceDeletionPreviews.Values.Where(p=>p.CreatedUtc<DateTimeOffset.UtcNow.AddMinutes(-10)).Select(p=>p.Id).ToArray())sourceDeletionPreviews.Remove(old);if(sourceDeletionPreviews.Count>=32)throw new InvalidOperationException("Close expired source-deletion reviews before creating more.");sourceDeletionPreviews[preview.Id]=preview;}return preview;
    }
    public SourceDeletionResult DeleteExternalSources(SourceDeletionExecution request,MissionStore missions,Func<IDisposable> missionMaintenance,Func<string?>? busy=null,CancellationToken token=default,Func<ScientificSource,string[]>? planningDependencies=null)
    {
        if(AvailabilityError() is {} unavailable)throw new IOException(unavailable);
        var journalPath=Path.Combine(SourceDeletionFolder(request.PreviewId),"journal.json");
        if(File.Exists(journalPath)){var prior=ReadDeletionJournal(journalPath);AuthorizeDeletion(prior.Preview,request);using var held=missionMaintenance();using var sourceLock=EnterSourceMaintenance();return RecoverSourceDeletion(journalPath,prior);}
        SourceDeletionPreview preview;lock(gate)preview=sourceDeletionPreviews.GetValueOrDefault(request.PreviewId)??throw new InvalidOperationException("Deletion review expired or belongs to another profile. Review the exact sources again.");
        AuthorizeDeletion(preview,request);if(preview.CreatedUtc<DateTimeOffset.UtcNow.AddMinutes(-10))throw new InvalidOperationException("Deletion review expired. Preview again.");
        if(preview.Sources.Any(s=>s.Blockers.Length>0))throw new InvalidOperationException("Selected sources are blocked. Review a new explicit eligible selection; no reduced set was silently deleted.");
        using var missionLock=missionMaintenance();
        var current=PreviewSourceDeletion(new(preview.All?null:preview.Sources.Select(s=>s.Id).ToArray(),preview.All),missions,busy,token,planningDependencies);
        if(current.SelectionHash!=preview.SelectionHash||current.Sources.Any(s=>s.Blockers.Length>0))throw new InvalidOperationException("Source files, dependencies or selection changed. Review and confirm again.");
        using var maintenance=EnterSourceMaintenance();
        // New readers/imports/flight starts are now excluded. Recheck lineage in this
        // boundary; no long-running source hash occurs under the telemetry gate.
        if(preview.All&&!Sources().Select(s=>s.Id).Order().SequenceEqual(preview.Sources.Select(s=>s.Id).Order()))throw new InvalidOperationException("The source inventory changed; review Delete All again.");
        foreach(var item in preview.Sources){var source=Sources().Single(s=>s.Id==item.Id);if(missions.ExternalSourceDependencies(item.Id).Length>0||ScientificSourceDependencies(source).Length>0||planningDependencies?.Invoke(source).Length>0)throw new InvalidOperationException("A new source dependency blocks deletion; preview again.");}
        var directory=SourceDeletionFolder(preview.Id);Directory.CreateDirectory(directory);
        var paths=preview.Sources.SelectMany(s=>s.Files.Select(f=>$"Metadata/SourceDeletions/{preview.Id:N}/objects/{s.Id:N}/{f.RelativePath}"));
        var ledgers=paths.Chunk(1024).Select(chunk=>Path.GetFileName(BeginManagedWrite("Approved recoverable external source deletion",chunk))).ToArray();
        long? free=null;try{free=disk.FreeBytes(DirectoryPath);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        var journal=new SourceDeletionJournal(preview,"prepared",ledgers,free);PreferenceStore.WriteAtomic(journalPath,journal);SourceDeletionFault?.Invoke("prepared");
        try{
            foreach(var source in preview.Sources){token.ThrowIfCancellationRequested();var from=StorageManager.ResolveDataset(DirectoryPath,preview.LibraryId,source.RelativeDirectory);var to=Path.Combine(directory,"objects",source.Id.ToString("N"));Directory.CreateDirectory(Path.GetDirectoryName(to)!);Directory.Move(from,to);VerifyDeletionFiles(to,source);SourceDeletionFault?.Invoke("quarantined");}
            // The source/mission maintenance leases exclude writers. SQLite owns the
            // atomic commit; do not hold an application monitor across rollback/fault unwinding.
            {using var db=Open(true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
                foreach(var source in preview.Sources){cmd.Parameters.Clear();cmd.CommandText="DELETE FROM remote_sources WHERE id=$id AND manifest_sha=$sha";cmd.Parameters.AddWithValue("$id",source.Id.ToString());cmd.Parameters.AddWithValue("$sha",source.ManifestSha256);if(cmd.ExecuteNonQuery()!=1)throw new IOException("Source identity changed before commit.");}
                cmd.Parameters.Clear();cmd.CommandText="INSERT INTO metadata(key,value) VALUES($key,$hash)";cmd.Parameters.AddWithValue("$key","source_delete:"+preview.Id.ToString("N"));cmd.Parameters.AddWithValue("$hash",preview.SelectionHash);cmd.ExecuteNonQuery();SourceDeletionFault?.Invoke("before-commit");tx.Commit();}
            SourceDeletionFault?.Invoke("committed");return RecoverSourceDeletion(journalPath,journal);
        }catch(Exception e)when(e is IOException or SqliteException or UnauthorizedAccessException or OperationCanceledException){
            // The durable SQLite receipt decides rollback versus committed removal,
            // including a crash between commit and updating the filesystem journal.
            var result=RecoverSourceDeletion(journalPath,journal);if(result.State=="Rolled back")throw new IOException("Source deletion was rolled back; originals and catalog were preserved. "+e.Message,e);return result;
        }
    }
    private static string SourceDeleteHash(Guid library,bool all,IEnumerable<SourceDeletionItem> sources)=>ScientificCatalog.Hash(new{library,all,sources=sources.Select(i=>new{i.Id,i.ManifestSha256,i.Files,i.Blockers})});
    private static void AuthorizeDeletion(SourceDeletionPreview p,SourceDeletionExecution r){if(p.Id!=r.PreviewId||p.SelectionHash!=r.SelectionHash||p.Confirmation!=r.Confirmation)throw new InvalidOperationException("Review the exact source selection and supply its required confirmation.");}
    private SourceDeletionJournal ReadDeletionJournal(string path)
    {
        StorageManager.ValidateLocalPath(path);if(new FileInfo(path).Length>33554432)throw new IOException("Source deletion journal exceeds supported bounds; preserve it for recovery.");
        var j=JsonSerializer.Deserialize<SourceDeletionJournal>(File.ReadAllText(path),WireJson.Options)??throw new IOException("Missing source deletion journal.");
        if(j.Version!=1||j.Preview.LibraryId!=LibraryId||Path.GetFullPath(path)!=Path.Combine(SourceDeletionFolder(j.Preview.Id),"journal.json")||j.Preview.Sources.Length is <1 or >500||j.Preview.Sources.Sum(s=>s.Files.Length)>20000||j.Preview.Sources.Any(s=>s.RelativeDirectory!=$"Sources/{s.Id:N}")||j.Ledgers.Any(n=>!Guid.TryParseExact(Path.GetFileNameWithoutExtension(n),"N",out _)||n!=Path.GetFileName(n)||!n.EndsWith(".json",StringComparison.Ordinal)))throw new IOException("Source deletion ownership is invalid; no recovery paths were modified.");
        if(j.Preview.SelectionHash!=SourceDeleteHash(j.Preview.LibraryId,j.Preview.All,j.Preview.Sources)||j.Preview.Sources.Any(s=>s.Blockers.Length>0||s.Files.Any(f=>f.Bytes<0||f.Sha256.Length!=64)))throw new IOException("Deletion journal content identity is invalid; preserve all files.");
        return j;
    }
    private SourceDeletionResult RecoverSourceDeletion(string path,SourceDeletionJournal journal)
    {
        var preview=journal.Preview;using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM metadata WHERE key=$key";cmd.Parameters.AddWithValue("$key","source_delete:"+preview.Id.ToString("N"));var receipt=cmd.ExecuteScalar() as string;
        if(receipt is not null&&receipt!=preview.SelectionHash)throw new IOException("Source deletion receipt identity mismatch; preserve all files.");
        var committed=receipt is not null;long pending=0,removed=0;string? warning=null;
        foreach(var source in preview.Sources){var original=StorageManager.ResolveDataset(DirectoryPath,preview.LibraryId,source.RelativeDirectory);var quarantine=Path.Combine(SourceDeletionFolder(preview.Id),"objects",source.Id.ToString("N"));StorageManager.ValidateLocalPath(quarantine);
            if(!committed){if(Directory.Exists(quarantine)){if(Directory.Exists(original))throw new IOException("Recovery found conflicting source folders; neither copy was overwritten.");VerifyDeletionFiles(quarantine,source);Directory.Move(quarantine,original);}else if(!Directory.Exists(original))throw new IOException("Source recovery needs its original directory; an empty source was not invented.");continue;}
            foreach(var file in source.Files){var target=StorageManager.ResolveDataset(DirectoryPath,LibraryId,$"Metadata/SourceDeletions/{preview.Id:N}/objects/{source.Id:N}/{file.RelativePath}");
                try{if(File.Exists(target)){if(new FileInfo(target).Length!=file.Bytes||Digest(target)!=file.Sha256)throw new IOException("A quarantined file changed; preserved for review.");SourceDeletionFault?.Invoke("before-file-remove");File.Delete(target);}removed+=file.Bytes;}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException){pending+=file.Bytes;warning=e.Message;}
            }
            // No recursive deletion: unexpected files, empty subfolders and backups
            // are never swept up by a broad tree-removal operation.
        }
        var state=committed?(pending==0?"Completed":"Cleanup pending"):"Rolled back";
        PreferenceStore.WriteAtomic(path,journal with{State=state,Warning=warning});
        if(pending==0)foreach(var name in journal.Ledgers){var ledger=StorageManager.ResolveDataset(DirectoryPath,LibraryId,"Metadata/Work/"+name);if(File.Exists(ledger))File.Delete(ledger);}
        SourceDeletionWarning=warning;long? delta=null;try{if(journal.FreeBefore is {} before)delta=disk.FreeBytes(DirectoryPath)-before;}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        return new(preview.Id,committed?preview.Sources.Length:0,removed,pending,delta,state,warning,committed?preview.Sources.Select(s=>s.Id).ToArray():[]);
    }
    private static void VerifyDeletionFiles(string root,SourceDeletionItem source){foreach(var file in source.Files){var target=ScientificRaster.SafeFile(root,file.RelativePath);if(new FileInfo(target).Length!=file.Bytes||Digest(target)!=file.Sha256)throw new IOException("Recovery source is incomplete or changed; it was not published as verified.");}}
    private void RecoverSourceDeletionJournals()
    {
        var folder=StorageManager.ResolveDataset(DirectoryPath,LibraryId,"Metadata/SourceDeletions");if(!Directory.Exists(folder))return;
        var directories=Directory.EnumerateDirectories(folder).Take(1001).ToArray();if(directories.Length>1000)throw new IOException("Too many source deletion records; review recovery history before continuing.");
        foreach(var directory in directories){if(!Guid.TryParseExact(Path.GetFileName(directory),"N",out _))continue;var path=Path.Combine(directory,"journal.json");StorageManager.ValidateLocalPath(path);if(!File.Exists(path))continue;var journal=ReadDeletionJournal(path);if(journal.State is "Completed" or "Rolled back")continue;_=RecoverSourceDeletion(path,journal);}
    }
}
