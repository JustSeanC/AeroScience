using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    private static void MigrateExperience(SqliteConnection db)
    {
        using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS instrument_presets(id TEXT PRIMARY KEY,name TEXT NOT NULL,updated_utc TEXT NOT NULL,payload_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS analysis_settings(mission_id TEXT PRIMARY KEY REFERENCES missions(id),payload_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS telemetry_receipt_time ON telemetry(mission_id,timestamp_utc,sequence);
            PRAGMA user_version=5;
            """; cmd.ExecuteNonQuery(); tx.Commit();
    }
    public InstrumentPreset SavePreset(InstrumentPreset input)
    {
        var name = MissionPlanning.Text(input.Name, 100);
        if (string.IsNullOrWhiteSpace(name) || input.Instruments is null || input.Instruments.Length > 100) throw new ArgumentException("Name the instrument preset and provide its configuration.");
        // Preserve unknown future instrument IDs in stored presets; resolving never enables them.
        foreach (var c in input.Instruments) if (string.IsNullOrWhiteSpace(c.InstrumentId) || c.InstrumentId.Length > 100 || c.IntervalMs is < 1000 or > 60000) throw new ArgumentException("Invalid preset instrument or interval.");
        if (input.Instruments.Select(c => c.InstrumentId).Distinct().Count() != input.Instruments.Length) throw new ArgumentException("Duplicate preset instrument.");
        var value = input with { Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id, Name = name, Description = MissionPlanning.Text(input.Description), UpdatedUtc = DateTimeOffset.UtcNow };
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO instrument_presets VALUES($id,$name,$time,$json) ON CONFLICT(id) DO UPDATE SET name=$name,updated_utc=$time,payload_json=$json";
        cmd.Parameters.AddWithValue("$id", value.Id.ToString()); cmd.Parameters.AddWithValue("$name", value.Name); cmd.Parameters.AddWithValue("$time", Stamp(value.UpdatedUtc.Value)); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, WireJson.Options)); cmd.ExecuteNonQuery(); return value;
    }
    public InstrumentPreset[] Presets()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload_json FROM instrument_presets ORDER BY name COLLATE NOCASE LIMIT 500";
        using var reader = cmd.ExecuteReader(); var rows = new List<InstrumentPreset>(); while (reader.Read()) rows.Add(JsonSerializer.Deserialize<InstrumentPreset>(reader.GetString(0), WireJson.Options)!); return rows.ToArray();
    }
    public bool DeletePreset(Guid id)
    { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM instrument_presets WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString()); return cmd.ExecuteNonQuery() == 1; }
    public static PresetResolution ResolvePreset(InstrumentPreset preset)
    {
        var unknown = preset.Instruments.Where(c => !ScienceCatalog.Definitions.Any(d => d.Id == c.InstrumentId)).ToArray();
        return new(ScienceCatalog.Normalize(preset.Instruments.Except(unknown)), unknown.Select(c => $"Unsupported instrument '{c.InstrumentId}' was retained in the preset but will not be enabled.").ToArray());
    }
    public SciencePauseInterval[] SciencePauses(Guid id)
    {
        var intervals = new List<SciencePauseInterval>(); DateTimeOffset? started = null;
        foreach (var item in StreamEvents(id))
        {
            if (item.Kind == "science-paused") started ??= item.TimestampUtc;
            else if (item.Kind is "science-resumed" or "science-stopped" && started is { } at)
            { intervals.Add(new(at, item.TimestampUtc, Math.Max(0, (item.TimestampUtc - at).TotalSeconds), false)); started = null; }
        }
        if (started is { } open)
        {
            var detail = Detail(id)!; var end = detail.Mission.EndedUtc;
            intervals.Add(new(open, end is { } utc && utc >= open ? utc : null, end is { } valid && valid >= open ? (valid - open).TotalSeconds : null, detail.Mission.Status == MissionStatus.Interrupted));
        }
        return intervals.ToArray();
    }
    public AnalysisSettings GetAnalysisSettings(Guid id)
    { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload_json FROM analysis_settings WHERE mission_id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString()); return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<AnalysisSettings>(json, WireJson.Options)! : new(); }
    public AnalysisSettings SaveAnalysisSettings(Guid id, AnalysisSettings value)
    {
        MissionAnalysis.Validate(value); if (Detail(id) is null) throw new ArgumentException("Mission not found.");
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO analysis_settings VALUES($id,$json) ON CONFLICT(mission_id) DO UPDATE SET payload_json=$json";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, WireJson.Options)); cmd.ExecuteNonQuery(); return value;
    }
}
