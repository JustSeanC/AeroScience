using System.Globalization;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Tests;

internal static class ArchiveFixture
{
    // These export tests exercise thousands of archived rows, not thousands of fsyncs.
    // Keep FULL durability and one real Append (including metadata) before seeding the
    // remaining valid synthetic rows in a single prepared SQLite transaction. Runtime
    // recording, crash recovery and single-row persistence have separate integration tests.
    internal static void Telemetry(MissionStore store, Guid mission, IEnumerable<TelemetryFrame> frames)
    {
        using var input = frames.GetEnumerator(); if (!input.MoveNext()) return;
        store.Append(mission, input.Current);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
        db.Open(); using (var setup = db.CreateCommand()) { setup.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL"; setup.ExecuteNonQuery(); }
        using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO telemetry(mission_id,timestamp_utc,latitude,longitude,altitude_msl_ft,payload_json) VALUES($mission,$time,$lat,$lon,$alt,$json)";
        cmd.Parameters.AddWithValue("$mission", mission.ToString());
        foreach (var name in new[] { "$time", "$lat", "$lon", "$alt", "$json" }) cmd.Parameters.AddWithValue(name, DBNull.Value);
        while (input.MoveNext())
        {
            var frame = input.Current; if (!frame.IsValid) throw new ArgumentException("Invalid synthetic archive fixture.");
            cmd.Parameters["$time"].Value = frame.TimestampUtc.ToString("O", CultureInfo.InvariantCulture);
            cmd.Parameters["$lat"].Value = frame.Latitude; cmd.Parameters["$lon"].Value = frame.Longitude;
            cmd.Parameters["$alt"].Value = frame.AltitudeMslFeet; cmd.Parameters["$json"].Value = JsonSerializer.Serialize(frame, WireJson.Options);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
