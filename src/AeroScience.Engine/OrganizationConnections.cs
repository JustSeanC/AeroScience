using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using AeroScience.Missions;

namespace AeroScience.Engine;

public interface IOrganizationCredentials
{
    bool Available {get;}
    string? Read(Guid connection);
    void Save(Guid connection,string credential);
    void Remove(Guid connection);
}
public sealed class UnavailableOrganizationCredentials:IOrganizationCredentials
{
    public bool Available=>false;
    public string? Read(Guid id)=>null;
    public void Save(Guid id,string credential)=>throw new IOException("Connect on the Windows companion PC; secure organization credential storage is unavailable here.");
    public void Remove(Guid id)=>throw new IOException("Secure organization credential storage is unavailable here.");
}
public sealed record OrganizationSavedConnection(Guid Id,Guid OrganizationId,string Name,string Origin,DateTimeOffset ExpiresUtc);
public sealed record OrganizationConnectionStatus(OrganizationSavedConnection Connection,string Status,OrganizationMemberInformation? Information=null);
public sealed record OrganizationConnectReview(Guid ReviewId,OrganizationPublicIdentity Organization,bool VaultAvailable);
public sealed record OrganizationConnectBegin(Guid PendingId,OrganizationPublicIdentity Organization,string UserCode,string ApprovalUrl,DateTimeOffset ExpiresUtc,int PollSeconds);
public sealed record OrganizationConnectRequest(string Address);
public sealed record OrganizationConnectApproval(Guid ReviewId,Guid OrganizationId,bool Confirm);
public sealed record OrganizationConnectionFinish(Guid PendingId);

// Explicitly user-initiated connections only. Never called by telemetry, acquisition or personal research evaluation.
public sealed class OrganizationConnections:IDisposable
{
    readonly IOrganizationCredentials vault;
    readonly string file;
    readonly HttpClient client;
    readonly bool localDevelopment;
    readonly SemaphoreSlim gate=new(1,1);
    readonly Dictionary<Guid,(OrganizationPublicIdentity Identity,DateTimeOffset Expires)> reviews=[];
    readonly Dictionary<Guid,(OrganizationPublicIdentity Identity,OrganizationDevicePending Device,string Verifier)> pending=[];
    public OrganizationConnections(EngineOptions options,IHostEnvironment environment,IOrganizationCredentials vault)
    {
        this.vault=vault;localDevelopment=options.OrganizationLoopbackDevelopment;
        if(localDevelopment&&!environment.IsDevelopment()&&!environment.IsEnvironment("Testing"))throw new InvalidOperationException("Organization loopback access requires an explicit Development or Testing host.");
        file=Path.Combine(options.ConfigurationDirectory??options.DataDirectory,"organization-connections.json");
        var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false,ConnectTimeout=TimeSpan.FromSeconds(5),MaxConnectionsPerServer=2,PooledConnectionLifetime=TimeSpan.FromMinutes(2),MaxResponseHeadersLength=16};
        handler.ConnectCallback=async(context,ct)=>{
            var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,ct);
            if(addresses.Length==0||addresses.Any(a=>!PermittedAddress(a,localDevelopment)))throw new IOException("This organization address resolves to a protected or unsupported network. Use a public HTTPS server.");
            var socket=new Socket(SocketType.Stream,ProtocolType.Tcp);try{await socket.ConnectAsync(new IPEndPoint(addresses[0],context.DnsEndPoint.Port),ct);return new NetworkStream(socket,true);}catch{socket.Dispose();throw;}
        };
        client=new(handler){Timeout=TimeSpan.FromSeconds(12),MaxResponseContentBufferSize=1048576};
    }
    public static bool PermittedAddress(IPAddress ip,bool development=false)
    {
        if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();
        if(development)return IPAddress.IsLoopback(ip);
        if(IPAddress.IsLoopback(ip)||ip.Equals(IPAddress.Any)||ip.Equals(IPAddress.IPv6Any)||ip.IsIPv6LinkLocal||ip.IsIPv6Multicast||ip.IsIPv6SiteLocal)return false;
        var b=ip.GetAddressBytes();
        if(b.Length==16)return (b[0]&0xe0)==0x20&&!(b[0]==0x20&&b[1]==0x02)&&!(b[0]==0x20&&b[1]==0x01&&b[2]<=1)&&!(b[0]==0x20&&b[1]==0x01&&b[2]==0x0d&&b[3]==0xb8)&&!(b[0]==0x3f&&b[1]==0xff); // Global unicast only, excludes ULA, mapped/tunnel and local metadata.
        return b[0] is not(0 or 10 or 127)&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1] is >=16 and <=31)&&!(b[0]==192&&b[1]==168)&&!(b[0]==100&&b[1] is >=64 and <=127)&&!(b[0]==198&&b[1] is 18 or 19)&&!(b[0]==192&&b[1]==0&&b[2] is 0 or 2)&&!(b[0]==192&&b[1]==88&&b[2]==99)&&!(b[0]==198&&b[1]==51&&b[2]==100)&&!(b[0]==203&&b[1]==0&&b[2]==113);
    }
    public static Uri Origin(string address,bool development=false)
    {
        if(address is null||address.Length>2048||!Uri.TryCreate(address,UriKind.Absolute,out var uri)||uri.UserInfo!=""||uri.AbsolutePath!="/"||uri.Query!=""||uri.HostNameType==UriHostNameType.Unknown||uri.Host.EndsWith('.'))throw new ArgumentException("Enter the organization's HTTPS server address or its root enrollment link.");
        if(development?(uri.Scheme!="http"||!IPAddress.TryParse(uri.Host.Trim('[',']'),out var ip)||!IPAddress.IsLoopback(ip)):(uri.Scheme!="https"||uri.Port!=443||uri.IsLoopback))throw new ArgumentException("Use a public HTTPS organization server on port 443. Only explicit loopback development permits local HTTP.");
        if(IPAddress.TryParse(uri.Host.Trim('[',']'),out var literal)&&!PermittedAddress(literal,development))throw new ArgumentException("Protected network addresses cannot be used as organization servers.");
        return new(uri.GetLeftPart(UriPartial.Authority));
    }
    async Task<T> Request<T>(string origin,string path,object? body,CancellationToken ct,string? credential=null)
    {
        using var request=new HttpRequestMessage(body is null?HttpMethod.Get:HttpMethod.Post,new Uri(new Uri(origin),path));
        if(body is not null)request.Content=JsonContent.Create(body,options:WireJson.Options);
        if(credential is not null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential);
        HttpResponseMessage received;
        try{received=await client.SendAsync(request,ct);}
        catch(TaskCanceledException) when(!ct.IsCancellationRequested){throw new IOException("The organization did not respond in time. Personal Research is unchanged.");}
        catch(HttpRequestException){throw new IOException("The organization could not be reached securely. Check its address or try again later.");}
        using var response=received;
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)throw new UnauthorizedAccessException("Organization authorization is expired or revoked. Connect again if your membership is active.");
        if(!response.IsSuccessStatusCode)throw new IOException("The organization could not complete this request. No local research was changed. Try again or contact its administrator.");
        return await response.Content.ReadFromJsonAsync<T>(WireJson.Options,ct)??throw new IOException("The organization returned an unsupported response.");
    }
    OrganizationSavedConnection[] Read()=>File.Exists(file)?JsonSerializer.Deserialize<OrganizationSavedConnection[]>(File.ReadAllBytes(file),WireJson.Options)??[]:[];
    void Write(OrganizationSavedConnection[] rows)=>PreferenceStore.WriteAtomic(file,rows);
    public async Task<OrganizationSavedConnection[]> List(CancellationToken ct){await gate.WaitAsync(ct);try{return Read();}finally{gate.Release();}}
    public async Task<OrganizationConnectReview> Review(string address,CancellationToken ct)
    {
        var origin=Origin(address,localDevelopment).GetLeftPart(UriPartial.Authority);var identity=await Request<OrganizationPublicIdentity>(origin,"/api/public/organization",null,ct);
        if(identity.Version!=1||identity.Id==Guid.Empty||identity.Origin!=origin||identity.Name is not {Length:>=1 and <=120}||identity.Description is not {Length:<=4000})throw new IOException("The server identity did not match the requested organization address.");
        await gate.WaitAsync(ct);try{foreach(var id in reviews.Where(p=>p.Value.Expires<DateTimeOffset.UtcNow).Select(p=>p.Key).ToArray())reviews.Remove(id);if(reviews.Count>=8)throw new IOException("Finish or cancel an earlier connection review first.");var key=Guid.NewGuid();reviews[key]=(identity,DateTimeOffset.UtcNow.AddMinutes(10));return new(key,identity,vault.Available);}finally{gate.Release();}
    }
    public async Task<OrganizationConnectBegin> Begin(OrganizationConnectApproval approval,CancellationToken ct)
    {
        await gate.WaitAsync(ct);try{
            if(!approval.Confirm||!reviews.TryGetValue(approval.ReviewId,out var review)||review.Expires<=DateTimeOffset.UtcNow||review.Identity.Id!=approval.OrganizationId)throw new ArgumentException("Review and confirm the organization's identity first.");
            if(!vault.Available)throw new IOException("Connect on the Windows companion PC using its secure credential vault.");
            foreach(var id in pending.Where(p=>p.Value.Device.ExpiresUtc<DateTimeOffset.UtcNow).Select(p=>p.Key).ToArray())pending.Remove(id);if(pending.Count>=8)throw new IOException("Finish an earlier pending connection first.");
            var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));var challenge=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
            var device=await Request<OrganizationDevicePending>(review.Identity.Origin,"/api/device/start",new OrganizationDeviceStart(review.Identity.Id,"AeroScience Desktop",challenge),ct);
            if(device.Id==Guid.Empty||device.DeviceSecret is not {Length:64}||device.UserCode is not {Length:11}||device.ApprovalUrl!=review.Identity.Origin+"/#connect="+device.Id||device.ExpiresUtc<=DateTimeOffset.UtcNow||device.ExpiresUtc>DateTimeOffset.UtcNow.AddMinutes(11)||device.PollSeconds is <5 or >30)throw new IOException("Unsupported organization authorization response.");
            if(Read().Any(c=>c.Id==device.Id)||pending.ContainsKey(device.Id))throw new IOException("The server reused an existing connection identity. No saved authorization was changed.");
            pending[device.Id]=(review.Identity,device,verifier);reviews.Remove(approval.ReviewId);
            return new(device.Id,review.Identity,device.UserCode,device.ApprovalUrl,device.ExpiresUtc,device.PollSeconds);
        }finally{gate.Release();}
    }
    public async Task<object> Finish(Guid id,CancellationToken ct)
    {
        await gate.WaitAsync(ct);try{
            if(!pending.TryGetValue(id,out var item)||item.Device.ExpiresUtc<=DateTimeOffset.UtcNow)throw new ArgumentException("This pending connection expired. Start again.");
            var token=await Request<OrganizationDeviceToken>(item.Identity.Origin,"/api/device/poll",new OrganizationDevicePoll(id,item.Identity.Id,item.Device.DeviceSecret,item.Verifier),ct);
            if(token.Status!="approved")return new{status=token.Status};
            if(token.ConnectionId!=id||token.Credential is not {Length:64}||token.Credential.Any(c=>!char.IsAsciiHexDigit(c))||token.ExpiresUtc is null||token.ExpiresUtc<=DateTimeOffset.UtcNow||token.ExpiresUtc>DateTimeOffset.UtcNow.AddHours(25))throw new IOException("Invalid organization credential response.");
            var saved=new OrganizationSavedConnection(id,item.Identity.Id,item.Identity.Name,item.Identity.Origin,token.ExpiresUtc.Value);
            var rows=Read();if(rows.Any(r=>r.OrganizationId==saved.OrganizationId&&r.Origin!=saved.Origin))throw new IOException("This organization identity was previously connected at a different address. Disconnect it before changing servers.");
            if(rows.Any(r=>r.Id==id))throw new IOException("This connection identity is already saved. No existing authorization was replaced.");
            // Never overwrite an existing connection or credential. Both identities remain independently disconnectable.
            vault.Save(id,token.Credential);try{Write(rows.Where(r=>r.Id!=id).Append(saved).ToArray());}catch{vault.Remove(id);throw;}pending.Remove(id);
            return new{status="connected",connection=saved};
        }finally{gate.Release();}
    }
    public async Task<Uri> ApprovalUri(Guid id,CancellationToken ct)
    {
        await gate.WaitAsync(ct);try{if(!pending.TryGetValue(id,out var item)||item.Device.ExpiresUtc<=DateTimeOffset.UtcNow)throw new ArgumentException("Start a new organization connection first.");return new(item.Device.ApprovalUrl);}finally{gate.Release();}
    }
    async Task VerifyIdentity(OrganizationSavedConnection row,CancellationToken ct)
    {
        _=Origin(row.Origin,localDevelopment);
        var identity=await Request<OrganizationPublicIdentity>(row.Origin,"/api/public/organization",null,ct);
        if(identity.Id!=row.OrganizationId||identity.Origin!=row.Origin)throw new IOException("This server's organization identity has changed. No authorization was sent. Disconnect and review a new connection.");
    }
    public async Task<OrganizationConnectionStatus> Status(Guid id,int offset,CancellationToken ct)
    {
        var row=(await List(ct)).SingleOrDefault(r=>r.Id==id)??throw new ArgumentException("This organization connection was not found.");
        var credential=vault.Read(id);if(credential is null||row.ExpiresUtc<=DateTimeOffset.UtcNow)return new(row,"Expired / Reconnect");
        try{await VerifyIdentity(row,ct);var value=await Request<OrganizationMemberInformation>(row.Origin,"/api/member/published?offset="+Math.Max(0,offset),null,ct,credential);if(value.Organization.Id!=row.OrganizationId||value.Organization.Origin!=row.Origin)throw new IOException("Organization identity changed.");return new(row,"Connected",value);}
        catch(UnauthorizedAccessException){vault.Remove(id);return new(row,"Access Revoked / Reconnect");}
        catch(Exception e) when(e is HttpRequestException or IOException or TaskCanceledException){return new(row,"Offline / Unable to Connect");}
    }
    public async Task<object> Disconnect(Guid id,CancellationToken ct)
    {
        await gate.WaitAsync(ct);try{
            var rows=Read();var row=rows.SingleOrDefault(r=>r.Id==id)??throw new ArgumentException("This organization connection was not found.");var credential=vault.Read(id);var revoked=false;
            if(credential is not null)try{await VerifyIdentity(row,ct);await Request<JsonElement>(row.Origin,"/api/device/disconnect",new{},ct,credential);revoked=true;}catch(UnauthorizedAccessException){revoked=true;}catch(Exception e) when(e is HttpRequestException or IOException or TaskCanceledException){}
            vault.Remove(id);Write(rows.Where(r=>r.Id!=id).ToArray());return new{message=revoked?"Disconnected. Server authorization revoked. Personal Research is unchanged.":"Local authorization removed. The server was unavailable; its authorization expires within 24 hours. Personal Research is unchanged.",serverRevoked=revoked};
        }finally{gate.Release();}
    }
    public void Dispose(){client.Dispose();gate.Dispose();}
}
