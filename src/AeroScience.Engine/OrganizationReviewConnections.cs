using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed partial class OrganizationConnections
{
    async Task<(OrganizationSavedConnection Connection,string Credential,Guid Root)> ReviewConnection(SubmissionDraft draft,Guid connection,Guid root,CancellationToken ct)
    {
        var expected=draft.Manifest.ReviewLink?.RootReceiptId??draft.Receipt?.Id;
        if(expected!=root)throw new ArgumentException("Choose this exact received submission and review. A local result or draft is not a server receipt.");
        var (saved,credential)=await SubmissionConnection(connection,draft.Manifest,ct);return(saved,credential,root);
    }
    public async Task<LocalReviewStatus> ReviewStatus(Guid draftId,LocalReviewCheck check,MissionStore store,CancellationToken ct)
    {
        if(check.RequestOffset<0)throw new ArgumentException("Choose a valid review request page.");var draft=store.SubmissionDraft(draftId);var old=store.CachedReview(draft);
        try{var (saved,credential,root)=await ReviewConnection(draft,check.ConnectionId,check.RootReceiptId,ct);var snapshot=await Request<ResearchReviewSnapshot>(saved.Origin,$"/api/member/reviews/{root}?requestOffset={check.RequestOffset}",null,ct,credential,evidence:true,review:true);var retained=store.CacheReview(draft,snapshot);return new(retained,true,"Current organization review metadata verified now. Received evidence was not downloaded and no flight/science data was uploaded.");}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException){if(ct.IsCancellationRequested)throw;return new(old,false,old is null?"The organization review is unavailable. No decision was invented; reconnect with Submit Evidence permission.":"Offline / Not Currently Verified. This is the previously checked review status; it is not a new organization decision.");}
    }
    public async Task<ResearchReviewHistory> ReviewHistory(Guid draftId,LocalReviewCheck check,int after,MissionStore store,CancellationToken ct)
    {if(after<0)throw new ArgumentException("Choose a valid review-history cursor.");var (saved,credential,root)=await ReviewConnection(store.SubmissionDraft(draftId),check.ConnectionId,check.RootReceiptId,ct);return await Request<ResearchReviewHistory>(saved.Origin,$"/api/member/reviews/{root}/history?after={after}",null,ct,credential,evidence:true,review:true);}
    public async Task<ResearchReviewCommandResult> RespondToReview(Guid draftId,LocalReviewResponse response,MissionStore store,CancellationToken ct)
    {
        if(response.Command.Action!=ResearchReviewAction.PilotResponse||!response.Command.ExplicitSharing)throw new ArgumentException("Review the exact destination and reply text, then explicitly choose Send Response. A request does not grant permission to share data.");
        var draft=store.SubmissionDraft(draftId);var (saved,credential,root)=await ReviewConnection(draft,response.ConnectionId,response.RootReceiptId,ct);var value=await Request<ResearchReviewCommandResult>(saved.Origin,$"/api/member/reviews/{root}/respond",response.Command,ct,credential,evidence:true,review:true);store.CacheReview(draft,value.Review);return value;
    }
    public SubmissionReviewLink SupplementalReviewLink(Guid draftId,Guid request,MissionStore store)
    {
        var draft=store.SubmissionDraft(draftId);var old=store.CachedReview(draft)??throw new ArgumentException("Check this organization's actual review before selecting a response.");var row=old.Snapshot.Requests.Items.SingleOrDefault(r=>r.EventId==request&&r.State!=ResearchReviewRequestState.Resolved)??throw new ArgumentException("Choose an unresolved evidence request from this review page.");return new(1,old.RootReceiptId,old.Snapshot.Summary.RootPayloadSha256,row.EventId);
    }
}
