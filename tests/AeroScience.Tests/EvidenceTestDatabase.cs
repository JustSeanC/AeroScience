using Microsoft.Data.Sqlite;

namespace AeroScience.Tests;

internal static class EvidenceTestDatabase
{
    // Older migration fixtures deliberately remove later schema objects. Do not let a
    // fabricated version number leave later tables in an alleged pre-session archive.
    internal static void RemoveSchemaEight(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS organization_review_cache; DROP TABLE submission_grants; DROP TABLE submission_policies; DROP TABLE submission_drafts; DROP TABLE organization_claim_checks; DROP TABLE organization_mission_preparations; DROP TABLE personal_card_attempts; DROP TABLE personal_research_state; DROP TABLE personal_flight_cards; DROP TABLE personal_research_attempts; DROP TABLE personal_research_cards; DROP TABLE science_intents; DROP TABLE collection_inputs; DROP TABLE collection_receipts; DROP TABLE collection_events; DROP TABLE science_sessions; DROP TABLE collection_snapshots; DROP TABLE collection_states; PRAGMA user_version=7;";
        cmd.ExecuteNonQuery();
    }
}
