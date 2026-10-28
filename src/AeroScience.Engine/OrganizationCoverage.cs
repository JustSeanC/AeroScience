using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed partial class OrganizationConnections
{
    public Task<OrganizationProgressResponse<CampaignCoverageView>> CampaignCoverage(Guid connection,Guid campaign,CancellationToken ct)=>ProgressRead<CampaignCoverageView>(connection,$"/campaigns/{campaign}/coverage",ct,(v,org)=>v.OrganizationId==org&&v.CampaignId==campaign);
    internal Task<OrganizationProgressResponse<T>> CoverageRead<T>(Guid connection,string path,CancellationToken ct)=>ProgressRead<T>(connection,path,ct);
}
public static class OrganizationCoverageApi
{
    // Only these bounded query parameters and fixed read-only paths can cross the connection.
    static string Query(HttpContext c)
    {
        string[] allowed=["offset","compatibilityId","opportunityId","publicationRevision","requirementKey","variable","instrument","category","fromUtc","throughUtc","flightReference","bandFeet","revision"];
        if(c.Request.Query.Any(q=>!allowed.Contains(q.Key)||q.Value.Count!=1||q.Value.ToString().Length>150))throw new ArgumentException("Choose supported coverage filters.");
        return QueryString.Create(c.Request.Query.Select(q=>new KeyValuePair<string,string?>(q.Key,q.Value.ToString()))).Value??"";
    }
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/{id:guid}/progress/campaigns/{campaign:guid}/coverage",(Guid id,Guid campaign,OrganizationConnections connections,HttpContext c)=>connections.CampaignCoverage(id,campaign,c.RequestAborted));
        api.MapGet("/{id:guid}/progress/campaigns/{campaign:guid}/coverage/{view}",async(Guid id,Guid campaign,string view,OrganizationConnections connections,HttpContext c)=>{
            var path=$"/campaigns/{campaign}/coverage/{view}"+Query(c);return view switch{
                "groups"=>(object)await connections.CoverageRead<OrganizationPage<CoverageGroup>>(id,path,c.RequestAborted),
                "locations"=>await connections.CoverageRead<OrganizationPage<CoverageLocation>>(id,path,c.RequestAborted),
                "altitude"=>await connections.CoverageRead<OrganizationPage<CoverageBand>>(id,path,c.RequestAborted),
                "dates"=>await connections.CoverageRead<OrganizationPage<CoverageTemporalSlice>>(id,path,c.RequestAborted),
                "goals"=>await connections.CoverageRead<OrganizationPage<CoverageGoalView>>(id,path,c.RequestAborted),_=>throw new ArgumentException("Choose an available scientific coverage view.")};
        });
        api.MapGet("/{id:guid}/progress/coverage-goals/{goal:guid}",(Guid id,Guid goal,OrganizationConnections connections,HttpContext c)=>connections.CoverageRead<CoverageGoalView>(id,$"/coverage-goals/{goal}"+Query(c),c.RequestAborted));
        api.MapGet("/{id:guid}/progress/coverage-goals/{goal:guid}/{view}",async(Guid id,Guid goal,string view,OrganizationConnections connections,HttpContext c)=>{
            var path=$"/coverage-goals/{goal}/{view}"+Query(c);return view switch{"history"=>(object)await connections.CoverageRead<OrganizationPage<CoverageGoalView>>(id,path,c.RequestAborted),"assessment"=>await connections.CoverageRead<CoverageGoalAssessment>(id,path,c.RequestAborted),_=>throw new ArgumentException("Choose goal history or assessment.")};
        });
    }
}
