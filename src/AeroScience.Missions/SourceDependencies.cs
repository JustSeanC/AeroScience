using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

// Read-only conservative lineage checks. Failed reads are never permission to delete.
internal static class SourceDependencies
{
    static string Quote(string s)=>'"'+s.Replace("\"","\"\"")+'"';
    internal static string[] ArchivedReferences(SqliteConnection db,Guid source,params string[] excluded)
    {
        var result=new List<string>();using var cmd=db.CreateCommand();cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var tables=new List<string>();using(var read=cmd.ExecuteReader())while(read.Read())tables.Add(read.GetString(0));
        foreach(var table in tables.Where(t=>!excluded.Contains(t)))
        {
            // Raw TelemetryFrame contains no external source identity. Avoid a full
            // flight-position scan; all persisted JSON evidence/provenance is checked.
            if(table=="telemetry")continue;
            cmd.Parameters.Clear();cmd.CommandText="PRAGMA table_info("+Quote(table)+")";var columns=new List<string>();
            using(var read=cmd.ExecuteReader())while(read.Read()){var name=read.GetString(1);if(name.EndsWith("_json",StringComparison.Ordinal))columns.Add(name);}
            foreach(var column in columns){cmd.Parameters.Clear();cmd.CommandText="SELECT COUNT(*) FROM "+Quote(table)+" WHERE instr(lower("+Quote(column)+"),$id)>0 OR instr(lower("+Quote(column)+"),$compact)>0";
                cmd.Parameters.AddWithValue("$id",source.ToString());cmd.Parameters.AddWithValue("$compact",source.ToString("N"));var n=Convert.ToInt64(cmd.ExecuteScalar());if(n>0)result.Add($"{table}: {n} persisted reference(s)");}
        }
        return result.ToArray();
    }
}
public sealed partial class MissionStore
{
    public string[] ExternalSourceDependencies(Guid source)
    {
        StorageManager.RequireMissionDatabase(Path.GetDirectoryName(DatabasePath)!);
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";if(Convert.ToInt32(cmd.ExecuteScalar())!=CurrentSchemaVersion)throw new IOException("Mission evidence schema is unsupported for deletion dependency checks.");
        // A legacy direct path into Sources still needs the original. Frozen plan,
        // session and grading provenance does not: it contains retained evidence.
        using var command=db.CreateCommand();command.CommandText="SELECT relative_path FROM dataset_references WHERE relative_path LIKE $path";
        command.Parameters.AddWithValue("$path","Sources/"+source.ToString("N")+"%");
        using var rows=command.ExecuteReader();var reasons=new List<string>();while(rows.Read())reasons.Add("Unresolved legacy original-file reference: "+rows.GetString(0));return reasons.ToArray();
    }
}
