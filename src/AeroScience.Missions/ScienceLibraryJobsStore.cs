using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed partial class ScienceStore
{
    public const int CurrentSchemaVersion=3;
    private static void InitializeLibraryJobs(SqliteConnection db)
    {
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS library_jobs(id TEXT PRIMARY KEY,state TEXT NOT NULL,preview_json TEXT NOT NULL,
                approval_id TEXT,updated_utc TEXT NOT NULL,error TEXT);
            CREATE TABLE IF NOT EXISTS library_job_items(job_id TEXT NOT NULL REFERENCES library_jobs(id),ordinal INTEGER NOT NULL,
                state TEXT NOT NULL,downloaded INTEGER NOT NULL DEFAULT 0,uncertain INTEGER NOT NULL DEFAULT 0,
                reserved INTEGER NOT NULL DEFAULT 0,attempts INTEGER NOT NULL DEFAULT 0,source_id TEXT,verified INTEGER NOT NULL DEFAULT 0,
                error TEXT,updated_utc TEXT NOT NULL,PRIMARY KEY(job_id,ordinal));
            CREATE INDEX IF NOT EXISTS library_jobs_state ON library_jobs(state,updated_utc);
            PRAGMA user_version=3;
            """;cmd.ExecuteNonQuery();tx.Commit();
        cmd.Transaction=null;cmd.CommandText="PRAGMA integrity_check";if(cmd.ExecuteScalar() as string!="ok")throw new IOException("Library migration integrity check failed; restore its preserved verified backup.");
    }
    public LibraryDownloadJob SaveLibraryPreview(LibraryDownloadPreview preview)
    {
        Initialize();lock(gate)
        {
            using var db=Open(true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="SELECT COUNT(*) FROM library_jobs WHERE state NOT IN ('Completed','Cancelled','Failed')";
            if(Convert.ToInt32(cmd.ExecuteScalar())>=32)throw new InvalidOperationException("Finish or cancel existing Library work before adding more jobs (32 active previews/jobs maximum).");
            cmd.CommandText="INSERT INTO library_jobs VALUES($id,'Awaiting Approval',$preview,NULL,$utc,NULL)";cmd.Parameters.AddWithValue("$id",preview.Id.ToString());cmd.Parameters.AddWithValue("$preview",JsonSerializer.Serialize(preview,WireJson.Options));cmd.Parameters.AddWithValue("$utc",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
            foreach(var work in preview.Work){cmd.Parameters.Clear();cmd.CommandText="INSERT INTO library_job_items(job_id,ordinal,state,updated_utc) VALUES($id,$i,'Queued',$utc)";cmd.Parameters.AddWithValue("$id",preview.Id.ToString());cmd.Parameters.AddWithValue("$i",work.Index);cmd.Parameters.AddWithValue("$utc",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();}tx.Commit();
        }
        return LibraryJob(preview.Id);
    }
    public LibraryDownloadJob LibraryJob(Guid id)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT state,preview_json,approval_id,updated_utc,error FROM library_jobs WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());
        string state;LibraryDownloadPreview preview;Guid? approval;DateTimeOffset utc;string? error;
        using(var r=cmd.ExecuteReader()){if(!r.Read())throw new ArgumentException("Library job not found.");state=r.GetString(0);preview=ReadCatalog<LibraryDownloadPreview>(r.GetString(1));approval=r.IsDBNull(2)?null:Guid.Parse(r.GetString(2));utc=DateTimeOffset.Parse(r.GetString(3));error=r.IsDBNull(4)?null:r.GetString(4);}
        if(preview.Version!=1)throw new NotSupportedException("Unsupported Library job version; preserve these records.");
        cmd.CommandText="SELECT ordinal,state,downloaded,uncertain,reserved,attempts,source_id,verified,error,updated_utc FROM library_job_items WHERE job_id=$id ORDER BY ordinal";
        using var reader=cmd.ExecuteReader();var items=new List<LibraryDownloadItem>();while(reader.Read())items.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4),reader.GetInt32(5),reader.IsDBNull(6)?null:Guid.Parse(reader.GetString(6)),reader.GetInt64(7),reader.IsDBNull(8)?null:reader.GetString(8),DateTimeOffset.Parse(reader.GetString(9))));
        return new(id,state,preview,approval,utc,items.ToArray(),error);
    }
    public LibraryDownloadJob[] LibraryJobs(int offset=0)
    {
        if(offset is <0 or >100000)throw new ArgumentException("Invalid Library job page.");
        if(AvailabilityError() is {} error)throw new IOException(error);if(!File.Exists(DatabasePath))return [];
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id FROM library_jobs ORDER BY CASE WHEN state IN ('Completed','Cancelled') THEN 1 ELSE 0 END,updated_utc DESC LIMIT 64 OFFSET $offset";cmd.Parameters.AddWithValue("$offset",offset);
        var ids=new List<Guid>();using(var r=cmd.ExecuteReader())while(r.Read())ids.Add(Guid.Parse(r.GetString(0)));return ids.Select(LibraryJob).ToArray();
    }
    internal LibraryDownloadJob? NextQueuedLibraryJob()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id FROM library_jobs WHERE state='Queued' ORDER BY updated_utc LIMIT 1";
        return cmd.ExecuteScalar() is string id?LibraryJob(Guid.Parse(id)):null;
    }
    internal Guid[] UnfinishedNasaJobs()
    {
        if(!File.Exists(DatabasePath))return [];
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="""
            SELECT id FROM library_jobs WHERE state IN ('Awaiting Approval','Queued','Preparing','Downloading','Verifying','Paused','Interrupted')
                AND json_extract(preview_json,'$.version')=1 AND json_type(preview_json,'$.nasa')='object'
            """;
        using var rows=cmd.ExecuteReader();var ids=new List<Guid>();while(rows.Read())ids.Add(Guid.Parse(rows.GetString(0)));return ids.ToArray();
    }
    internal LibraryDownloadJob SetLibraryState(Guid id,string state,string? error=null,bool approve=false)
    {
        lock(gate)
        {
            using var db=Open(true);using var cmd=db.CreateCommand();cmd.CommandText="UPDATE library_jobs SET state=$s,error=$e,updated_utc=$u,approval_id=CASE WHEN $approve=1 THEN $approval ELSE approval_id END WHERE id=$id";
            cmd.Parameters.AddWithValue("$s",state);cmd.Parameters.AddWithValue("$e",(object?)error??DBNull.Value);cmd.Parameters.AddWithValue("$u",DateTimeOffset.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$approve",approve?1:0);cmd.Parameters.AddWithValue("$approval",Guid.NewGuid().ToString());cmd.Parameters.AddWithValue("$id",id.ToString());cmd.ExecuteNonQuery();return LibraryJob(id);
        }
    }
    internal void SetLibraryItem(Guid id,int index,string state,string? error=null,Guid? source=null,long verifiedBytes=0)
    {
        lock(gate)
        {
            using var db=Open(true);using var cmd=db.CreateCommand();cmd.CommandText="UPDATE library_job_items SET state=$s,error=$e,source_id=COALESCE($source,source_id),verified=MAX(verified,$v),attempts=attempts+CASE WHEN $s='Preparing' THEN 1 ELSE 0 END,updated_utc=$u WHERE job_id=$id AND ordinal=$i";
            cmd.Parameters.AddWithValue("$s",state);cmd.Parameters.AddWithValue("$e",(object?)error??DBNull.Value);cmd.Parameters.AddWithValue("$source",(object?)source?.ToString()??DBNull.Value);cmd.Parameters.AddWithValue("$v",verifiedBytes);cmd.Parameters.AddWithValue("$u",DateTimeOffset.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$id",id.ToString());cmd.Parameters.AddWithValue("$i",index);cmd.ExecuteNonQuery();
        }
    }
    // Reserve the next read durably BEFORE network I/O. A process kill charges its unresolved
    // reservation as uncertainty on recovery, never silently replenishing the approval cap.
    internal void LibraryBudget(Guid id,int index,long cumulative,long requiredTotal,bool finish=false)
    {
        lock(gate)
        {
            using var db=Open(true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="SELECT COALESCE(SUM(downloaded+uncertain+reserved),0) FROM library_job_items WHERE job_id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());var used=Convert.ToInt64(cmd.ExecuteScalar());
            cmd.CommandText="SELECT downloaded,reserved FROM library_job_items WHERE job_id=$id AND ordinal=$i";cmd.Parameters.AddWithValue("$i",index);long prior,reserved;using(var r=cmd.ExecuteReader()){r.Read();prior=r.GetInt64(0);reserved=r.GetInt64(1);}
            // cumulative is the item's lifetime confirmed bytes, supplied by the active attempt.
            var delta=cumulative-prior;if(delta<0||delta>reserved)throw new InvalidOperationException("Non-monotonic Library transfer evidence.");
            var hold=finish?0:Math.Max(reserved-delta,requiredTotal-cumulative);
            cmd.CommandText="SELECT json_extract(preview_json,'$.maximumTransferBytes') FROM library_jobs WHERE id=$id";var cap=Convert.ToInt64(cmd.ExecuteScalar());if(used-reserved+delta+hold>cap)throw new IOException("Approved Library transfer budget reached, including retries and unresolved interrupted reads. Preview a smaller new job; the allowance was not silently enlarged.");
            cmd.CommandText="UPDATE library_job_items SET downloaded=$n,reserved=$r WHERE job_id=$id AND ordinal=$i";cmd.Parameters.AddWithValue("$n",cumulative);cmd.Parameters.AddWithValue("$r",hold);cmd.ExecuteNonQuery();tx.Commit();
        }
    }
    internal void RecoverLibraryJobs()
    {
        Initialize();lock(gate)
        {
            using var db=Open(true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="""
                UPDATE library_job_items SET uncertain=uncertain+reserved,reserved=0,
                    state=CASE WHEN state IN ('Preparing','Downloading','Verifying') THEN 'Interrupted' ELSE state END;
                UPDATE library_jobs SET state='Interrupted',error='Application stopped during acquisition. Review and Resume; unresolved read reservations remain conservatively charged.'
                    WHERE state IN ('Preparing','Downloading','Verifying');
                """;cmd.ExecuteNonQuery();tx.Commit();
        }
    }
}
