using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed partial class OrganizationConnections
{
    async Task<(OrganizationSavedConnection Connection,OrganizationClaimOriginal Original)> ClaimOriginal(Guid id,Guid claim,CancellationToken ct)
    {
        var connection=(await List(ct)).SingleOrDefault(r=>r.Id==id)??throw new IOException("Choose a connected organization. Previously prepared local missions and evidence remain available offline.");
        var credential=vault.Read(id);if(credential is null||connection.ExpiresUtc<=DateTimeOffset.UtcNow)throw new IOException("Reconnect to review current eligibility. Previously prepared missions can still record locally with eligibility pending verification.");
        await VerifyIdentity(connection,ct);
        OrganizationClaimOriginal original;
        try{original=await Request<OrganizationClaimOriginal>(connection.Origin,$"/api/member/claims/{claim}/original",null,ct,credential);}
        catch(UnauthorizedAccessException){vault.Remove(id);throw new IOException("Organization access is expired or revoked. Current claim eligibility could not be verified; retained local evidence is unchanged.");}
        var value=original.Claim;
        if(value is null||value.Id!=claim||value.OrganizationId!=connection.OrganizationId||original.IssuedJson is not {Length:>0 and <=65536}
            ||Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original.IssuedJson)))!=value.IssuedSha256)
            throw new IOException("The organization returned a mismatched claim or original publication hash. No local plan was changed.");
        var publication=JsonSerializer.Deserialize<OrganizationOpportunitySnapshot>(original.IssuedJson,WireJson.Options);
        if(PreflightReadiness.Hash(publication!)!=PreflightReadiness.Hash(value.IssuedCard))throw new IOException("The claim and original issued publication differ. No local plan was changed.");
        return(connection,original);
    }
    public async Task<OrganizationMissionPreview> PreviewMission(Guid connection,Guid claim,OrganizationMissionPreviewRequest request,MissionStore store,CancellationToken ct)
    {
        var (saved,original)=await ClaimOriginal(connection,claim,ct);
        var source=MissionStore.VerifiedOrganizationAssignment(saved.Id,saved.Origin,saved.OrganizationId,original);
        return store.PreviewOrganizationMission(source,request.PlanId);
    }
    public async Task<PreparedOrganizationMission> PrepareMission(Guid connection,Guid claim,OrganizationMissionSave request,MissionStore store,CancellationToken ct)
    {
        // A replay returns an already committed local preparation; it never grants fresh server authority.
        var retained=store.RetainedOrganizationPreparation(connection,claim,request);if(retained is not null)return retained;
        var (saved,original)=await ClaimOriginal(connection,claim,ct);
        var source=MissionStore.VerifiedOrganizationAssignment(saved.Id,saved.Origin,saved.OrganizationId,original);
        return store.SaveOrganizationMission(connection,claim,source,request);
    }
    public async Task<OrganizationClaimCheck> ReconcileMission(Guid cardId,OrganizationClaimReconcile request,MissionStore store,CancellationToken ct)
    {
        var source=store.OrganizationAssignmentForCard(cardId);OrganizationClaimCheck check;
        try{
            var target=request.ConnectionId??source.ConnectionId;
            var selected=(await List(ct)).SingleOrDefault(c=>c.Id==target)??throw new IOException("Choose a connected organization to recheck eligibility. Prepared local evidence remains available.");
            // Reject the wrong issuer before disclosing even an original claim identifier to it.
            if(selected.OrganizationId!=source.OrganizationId||selected.Origin!=source.Origin)throw new ArgumentException("Choose a connection to this assignment's original organization. No request was sent to the other server.");
            var (saved,original)=await ClaimOriginal(target,source.ClaimId,ct);
            if(saved.OrganizationId!=source.OrganizationId||saved.Origin!=source.Origin||original.Claim.PilotId!=source.PilotId)throw new ArgumentException("This connection belongs to a different organization or pilot. No evidence was changed.");
            check=new(DateTimeOffset.UtcNow,original.AvailableForPreparation?"Current Claim Confirmed":"Unavailable For New Preparation",original,
                "Current server reference only. Historical flights retain their original timestamps and claim identity. Claim-time eligibility remains pending; Not Submitted. No evidence was transmitted.");
        }catch(Exception e) when(e is IOException or System.Net.Http.HttpRequestException or TaskCanceledException){
            if(ct.IsCancellationRequested)throw;
            check=new(DateTimeOffset.UtcNow,"Organization Eligibility Pending Verification",null,e.Message+" No claim mutation or evidence upload was queued.");
        }
        store.RecordOrganizationClaimCheck(cardId,check);return check;
    }
}
