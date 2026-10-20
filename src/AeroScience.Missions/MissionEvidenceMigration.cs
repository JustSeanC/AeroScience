using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

[assembly: InternalsVisibleTo("AeroScience.Tests")]
[assembly: InternalsVisibleTo("AeroScience.TestHost")]

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    // Test-only crash/capacity seams, never connected to configuration or a public endpoint.
    internal Action<string>? EvidenceFault { get; set; }
    internal Func<long>? EvidenceFreeBytes { get; set; }

    private void MigrateCollectionEvidence(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE collection_states(mission_id TEXT PRIMARY KEY REFERENCES missions(id) ON DELETE CASCADE,
              revision INTEGER NOT NULL CHECK(revision>=0),payload_json TEXT NOT NULL);
            CREATE TABLE collection_snapshots(mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
              kind TEXT NOT NULL,sha256 TEXT NOT NULL,payload_json TEXT NOT NULL,PRIMARY KEY(mission_id,kind));
            CREATE TABLE science_sessions(id TEXT PRIMARY KEY,mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
              sequence INTEGER NOT NULL CHECK(sequence>0),status TEXT NOT NULL,payload_json TEXT NOT NULL,
              UNIQUE(mission_id,sequence),UNIQUE(mission_id,id));
            CREATE UNIQUE INDEX one_open_science_session ON science_sessions(mission_id) WHERE status IN ('Collecting','Paused');
            CREATE TABLE collection_events(mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
              sequence INTEGER NOT NULL CHECK(sequence>0),session_id TEXT,payload_json TEXT NOT NULL,
              PRIMARY KEY(mission_id,sequence),FOREIGN KEY(mission_id,session_id) REFERENCES science_sessions(mission_id,id) ON DELETE CASCADE);
            CREATE TABLE collection_receipts(mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
              kind TEXT NOT NULL,receipt_id TEXT NOT NULL,input_sha256 TEXT NOT NULL,result_json TEXT NOT NULL,
              PRIMARY KEY(mission_id,kind,receipt_id));
            CREATE TABLE collection_inputs(mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
              receipt_id TEXT NOT NULL,sha256 TEXT NOT NULL,payload_json TEXT NOT NULL,PRIMARY KEY(mission_id,receipt_id));
            CREATE TABLE science_intents(id TEXT PRIMARY KEY,mission_id TEXT NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
              session_id TEXT NOT NULL,instrument_id TEXT NOT NULL,slot INTEGER NOT NULL,configuration_id TEXT NOT NULL,
              epoch INTEGER NOT NULL,state TEXT NOT NULL,payload_json TEXT NOT NULL,product_id TEXT,product_sha256 TEXT,
              UNIQUE(mission_id,session_id,instrument_id,slot,configuration_id,epoch),
              FOREIGN KEY(mission_id,session_id) REFERENCES science_sessions(mission_id,id) ON DELETE CASCADE);
            CREATE UNIQUE INDEX one_intent_product ON science_intents(product_id) WHERE product_id IS NOT NULL;
            CREATE INDEX pending_science_intents ON science_intents(mission_id,state,id);
            PRAGMA user_version=8;
            """;
        cmd.ExecuteNonQuery();
        EvidenceFault?.Invoke("migration-before-commit");
        cmd.CommandText = "PRAGMA foreign_key_check";
        using (var check = cmd.ExecuteReader()) if (check.Read()) throw new IOException("Evidence migration foreign-key verification failed; preserve the upgrade backup.");
        tx.Commit();
    }
}
