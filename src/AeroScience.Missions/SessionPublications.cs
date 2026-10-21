using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    public bool HasTelemetryTime(Guid mission,DateTimeOffset utc)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT 1 FROM telemetry WHERE mission_id=$id AND timestamp_utc=$utc LIMIT 1";
        cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$utc",Stamp(utc));return cmd.ExecuteScalar() is not null;
    }
    public ScienceCollectionSession? CollectionSession(Guid mission,Guid id)
    {using var db=Open();using var tx=db.BeginTransaction();return EvidenceSession(db,tx,mission,id);}
    public long CollectionSessionCount(Guid mission)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM science_sessions WHERE mission_id=$id";
        cmd.Parameters.AddWithValue("$id",mission.ToString());return Convert.ToInt64(cmd.ExecuteScalar());
    }
    public int ReconcileSessionPublications(ScienceStore science,int offset=0,int limit=32)
    {
        limit=Math.Clamp(limit,1,32);using var db=Open();using var query=db.CreateCommand();
        query.CommandText="SELECT payload_json FROM science_intents WHERE state IN ('Pending','Cancelled') ORDER BY rowid LIMIT $limit OFFSET $offset";
        query.Parameters.AddWithValue("$limit",limit);query.Parameters.AddWithValue("$offset",Math.Max(0,offset));
        var intents=new List<ScienceAcquisitionIntent>();using(var reader=query.ExecuteReader())while(reader.Read())intents.Add(JsonSerializer.Deserialize<ScienceAcquisitionIntent>(reader.GetString(0),WireJson.Options)!);
        foreach(var intent in intents)
        {
            if(science.SessionPublication(intent) is not {} publication)continue;
            // Only reconcile already committed verified originals; no retrieval, sampling or new rows.
            var session=CollectionSession(intent.MissionId,intent.SessionId)??throw new IOException("Publication session is missing.");
            var packet=CollectionInput(intent.MissionId,intent.InputReceiptId)??throw new IOException("Publication receipt is missing.");
            if(DescribeCollectionInput(packet).ObservedUtc!=intent.ExposureUtc||intent.ExposureUtc<session.StartedUtc)
                throw new IOException("Publication does not match its original eligible exposure.");
            using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="UPDATE science_intents SET state=$state,product_id=$product,product_sha256=$hash WHERE id=$id AND session_id=$session AND state IN ('Pending','Cancelled')";
            cmd.Parameters.AddWithValue("$state",publication.Reconstructed?"Reconstructed":"Published");cmd.Parameters.AddWithValue("$product",intent.Id.ToString());
            cmd.Parameters.AddWithValue("$hash",publication.Hash);cmd.Parameters.AddWithValue("$id",intent.Id.ToString());cmd.Parameters.AddWithValue("$session",intent.SessionId.ToString());
            if(cmd.ExecuteNonQuery()>0)Event(db,tx,intent.MissionId,DateTimeOffset.UtcNow,"session-publication-reconciled","Verified previously committed acquisition "+intent.Id+" retains originating session "+intent.SessionId+". No live observation was backfilled.");
            tx.Commit();
        }
        return intents.Count;
    }
}

public sealed partial class ScienceStore
{
    // A bounded original-scalar lookup for live evidence, not a cube/archive scan.
    public ScienceObservation[] IntentObservations(ScienceAcquisitionIntent intent)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT payload_json FROM observations WHERE mission_id=$mission AND timestamp_utc=$utc AND json_extract(payload_json,'$.intentId')=$id ORDER BY measurement_id LIMIT 16";
        cmd.Parameters.AddWithValue("$mission",intent.MissionId.ToString());cmd.Parameters.AddWithValue("$utc",intent.ExposureUtc.ToString("O"));cmd.Parameters.AddWithValue("$id",intent.Id.ToString());
        var rows=new List<ScienceObservation>();using var reader=cmd.ExecuteReader();while(reader.Read())rows.Add(ReadCatalog<ScienceObservation>(reader.GetString(0)));
        if(rows.Any(r=>r.SessionId!=intent.SessionId||r.MissionId!=intent.MissionId||r.InstrumentId!=intent.InstrumentId||r.IntentId!=intent.Id))throw new IOException("Live original/session identity mismatch.");
        return rows.Where(r=>r.Calibration is null).GroupBy(r=>r.MeasurementId).Select(g=>g.First()).ToArray();
    }
    public static string ObservationBatchHash(IEnumerable<ScienceObservation> values) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(values.OrderBy(v=>v.MeasurementId,StringComparer.Ordinal).Select(v=>v with{Id=0}).ToArray(),WireJson.Options)));
    public (string Hash,bool Reconstructed)? SessionPublication(ScienceAcquisitionIntent intent)
    {
        if(!File.Exists(DatabasePath))return null;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM remote_products WHERE id=$id";cmd.Parameters.AddWithValue("$id",intent.Id.ToString());
        if(cmd.ExecuteScalar() is string)
        {
            var product=ReadProduct(intent.Id).Product;
            if(product.SessionId!=intent.SessionId||product.IntentId!=intent.Id||product.MissionId!=intent.MissionId||product.InstrumentId!=intent.InstrumentId||product.FlightPacketUtc!=intent.ExposureUtc)
                throw new IOException("Scientific publication/session identity mismatch.");
            return(product.Sha256,product.Provenance.StartsWith("Post-flight derived reconstruction",StringComparison.Ordinal));
        }
        cmd.CommandText="SELECT payload_json FROM observations WHERE mission_id=$mission AND timestamp_utc=$utc AND json_extract(payload_json,'$.intentId')=$id ORDER BY measurement_id LIMIT 16";
        cmd.Parameters.AddWithValue("$mission",intent.MissionId.ToString());cmd.Parameters.AddWithValue("$utc",intent.ExposureUtc.ToString("O"));
        var rows=new List<ScienceObservation>();using(var read=cmd.ExecuteReader())while(read.Read())rows.Add(ReadCatalog<ScienceObservation>(read.GetString(0)));
        if(rows.Count==0)return null;
        if(rows.Any(r=>r.SessionId!=intent.SessionId||r.IntentId!=intent.Id||r.MissionId!=intent.MissionId||r.InstrumentId!=intent.InstrumentId||r.TimestampUtc!=intent.ExposureUtc))
            throw new IOException("Scientific observation/session identity mismatch.");
        return(ObservationBatchHash(rows),false);
    }
}

public sealed record SessionScienceCounts(Guid SessionId,long OriginalAtmosphericValues,long DerivedAtmosphericValues,long RemoteProducts,
    string Limitation="Counts of retained values/products, not scientific accuracy, valid survey coverage or a mission grade.");
public sealed partial class ScienceStore
{
    public SessionScienceCounts[] SessionCounts(Guid mission)
    {
        if(AvailabilityError() is {} error)throw new IOException(error);
        if(!File.Exists(DatabasePath))return [];
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT json_extract(payload_json,'$.sessionId'),SUM(CASE WHEN json_extract(payload_json,'$.provenance')='derived' THEN 0 ELSE 1 END),SUM(CASE WHEN json_extract(payload_json,'$.provenance')='derived' THEN 1 ELSE 0 END),0 FROM observations WHERE mission_id=$mission AND json_extract(payload_json,'$.sessionId') IS NOT NULL GROUP BY 1 UNION ALL SELECT json_extract(payload_json,'$.sessionId'),0,0,COUNT(*) FROM remote_products WHERE mission_id=$mission AND json_extract(payload_json,'$.sessionId') IS NOT NULL GROUP BY 1";
        cmd.Parameters.AddWithValue("$mission",mission.ToString());using var reader=cmd.ExecuteReader();var rows=new List<SessionScienceCounts>();
        while(reader.Read())rows.Add(new(Guid.Parse(reader.GetString(0)),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3)));
        return rows.GroupBy(r=>r.SessionId).Select(g=>new SessionScienceCounts(g.Key,g.Sum(r=>r.OriginalAtmosphericValues),g.Sum(r=>r.DerivedAtmosphericValues),g.Sum(r=>r.RemoteProducts))).ToArray();
    }
}
