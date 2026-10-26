using System.Security.Cryptography;
using System.Text;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    internal IEnumerable<Guid> DeletionMissionIds()
    {using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT id FROM missions ORDER BY id";using var rows=command.ExecuteReader();while(rows.Read())yield return Guid.Parse(rows.GetString(0));}
    internal static void RegisterScienceBearingBackup(string source,string backup)
    {if(!Path.GetFileName(source).Equals("missions.db",StringComparison.OrdinalIgnoreCase))return;var file=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!,"Metadata","ScientificBackupLocations",Guid.NewGuid().ToString("N")+".json");PreferenceStore.WriteAtomic(file,new{version=1,source=Path.GetFullPath(source),backup=Path.GetFullPath(backup)});}
    private IEnumerable<string> ScienceBearingBackups()
    {
        var root=Path.GetDirectoryName(DatabasePath)!;var backups=Path.Combine(root,"Backups");
        if(Directory.Exists(backups))foreach(var path in Directory.EnumerateFiles(backups).Where(path=>Path.GetFileName(path).StartsWith("AeroScience-missions-",StringComparison.Ordinal)&&Path.GetExtension(path)==".sqlite"||Path.GetFileName(path).StartsWith("missions-schema",StringComparison.Ordinal)&&Path.GetExtension(path)==".db"))yield return path;
        var locations=Path.Combine(root,"Metadata","ScientificBackupLocations");if(!Directory.Exists(locations))yield break;
        foreach(var file in Directory.EnumerateFiles(locations,"*.json")){
            StorageManager.ValidateLocalPath(file);if(new FileInfo(file).Length>65536)throw new IOException("Mission backup ownership is invalid; scientific purge remains pending.");using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));var record=json.RootElement;
            if(record.GetProperty("version").GetInt32()!=1||record.GetProperty("source").GetString()!=Path.GetFullPath(DatabasePath))throw new IOException("Mission backup ownership changed; scientific purge remains pending.");var path=record.GetProperty("backup").GetString()!;StorageManager.ValidateLocalPath(path);if(!File.Exists(path))throw new IOException("Reconnect or resolve the registered mission backup before permanent scientific deletion: "+path);yield return path;
        }
    }
    public bool ScienceDeleted(Guid mission)
    {
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT EXISTS(SELECT 1 FROM events WHERE mission_id=$id AND kind='science-deleted')";command.Parameters.AddWithValue("$id",mission.ToString());return Convert.ToInt64(command.ExecuteScalar())!=0;
    }
    internal string ScienceDeletionFingerprint(Guid mission)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);using var db=Open();using var command=db.CreateCommand();command.Parameters.AddWithValue("$id",mission.ToString());
        foreach(var sql in new[]{"SELECT payload_json FROM observations WHERE mission_id=$id ORDER BY id","SELECT message FROM events WHERE mission_id=$id AND kind IN ('mission-performance-report','operating-assessment') ORDER BY id","SELECT payload_json FROM scientific_recovery WHERE mission_id=$id ORDER BY id","SELECT metadata_json FROM instrument_metadata WHERE mission_id=$id ORDER BY instrument_id"})
        {command.CommandText=sql;using var rows=command.ExecuteReader();while(rows.Read()){var bytes=Encoding.UTF8.GetBytes(rows.GetString(0));hash.AppendData(BitConverter.GetBytes(bytes.Length));hash.AppendData(bytes);}}
        command.CommandText="SELECT payload_json FROM personal_research_attempts WHERE mission_id=$id AND json_extract(payload_json,'$.card.templateId') IN ('vertical-atmospheric-profile','atmospheric-transect','terrain-temperature-survey') ORDER BY id";
        using(var rows=command.ExecuteReader())while(rows.Read()){var bytes=Encoding.UTF8.GetBytes(rows.GetString(0));hash.AppendData(BitConverter.GetBytes(bytes.Length));hash.AppendData(bytes);}
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal void DeleteCollectedScience(Guid mission)
    {
        if(Detail(mission)?.Mission.Status==MissionStatus.Recording)throw new InvalidOperationException("End flight recording before deleting collected science.");
        using var db=Open();PurgeMissionScience(db,mission);
        foreach(var path in ScienceBearingBackups().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            StorageManager.ValidateLocalPath(path);using var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());backup.Open();
            using var identity=backup.CreateCommand();identity.CommandText="SELECT COUNT(*) FROM missions WHERE id=$id";identity.Parameters.AddWithValue("$id",mission.ToString());if(Convert.ToInt64(identity.ExecuteScalar())>0)PurgeMissionScience(backup,mission);
        }
    }
    private static void PurgeMissionScience(SqliteConnection db,Guid mission)
    {
        using var command=db.CreateCommand();command.CommandText="PRAGMA secure_delete=ON";command.ExecuteNonQuery();
        using(var tx=db.BeginTransaction())
        {
            command.Transaction=tx;command.Parameters.AddWithValue("$id",mission.ToString());
            command.CommandText="DELETE FROM observations WHERE mission_id=$id; DELETE FROM events WHERE mission_id=$id AND kind IN ('mission-performance-report','operating-assessment'); DELETE FROM instrument_metadata WHERE mission_id=$id";command.ExecuteNonQuery();
            command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='personal_research_attempts'";
            if(Convert.ToInt64(command.ExecuteScalar())>0){command.CommandText="DELETE FROM personal_research_attempts WHERE mission_id=$id AND json_extract(payload_json,'$.card.templateId') IN ('vertical-atmospheric-profile','atmospheric-transect','terrain-temperature-survey')";command.ExecuteNonQuery();}
            foreach(var table in new[]{"scientific_recovery","analysis_settings","collection_inputs","collection_receipts","science_intents","dataset_references"}){
                command.Parameters.AddWithValue("$table",table);command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name=$table";var exists=Convert.ToInt64(command.ExecuteScalar())>0;command.Parameters.RemoveAt("$table");if(!exists)continue;
                command.CommandText=table=="dataset_references"?"UPDATE dataset_references SET available=0 WHERE mission_id=$id":$"DELETE FROM {table} WHERE mission_id=$id";command.ExecuteNonQuery();
            }
            command.CommandText="SELECT EXISTS(SELECT 1 FROM events WHERE mission_id=$id AND kind='science-deleted')";
            if(Convert.ToInt64(command.ExecuteScalar())==0)Event(db,tx,mission,DateTimeOffset.UtcNow,"science-deleted","Collected scientific measurements and dependent analysis were permanently deleted by the user. Flight recording and external sources were preserved. No grade may be reconstructed from deleted evidence.");
            tx.Commit();command.Transaction=null;
        }
        command.Parameters.Clear();command.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";
        using(var rows=command.ExecuteReader())if(!rows.Read()||rows.GetInt32(0)!=0)throw new IOException("Mission database cleanup is busy; permanent deletion remains pending.");
        command.CommandText="VACUUM";command.ExecuteNonQuery();command.CommandText="PRAGMA integrity_check";if(command.ExecuteScalar() as string!="ok")throw new IOException("Mission scientific purge failed integrity verification.");
    }
}
