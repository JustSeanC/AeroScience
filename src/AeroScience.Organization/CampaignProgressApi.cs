using AeroScience.Shared;
namespace AeroScience.Organization;

public static class CampaignProgressApi
{
    static CampaignWorkState? State(string? state)=>state is null or ""?null:Enum.TryParse<CampaignWorkState>(state,true,out var value)&&Enum.IsDefined(value)&&!int.TryParse(state,out _)?value:throw new ArgumentException("Choose a supported campaign progress state.");
    public static void Map(WebApplication app)
    {
        var browser=app.MapGroup("/api/membership/research-progress").RequireAuthorization();
        browser.MapGet("/programs/{id:guid}",(Guid id,int? offset,OrganizationCampaignProgress p,HttpContext c)=>p.Program(id,offset??0,c.User,c.RequestAborted));
        browser.MapGet("/campaigns/{id:guid}",(Guid id,OrganizationCampaignProgress p,HttpContext c)=>p.Campaign(id,c.User,c.RequestAborted));
        browser.MapGet("/campaigns/{id:guid}/cards",(Guid id,int? offset,string? search,string? sort,string? state,OrganizationCampaignProgress p,HttpContext c)=>p.CardPage(id,offset??0,search,sort??"name",State(state),c.User,c.RequestAborted));
        browser.MapGet("/cards/{id:guid}",(Guid id,int? revision,int? offset,OrganizationCampaignProgress p,HttpContext c)=>p.Card(id,revision,offset??0,c.User,c.RequestAborted));
        browser.MapGet("/campaigns/{id:guid}/approved",(Guid id,Guid? opportunity,int? revision,int? offset,string? search,string? sort,OrganizationCampaignProgress p,HttpContext c)=>p.Results(id,opportunity,revision,offset??0,search,sort??"newest",c.User,c.RequestAborted));
        browser.MapGet("/approved/{root:guid}/{card:guid}",(Guid root,Guid card,int? offset,OrganizationCampaignProgress p,HttpContext c)=>p.ResultDetail(root,card,offset??0,c.User,c.RequestAborted));
        browser.MapGet("/approved/{root:guid}/{card:guid}/locations",(Guid root,Guid card,int? offset,OrganizationCampaignProgress p,HttpContext c)=>p.Locations(root,card,offset??0,c.User,c.RequestAborted));
        browser.MapGet("/campaigns/{id:guid}/own",(Guid id,int? offset,OrganizationCampaignProgress p,HttpContext c)=>p.Own(id,offset??0,c.User,c.RequestAborted));
        browser.MapGet("/campaigns/{id:guid}/reviews",(Guid id,int? offset,OrganizationCampaignProgress p,HttpContext c)=>p.Attention(id,offset??0,c.User,c.RequestAborted));
        var native=app.MapGroup("/api/member/research-progress");
        native.MapGet("/programs/{id:guid}",async(Guid id,int? offset,OrganizationCampaignProgress p,OrganizationDevices d,HttpContext c)=>await p.Program(id,offset??0,c.User,c.RequestAborted,(await d.Authorize(c,c.RequestAborted)).UserId));
        native.MapGet("/campaigns/{id:guid}",async(Guid id,OrganizationCampaignProgress p,OrganizationDevices d,HttpContext c)=>await p.Campaign(id,c.User,c.RequestAborted,(await d.Authorize(c,c.RequestAborted)).UserId));
        native.MapGet("/campaigns/{id:guid}/cards",async(Guid id,int? offset,string? search,string? sort,string? state,OrganizationCampaignProgress p,OrganizationDevices d,HttpContext c)=>await p.CardPage(id,offset??0,search,sort??"name",State(state),c.User,c.RequestAborted,(await d.Authorize(c,c.RequestAborted)).UserId));
        native.MapGet("/cards/{id:guid}",async(Guid id,int? revision,int? offset,OrganizationCampaignProgress p,OrganizationDevices d,HttpContext c)=>await p.Card(id,revision,offset??0,c.User,c.RequestAborted,(await d.Authorize(c,c.RequestAborted)).UserId));
        native.MapGet("/campaigns/{id:guid}/approved",async(Guid id,Guid? opportunity,int? revision,int? offset,string? search,string? sort,OrganizationCampaignProgress p,OrganizationDevices d,HttpContext c)=>await p.Results(id,opportunity,revision,offset??0,search,sort??"newest",c.User,c.RequestAborted,(await d.Authorize(c,c.RequestAborted)).UserId));
        native.MapGet("/approved/{root:guid}/{card:guid}",async(Guid root,Guid card,int? offset,OrganizationCampaignProgress p,OrganizationDevices d,HttpContext c)=>await p.ResultDetail(root,card,offset??0,c.User,c.RequestAborted,(await d.Authorize(c,c.RequestAborted)).UserId));
        native.MapGet("/approved/{root:guid}/{card:guid}/locations",async(Guid root,Guid card,int? offset,OrganizationCampaignProgress p,OrganizationDevices d,HttpContext c)=>await p.Locations(root,card,offset??0,c.User,c.RequestAborted,(await d.Authorize(c,c.RequestAborted)).UserId));
        native.MapGet("/campaigns/{id:guid}/own",async(Guid id,int? offset,OrganizationCampaignProgress p,OrganizationDevices d,HttpContext c)=>await p.Own(id,offset??0,c.User,c.RequestAborted,(await d.Authorize(c,c.RequestAborted)).UserId));
    }
}
