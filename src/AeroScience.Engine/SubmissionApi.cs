using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed record SubmissionPolicyRefresh(Guid ConnectionId);
public sealed record SupplementalReviewRequest(Guid RequestEventId);
public static class SubmissionApi
{
    static SubmissionInventory SubmissionChoices(MissionStore store,Guid flight,Guid? card,int offset){var rows=store.SubmissionChoices(flight,card,offset,includeNext:true);return new(1,rows.Take(20).ToArray(),store.SubmissionDrafts(flight,offset),rows.Length>20?checked(offset+20):null);}
    public static void MapSubmissions(this WebApplication app)
    {
        var api=app.MapGroup("/api/submissions");api.AddEndpointFilter(async(context,next)=>{
            if(!AccessControl.IsLoopback(context.HttpContext))return Results.Json(new{error="Review and authorize evidence sharing on the companion PC. Personal research and existing scientific results remain available here."},statusCode:403);return await next(context);
        });
        api.MapGet("/drafts/{id:guid}/review-cache",(Guid id,MissionStore store)=>new LocalReviewStatus(store.CachedReview(store.SubmissionDraft(id)),false,"Previously checked review metadata only. Use Check Review Status for a current server decision."));
        api.MapPost("/drafts/{id:guid}/review",(Guid id,LocalReviewCheck r,OrganizationConnections connections,MissionStore store,HttpContext c)=>connections.ReviewStatus(id,r,store,c.RequestAborted));
        api.MapPost("/drafts/{id:guid}/review-history",(Guid id,LocalReviewCheck r,int? after,OrganizationConnections connections,MissionStore store,HttpContext c)=>connections.ReviewHistory(id,r,after??0,store,c.RequestAborted));
        api.MapPost("/drafts/{id:guid}/review-response",(Guid id,LocalReviewResponse r,OrganizationConnections connections,MissionStore store,HttpContext c)=>connections.RespondToReview(id,r,store,c.RequestAborted));
        api.MapPost("/drafts/{id:guid}/supplement",(Guid id,SupplementalReviewRequest r,OrganizationConnections connections,MissionStore store)=>new{reviewLink=connections.SupplementalReviewLink(id,r.RequestEventId,store)});
        api.MapGet("/context",(Guid cardId,Guid? flightId,MissionStore store)=>store.SubmissionStatusForCard(cardId,flightId));
        api.MapGet("/choices",(Guid flightId,Guid? cardId,int? offset,MissionStore store)=>SubmissionChoices(store,flightId,cardId,offset??0));
        api.MapGet("/drafts",(Guid? flightId,int? offset,MissionStore store)=>store.SubmissionDrafts(flightId,offset??0));
        api.MapGet("/drafts/{id:guid}/status",(Guid id,MissionStore store)=>{var r=store.SubmissionDraft(id);return new{r.Id,r.Revision,r.State,r.Message,r.UpdatedUtc,r.Consent,r.Receipt,r.NextAttemptUtc,r.DeliveryAttempts};});
        api.MapPost("/preview",(SubmissionDraftRequest r,MissionStore store,ScienceStore science,HttpContext c)=>store.PreviewSubmission(r,science,c.RequestAborted));
        api.MapPost("/drafts",(SubmissionDraftRequest r,MissionStore store,ScienceStore science,HttpContext c)=>store.SaveSubmissionDraft(r,science,c.RequestAborted));
        api.MapGet("/drafts/{id:guid}",(Guid id,MissionStore store,ScienceStore science,HttpContext c)=>{
            var draft=store.SubmissionDraft(id);SubmissionPreview? preview=null;SubmissionEvidenceReference[] options=[];string? issue=null;
            try{preview=store.PreviewSavedSubmission(id,science,c.RequestAborted);options=store.PreviewSubmission(MissionStore.SubmissionRequest(draft) with{SelectedKeys=null},science,c.RequestAborted).Manifest.Items;}
            catch(Exception e) when(e is IOException or ArgumentException or InvalidOperationException or NotSupportedException){issue="Original evidence is missing, changed, unavailable or outside the supported bounded format. The retained draft/receipt remains visible; no deleted values were reconstructed.";}
            return new{draft,preview,options,issue};
        });
        api.MapGet("/drafts/{id:guid}/item",(Guid id,string key,MissionStore store,ScienceStore science,HttpContext c)=>{
            var row=store.SubmissionDraft(id);var material=store.MaterializeSubmission(MissionStore.SubmissionRequest(row) with{SelectedKeys=null},science,c.RequestAborted);var item=material.Document.Evidence.SingleOrDefault(i=>i.Reference.Key==key)??throw new ArgumentException("Select a supported original evidence item.");var data=item.Data;bool truncated=false;
            if(data.Telemetry is {} t){truncated=t.Samples.Length>20;data=data with{Telemetry=t with{Samples=t.Samples.Take(20).ToArray()}};}return new{reference=item.Reference,data,truncated};
        });
        api.MapPost("/drafts/{id:guid}/policies",async(Guid id,SubmissionPolicyRefresh r,OrganizationConnections connections,MissionStore store,HttpContext c)=>new{capabilities=await connections.RefreshSubmissionPolicies(r.ConnectionId,store.SubmissionDraft(id),store,c.RequestAborted),message="Policy metadata reviewed. Drafts still authorize no upload. Re-preview/save any changed policy before approval."});
        api.MapPost("/drafts/{id:guid}/approve",(Guid id,SubmissionApproval r,OrganizationConnections connections,MissionStore store,ScienceStore science,HttpContext c)=>connections.ApproveSubmission(id,r,store,science,c.RequestAborted));
        api.MapPost("/drafts/{id:guid}/send",(Guid id,SubmissionDelivery delivery,HttpContext c)=>delivery.Send(id,c.RequestAborted));
        api.MapPost("/drafts/{id:guid}/retry",(Guid id,SubmissionChange r,MissionStore store)=>store.RetrySubmission(id,r.ExpectedRevision));
        api.MapPost("/drafts/{id:guid}/cancel",(Guid id,SubmissionChange r,SubmissionDelivery delivery,HttpContext c)=>delivery.Cancel(id,r.ExpectedRevision,c.RequestAborted));
        api.MapPost("/drafts/{id:guid}/revise",async(Guid id,SubmissionChange r,SubmissionDelivery delivery,MissionStore store,HttpContext c)=>{var old=store.SubmissionDraft(id);if(old.Revision!=r.ExpectedRevision)throw new InvalidOperationException("This submission changed. Refresh it before revising.");if(old.State!=SubmissionDeliveryState.Cancelled&&old.State!=SubmissionDeliveryState.Draft)old=await delivery.Cancel(id,r.ExpectedRevision,c.RequestAborted);return store.ReopenSubmission(id,old.Revision);});
        api.MapPost("/drafts/{id:guid}/receipt",async(Guid id,OrganizationConnections connections,MissionStore store,HttpContext c)=>{var row=store.SubmissionDraft(id);if(row.Consent is null)throw new ArgumentException("This draft has no approved submission identity.");var remote=await connections.SubmissionStatus(row,c.RequestAborted);return remote.Receipt is {} receipt?store.RecordSubmissionReceipt(id,receipt):row;});
    }
}
