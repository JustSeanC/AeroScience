using System.Text.Json;
using Microsoft.Data.Sqlite;
using AeroScience.Shared;
namespace AeroScience.Missions;
public sealed partial class ScienceStore
{
    private static void InitializeStatisticsCache(SqliteConnection db)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS scientific_statistics(product_id TEXT PRIMARY KEY REFERENCES remote_products(id) ON DELETE CASCADE,product_sha TEXT NOT NULL,statistics_json TEXT NOT NULL)";cmd.ExecuteNonQuery();
    }
    public RemoteBandStatistics[] ProductStatistics(RemoteProduct product)
    {
        if(product.Statistics is {} saved)return saved;
        using(var db=Open())using(var query=db.CreateCommand())
        {
            query.CommandText="SELECT statistics_json FROM scientific_statistics WHERE product_id=$id AND product_sha=$sha";query.Parameters.AddWithValue("$id",product.Id.ToString());query.Parameters.AddWithValue("$sha",product.Sha256);
            if(query.ExecuteScalar() is string json)return JsonSerializer.Deserialize<RemoteBandStatistics[]>(json,WireJson.Options)??[];
        }
        // Legacy product bytes stay immutable. Derive a bounded per-acquisition
        // summary from its hash-verified saved values and cache by original SHA.
        var data=ReadProduct(product.Id);var statistics=RemoteCalculations.Summarize(data.Bands,data.Derived);
        lock(gate)
        {
            using var db=Open(write:true);using var cmd=db.CreateCommand();cmd.CommandText="INSERT OR IGNORE INTO scientific_statistics VALUES($id,$sha,$json)";
            cmd.Parameters.AddWithValue("$id",product.Id.ToString());cmd.Parameters.AddWithValue("$sha",product.Sha256);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(statistics,WireJson.Options));cmd.ExecuteNonQuery();
        }
        return statistics;
    }
}
