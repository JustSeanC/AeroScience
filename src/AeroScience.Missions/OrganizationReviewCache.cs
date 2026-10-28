using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    void MigrateReviewCache(SqliteConnection db)
    {
        using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="""
          CREATE TABLE organization_review_cache(origin TEXT NOT NULL,organization_id TEXT NOT NULL,pilot_id TEXT NOT NULL,receipt_id TEXT NOT NULL,revision INTEGER NOT NULL,payload_json TEXT NOT NULL,
          PRIMARY KEY(origin,organization_id,pilot_id,receipt_id));
          PRAGMA user_version=13;
          """;c.ExecuteNonQuery();EvidenceFault?.Invoke("review-cache-migration-before-commit");tx.Commit();
    }
    public CachedResearchReview? CachedReview(SubmissionDraft draft)
    {
        var root=draft.Manifest.ReviewLink?.RootReceiptId??draft.Receipt?.Id;if(root is null)return null;using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT payload_json FROM organization_review_cache WHERE origin=$origin AND organization_id=$organization AND pilot_id=$pilot AND receipt_id=$receipt";c.Parameters.AddWithValue("$origin",draft.Manifest.Origin);c.Parameters.AddWithValue("$organization",draft.Manifest.OrganizationId.ToString());c.Parameters.AddWithValue("$pilot",draft.Manifest.PilotId.ToString());c.Parameters.AddWithValue("$receipt",root.ToString());return c.ExecuteScalar() is string json?JsonSerializer.Deserialize<CachedResearchReview>(json,WireJson.Options):null;
    }
    public CachedResearchReview CacheReview(SubmissionDraft draft,ResearchReviewSnapshot snapshot)
    {
        var expected=draft.Manifest.ReviewLink?.RootReceiptId??draft.Receipt?.Id??throw new ArgumentException("A genuine received submission is required before review status exists.");var s=snapshot.Summary;
        if(s.Version!=1||s.RootReceiptId!=expected||s.PilotId!=draft.Manifest.PilotId||s.OrganizationId!=draft.Manifest.OrganizationId||s.CampaignId!=draft.Manifest.CampaignId||s.RootPayloadSha256!=(draft.Manifest.ReviewLink?.RootPayloadSha256??draft.Consent?.PayloadSha256))throw new IOException("The review response belongs to a different received package, pilot or organization. No status was cached.");
        var old=CachedReview(draft);if(old is not null&&old.Snapshot.Summary.Revision>s.Revision)throw new IOException("The server returned an older review revision. The newer previously checked status remains retained.");
        var row=new CachedResearchReview(draft.Manifest.Origin,s.OrganizationId,s.PilotId,expected,DateTimeOffset.UtcNow,snapshot);using var db=Open();using var c=db.CreateCommand();c.CommandText="INSERT INTO organization_review_cache VALUES($origin,$org,$pilot,$receipt,$revision,$json) ON CONFLICT(origin,organization_id,pilot_id,receipt_id) DO UPDATE SET revision=excluded.revision,payload_json=excluded.payload_json WHERE organization_review_cache.revision <= excluded.revision";c.Parameters.AddWithValue("$origin",row.Origin);c.Parameters.AddWithValue("$org",row.OrganizationId.ToString());c.Parameters.AddWithValue("$pilot",row.PilotId.ToString());c.Parameters.AddWithValue("$receipt",row.RootReceiptId.ToString());c.Parameters.AddWithValue("$revision",s.Revision);c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(row,WireJson.Options));c.ExecuteNonQuery();return row;
    }
}
