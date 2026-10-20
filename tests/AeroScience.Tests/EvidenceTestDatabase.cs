using Microsoft.Data.Sqlite;

namespace AeroScience.Tests;

internal static class EvidenceTestDatabase
{
    // Older migration fixtures deliberately remove later schema objects. Do not let a
    // fabricated version number leave schema-8 tables in an alleged pre-session archive.
    internal static void RemoveSchemaEight(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "DROP TABLE science_intents; DROP TABLE collection_inputs; DROP TABLE collection_receipts; DROP TABLE collection_events; DROP TABLE science_sessions; DROP TABLE collection_snapshots; DROP TABLE collection_states; PRAGMA user_version=7;";
        cmd.ExecuteNonQuery();
    }
}
