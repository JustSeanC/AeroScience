using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed record ScientificArtifact(Guid Id,Guid LibraryId,Guid MissionId,string Kind,string Format,string RelativePath,long Bytes,
    string Sha256,DateTimeOffset CreatedUtc,string InputIdentity,string MetadataJson);
public sealed class AnalysisScratch(string path,long maximumBytes,Action release):IDisposable
{public string Path{get;}=path;public long MaximumBytes{get;}=maximumBytes;private int disposed;public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)release();}}
public sealed partial class ScienceStore
{
    public AnalysisScratch CreateAnalysisScratch(long maximumBytes)
    {
        if(maximumBytes is <1048576 or >2147483648)throw new ArgumentException("Analysis working space must have a bounded reservation.");
        if(disk.FreeBytes(DirectoryPath)<maximumBytes+StorageManager.MinimumFreeBytes)throw new IOException("Insufficient scientific working space; originals and recordings were preserved.");
        var reservation=Management?.ReserveScience(maximumBytes);var relative=$"Metadata/AnalysisWork/{Guid.NewGuid():N}.sqlite";
        try
        {
        var path=StorageManager.ResolveDataset(DirectoryPath,LibraryId,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var suffixes=new[]{"","-journal","-wal","-shm"};var ledger=BeginManagedWrite("Disposable scientific analysis working index",suffixes.Select(s=>relative+s));
        return new(path,maximumBytes,()=>{try{foreach(var suffix in suffixes)if(File.Exists(path+suffix))File.Delete(path+suffix);File.Delete(ledger);}finally{reservation?.Dispose();}});
        }
        catch{reservation?.Dispose();throw;}
    }
    private int activeAnalyses;
    private void InitializeArtifactTable(SqliteConnection db)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT name FROM sqlite_master WHERE name='scientific_artifacts'";
        if(cmd.ExecuteScalar() is not null){cmd.CommandText="CREATE INDEX IF NOT EXISTS observations_receipt_time ON observations(mission_id,timestamp_utc,id)";cmd.ExecuteNonQuery();return;}
        var backups=Path.Combine(DirectoryPath,"Metadata","Backups");Directory.CreateDirectory(backups);_=DataBackup.Create(DatabasePath,backups,true);
        using var tx=db.BeginTransaction();cmd.Transaction=tx;cmd.CommandText="CREATE TABLE scientific_artifacts(id TEXT PRIMARY KEY,mission_id TEXT NOT NULL,created_utc TEXT NOT NULL,payload_json TEXT NOT NULL); CREATE INDEX artifacts_mission ON scientific_artifacts(mission_id,created_utc)";
        cmd.ExecuteNonQuery();tx.Commit();
        cmd.Transaction=null;cmd.CommandText="CREATE INDEX IF NOT EXISTS observations_receipt_time ON observations(mission_id,timestamp_utc,id)";cmd.ExecuteNonQuery();
    }
    public IEnumerable<ScientificArtifact> Artifacts(Guid? mission=null)
    {
        if(AvailabilityError() is {} unavailable)throw new IOException(unavailable);
        if(!File.Exists(DatabasePath))yield break;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT name FROM sqlite_master WHERE name='scientific_artifacts'";if(cmd.ExecuteScalar() is null)yield break;
        cmd.CommandText="SELECT payload_json FROM scientific_artifacts WHERE ($mission='' OR mission_id=$mission) ORDER BY created_utc,id";cmd.Parameters.AddWithValue("$mission",mission?.ToString()??"");
        using var reader=cmd.ExecuteReader();while(reader.Read())yield return ReadCatalog<ScientificArtifact>(reader.GetString(0));
    }
    public ScientificArtifact Artifact(Guid id)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM scientific_artifacts WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());
        return ReadCatalog<ScientificArtifact>(cmd.ExecuteScalar() as string??throw new ArgumentException("Scientific analysis product not found."));
    }
    public string VerifiedArtifactPath(ScientificArtifact artifact)
    {
        if(artifact.LibraryId!=LibraryId)throw new IOException("Analysis product belongs to another library.");
        var path=StorageManager.ResolveDataset(DirectoryPath,LibraryId,artifact.RelativePath);
        if(new FileInfo(path).Length!=artifact.Bytes||Digest(path)!=artifact.Sha256)throw new IOException("Analysis product is missing or changed; original data was preserved.");return path;
    }
    public IDisposable BeginAnalysis()
    {
        lock(gate){Initialize();activeAnalyses++;return new AnalysisLease(()=>{lock(gate)activeAnalyses--;});}
    }
    private sealed class AnalysisLease(Action release):IDisposable
    {private int done;public void Dispose(){if(Interlocked.Exchange(ref done,1)==0)release();}}
    public ScientificArtifact PublishArtifact(Guid mission,string kind,string format,string inputIdentity,object metadata,long maximumBytes,Action<string> write,CancellationToken token=default)
    {
        if(format is not("json" or "tif")||kind.Length>100||maximumBytes is <=0 or >536870912)throw new ArgumentException("Unsupported or unbounded analysis product.");
        using var operation=BeginAnalysis();using var reservation=Management?.ReserveScience(maximumBytes+65536);
        if(disk.FreeBytes(DirectoryPath)<maximumBytes+StorageManager.MinimumFreeBytes)throw new IOException("Insufficient space for the analysis product and reserve.");
        var id=Guid.NewGuid();var relative=$"Products/{mission:N}/Derived/{id:N}.{format}";var path=StorageManager.ResolveDataset(DirectoryPath,LibraryId,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial=path+".partial";var ledger=BeginManagedWrite("Scientific analysis",[relative,relative+".partial"]);
        try
        {
            token.ThrowIfCancellationRequested();write(partial);token.ThrowIfCancellationRequested();
            var size=new FileInfo(partial).Length;if(size<=0||size>maximumBytes)throw new IOException("Analysis output exceeds its reserved bound.");
            var artifact=new ScientificArtifact(id,LibraryId,mission,kind,format,relative,size,Digest(partial),DateTimeOffset.UtcNow,inputIdentity,JsonSerializer.Serialize(metadata,WireJson.Options));
            lock(gate)
            {
                using var db=Open(write:true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
                cmd.CommandText="INSERT INTO scientific_artifacts VALUES($id,$mission,$utc,$json)";cmd.Parameters.AddWithValue("$id",id.ToString());cmd.Parameters.AddWithValue("$mission",mission.ToString());cmd.Parameters.AddWithValue("$utc",artifact.CreatedUtc.ToString("O",CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(artifact,WireJson.Options));
                token.ThrowIfCancellationRequested();cmd.ExecuteNonQuery();File.Move(partial,path);tx.Commit();
            }
            Management?.AddedScienceFile(size,product:true);File.Delete(ledger);return artifact;
        }
        catch
        {
            // Retain the ledger if a publication interruption left an unindexed final file. It remains accounted, never accepted or silently removed.
            if(!File.Exists(path)){if(File.Exists(partial))File.Delete(partial);File.Delete(ledger);}throw;
        }
    }
}
