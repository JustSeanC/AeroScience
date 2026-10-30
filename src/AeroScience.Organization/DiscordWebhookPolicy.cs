using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AeroScience.Organization;

// Data validation only. This class does not authorize disclosure or perform delivery.
public sealed class DiscordWebhookAddress
{
    readonly string identifier,token;
    DiscordWebhookAddress(string identifier,string token){this.identifier=identifier;this.token=token;}
    public string MaskedIdentifier=>"…"+identifier[^4..];
    public override string ToString()=>"Discord webhook "+MaskedIdentifier;
    internal Uri ExecutionUri()=>new("https://discord.com/api/v10/webhooks/"+identifier+"/"+token+"?wait=true");
    public static DiscordWebhookAddress Parse(string value)
    {
        if(value is null||value.Length>512)throw new ArgumentException("Enter a supported Discord HTTPS incoming webhook. The credential is never included in this error.");
        var match=Regex.Match(value,@"\Ahttps://discord\.com/api/(?:v10/)?webhooks/([1-9][0-9]{16,19})/([A-Za-z0-9._-]{50,200})\z",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking);
        if(!match.Success)throw new ArgumentException("Use the Discord HTTPS incoming webhook URL without a query, fragment, alternate host or port.");
        return new(match.Groups[1].Value,match.Groups[2].Value);
    }
}

public enum DiscordAcknowledgment { Delivered, RateLimited, Rejected, Ambiguous }
public sealed record DiscordResponse(DiscordAcknowledgment State,string? MessageId=null,int? RetryAfterSeconds=null);
public static class DiscordWebhookPolicy
{
    public const int MaximumMessageCharacters=2000,MaximumResponseBytes=16384,MaximumAttempts=5;
    public static bool PublicAddress(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)return PublicAddress(address.MapToIPv4());
        var b=address.GetAddressBytes();
        if(address.AddressFamily==AddressFamily.InterNetwork){
            if(b[0] is 0 or 10 or 127 or >=224||b[0]==100&&b[1] is >=64 and <=127||b[0]==169&&b[1]==254||b[0]==172&&b[1] is >=16 and <=31||b[0]==192&&b[1]==168)return false;
            if(b[0]==192&&b[1]==0&&b[2] is 0 or 2||b[0]==192&&b[1]==88&&b[2]==99||b[0]==198&&b[1] is 18 or 19||b[0]==198&&b[1]==51&&b[2]==100||b[0]==203&&b[1]==0&&b[2]==113)return false;
            return true;
        }
        if(address.AddressFamily!=AddressFamily.InterNetworkV6||address.ScopeId!=0||(b[0]&0xe0)!=0x20)return false;
        if(b[0]==0x20&&b[1]==0x01&&(b[2]<2||b[2]==0x0d&&b[3]==0xb8)||b[0]==0x20&&b[1]==0x02||b[0]==0x3f&&b[1]==0xff&&(b[2]&0xf0)==0)return false;
        return true;
    }
    public static void ValidateResolution(IPAddress[] addresses)
    {
        if(addresses.Length is 0 or >32||addresses.Any(a=>!PublicAddress(a)))throw new InvalidOperationException("Discord destination resolution is unavailable or unsafe. No request was authorized.");
    }
    public static string PlainText(string value,int maximum=160)
    {
        if(maximum is <1 or >MaximumMessageCharacters)throw new ArgumentOutOfRangeException(nameof(maximum));
        var output=new StringBuilder();
        foreach(var c in value){if(output.Length>=maximum)break;if(char.IsControl(c)||char.IsSurrogate(c)||char.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.Format)continue;output.Append("@`*_~[]()<>#\\".Contains(c,StringComparison.Ordinal)?' ':c);}
        return output.ToString().Trim();
    }
    public static byte[] Message(string content)
    {
        if(string.IsNullOrWhiteSpace(content)||content.Length>MaximumMessageCharacters)throw new ArgumentException("Notification content is empty or too long.");
        return JsonSerializer.SerializeToUtf8Bytes(new{content,allowed_mentions=new{parse=Array.Empty<string>(),users=Array.Empty<string>(),roles=Array.Empty<string>(),replied_user=false}});
    }
    public static DiscordResponse Interpret(int status,ReadOnlySpan<byte> body)
    {
        if(body.Length>MaximumResponseBytes)return new(DiscordAcknowledgment.Ambiguous);
        try{
            using var document=JsonDocument.Parse(body.ToArray(),new JsonDocumentOptions{MaxDepth=16});var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return new(DiscordAcknowledgment.Ambiguous);
            if(status==200&&root.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.String&&Regex.IsMatch(id.GetString()!,@"\A[1-9][0-9]{16,19}\z",RegexOptions.NonBacktracking))return new(DiscordAcknowledgment.Delivered,id.GetString());
            if(status==429&&root.TryGetProperty("retry_after",out var retry)&&retry.ValueKind==JsonValueKind.Number&&retry.TryGetDouble(out var seconds)&&double.IsFinite(seconds)&&seconds is >0 and <=3600)return new(DiscordAcknowledgment.RateLimited,RetryAfterSeconds:(int)Math.Ceiling(seconds));
        }catch(JsonException){/* Never surface a response body, token or destination in an error. */}
        return new(status is >=400 and <500 and not 429?DiscordAcknowledgment.Rejected:DiscordAcknowledgment.Ambiguous);
    }
}
