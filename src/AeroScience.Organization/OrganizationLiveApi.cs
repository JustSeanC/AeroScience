using AeroScience.Shared;
namespace AeroScience.Organization;

public static class OrganizationLiveApi
{
    public static bool IsNativeWrite(PathString path)=>path is var p&&(p=="/api/member/live/start"||p=="/api/member/live/update"||p=="/api/member/live/stop");
    public static void Map(WebApplication app)
    {
        var browser=app.MapGroup("/api/membership/live").RequireAuthorization().RequireRateLimiting("live-read");
        browser.MapGet("",(OrganizationLiveFlights live,HttpContext c)=>live.Dashboard(c.User,c.RequestAborted));
        browser.MapGet("/policy",(OrganizationLiveFlights live,HttpContext c)=>live.Policy(c.User,c.RequestAborted));
        browser.MapPost("/policy",(LivePolicyCommand r,OrganizationLiveFlights live,HttpContext c)=>live.SetPolicy(r,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.PublishPolicy);
        app.MapGet("/api/member/live",(OrganizationLiveFlights live,HttpContext c)=>live.Dashboard(c.User,c.RequestAborted,c)).RequireRateLimiting("live-read");
        var native=app.MapGroup("/api/member/live").RequireRateLimiting("live-write");
        native.MapPost("/start",(LiveSharingStart r,OrganizationLiveFlights live,HttpContext c)=>live.Start(r,c,c.RequestAborted));
        native.MapPost("/update",(LiveFlightUpdate r,OrganizationLiveFlights live,HttpContext c)=>live.Update(r,c,c.RequestAborted));
        native.MapPost("/stop",(LiveSharingStop r,OrganizationLiveFlights live,HttpContext c)=>live.Stop(r,c,c.RequestAborted));
        app.MapGet("/api/membership/events",(long? after,OrganizationEvents events,HttpContext c)=>events.Page(after??0,c.User,c.RequestAborted)).RequireAuthorization().RequireRateLimiting("live-read");
    }
}
public sealed class OrganizationLiveWorker(IServiceScopeFactory scopes,ILogger<OrganizationLiveWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));
        while(await timer.WaitForNextTickAsync(stoppingToken))try{
            using var scope=scopes.CreateScope();await scope.ServiceProvider.GetRequiredService<OrganizationLiveFlights>().Sweep(stoppingToken);
        }catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}
        catch(Exception e){log.LogWarning("Live presence cleanup could not finish ({Type}). Positions are never persisted; reads revalidate authorization and age.",e.GetType().Name);}
    }
}
