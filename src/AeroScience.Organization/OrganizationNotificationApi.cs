namespace AeroScience.Organization;
public static class OrganizationNotificationApi
{
    public static void Map(WebApplication app)
    {
        var admin=app.MapGroup("/api/organization/notifications").RequireAuthorization(OrganizationApplication.PublishPolicy).RequireRateLimiting("public");
        admin.MapGet("",(OrganizationNotifications n,HttpContext c)=>n.Settings(c.User,c.RequestAborted));
        admin.MapPost("",(DiscordSettingsCommand r,OrganizationNotifications n,HttpContext c)=>n.Save(r,c.User,c.RequestAborted));
        admin.MapPost("/test",(DiscordAction r,OrganizationNotifications n,HttpContext c)=>n.Test(r,c.User,c.RequestAborted));
        admin.MapGet("/history",(long? before,OrganizationNotifications n,HttpContext c)=>n.History(before??0,c.User,c.RequestAborted));
        admin.MapPost("/history/{id:guid}/cancel",(Guid id,DiscordAction r,OrganizationNotifications n,HttpContext c)=>n.Cancel(id,r,c.User,c.RequestAborted));
        admin.MapGet("/announcements",(long? before,OrganizationNotifications n,HttpContext c)=>n.Announcements(c.User,c.RequestAborted,before??0));
        admin.MapPost("/disclose",(DiscordAction r,OrganizationNotifications n,HttpContext c)=>n.Disclose(r,c.User,c.RequestAborted));
        var pilot=app.MapGroup("/api/membership/discord").RequireAuthorization().RequireRateLimiting("public");
        pilot.MapGet("",(OrganizationNotifications n,HttpContext c)=>n.Pilot(c.User,c.RequestAborted));
        pilot.MapPost("/consent",(DiscordConsentCommand r,OrganizationNotifications n,HttpContext c)=>n.Consent(r,c.User,c.RequestAborted));
    }
}
public sealed class OrganizationNotificationWorker(IServiceScopeFactory scopes,ILogger<OrganizationNotificationWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        while(!stoppingToken.IsCancellationRequested){
            try{if(!await timer.WaitForNextTickAsync(stoppingToken))return;using var scope=scopes.CreateScope();await scope.ServiceProvider.GetRequiredService<OrganizationNotifications>().Tick(stoppingToken);}
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}
            catch(Exception){log.LogWarning("Notification processing unavailable. Durable intents remain retained; no credential or response is logged.");}
        }
    }
}
