using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class ScienceStore
{
    bool TerrainJobsExist()
    {
        if(!File.Exists(DatabasePath))return false;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT 1 FROM sqlite_master WHERE name='terrain_elevation_jobs'";return cmd.ExecuteScalar() is not null;
    }
    void EnsureTerrainJobs()
    {
        Initialize();lock(gate)
        {
            if(TerrainJobsExist())return;
            var backups=Path.Combine(DirectoryPath,"Metadata","Backups");Directory.CreateDirectory(backups);_=DataBackup.Create(DatabasePath,backups,true);
            using var db=Open(true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="""
                CREATE TABLE terrain_elevation_jobs(id TEXT PRIMARY KEY,mission_id TEXT NOT NULL,revision INTEGER NOT NULL,state TEXT NOT NULL,payload_json TEXT NOT NULL);
                CREATE INDEX terrain_jobs_mission ON terrain_elevation_jobs(mission_id,id);
                CREATE TABLE terrain_elevation_tiles(job_id TEXT NOT NULL REFERENCES terrain_elevation_jobs(id) ON DELETE CASCADE,
                    source_id TEXT NOT NULL,col INTEGER NOT NULL,row INTEGER NOT NULL,payload_json TEXT NOT NULL,PRIMARY KEY(job_id,source_id,row,col));
                CREATE TABLE terrain_elevation_receipts(job_id TEXT NOT NULL REFERENCES terrain_elevation_jobs(id) ON DELETE CASCADE,
                    request_id TEXT NOT NULL,input_sha TEXT NOT NULL,result_json TEXT NOT NULL,PRIMARY KEY(job_id,request_id));
                CREATE INDEX IF NOT EXISTS artifacts_terrain_input ON scientific_artifacts(mission_id,json_extract(payload_json,'$.kind'),json_extract(payload_json,'$.inputIdentity'));
                """;cmd.ExecuteNonQuery();tx.Commit();
        }
    }
    internal ScientificArtifact? TerrainTileArtifact(Guid mission,string identity)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM scientific_artifacts WHERE mission_id=$m AND json_extract(payload_json,'$.kind')='terrain-elevation-tile' AND json_extract(payload_json,'$.inputIdentity')=$i ORDER BY id LIMIT 1";
        cmd.Parameters.AddWithValue("$m",mission.ToString());cmd.Parameters.AddWithValue("$i",identity);return cmd.ExecuteScalar() is string json?ReadCatalog<ScientificArtifact>(json):null;
    }
    public TerrainElevationJob TerrainJob(Guid id)
    {
        if(!TerrainJobsExist())throw new ArgumentException("Terrain processing job not found.");
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM terrain_elevation_jobs WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());
        var job=ReadCatalog<TerrainElevationJob>(cmd.ExecuteScalar() as string??throw new ArgumentException("Terrain processing job not found."));
        if(job.Id!=id||job.LibraryId!=LibraryId)throw new IOException("Terrain job/library identity mismatch.");return CurrentTerrainJob(job);
    }
    public TerrainElevationJob[] TerrainJobs(Guid? mission=null,int offset=0)
    {
        if(!TerrainJobsExist())return [];
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM terrain_elevation_jobs WHERE ($m='' OR mission_id=$m) ORDER BY rowid DESC LIMIT 20 OFFSET $offset";cmd.Parameters.AddWithValue("$m",mission?.ToString()??"");cmd.Parameters.AddWithValue("$offset",Math.Max(0,offset));
        using var rows=cmd.ExecuteReader();var items=new List<TerrainElevationJob>();while(rows.Read())items.Add(CurrentTerrainJob(ReadCatalog<TerrainElevationJob>(rows.GetString(0))));return items.ToArray();
    }
    TerrainElevationJob CurrentTerrainJob(TerrainElevationJob job)=>job.Sources.Any(s=>Source(s.Original.Id)?.ManifestSha256!=s.Original.ManifestSha256)
        ?job with{State="Invalidated",Message="A required original source was deleted or changed. Dependent values are unavailable; review sources again."}:job;
    public TerrainElevationJob SaveTerrainReview(TerrainElevationJob job)
    {
        using var lease=BeginAnalysis();EnsureTerrainJobs();lock(gate)
        {
            using var db=Open(true);using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM terrain_elevation_jobs WHERE state NOT IN ('Ready','PartiallyComplete','Cancelled','Invalidated')";
            if(Convert.ToInt64(cmd.ExecuteScalar())>=32)throw new InvalidOperationException("Finish/cancel existing terrain work before adding more than 32 active reviews/jobs.");
            var json=JsonSerializer.Serialize(job,WireJson.Options);if(System.Text.Encoding.UTF8.GetByteCount(json)>16777216)throw new ArgumentException("Terrain review metadata exceeds its bound.");
            Management?.RequireScienceCapacity(json.Length*4L+65536);
            cmd.CommandText="INSERT INTO terrain_elevation_jobs VALUES($id,$m,$r,$s,$j)";cmd.Parameters.AddWithValue("$id",job.Id.ToString());cmd.Parameters.AddWithValue("$m",job.MissionId.ToString());cmd.Parameters.AddWithValue("$r",job.Revision);cmd.Parameters.AddWithValue("$s",job.State);cmd.Parameters.AddWithValue("$j",json);cmd.ExecuteNonQuery();
        }
        return job;
    }
    public TerrainElevationJob ChangeTerrainJob(Guid id,Func<TerrainElevationJob,TerrainElevationJob> change)
    {
        lock(gate)
        {
            var old=TerrainJob(id);var next=change(old) with{Revision=old.Revision+1,UpdatedUtc=DateTimeOffset.UtcNow};
            using var db=Open(true);using var cmd=db.CreateCommand();cmd.CommandText="UPDATE terrain_elevation_jobs SET revision=$r,state=$s,payload_json=$j WHERE id=$id AND revision=$old";
            cmd.Parameters.AddWithValue("$r",next.Revision);cmd.Parameters.AddWithValue("$s",next.State);cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(next,WireJson.Options));cmd.Parameters.AddWithValue("$id",id.ToString());cmd.Parameters.AddWithValue("$old",old.Revision);
            if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Terrain job revision changed. Refresh before retrying.");return next;
        }
    }
    public TerrainElevationJob ControlTerrainJob(Guid id,TerrainElevationControl request)
    {
        if(request.RequestId==Guid.Empty||request.ExpectedRevision<1||request.Action is not("start" or "pause" or "resume" or "retry" or "cancel"))throw new ArgumentException("Use an identified, revision-bound terrain command.");
        lock(gate)
        {
            _=TerrainJob(id);
            var hash=ScientificComposites.Identity(request);using var db=Open(true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="SELECT input_sha,result_json FROM terrain_elevation_receipts WHERE job_id=$id AND request_id=$request";cmd.Parameters.AddWithValue("$id",id.ToString());cmd.Parameters.AddWithValue("$request",request.RequestId.ToString());
            using(var row=cmd.ExecuteReader())if(row.Read())
            {if(row.GetString(0)!=hash)throw new InvalidOperationException("Terrain command ID was reused for different contents.");return ReadCatalog<TerrainElevationJob>(row.GetString(1));}
            var old=TerrainJob(id);if(old.Revision!=request.ExpectedRevision)throw new InvalidOperationException("Terrain job revision is stale. Refresh before issuing another command.");
            var state=request.Action switch
            {
                "start" when old.State=="Reviewed"&&old.Sources.Length>0=>"Queued",
                "resume" or "retry" when old.Sources.Length>0&&old.State is "Paused" or "Interrupted" or "Failed" or "Cancelled"=>"Queued",
                "pause" when old.State is "Queued" or "Running"=>old.State=="Running"?"Pausing":"Paused",
                "cancel" when old.State is not("Ready" or "PartiallyComplete" or "Invalidated")=>"Cancelled",
                _=>throw new InvalidOperationException("That command is not available in the current terrain processing state.")
            };
            var next=old with{State=state,Revision=old.Revision+1,UpdatedUtc=DateTimeOffset.UtcNow,Message=state=="Queued"?"Explicitly queued; only retained sources will be read.":"Completed derived tiles remain visible; temporary work is discarded. No source download was authorized."};
            cmd.CommandText="UPDATE terrain_elevation_jobs SET revision=$r,state=$s,payload_json=$j WHERE id=$id AND revision=$old";cmd.Parameters.AddWithValue("$r",next.Revision);cmd.Parameters.AddWithValue("$s",state);cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(next,WireJson.Options));cmd.Parameters.AddWithValue("$old",old.Revision);if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Terrain job changed.");
            cmd.CommandText="INSERT INTO terrain_elevation_receipts VALUES($id,$request,$hash,$j)";cmd.Parameters.AddWithValue("$hash",hash);cmd.ExecuteNonQuery();tx.Commit();return next;
        }
    }
    internal void SaveTerrainCheckpoint(Guid job,TerrainElevationCheckpoint tile)
    {
        lock(gate)
        {
            var old=TerrainJob(job);using var db=Open(true);using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT OR IGNORE INTO terrain_elevation_tiles VALUES($id,$s,$c,$r,$j)";cmd.Parameters.AddWithValue("$id",job.ToString());cmd.Parameters.AddWithValue("$s",tile.SourceId.ToString());cmd.Parameters.AddWithValue("$c",tile.TileColumn);cmd.Parameters.AddWithValue("$r",tile.TileRow);cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(tile,WireJson.Options));
            if(cmd.ExecuteNonQuery()==0)return;
            // Progress does not change the command/state revision. Otherwise a long
            // tile stream would continuously invalidate an operator's Pause click.
            var next=old with{UpdatedUtc=DateTimeOffset.UtcNow,CompletedTiles=old.CompletedTiles+1,ValidCells=old.ValidCells+tile.ValidCells,MissingCells=old.MissingCells+tile.MissingCells,OutputBytes=old.OutputBytes+tile.Bytes};
            cmd.Parameters.Clear();cmd.CommandText="UPDATE terrain_elevation_jobs SET revision=$r,payload_json=$j WHERE id=$id AND revision=$old";
            cmd.Parameters.AddWithValue("$id",job.ToString());cmd.Parameters.AddWithValue("$r",next.Revision);cmd.Parameters.AddWithValue("$old",old.Revision);cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(next,WireJson.Options));
            if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Terrain checkpoint revision changed.");tx.Commit();
        }
    }
    public IEnumerable<TerrainElevationCheckpoint> TerrainCheckpoints(Guid id)
    {
        if(!TerrainJobsExist())yield break;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM terrain_elevation_tiles WHERE job_id=$id ORDER BY source_id,row,col";cmd.Parameters.AddWithValue("$id",id.ToString());using var rows=cmd.ExecuteReader();while(rows.Read())yield return ReadCatalog<TerrainElevationCheckpoint>(rows.GetString(0));
    }
    internal TerrainElevationCheckpoint? TerrainCheckpoint(Guid id,Guid source,int col,int row)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM terrain_elevation_tiles WHERE job_id=$id AND source_id=$s AND col=$c AND row=$r";cmd.Parameters.AddWithValue("$id",id.ToString());cmd.Parameters.AddWithValue("$s",source.ToString());cmd.Parameters.AddWithValue("$c",col);cmd.Parameters.AddWithValue("$r",row);return cmd.ExecuteScalar() is string json?ReadCatalog<TerrainElevationCheckpoint>(json):null;
    }
    public void RecoverTerrainJobs()
    {
        if(!TerrainJobsExist())return;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id FROM terrain_elevation_jobs WHERE state IN ('Running','Queued','Pausing')";var ids=new List<Guid>();using(var rows=cmd.ExecuteReader())while(rows.Read())ids.Add(Guid.Parse(rows.GetString(0)));
        foreach(var id in ids)ChangeTerrainJob(id,j=>j with{State="Interrupted",Message="Processing was interrupted. Explicit resume revalidates geometry, originals and completed tile checksums; no network work resumes automatically."});
    }
}
