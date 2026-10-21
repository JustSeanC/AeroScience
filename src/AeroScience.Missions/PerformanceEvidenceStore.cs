using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;
public sealed partial class MissionStore
{
    public (long Sequence,TelemetryFrame Frame)[] EvaluationPositions(Guid mission,long after,int limit=1000)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT sequence,payload_json FROM telemetry WHERE mission_id=$id AND sequence>$after ORDER BY sequence LIMIT $limit";
        cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$after",after);cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,1000));using var r=cmd.ExecuteReader();var result=new List<(long,TelemetryFrame)>();while(r.Read())result.Add((r.GetInt64(0),JsonSerializer.Deserialize<TelemetryFrame>(r.GetString(1),WireJson.Options)!));return result.ToArray();
    }
    public MissionPerformanceReport? LatestPerformanceReport(Guid mission)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT message FROM events WHERE mission_id=$id AND kind='mission-performance-report' ORDER BY id DESC LIMIT 1";cmd.Parameters.AddWithValue("$id",mission.ToString());
        if(cmd.ExecuteScalar() is not string json)return null;var report=JsonSerializer.Deserialize<MissionPerformanceReport>(json,WireJson.Options)!;MissionGrading.Verify(report);return report;
    }
    public MissionPerformanceReport PerformanceRevision(Guid mission,int revision)
    {
        if(revision<1)throw new ArgumentException("Choose a positive report revision.");
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT message FROM events WHERE mission_id=$id AND kind='mission-performance-report' AND json_extract(message,'$.revision')=$revision ORDER BY id LIMIT 1";cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$revision",revision);
        var json=cmd.ExecuteScalar() as string??throw new ArgumentException("Report revision not found.");var report=JsonSerializer.Deserialize<MissionPerformanceReport>(json,WireJson.Options)!;MissionGrading.Verify(report);return report;
    }
    public object[] PerformanceRevisions(Guid mission)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json_extract(message,'$.revision'),json_extract(message,'$.sha256'),json_extract(message,'$.reason'),json_extract(message,'$.grade.status') FROM events WHERE mission_id=$id AND kind='mission-performance-report' ORDER BY id DESC LIMIT 100";cmd.Parameters.AddWithValue("$id",mission.ToString());using var r=cmd.ExecuteReader();var result=new List<object>();while(r.Read())result.Add(new{revision=r.GetInt32(0),sha256=r.GetString(1),reason=r.GetString(2),status=r.GetString(3)});return result.ToArray();
    }
    public MissionPerformanceReport[] PerformanceReports(Guid mission)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT message FROM events WHERE mission_id=$id AND kind='mission-performance-report' ORDER BY id";cmd.Parameters.AddWithValue("$id",mission.ToString());
        using var reader=cmd.ExecuteReader();var reports=new List<MissionPerformanceReport>();
        while(reader.Read())
        {
            var report=JsonSerializer.Deserialize<MissionPerformanceReport>(reader.GetString(0),WireJson.Options)!;MissionGrading.Verify(report);
            if(report.Revision!=reports.Count+1||report.PreviousSha256!=reports.LastOrDefault()?.Sha256)throw new InvalidDataException("Performance revision chain verification failed.");reports.Add(report);
        }
        return reports.ToArray();
    }
    public async Task ExportPerformanceJsonAsync(Guid mission,Stream stream,CancellationToken token=default)
    {
        var buffer=new System.Buffers.ArrayBufferWriter<byte>();using var writer=new Utf8JsonWriter(buffer);
        async Task Flush(){writer.Flush();await stream.WriteAsync(buffer.WrittenMemory,token);buffer.Clear();}
        writer.WriteStartObject();writer.WriteNumber("formatVersion",1);writer.WritePropertyName("reports");writer.WriteStartArray();
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT message FROM events WHERE mission_id=$id AND kind='mission-performance-report' ORDER BY id";cmd.Parameters.AddWithValue("$id",mission.ToString());using var reader=cmd.ExecuteReader();
        int revision=0;string? previous=null;
        while(reader.Read())
        {
            token.ThrowIfCancellationRequested();var json=reader.GetString(0);var report=JsonSerializer.Deserialize<MissionPerformanceReport>(json,WireJson.Options)!;MissionGrading.Verify(report);
            if(report.Revision!=++revision||report.PreviousSha256!=previous)throw new InvalidDataException("Performance revision chain verification failed.");previous=report.Sha256;writer.WriteRawValue(json);await Flush();
        }
        writer.WriteEndArray();writer.WriteEndObject();await Flush();
    }
    public MissionPerformanceReport SavePerformanceReport(MissionEvaluationInput input,string reason,Action<long>? ensureCapacity=null)
    {
        if(!input.Completed||Detail(input.MissionId)?.Mission.Status==MissionStatus.Recording)throw new InvalidOperationException("Only End Flight finalizes an archived overall report.");
        if(string.IsNullOrWhiteSpace(reason)||reason.Length>500||reason.Any(char.IsControl))throw new ArgumentException("A concise revision reason is required.");
        var frozen=CollectionSnapshot<MissionAssessmentContext>(input.MissionId);
        if(PreflightReadiness.Hash(frozen)!=PreflightReadiness.Hash(input.Context))throw new InvalidDataException("Report does not use this flight's original frozen contract.");
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT message FROM events WHERE mission_id=$id AND kind='mission-performance-report' ORDER BY id DESC LIMIT 1";cmd.Parameters.AddWithValue("$id",input.MissionId.ToString());
        var previous=cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<MissionPerformanceReport>(json,WireJson.Options):null;
        if(previous is not null){MissionGrading.Verify(previous);if(previous.Grade.EvidenceSha256==PreflightReadiness.Hash(input))return previous;}
        var report=MissionGrading.Report(input,(previous?.Revision??0)+1,reason,previous?.Sha256,DateTimeOffset.UtcNow);MissionGrading.Verify(report);
        var payload=JsonSerializer.Serialize(report,WireJson.Options);if(System.Text.Encoding.UTF8.GetByteCount(payload)>8*1024*1024)throw new InvalidOperationException("Performance report exceeds the 8 MiB evidence bound; no partial final report was accepted.");
        ensureCapacity?.Invoke(checked(System.Text.Encoding.UTF8.GetByteCount(payload)+65536L));
        Event(db,tx,input.MissionId,report.CreatedUtc,"mission-performance-report",payload);tx.Commit();return report;
    }
    public IEnumerable<InstrumentOperatingReport> StreamOperatingReports(Guid mission)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT message FROM events WHERE mission_id=$id AND kind='operating-assessment' ORDER BY timestamp_utc,id";cmd.Parameters.AddWithValue("$id",mission.ToString());
        using var reader=cmd.ExecuteReader();while(reader.Read()){var r=JsonSerializer.Deserialize<InstrumentOperatingReport>(reader.GetString(0),WireJson.Options)!;ValidateOperatingReport(r);yield return r;}
    }
}
