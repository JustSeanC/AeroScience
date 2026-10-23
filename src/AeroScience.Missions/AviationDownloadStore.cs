using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class AviationMetadataStore
{
    // WAL-aware backup and transactional migration. The retained schema-1 backup is never pruned.
    private void MigrateJobs()
    {
        StorageManager.ValidateLocalPath(directory);
        if (new FileInfo(database).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked aviation database is unsupported.");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version == SchemaVersion) return;
        if (version is not (1 or 2 or 3 or 4)) throw new NotSupportedException("Unsupported aviation database schema. Preserve the original database.");
        cmd.CommandText = "SELECT id FROM identity";
        if (cmd.ExecuteScalar() as string != identity.Id.ToString()) throw new IOException("Aviation store identity mismatch.");
        var folder = Path.Combine(directory, "Backups"); StorageManager.ValidateLocalPath(folder); Directory.CreateDirectory(folder);
        var backup = Path.Combine(folder, $"aviation-schema{version}-{Guid.NewGuid():N}.sqlite");
        using (var copy = new SqliteConnection($"Data Source={backup};Pooling=False"))
        {
            copy.Open(); db.BackupDatabase(copy); using var check = copy.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
            if (check.ExecuteScalar() as string != "ok") throw new IOException("Aviation migration backup failed integrity verification.");
            check.CommandText = "SELECT id FROM identity";
            if (check.ExecuteScalar() as string != identity.Id.ToString()) throw new IOException("Aviation backup identity failed.");
        }
        using var tx = db.BeginTransaction(); cmd.Transaction = tx;
        cmd.CommandText = (version == 1 ? "CREATE TABLE download_jobs(id TEXT PRIMARY KEY,revision INTEGER NOT NULL,state TEXT NOT NULL,payload TEXT NOT NULL);" : "") +
            (version < 3 ? "CREATE TABLE vector_datasets(id TEXT PRIMARY KEY,job_id TEXT NOT NULL,payload TEXT NOT NULL);" +
            "CREATE TABLE vector_features(dataset_id TEXT NOT NULL,id TEXT NOT NULL,west REAL NOT NULL,south REAL NOT NULL,east REAL NOT NULL,north REAL NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(dataset_id,id));" : "") +
            (version < 4 ? "CREATE TABLE airspace_features(dataset_id TEXT NOT NULL,id TEXT NOT NULL,kind TEXT NOT NULL,west REAL NOT NULL,south REAL NOT NULL,east REAL NOT NULL,north REAL NOT NULL,vertices INTEGER NOT NULL,tolerance REAL NOT NULL,geometry BLOB NOT NULL,display BLOB,payload TEXT NOT NULL,PRIMARY KEY(dataset_id,id));" : "") +
            // A verified original may have immutable derived interpretations from different
            // parser versions. Preserve every old row and geometry; identity remains the key.
            (version >= 3 ? "CREATE TABLE vector_datasets_v5(id TEXT PRIMARY KEY,job_id TEXT NOT NULL,payload TEXT NOT NULL);" +
            "INSERT INTO vector_datasets_v5 SELECT id,job_id,payload FROM vector_datasets;" +
            "DROP TABLE vector_datasets; ALTER TABLE vector_datasets_v5 RENAME TO vector_datasets;" : "") +
            "CREATE INDEX vector_datasets_by_job ON vector_datasets(job_id); PRAGMA user_version=5;";
        cmd.ExecuteNonQuery(); tx.Commit();
    }
    public AviationDownloadJob[] Jobs(int offset = 0)
    {
        if (offset < 0 || offset > 10000 || offset % 50 != 0) throw new ArgumentException("Use 50-job pages.");
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM download_jobs ORDER BY rowid DESC LIMIT 50 OFFSET $offset"; cmd.Parameters.AddWithValue("$offset", offset);
            using var rows = cmd.ExecuteReader(); var list = new List<AviationDownloadJob>();
            while (rows.Read()) list.Add(ParseJob(rows.GetString(0))); return list.ToArray();
        }
    }
    public AviationDownloadJob Job(Guid id)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM download_jobs WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString());
            return cmd.ExecuteScalar() is string json ? ParseJob(json) : throw new KeyNotFoundException("Aviation job not found.");
        }
    }
    public (AviationDownloadJob Job, AviationRetainedFile File)[] ChartFiles(int offset = 0)
    {
        if(offset<0||offset>10000||offset%50!=0)throw new ArgumentException("Use 50-sheet pages.");
        lock(gate)
        {
            using var db=Open();using var cmd=db.CreateCommand();
            cmd.CommandText="""
                SELECT j.payload,json_extract(f.value,'$.name') FROM download_jobs j,json_each(j.payload,'$.files') f
                WHERE j.state='Completed' AND (lower(json_extract(f.value,'$.name')) LIKE '%.tif' OR lower(json_extract(f.value,'$.name')) LIKE '%.tiff')
                ORDER BY j.rowid DESC, json_extract(f.value,'$.name') LIMIT 50 OFFSET $offset
                """;
            cmd.Parameters.AddWithValue("$offset",offset);using var rows=cmd.ExecuteReader();var result=new List<(AviationDownloadJob,AviationRetainedFile)>();
            while(rows.Read()){var job=ParseJob(rows.GetString(0));var name=rows.GetString(1);result.Add((job,job.Files!.Single(f=>f.Name==name)));}
            return result.ToArray();
        }
    }
    public AviationDownloadJob[] RetainedJobs(string itemId)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM download_jobs WHERE state='Completed' AND json_extract(payload,'$.preview.item.id')=$item LIMIT 16";
            cmd.Parameters.AddWithValue("$item", itemId); using var rows = cmd.ExecuteReader(); var list = new List<AviationDownloadJob>();
            while (rows.Read()) list.Add(ParseJob(rows.GetString(0))); return list.ToArray();
        }
    }
    public AviationDownloadJob SaveJob(AviationDownloadJob job, int? expectedRevision = null)
    {
        AviationDownloads.Validate(job); lock (gate)
        {
            using var db = Open(true); using var cmd = db.CreateCommand();
            cmd.CommandText = expectedRevision is null ? "INSERT INTO download_jobs VALUES($id,$rev,$state,$json)" :
                "UPDATE download_jobs SET revision=$rev,state=$state,payload=$json WHERE id=$id AND revision=$expected";
            cmd.Parameters.AddWithValue("$id", job.Id.ToString()); cmd.Parameters.AddWithValue("$rev", job.Revision);
            cmd.Parameters.AddWithValue("$state", job.State); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(job, WireJson.Options));
            if (expectedRevision is {} rev) cmd.Parameters.AddWithValue("$expected", rev);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Aviation job changed. Refresh before issuing another command.");
            return job;
        }
    }
    public AviationDownloadJob[] PendingJobs()
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM download_jobs WHERE state IN ('Queued','Preparing','Downloading','Verifying','Waiting for Flight Priority','Waiting for Network','Interrupted') LIMIT 257";
            using var rows = cmd.ExecuteReader(); var list = new List<AviationDownloadJob>(); while (rows.Read()) list.Add(ParseJob(rows.GetString(0)));
            if (list.Count > 256) throw new IOException("Aviation pending-job bound exceeded. Preserve this store for recovery."); return list.ToArray();
        }
    }
    private static AviationDownloadJob ParseJob(string json)
    {
        if (json.Length > 65536) throw new IOException("Oversized aviation job.");
        var job = JsonSerializer.Deserialize<AviationDownloadJob>(json, WireJson.Options) ?? throw new IOException("Invalid aviation job.");
        AviationDownloads.Validate(job); return job;
    }
}
