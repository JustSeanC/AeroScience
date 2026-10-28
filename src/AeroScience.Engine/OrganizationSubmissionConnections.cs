using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed class OrganizationSubmissionReviewRequired(string message):IOException(message);
public sealed class OrganizationSubmissionAuthorizationRequired(string message):IOException(message);
public sealed partial class OrganizationConnections
{
    async Task<(OrganizationSavedConnection Connection,string Credential)> SubmissionConnection(Guid connection,SubmissionManifest manifest,CancellationToken ct,bool currentIdentity=true)
    {
        var row=(await List(ct)).SingleOrDefault(c=>c.Id==connection)??throw new OrganizationSubmissionAuthorizationRequired("Reconnect on the companion PC and explicitly authorize Submit Evidence. No other organization's credentials will be used.");
        // Check the intended issuer before any claim ID, credential or selected evidence can leave the host.
        if(row.OrganizationId!=manifest.OrganizationId||row.Origin!=manifest.Origin)throw new ArgumentException("Choose the submission's exact original organization and server. No request was sent to the other connection.");
        if(row.Permission!=OrganizationDesktopPermission.SubmitEvidence||row.ExpiresUtc<=DateTimeOffset.UtcNow||vault.Read(row.Id) is not { } credential)throw new OrganizationSubmissionAuthorizationRequired("This connection does not have current Submit Evidence permission. Reconnect with explicit browser approval, then review and approve a new package. Read-only and Manage Own Claims grants cannot upload evidence.");
        if(currentIdentity)await VerifyIdentity(row,ct,evidence:true);return(row,credential);
    }
    public async Task<SubmissionCapabilities[]> RefreshSubmissionPolicies(Guid connection,SubmissionDraft draft,MissionStore store,CancellationToken ct)
    {
        // Read-only grants may review policy, but approval/delivery below still requires the independent submission grant.
        var saved=(await List(ct)).SingleOrDefault(c=>c.Id==connection)??throw new IOException("Choose a connected organization to review its submission policies.");
        if(saved.OrganizationId!=draft.Manifest.OrganizationId||saved.Origin!=draft.Manifest.Origin)throw new ArgumentException("This connection is for another destination; no claim information was sent.");
        var credential=vault.Read(connection);if(credential is null||saved.ExpiresUtc<=DateTimeOffset.UtcNow)throw new OrganizationSubmissionAuthorizationRequired("Reconnect to review current submission policy and permission.");await VerifyIdentity(saved,ct,evidence:true);
        var results=new List<SubmissionCapabilities>();foreach(var card in draft.Manifest.Cards){
            var value=await Request<SubmissionCapabilities>(saved.Origin,$"/api/member/claims/{card.ClaimId}/submission-policy",null,ct,credential,evidence:true);
            if(value.OrganizationId!=draft.Manifest.OrganizationId||value.PilotId!=draft.Manifest.PilotId||value.Policy.OpportunityId!=card.OpportunityId||value.Permission!=saved.Permission)throw new OrganizationSubmissionAuthorizationRequired("This connection belongs to a different pilot or scope. The original claim cannot be substituted.");
            store.CacheSubmissionCapabilities(connection,saved.Origin,value);results.Add(value);
        }return results.ToArray();
    }
    public async Task<SubmissionDraft> ApproveSubmission(Guid id,SubmissionApproval request,MissionStore store,ScienceStore science,CancellationToken ct)
    {
        var draft=store.SubmissionDraft(id);var (connection,_)=await SubmissionConnection(request.ConnectionId,draft.Manifest,ct,currentIdentity:false);
        try{await RefreshSubmissionPolicies(connection.Id,draft,store,ct);}
        catch(Exception e) when(e is IOException or System.Net.Http.HttpRequestException or TaskCanceledException){
            if(ct.IsCancellationRequested)throw;if(e is OrganizationSubmissionAuthorizationRequired or OrganizationSubmissionReviewRequired)throw;
            // Offline approval is valid only against previously verified metadata and this unexpired, explicitly approved grant.
        }
        var cached=store.CachedSubmissionCapabilities(connection.Id,connection.Origin);
        if(cached is null||cached.OrganizationId!=draft.Manifest.OrganizationId||cached.PilotId!=draft.Manifest.PilotId||cached.Permission!=OrganizationDesktopPermission.SubmitEvidence)throw new OrganizationSubmissionAuthorizationRequired("Connect once with Submit Evidence permission to verify this pilot and policy. You can keep the offline draft; no upload is authorized yet.");
        return store.ApproveSubmission(id,request,science,cached.PilotId,cached.OrganizationId,ct);
    }
    internal async Task<SubmissionRemoteState> SubmissionStatus(SubmissionDraft draft,CancellationToken ct)
    {
        var (saved,credential)=await SubmissionConnection(draft.ConnectionId??Guid.Empty,draft.Manifest,ct);
        return await Request<SubmissionRemoteState>(saved.Origin,$"/api/member/submissions/{draft.Id}?manifestSha={draft.Consent!.ManifestSha256}",null,ct,credential,evidence:true);
    }
    internal async Task<SubmissionReceipt> SendSubmission(SubmissionDraft draft,SubmissionDocument document,CancellationToken ct)
    {
        var (saved,credential)=await SubmissionConnection(draft.ConnectionId??Guid.Empty,draft.Manifest,ct);
        return await Request<SubmissionReceipt>(saved.Origin,"/api/member/submissions/receive",new SubmissionUpload(document,draft.Consent!),ct,credential,evidence:true);
    }
    internal async Task<SubmissionRemoteState> CancelSubmission(SubmissionDraft draft,CancellationToken ct)
    {
        var (saved,credential)=await SubmissionConnection(draft.ConnectionId??Guid.Empty,draft.Manifest,ct);
        return await Request<SubmissionRemoteState>(saved.Origin,"/api/member/submissions/cancel",new SubmissionCancellation(draft.Id,draft.Consent!.ManifestSha256),ct,credential,evidence:true);
    }
}
