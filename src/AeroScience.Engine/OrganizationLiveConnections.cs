using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed partial class OrganizationConnections
{
    public async Task<OrganizationSavedConnection> LiveConnection(Guid id,CancellationToken ct,bool write=false)
    {
        var row=(await List(ct)).SingleOrDefault(x=>x.Id==id)??throw new ArgumentException("This organization connection was not found.");
        if(write&&row.Permission!=OrganizationDesktopPermission.ShareLiveFlight)throw new InvalidOperationException("Authorize Live Flight Sharing in your organization browser first. Read, claim and evidence grants cannot share live flights.");
        if(row.ExpiresUtc<=DateTimeOffset.UtcNow||vault.Read(row.Id) is null)throw new UnauthorizedAccessException("Reconnect this organization before sharing or viewing live flights.");
        return row;
    }
    public async Task<LiveDashboard> LiveDashboard(Guid id,CancellationToken ct)
    {
        var row=await LiveConnection(id,ct,true);await VerifyIdentity(row,ct);
        return await Request<LiveDashboard>(row.Origin,"/api/member/live",null,ct,vault.Read(id));
    }
    public async Task<LiveSharingSession> LiveStart(Guid id,LiveSharingStart request,CancellationToken ct)
    {
        var row=await LiveConnection(id,ct,true);await VerifyIdentity(row,ct);
        if(request.OrganizationId!=row.OrganizationId)throw new ArgumentException("Review the exact organization before sharing.");
        var value=await Request<LiveSharingSession>(row.Origin,"/api/member/live/start",request,ct,vault.Read(id),live:true);
        if(value.OrganizationId!=row.OrganizationId||value.FlightId!=request.FlightId||value.Id==Guid.Empty||value.ConsentRevision!=1||value.UpdateSeconds!=5||value.ExpiresUtc<=DateTimeOffset.UtcNow||value.ExpiresUtc>DateTimeOffset.UtcNow.AddHours(9))throw new IOException("The server did not confirm the exact bounded flight consent.");
        return value;
    }
    public async Task<LiveUpdateReceipt> LiveUpdate(Guid id,LiveFlightUpdate update,CancellationToken ct)
    {
        var row=await LiveConnection(id,ct,true);
        return await Request<LiveUpdateReceipt>(row.Origin,"/api/member/live/update",update,ct,vault.Read(id),live:true);
    }
    public async Task<LiveSharingSession> LiveStop(Guid id,LiveSharingStop stop,CancellationToken ct)
    {
        var row=await LiveConnection(id,ct,true);
        return await Request<LiveSharingSession>(row.Origin,"/api/member/live/stop",stop,ct,vault.Read(id),live:true);
    }
}
