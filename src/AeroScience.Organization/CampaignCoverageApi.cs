using System.Globalization;
using AeroScience.Shared;
namespace AeroScience.Organization;

public static class CampaignCoverageApi
{
    public static CampaignCoverageFilter Filter(IQueryCollection q)
    {
        string? Text(string key)=>q[key].ToString() is {Length:>0} value?value:null;
        Guid? Id(string key)=>Text(key) is {} s?Guid.TryParse(s,out var v)?v:throw new ArgumentException("Choose a valid published card."):null;
        int? Revision()=>Text("publicationRevision") is {} s?int.TryParse(s,out var v)?v:throw new ArgumentException("Choose an exact publication revision."):null;
        DateTimeOffset? Time(string key)=>Text(key) is {} s?DateTimeOffset.TryParse(s,CultureInfo.InvariantCulture,DateTimeStyles.None,out var v)?v:throw new ArgumentException("Choose a valid UTC observation window."):null;
        CoverageEvidenceCategory? category=Text("category") is {} value?Enum.TryParse<CoverageEvidenceCategory>(value,true,out var c)&&Enum.IsDefined(c)&&!int.TryParse(value,out _)?c:throw new ArgumentException("Choose a supported source category."):null;
        return new(Text("compatibilityId"),Id("opportunityId"),Revision(),Text("requirementKey"),Text("variable"),Text("instrument"),category,Time("fromUtc"),Time("throughUtc"),Text("flightReference"));
    }
    public static void Map(WebApplication app)
    {
        foreach(var native in new[]{false,true}){
            var api=app.MapGroup(native?"/api/member/research-progress":"/api/membership/research-progress");if(!native)api.RequireAuthorization();
            async Task<Guid?> Pilot(OrganizationDevices devices,HttpContext c)=>native?(await devices.Authorize(c,c.RequestAborted)).UserId:null;
            api.MapGet("/campaigns/{id:guid}/coverage",async(Guid id,OrganizationCampaignProgress p,OrganizationDevices devices,HttpContext c)=>await p.Coverage(id,c.User,c.RequestAborted,await Pilot(devices,c)));
            api.MapGet("/campaigns/{id:guid}/coverage/groups",async(Guid id,int? offset,OrganizationCampaignProgress p,OrganizationDevices devices,HttpContext c)=>await p.CoverageGroups(id,Filter(c.Request.Query),offset??0,c.User,c.RequestAborted,await Pilot(devices,c)));
            api.MapGet("/campaigns/{id:guid}/coverage/locations",async(Guid id,int? offset,OrganizationCampaignProgress p,OrganizationDevices devices,HttpContext c)=>await p.CoverageLocations(id,Filter(c.Request.Query),offset??0,c.User,c.RequestAborted,await Pilot(devices,c)));
            api.MapGet("/campaigns/{id:guid}/coverage/altitude",async(Guid id,int? offset,double? bandFeet,OrganizationCampaignProgress p,OrganizationDevices devices,HttpContext c)=>await p.CoverageAltitude(id,Filter(c.Request.Query),offset??0,bandFeet??2000,c.User,c.RequestAborted,await Pilot(devices,c)));
            api.MapGet("/campaigns/{id:guid}/coverage/dates",async(Guid id,int? offset,OrganizationCampaignProgress p,OrganizationDevices devices,HttpContext c)=>await p.CoverageDates(id,Filter(c.Request.Query),offset??0,c.User,c.RequestAborted,await Pilot(devices,c)));
            api.MapGet("/campaigns/{id:guid}/coverage/goals",async(Guid id,int? offset,OrganizationCampaignProgress p,OrganizationDevices devices,HttpContext c)=>await p.CoverageGoals(id,offset??0,c.User,c.RequestAborted,await Pilot(devices,c)));
            api.MapGet("/coverage-goals/{id:guid}",async(Guid id,int? revision,OrganizationCampaignProgress p,OrganizationDevices devices,HttpContext c)=>await p.CoverageGoal(id,revision,c.User,c.RequestAborted,await Pilot(devices,c)));
            api.MapGet("/coverage-goals/{id:guid}/history",async(Guid id,int? offset,OrganizationCampaignProgress p,OrganizationDevices devices,HttpContext c)=>await p.CoverageGoalHistory(id,offset??0,c.User,c.RequestAborted,await Pilot(devices,c)));
            api.MapGet("/coverage-goals/{id:guid}/assessment",async(Guid id,int? revision,string? compatibilityId,OrganizationCampaignProgress p,OrganizationDevices devices,HttpContext c)=>await p.AssessCoverageGoal(id,revision,compatibilityId,c.User,c.RequestAborted,await Pilot(devices,c)));
            if(!native){
                api.MapPost("/campaigns/{id:guid}/coverage/goals",(Guid id,CoverageGoalSave request,OrganizationCampaignProgress p,HttpContext c)=>p.SaveCoverageGoal(id,null,request,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
                api.MapPut("/campaigns/{id:guid}/coverage/goals/{goal:guid}",(Guid id,Guid goal,CoverageGoalSave request,OrganizationCampaignProgress p,HttpContext c)=>p.SaveCoverageGoal(id,goal,request,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
            }
        }
    }
}
