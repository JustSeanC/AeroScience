using System.Net;
using System.Net.Sockets;
using System.Text;
namespace AeroScience.Organization;

public interface IDiscordTransport { Task<DiscordResponse> Send(DiscordWebhookAddress destination,byte[] message,CancellationToken ct); }
// No proxy, redirect, alternate endpoint or permissive TLS callback. DNS is resolved exactly once
// per connection; every answer is checked, then the connection is pinned to a checked address.
public sealed class DiscordTransport:IDiscordTransport
{
    internal static ValueTask<Stream> Connect(SocketsHttpConnectionContext context,CancellationToken ct)=>ConnectChecked(context.DnsEndPoint,
        token=>Dns.GetHostAddressesAsync("discord.com",token),async(address,token)=>{var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp){NoDelay=true};try{await socket.ConnectAsync(new IPEndPoint(address,443),token);return socket;}catch{socket.Dispose();throw;}},ct);
    internal static async ValueTask<Stream> ConnectChecked(DnsEndPoint endpoint,Func<CancellationToken,Task<IPAddress[]>> resolve,Func<IPAddress,CancellationToken,Task<Socket>> connect,CancellationToken ct)
    {
        if(endpoint.Host!="discord.com"||endpoint.Port!=443)throw new InvalidOperationException("Unsupported notification destination.");
        var addresses=await resolve(ct);DiscordWebhookPolicy.ValidateResolution(addresses);
        Exception? last=null;
        foreach(var address in addresses){Socket? socket=null;try{
            socket=await connect(address,ct);
            if(socket.RemoteEndPoint is not IPEndPoint peer||!peer.Address.Equals(address)||peer.Port!=443||!DiscordWebhookPolicy.PublicAddress(peer.Address))throw new InvalidOperationException("Unsafe notification connection.");
            return new NetworkStream(socket,true);
        }catch(Exception e)when(e is SocketException or IOException){socket?.Dispose();last=e;}catch{socket?.Dispose();throw;}}
        throw new IOException("Notification connection unavailable.",last);
    }
    public async Task<DiscordResponse> Send(DiscordWebhookAddress destination,byte[] message,CancellationToken ct)
    {
        using var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectCallback=Connect,ConnectTimeout=TimeSpan.FromSeconds(5),MaxConnectionsPerServer=1,AutomaticDecompression=DecompressionMethods.None,MaxResponseHeadersLength=16};
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(10)};
        return await Execute(client,destination,message,ct);
    }
    internal static async Task<DiscordResponse> Execute(HttpClient client,DiscordWebhookAddress destination,byte[] message,CancellationToken ct)
    {
        // ResponseHeadersRead ends HttpClient.Timeout at headers, so bound the entire body read too.
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));ct=deadline.Token;
        // Never log a URL/exception/response body: webhook paths contain credentials.
        try{
            using var request=new HttpRequestMessage(HttpMethod.Post,destination.ExecutionUri()){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact,Content=new ByteArrayContent(message)};
            request.Content.Headers.ContentType=new("application/json");request.Headers.UserAgent.ParseAdd("AeroScience-Organization/0.7");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if(response.Content.Headers.ContentLength>DiscordWebhookPolicy.MaximumResponseBytes)return new(DiscordAcknowledgment.Ambiguous);
            using var stream=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var buffer=new byte[2048];
            while(true){var n=await stream.ReadAsync(buffer,ct);if(n==0)break;if(output.Length+n>DiscordWebhookPolicy.MaximumResponseBytes)return new(DiscordAcknowledgment.Ambiguous);output.Write(buffer,0,n);}
            return DiscordWebhookPolicy.Interpret((int)response.StatusCode,output.ToArray());
        }catch(Exception e)when(e is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or System.Security.Authentication.AuthenticationException){return new(DiscordAcknowledgment.Ambiguous);}
    }
}
