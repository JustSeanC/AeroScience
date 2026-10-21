using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    public void SaveOperatingReports(Guid mission, InstrumentOperatingReport[] reports)
    {
        if (reports.Length>8) throw new ArgumentException("At most eight instrument reports per receipt.");
        using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var report in reports)
        {
            ValidateOperatingReport(report);
            if (report.Assessment.MissionId!=mission) throw new ArgumentException("Assessment belongs to another flight.");
            using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO events(mission_id,timestamp_utc,kind,message) VALUES($id,$utc,'operating-assessment',$json)";
            cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$utc",Stamp(report.Assessment.AssessedUtc));
            cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(report,WireJson.Options));cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public InstrumentOperatingReport[] OperatingReports(Guid mission, DateTimeOffset? at=null)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT message FROM events WHERE mission_id=$id AND kind='operating-assessment' AND timestamp_utc<=$utc ORDER BY timestamp_utc DESC,id DESC LIMIT 256";
        cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$utc",Stamp(at??DateTimeOffset.MaxValue));
        using var reader=cmd.ExecuteReader();var result=new Dictionary<string,InstrumentOperatingReport>();
        while(reader.Read())
        {
            var report=JsonSerializer.Deserialize<InstrumentOperatingReport>(reader.GetString(0),WireJson.Options)!;
            ValidateOperatingReport(report);result.TryAdd(report.Configuration.Settings.InstrumentId,report);
            if(result.Count==8)break;
        }
        return result.Values.ToArray();
    }
    private static void ValidateOperatingReport(InstrumentOperatingReport report)
    {
        EvidenceContracts.Validate(report.Assessment);
        if(!Enum.IsDefined(report.Mode)||report.Configuration.Id!=report.Assessment.ConfigurationId||report.Assessment.Evidence.Length!=1)
            throw new InvalidOperationException("Operating assessment configuration/evidence identity mismatch.");
        var link=report.Assessment.Evidence[0];
        if(DescribeCollectionInput(new(link.Id,report.Input))!=link)
            throw new InvalidOperationException("Operating assessment raw input hash mismatch.");
        _=InstrumentCalibration.Freeze(report.Configuration.Settings);
    }
}
