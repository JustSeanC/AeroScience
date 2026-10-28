using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    void MigrateSubmissions(SqliteConnection db)
    {
        using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="""
            CREATE TABLE submission_drafts(id TEXT PRIMARY KEY,flight_id TEXT NOT NULL,scope_key TEXT NOT NULL,
              revision INTEGER NOT NULL,state INTEGER NOT NULL,payload_json TEXT NOT NULL,due_utc TEXT);
            CREATE INDEX submission_inventory ON submission_drafts(flight_id,id);
            CREATE UNIQUE INDEX submission_one_draft ON submission_drafts(scope_key) WHERE state=0;
            CREATE INDEX submission_delivery_due ON submission_drafts(state,due_utc,id);
            CREATE TABLE submission_policies(origin TEXT NOT NULL,opportunity_id TEXT NOT NULL,payload_json TEXT NOT NULL,
              PRIMARY KEY(origin,opportunity_id));
            CREATE TABLE submission_grants(connection_id TEXT PRIMARY KEY,origin TEXT NOT NULL,payload_json TEXT NOT NULL);
            PRAGMA user_version=12;
            """;c.ExecuteNonQuery();EvidenceFault?.Invoke("submission-migration-before-commit");tx.Commit();
    }
    public SubmissionPolicy? CachedSubmissionPolicy(string origin,Guid opportunity)
    {
        using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT payload_json FROM submission_policies WHERE origin=$origin AND opportunity_id=$id";c.Parameters.AddWithValue("$origin",origin);c.Parameters.AddWithValue("$id",opportunity.ToString());
        return c.ExecuteScalar() is string json?JsonSerializer.Deserialize<SubmissionPolicy>(json,WireJson.Options):null;
    }
    public void CacheSubmissionPolicy(string origin,SubmissionPolicy policy)
    {
        if(policy.Version!=1||policy.Revision<1||policy.OpportunityId==Guid.Empty||!Enum.IsDefined(policy.Mode)||policy.MaximumPayloadBytes!=SubmissionEvidenceProtocol.MaximumPayloadBytes||policy.MinimumRule!=SubmissionEvidenceProtocol.MinimumRule||policy.RetentionDisclosure!=SubmissionEvidenceProtocol.RetentionDisclosure)throw new ArgumentException("Unsupported submission policy; no approval was granted.");
        using var db=Open();using var tx=db.BeginTransaction();using(var check=db.CreateCommand()){check.Transaction=tx;check.CommandText="SELECT payload_json FROM submission_policies WHERE origin=$origin AND opportunity_id=$id";check.Parameters.AddWithValue("$origin",origin);check.Parameters.AddWithValue("$id",policy.OpportunityId.ToString());if(check.ExecuteScalar() is string oldJson){var old=JsonSerializer.Deserialize<SubmissionPolicy>(oldJson,WireJson.Options)!;if(old.Revision>policy.Revision||old.Revision==policy.Revision&&PreflightReadiness.Hash(old)!=PreflightReadiness.Hash(policy))throw new InvalidOperationException("The retained published submission policy cannot be rolled back or replaced at the same revision. Ask the organization to reconcile its publication before approval.");}}
        using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="INSERT INTO submission_policies VALUES($origin,$id,$json) ON CONFLICT(origin,opportunity_id) DO UPDATE SET payload_json=excluded.payload_json";c.Parameters.AddWithValue("$origin",origin);c.Parameters.AddWithValue("$id",policy.OpportunityId.ToString());c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(policy,WireJson.Options));c.ExecuteNonQuery();tx.Commit();
    }
    public void CacheSubmissionCapabilities(Guid connection,string origin,SubmissionCapabilities value)
    {
        CacheSubmissionPolicy(origin,value.Policy);using var db=Open();using var c=db.CreateCommand();c.CommandText="INSERT INTO submission_grants VALUES($id,$origin,$json) ON CONFLICT(connection_id) DO UPDATE SET origin=excluded.origin,payload_json=excluded.payload_json";c.Parameters.AddWithValue("$id",connection.ToString());c.Parameters.AddWithValue("$origin",origin);c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(value,WireJson.Options));c.ExecuteNonQuery();
    }
    public SubmissionCapabilities? CachedSubmissionCapabilities(Guid connection,string origin)
    {using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT payload_json FROM submission_grants WHERE connection_id=$id AND origin=$origin";c.Parameters.AddWithValue("$id",connection.ToString());c.Parameters.AddWithValue("$origin",origin);return c.ExecuteScalar() is string json?JsonSerializer.Deserialize<SubmissionCapabilities>(json,WireJson.Options):null;}
    static SubmissionDraft? ReadSubmissionDraft(SqliteConnection db,SqliteTransaction? tx,Guid id)
    {
        using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="SELECT payload_json,revision,state FROM submission_drafts WHERE id=$id";c.Parameters.AddWithValue("$id",id.ToString());if(c.ExecuteReader() is not {} r)return null;using(r){if(!r.Read())return null;var row=JsonSerializer.Deserialize<SubmissionDraft>(r.GetString(0),WireJson.Options)!;if(row.Id!=id||row.Revision!=r.GetInt32(1)||(int)row.State!=r.GetInt32(2)||row.Manifest.Id!=id||row.Manifest.Version!=1)throw new IOException("The retained submission metadata failed its identity check.");return row;}
    }
    public SubmissionDraft SubmissionDraft(Guid id)
    {using var db=Open();return ReadSubmissionDraft(db,null,id)??throw new ArgumentException("This local submission was not found.");}
    public SubmissionDraft[] SubmissionDrafts(Guid? flight=null,int offset=0)
    {
        if(offset<0)throw new ArgumentException("Choose a valid submission history page.");using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT id FROM submission_drafts WHERE ($flight IS NULL OR flight_id=$flight) ORDER BY rowid DESC LIMIT 20 OFFSET $offset";c.Parameters.AddWithValue("$flight",(object?)flight?.ToString()??DBNull.Value);c.Parameters.AddWithValue("$offset",offset);var ids=new List<Guid>();using(var r=c.ExecuteReader())while(r.Read())ids.Add(Guid.Parse(r.GetString(0)));return ids.Select(id=>ReadSubmissionDraft(db,null,id)!).ToArray();
    }
    public SubmissionStatusSummary SubmissionStatusForCard(Guid card,Guid? flight=null)
    {
        using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT state,COUNT(*) FROM submission_drafts WHERE ($flight IS NULL OR flight_id=$flight) AND EXISTS(SELECT 1 FROM json_each(payload_json,'$.manifest.cards') b WHERE json_extract(b.value,'$.cardId')=$card) GROUP BY state";c.Parameters.AddWithValue("$flight",(object?)flight?.ToString()??DBNull.Value);c.Parameters.AddWithValue("$card",card.ToString());var totals=new long[8];using(var r=c.ExecuteReader())while(r.Read()){var state=r.GetInt32(0);if(state is >=0 and <8)totals[state]=r.GetInt64(1);}return new(totals[6],totals[1]+totals[2]+totals[3],totals[0],totals[4]+totals[5],totals[7]);
    }
    static string SubmissionScope(SubmissionManifest m)=>PreflightReadiness.Hash(new{m.FlightId,m.Origin,m.OrganizationId,m.PilotId,m.CampaignId});
    static void WriteSubmissionDraft(SqliteConnection db,SqliteTransaction tx,SubmissionDraft row,bool insert=false)
    {
        using var c=db.CreateCommand();c.Transaction=tx;c.CommandText=insert?"INSERT INTO submission_drafts VALUES($id,$flight,$scope,$revision,$state,$json,$due)":"UPDATE submission_drafts SET revision=$revision,state=$state,payload_json=$json,due_utc=$due WHERE id=$id";
        c.Parameters.AddWithValue("$id",row.Id.ToString());c.Parameters.AddWithValue("$flight",row.Manifest.FlightId.ToString());c.Parameters.AddWithValue("$scope",SubmissionScope(row.Manifest));c.Parameters.AddWithValue("$revision",row.Revision);c.Parameters.AddWithValue("$state",(int)row.State);c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(row,WireJson.Options));c.Parameters.AddWithValue("$due",(object?)row.NextAttemptUtc?.ToString("O")??DBNull.Value);c.ExecuteNonQuery();
    }
    public SubmissionDraft SaveSubmissionDraft(SubmissionDraftRequest request,ScienceStore science,CancellationToken ct=default)
    {
        var preview=PreviewSubmission(request,science,ct);using var db=Open();using var tx=db.BeginTransaction();var previous=ReadSubmissionDraft(db,tx,request.Id);
        if(previous is not null){
            if(previous.State!=SubmissionDeliveryState.Draft)throw new InvalidOperationException("This package is approved, cancelled or received. Cancel an unsent approval or create an explicitly new submission before changing its selection.");
            if(PreflightReadiness.Hash(previous.Manifest)==preview.ManifestSha256&&previous.ConnectionId==request.ConnectionId)return previous;
            if(previous.Revision!=request.ExpectedRevision)throw new InvalidOperationException("This draft changed in another view. Reopen it before saving.");
        }else{
            if(request.ExpectedRevision!=0)throw new InvalidOperationException("The original draft is missing; it was not recreated.");
            using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="SELECT id FROM submission_drafts WHERE scope_key=$scope AND state=0";c.Parameters.AddWithValue("$scope",SubmissionScope(preview.Manifest));if(c.ExecuteScalar() is string existing)return ReadSubmissionDraft(db,tx,Guid.Parse(existing))!;
        }
        var row=new SubmissionDraft(request.Id,(previous?.Revision??0)+1,SubmissionDeliveryState.Draft,preview.Manifest,request.ConnectionId,null,null,DateTimeOffset.UtcNow,"Draft saved locally. No upload is authorized.");WriteSubmissionDraft(db,tx,row,previous is null);EvidenceFault?.Invoke("submission-draft-before-commit");tx.Commit();return row;
    }
    public static SubmissionDraftRequest SubmissionRequest(SubmissionDraft row)=>new(1,row.Id,row.Revision,row.Manifest.FlightId,row.Manifest.ResultIds,row.Manifest.Items.Select(i=>i.Key).ToArray(),row.Manifest.Name,row.ConnectionId);
    public SubmissionPreview PreviewSavedSubmission(Guid id,ScienceStore science,CancellationToken ct=default)=>PreviewSubmission(SubmissionRequest(SubmissionDraft(id)),science,ct);
    public SubmissionDocument ApprovedSubmissionDocument(Guid id,ScienceStore science,CancellationToken ct=default)
    {
        var row=SubmissionDraft(id);if(row.Consent is not {} consent||row.State is SubmissionDeliveryState.Draft or SubmissionDeliveryState.Cancelled or SubmissionDeliveryState.ServerReceived)throw new InvalidOperationException("This package has no current explicit delivery approval.");
        var current=MaterializeSubmission(SubmissionRequest(row),science,ct);var d=current.Document;
        if(!current.PolicyVerified||PreflightReadiness.Hash(d.Manifest)!=consent.ManifestSha256||PreflightReadiness.Hash(d)!=consent.PayloadSha256||SubmissionEvidenceProtocol.Bytes(d)!=consent.PayloadBytes)throw new IOException("Evidence, source availability, selection or submission policy changed after approval. Delivery is held; review and approve a new exact package.");
        SubmissionEvidenceProtocol.Validate(d,ct);return d;
    }
    public SubmissionDraft ApproveSubmission(Guid id,SubmissionApproval request,ScienceStore science,Guid verifiedPilot,Guid verifiedOrganization,CancellationToken ct=default)
    {
        if(request.Version!=1||!request.ExplicitApproval||request.ConnectionId==Guid.Empty)throw new ArgumentException("Review the exact destination, evidence, privacy and size, then explicitly approve this package.");
        var preview=PreviewSavedSubmission(id,science,ct);
        if(!preview.PolicyVerified||preview.Manifest.PilotId!=verifiedPilot||preview.Manifest.OrganizationId!=verifiedOrganization||request.ManifestSha256!=preview.ManifestSha256||request.PayloadSha256!=preview.PayloadSha256||request.PayloadBytes!=preview.PayloadBytes)throw new InvalidOperationException("The exact package or authorized pilot changed. Review it again before approval.");
        if(!preview.CanApprove)throw new InvalidOperationException("This organization requires its supported minimum evidence. Save the incomplete draft or include the required originals; no Pass was invented.");
        using var db=Open();using var tx=db.BeginTransaction();var row=ReadSubmissionDraft(db,tx,id)??throw new ArgumentException("Draft missing.");
        if(row.Revision!=request.ExpectedRevision||row.State!=SubmissionDeliveryState.Draft||PreflightReadiness.Hash(row.Manifest)!=request.ManifestSha256)throw new InvalidOperationException("This draft or approval changed. Reopen the review.");
        row=row with{Revision=row.Revision+1,State=SubmissionDeliveryState.ApprovedQueued,ConnectionId=request.ConnectionId,Consent=new(1,request.ConnectionId,request.ManifestSha256,request.PayloadSha256,request.PayloadBytes,DateTimeOffset.UtcNow,true),UpdatedUtc=DateTimeOffset.UtcNow,Message="Approved — Queued. Only this unchanged package may be delivered automatically when its explicitly authorized connection is available.",DeliveryAttempts=0,NextAttemptUtc=DateTimeOffset.UtcNow};WriteSubmissionDraft(db,tx,row);EvidenceFault?.Invoke("submission-approval-before-commit");tx.Commit();return row;
    }
    public SubmissionDraft ChangeSubmission(Guid id,int expected,Func<SubmissionDraft,SubmissionDraft> change)
    {
        using var db=Open();using var tx=db.BeginTransaction();var row=ReadSubmissionDraft(db,tx,id)??throw new ArgumentException("Submission missing.");if(row.Revision!=expected)throw new InvalidOperationException("The submission state changed. Refresh before trying again.");var updated=change(row) with{Revision=row.Revision+1,UpdatedUtc=DateTimeOffset.UtcNow};if(updated.Id!=row.Id||PreflightReadiness.Hash(updated.Manifest)!=PreflightReadiness.Hash(row.Manifest))throw new InvalidOperationException("Delivery commands cannot change approved evidence or its destination.");WriteSubmissionDraft(db,tx,updated);tx.Commit();return updated;
    }
    public SubmissionDraft CancelSubmission(Guid id,int expected)=>ChangeSubmission(id,expected,row=>row.State==SubmissionDeliveryState.ServerReceived?throw new InvalidOperationException("The organization already received its authorized copy. Local cancellation cannot delete remote evidence."):row with{State=SubmissionDeliveryState.Cancelled,NextAttemptUtc=null,Message=row.State==SubmissionDeliveryState.Sending?"Cancelled locally. Transfer cancellation may race a server receipt; remote status must be confirmed. No further evidence will be sent.":"Cancelled. This local package will not be sent; original research remains unchanged."});
    public SubmissionDraft RetrySubmission(Guid id,int expected)=>ChangeSubmission(id,expected,row=>row.Consent is null||row.State is SubmissionDeliveryState.Draft or SubmissionDeliveryState.Cancelled or SubmissionDeliveryState.ServerReceived or SubmissionDeliveryState.HeldAuthorization?throw new InvalidOperationException("Review/reapprove this draft or restore its exact submission permission first."):row with{State=SubmissionDeliveryState.ApprovedQueued,NextAttemptUtc=DateTimeOffset.UtcNow,DeliveryAttempts=0,Message="Explicit retry requested for the same approved package. Original sources will be checked again."});
    public SubmissionDraft ReopenSubmission(Guid id,int expected)
    {
        using var db=Open();using var tx=db.BeginTransaction();var row=ReadSubmissionDraft(db,tx,id)??throw new ArgumentException("Submission missing.");if(row.Revision!=expected||row.State is SubmissionDeliveryState.Sending or SubmissionDeliveryState.ServerReceived)throw new InvalidOperationException("Wait for sending to finish; received packages retain their original identity.");
        if(row.State==SubmissionDeliveryState.Draft)return row;
        // A changed approval must never reuse an ID that a server may already have received or cancelled.
        WriteSubmissionDraft(db,tx,row with{State=SubmissionDeliveryState.Cancelled,Revision=row.Revision+1,NextAttemptUtc=null,UpdatedUtc=DateTimeOffset.UtcNow,Message="Approval revoked. A revised draft has a new submission identity and requires fresh consent."});
        var next=Guid.NewGuid();var revised=row with{Id=next,Revision=1,State=SubmissionDeliveryState.Draft,Manifest=row.Manifest with{Id=next},Consent=null,Receipt=null,UpdatedUtc=DateTimeOffset.UtcNow,NextAttemptUtc=null,DeliveryAttempts=0,Message="Revised draft saved. Nothing is authorized for upload until you review and approve this new package."};WriteSubmissionDraft(db,tx,revised,true);tx.Commit();return revised;
    }
    public SubmissionDraft? NextApprovedSubmission()
    {
        using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT id FROM submission_drafts WHERE state IN (1,3) AND due_utc IS NOT NULL AND due_utc <= $at ORDER BY due_utc,id LIMIT 1";c.Parameters.AddWithValue("$at",Stamp(DateTimeOffset.UtcNow));return c.ExecuteScalar() is string id?ReadSubmissionDraft(db,null,Guid.Parse(id)):null;
    }
    public int RecoverSubmissionDeliveries()
    {
        using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT id FROM submission_drafts WHERE state=2 LIMIT 20";var ids=new List<Guid>();using(var r=c.ExecuteReader())while(r.Read())ids.Add(Guid.Parse(r.GetString(0)));foreach(var id in ids){var row=SubmissionDraft(id);ChangeSubmission(id,row.Revision,r=>r.Consent is null?r with{State=SubmissionDeliveryState.HeldAuthorization,NextAttemptUtc=null,Message="This interrupted state has no explicit consent. It cannot authorize delivery."}:r with{State=SubmissionDeliveryState.RetryAvailable,NextAttemptUtc=DateTimeOffset.UtcNow,Message="Delivery was interrupted. Only the previously approved identity may resume, after receipt lookup and fresh source/authorization checks."});}return ids.Count;
    }
    public SubmissionDraft RecordSubmissionReceipt(Guid id,SubmissionReceipt receipt)
    {
        var row=SubmissionDraft(id);var consent=row.Consent;
        if(consent is null||receipt.Version!=1||receipt.Id==Guid.Empty||receipt.SubmissionId!=id||receipt.OrganizationId!=row.Manifest.OrganizationId||receipt.PilotId!=row.Manifest.PilotId||receipt.CampaignId!=row.Manifest.CampaignId||receipt.ManifestSha256!=consent.ManifestSha256||receipt.PayloadSha256!=consent.PayloadSha256||receipt.PayloadBytes!=consent.PayloadBytes||receipt.Status!="Received / Awaiting Review"||receipt.RetentionDisclosure!=SubmissionEvidenceProtocol.RetentionDisclosure)throw new IOException("The organization returned a mismatched receipt. No confirmed status was invented.");
        if(row.Receipt is {} old){if(PreflightReadiness.Hash(old)!=PreflightReadiness.Hash(receipt))throw new IOException("A conflicting server receipt was returned.");return row;}
        return ChangeSubmission(id,row.Revision,r=>r with{State=SubmissionDeliveryState.ServerReceived,Receipt=receipt,NextAttemptUtc=null,Message="Server Received — Awaiting Review. This is not scientific approval. The organization now holds a separate authorized copy; local deletion does not remove it."});
    }
}
