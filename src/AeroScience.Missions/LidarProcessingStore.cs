using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class ScienceStore
{
    bool LidarJobsExist()
    {
        if(!File.Exists(DatabasePath))return false;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT 1 FROM sqlite_master WHERE name='lidar_processing_jobs'";return cmd.ExecuteScalar() is not null;
    }
    void EnsureLidarJobs()
    {
        Initialize();lock(gate)
        {
            if(LidarJobsExist())return;
            var backups=Path.Combine(DirectoryPath,"Metadata","Backups");Directory.CreateDirectory(backups);_=DataBackup.Create(DatabasePath,backups,true);
            using var db=Open(true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="""
                CREATE TABLE lidar_processing_jobs(id TEXT PRIMARY KEY,mission_id TEXT NOT NULL,revision INTEGER NOT NULL,state TEXT NOT NULL,payload_json TEXT NOT NULL);
                CREATE INDEX lidar_jobs_mission ON lidar_processing_jobs(mission_id,id);
                CREATE TABLE lidar_point_tiles(job_id TEXT NOT NULL REFERENCES lidar_processing_jobs(id) ON DELETE CASCADE,
                    source_id TEXT NOT NULL,first_record INTEGER NOT NULL,payload_json TEXT NOT NULL,PRIMARY KEY(job_id,source_id,first_record));
                CREATE TABLE lidar_processing_receipts(job_id TEXT NOT NULL REFERENCES lidar_processing_jobs(id) ON DELETE CASCADE,
                    request_id TEXT NOT NULL,input_sha TEXT NOT NULL,result_json TEXT NOT NULL,PRIMARY KEY(job_id,request_id));
                CREATE TABLE lidar_create_receipts(request_id TEXT PRIMARY KEY,input_sha TEXT NOT NULL,job_id TEXT NOT NULL REFERENCES lidar_processing_jobs(id) ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS artifacts_terrain_input ON scientific_artifacts(mission_id,json_extract(payload_json,'$.kind'),json_extract(payload_json,'$.inputIdentity'));
                """;cmd.ExecuteNonQuery();tx.Commit();
        }
    }
    internal ScientificArtifact? LidarTileArtifact(Guid mission,string identity)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM scientific_artifacts WHERE mission_id=$m AND json_extract(payload_json,'$.kind')='lidar-point-tile' AND json_extract(payload_json,'$.inputIdentity')=$i ORDER BY id LIMIT 1";
        cmd.Parameters.AddWithValue("$m",mission.ToString());cmd.Parameters.AddWithValue("$i",identity);return cmd.ExecuteScalar() is string json?ReadCatalog<ScientificArtifact>(json):null;
    }
    public LidarProcessingJob LidarJob(Guid id)
    {
        if(!LidarJobsExist())throw new ArgumentException("Lidar processing job not found.");
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM lidar_processing_jobs WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());
        var job=ReadCatalog<LidarProcessingJob>(cmd.ExecuteScalar() as string??throw new ArgumentException("Lidar processing job not found."));
        if(job.Id!=id||job.LibraryId!=LibraryId)throw new IOException("Lidar job/library identity mismatch.");return CurrentLidarJob(job);
    }
    public LidarProcessingJob[] LidarJobs(Guid? mission=null,int offset=0)
    {
        if(!LidarJobsExist())return [];
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM lidar_processing_jobs WHERE ($m='' OR mission_id=$m) ORDER BY rowid DESC LIMIT 20 OFFSET $offset";cmd.Parameters.AddWithValue("$m",mission?.ToString()??"");cmd.Parameters.AddWithValue("$offset",Math.Max(0,offset));
        using var rows=cmd.ExecuteReader();var items=new List<LidarProcessingJob>();while(rows.Read())items.Add(CurrentLidarJob(ReadCatalog<LidarProcessingJob>(rows.GetString(0))));return items.ToArray();
    }
    LidarProcessingJob CurrentLidarJob(LidarProcessingJob job)=>job.Sources.Any(s=>Source(s.SourceId)?.ManifestSha256!=s.ManifestSha256)
        ?job with{State="Invalidated",Message="A required original source was deleted or changed. Dependent values are unavailable; review sources again."}:job;
    public LidarProcessingJob SaveLidarReview(LidarProcessingJob job,Guid requestId)
    {
        using var lease=BeginAnalysis();EnsureLidarJobs();lock(gate)
        {
            if(requestId==Guid.Empty)throw new ArgumentException("An identified processing review request is required.");
            using var db=Open(true);using var cmd=db.CreateCommand();cmd.CommandText="SELECT input_sha,job_id FROM lidar_create_receipts WHERE request_id=$request";cmd.Parameters.AddWithValue("$request",requestId.ToString());
            using(var read=cmd.ExecuteReader())if(read.Read()){if(read.GetString(0)!=job.Recipe)throw new InvalidOperationException("LiDAR review request ID was reused for changed contents.");return LidarJob(Guid.Parse(read.GetString(1)));}
            cmd.Parameters.Clear();cmd.CommandText="SELECT COUNT(*) FROM lidar_processing_jobs WHERE state NOT IN ('Ready','PartiallyComplete','Cancelled','Invalidated')";
            if(Convert.ToInt64(cmd.ExecuteScalar())>=32)throw new InvalidOperationException("Finish/cancel existing LiDAR work before adding more than 32 active reviews/jobs.");
            var json=JsonSerializer.Serialize(job,WireJson.Options);if(System.Text.Encoding.UTF8.GetByteCount(json)>16777216)throw new ArgumentException("Lidar review metadata exceeds its bound.");
            Management?.RequireScienceCapacity(json.Length*4L+65536);
            using var tx=db.BeginTransaction();cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO lidar_processing_jobs VALUES($id,$m,$r,$s,$j)";cmd.Parameters.AddWithValue("$id",job.Id.ToString());cmd.Parameters.AddWithValue("$m",job.MissionId.ToString());cmd.Parameters.AddWithValue("$r",job.Revision);cmd.Parameters.AddWithValue("$s",job.State);cmd.Parameters.AddWithValue("$j",json);cmd.ExecuteNonQuery();
            cmd.CommandText="INSERT INTO lidar_create_receipts VALUES($request,$sha,$id)";cmd.Parameters.AddWithValue("$request",requestId.ToString());cmd.Parameters.AddWithValue("$sha",job.Recipe);cmd.ExecuteNonQuery();tx.Commit();
        }
        return job;
    }
    public LidarProcessingJob ChangeLidarJob(Guid id,Func<LidarProcessingJob,LidarProcessingJob> change)
    {
        lock(gate)
        {
            var old=LidarJob(id);var next=change(old) with{Revision=old.Revision+1,UpdatedUtc=DateTimeOffset.UtcNow};
            using var db=Open(true);using var cmd=db.CreateCommand();cmd.CommandText="UPDATE lidar_processing_jobs SET revision=$r,state=$s,payload_json=$j WHERE id=$id AND revision=$old";
            cmd.Parameters.AddWithValue("$r",next.Revision);cmd.Parameters.AddWithValue("$s",next.State);cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(next,WireJson.Options));cmd.Parameters.AddWithValue("$id",id.ToString());cmd.Parameters.AddWithValue("$old",old.Revision);
            if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lidar job revision changed. Refresh before retrying.");return next;
        }
    }
    public LidarProcessingJob ControlLidarJob(Guid id,TerrainElevationControl request)
    {
        if(request.RequestId==Guid.Empty||request.ExpectedRevision<1||request.Action is not("start" or "pause" or "resume" or "retry" or "cancel"))throw new ArgumentException("Use an identified, revision-bound LiDAR command.");
        lock(gate)
        {
            _=LidarJob(id);
            var hash=ScientificComposites.Identity(request);using var db=Open(true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="SELECT input_sha,result_json FROM lidar_processing_receipts WHERE job_id=$id AND request_id=$request";cmd.Parameters.AddWithValue("$id",id.ToString());cmd.Parameters.AddWithValue("$request",request.RequestId.ToString());
            using(var row=cmd.ExecuteReader())if(row.Read())
            {if(row.GetString(0)!=hash)throw new InvalidOperationException("Lidar command ID was reused for different contents.");return ReadCatalog<LidarProcessingJob>(row.GetString(1));}
            var old=LidarJob(id);if(old.Revision!=request.ExpectedRevision)throw new InvalidOperationException("Lidar job revision is stale. Refresh before issuing another command.");
            using(var count=db.CreateCommand()){count.Transaction=tx;count.CommandText="SELECT COUNT(*) FROM lidar_processing_receipts WHERE job_id=$id";count.Parameters.AddWithValue("$id",id.ToString());if(Convert.ToInt64(count.ExecuteScalar())>=1024)throw new InvalidOperationException("This bounded job has reached its command-receipt limit. Preserve it and create a new explicit review; retries of existing identities remain available.");}
            var state=request.Action switch
            {
                "start" when old.State=="Reviewed"&&old.Sources.Length>0=>"Queued",
                "resume" or "retry" when old.Sources.Length>0&&old.State is "Paused" or "Interrupted" or "Failed" or "Cancelled"=>"Queued",
                "pause" when old.State is "Queued" or "Running"=>old.State=="Running"?"Pausing":"Paused",
                "cancel" when old.State is not("Ready" or "PartiallyComplete" or "Invalidated")=>"Cancelled",
                _=>throw new InvalidOperationException("That command is not available in the current historical point processing state.")
            };
            var next=old with{State=state,Revision=old.Revision+1,UpdatedUtc=DateTimeOffset.UtcNow,Message=state=="Queued"?"Explicitly queued; only retained sources will be read.":"Completed derived tiles remain visible; temporary work is discarded. No source download was authorized."};
            cmd.CommandText="UPDATE lidar_processing_jobs SET revision=$r,state=$s,payload_json=$j WHERE id=$id AND revision=$old";cmd.Parameters.AddWithValue("$r",next.Revision);cmd.Parameters.AddWithValue("$s",state);cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(next,WireJson.Options));cmd.Parameters.AddWithValue("$old",old.Revision);if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lidar job changed.");
            cmd.CommandText="INSERT INTO lidar_processing_receipts VALUES($id,$request,$hash,$j)";cmd.Parameters.AddWithValue("$hash",hash);cmd.ExecuteNonQuery();tx.Commit();return next;
        }
    }
    internal void SaveLidarCheckpoint(Guid job,LidarPointCheckpoint tile)
    {
        lock(gate)
        {
            var old=LidarJob(job);using var db=Open(true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT OR IGNORE INTO lidar_point_tiles VALUES($id,$s,$r,$j)";cmd.Parameters.AddWithValue("$id",job.ToString());cmd.Parameters.AddWithValue("$s",tile.SourceId.ToString());cmd.Parameters.AddWithValue("$r",tile.FirstRecord);cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(tile,WireJson.Options));
            if(cmd.ExecuteNonQuery()==0)return;
            // Progress does not change the command/state revision. Otherwise a long
            // tile stream would continuously invalidate an operator's Pause click.
            var next=old with{UpdatedUtc=DateTimeOffset.UtcNow,CompletedTiles=old.CompletedTiles+1,RetainedPoints=old.RetainedPoints+tile.RetainedPoints,ScannedPoints=old.ScannedPoints+tile.RecordsRead,OutputBytes=old.OutputBytes+tile.Bytes};
            cmd.Parameters.Clear();cmd.CommandText="UPDATE lidar_processing_jobs SET revision=$r,payload_json=$j WHERE id=$id AND revision=$old";
            cmd.Parameters.AddWithValue("$id",job.ToString());cmd.Parameters.AddWithValue("$r",next.Revision);cmd.Parameters.AddWithValue("$old",old.Revision);cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(next,WireJson.Options));
            if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lidar checkpoint revision changed.");tx.Commit();
        }
    }
    public IEnumerable<LidarPointCheckpoint> LidarCheckpoints(Guid id)
    {
        if(!LidarJobsExist())yield break;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM lidar_point_tiles WHERE job_id=$id ORDER BY source_id,first_record";cmd.Parameters.AddWithValue("$id",id.ToString());using var rows=cmd.ExecuteReader();while(rows.Read())yield return ReadCatalog<LidarPointCheckpoint>(rows.GetString(0));
    }
    internal LidarPointCheckpoint? LidarCheckpoint(Guid id,Guid source,long firstRecord)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM lidar_point_tiles WHERE job_id=$id AND source_id=$s AND first_record=$r";cmd.Parameters.AddWithValue("$id",id.ToString());cmd.Parameters.AddWithValue("$s",source.ToString());cmd.Parameters.AddWithValue("$r",firstRecord);return cmd.ExecuteScalar() is string json?ReadCatalog<LidarPointCheckpoint>(json):null;
    }
    public void RecoverLidarJobs()
    {
        if(!LidarJobsExist())return;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id FROM lidar_processing_jobs WHERE state IN ('Running','Queued','Pausing')";var ids=new List<Guid>();using(var rows=cmd.ExecuteReader())while(rows.Read())ids.Add(Guid.Parse(rows.GetString(0)));
        foreach(var id in ids)ChangeLidarJob(id,j=>j with{State="Interrupted",Message="Processing was interrupted. Explicit resume revalidates geometry, originals and completed tile checksums; no network work resumes automatically."});
    }
}
