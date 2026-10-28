using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed partial class OrganizationConnections
{
    // ReadPublished stays read-only. No local science/receipt cache and no credentials in these views.
    async Task<OrganizationProgressResponse<T>> ProgressRead<T>(Guid connection,string path,CancellationToken ct,Func<T,Guid,bool>? identity=null)
    {
        var row=(await List(ct)).SingleOrDefault(r=>r.Id==connection)??throw new ArgumentException("This organization connection was not found.");
        var credential=vault.Read(connection);if(credential is null||row.ExpiresUtc<=DateTimeOffset.UtcNow)return new("Expired / Reconnect",false,null,default);
        try{
            await VerifyIdentity(row,ct);var value=await Request<T>(row.Origin,"/api/member/research-progress"+path,null,ct,credential);
            if(identity is not null&&!identity(value,row.OrganizationId))throw new IOException("The progress view belongs to a different organization.");
            return new("Checked now — approved summaries only; no private review access",true,DateTimeOffset.UtcNow,value);
        }catch(UnauthorizedAccessException){vault.Remove(connection);return new("Access Revoked / Reconnect",false,null,default);}
        catch(Exception e) when(!ct.IsCancellationRequested&&e is IOException or HttpRequestException or TaskCanceledException){return new("Unavailable — organization progress could not be checked. Personal Research remains available offline.",false,null,default);}
    }
    static string ProgressQuery(int offset,string? search=null,string? sort=null,string? state=null)
    {
        if(offset<0||search?.Length>100||sort?.Length>20||state?.Length>40)throw new ArgumentException("Choose a valid progress page and short search.");
        return "?offset="+offset+"&search="+Uri.EscapeDataString(search??"")+"&sort="+Uri.EscapeDataString(sort??"name")+"&state="+Uri.EscapeDataString(state??"");
    }
    public Task<OrganizationProgressResponse<ProgramProgressView>> ProgramProgress(Guid connection,Guid id,int offset,CancellationToken ct)=>ProgressRead<ProgramProgressView>(connection,"/programs/"+id+"?offset="+Math.Max(0,offset),ct,(v,org)=>v.OrganizationId==org&&v.ProgramId==id);
    public Task<OrganizationProgressResponse<CampaignProgressView>> CampaignProgress(Guid connection,Guid id,CancellationToken ct)=>ProgressRead<CampaignProgressView>(connection,"/campaigns/"+id,ct,(v,org)=>v.OrganizationId==org&&v.CampaignId==id);
    public Task<OrganizationProgressResponse<OrganizationPage<CampaignCardProgress>>> CampaignCards(Guid connection,Guid id,int offset,string? search,string? sort,string? state,CancellationToken ct)=>ProgressRead<OrganizationPage<CampaignCardProgress>>(connection,"/campaigns/"+id+"/cards"+ProgressQuery(offset,search,sort,state),ct);
    public Task<OrganizationProgressResponse<CampaignCardDetail>> CampaignCard(Guid connection,Guid id,int? revision,int offset,CancellationToken ct)=>ProgressRead<CampaignCardDetail>(connection,"/cards/"+id+"?offset="+Math.Max(0,offset)+(revision is {} r?"&revision="+r:""),ct,(v,org)=>v.Campaign.OrganizationId==org&&v.Card.OpportunityId==id);
    public Task<OrganizationProgressResponse<OrganizationPage<ApprovedResearchSummary>>> ApprovedResults(Guid connection,Guid campaign,Guid? opportunity,int? revision,int offset,string? search,string? sort,CancellationToken ct)=>ProgressRead<OrganizationPage<ApprovedResearchSummary>>(connection,"/campaigns/"+campaign+"/approved"+ProgressQuery(offset,search,sort??"newest")+(opportunity is {} id?"&opportunity="+id:"")+(revision is {} r?"&revision="+r:""),ct,(v,_)=>v.Items.All(i=>i.CampaignId==campaign));
    public Task<OrganizationProgressResponse<ApprovedResearchDetail>> ApprovedResult(Guid connection,Guid root,Guid card,int offset,CancellationToken ct)=>ProgressRead<ApprovedResearchDetail>(connection,$"/approved/{root}/{card}?offset="+Math.Max(0,offset),ct,(v,_)=>v.Summary.RootReceiptId==root&&v.Summary.CardId==card);
    public Task<OrganizationProgressResponse<OrganizationPage<ApprovedResearchLocation>>> ApprovedLocations(Guid connection,Guid root,Guid card,int offset,CancellationToken ct)=>ProgressRead<OrganizationPage<ApprovedResearchLocation>>(connection,$"/approved/{root}/{card}/locations?offset="+Math.Max(0,offset),ct);
    public Task<OrganizationProgressResponse<OrganizationPage<CampaignOwnWork>>> CampaignOwn(Guid connection,Guid campaign,int offset,CancellationToken ct)=>ProgressRead<OrganizationPage<CampaignOwnWork>>(connection,$"/campaigns/{campaign}/own?offset="+Math.Max(0,offset),ct);
}
