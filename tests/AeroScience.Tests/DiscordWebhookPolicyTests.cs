using System.Net;
using System.Text;
using System.Text.Json;
using AeroScience.Organization;
using Xunit;
namespace AeroScience.Tests;

public sealed class DiscordWebhookPolicyTests
{
    const string Token="synthetic_only_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdef";
    [Fact]public void SupportedAddressIsMaskedAndNeverImplicitlyAuthorizesDisclosure(){var value=DiscordWebhookAddress.Parse("https://discord.com/api/webhooks/123456789012345678/"+Token);Assert.Equal("…5678",value.MaskedIdentifier);Assert.DoesNotContain(Token,value.ToString());}
    [Theory]
    [InlineData("http://discord.com")][InlineData("https://discord.com:443")][InlineData("https://discord.com:8443")][InlineData("https://user@discord.com")][InlineData("https://discord.com.evil.test")][InlineData("https://127.0.0.1")][InlineData("https://discordapp.com")]
    public void UnsupportedAuthorityCannotBecomeAWebhook(string origin){var ex=Assert.Throws<ArgumentException>(()=>DiscordWebhookAddress.Parse(origin+"/api/webhooks/123456789012345678/"+Token));Assert.DoesNotContain(Token,ex.Message);}
    [Theory][InlineData("?wait=false")][InlineData("#secret")][InlineData("/extra")][InlineData("%2f")]
    public void QueryFragmentsEscapesAndExtraPathsAreRejected(string suffix)=>Assert.Throws<ArgumentException>(()=>DiscordWebhookAddress.Parse("https://discord.com/api/webhooks/123456789012345678/"+Token+suffix));
    [Theory][InlineData("127.0.0.1")][InlineData("10.1.1.1")][InlineData("169.254.169.254")][InlineData("100.64.1.1")][InlineData("172.16.1.1")][InlineData("192.168.1.1")][InlineData("192.0.2.1")][InlineData("198.18.0.1")][InlineData("224.1.1.1")][InlineData("::1")][InlineData("::ffff:127.0.0.1")][InlineData("fe80::1")][InlineData("fd00::1")][InlineData("2001:db8::1")][InlineData("2002:7f00:1::1")]
    public void UnsafeResolvedAddressesAreRejected(string text)=>Assert.False(DiscordWebhookPolicy.PublicAddress(IPAddress.Parse(text)));
    [Fact]public void MixedDnsAnswersFailClosed(){Assert.Throws<InvalidOperationException>(()=>DiscordWebhookPolicy.ValidateResolution([IPAddress.Parse("162.159.128.1"),IPAddress.Loopback]));Assert.Throws<InvalidOperationException>(()=>DiscordWebhookPolicy.ValidateResolution([]));DiscordWebhookPolicy.ValidateResolution([IPAddress.Parse("162.159.128.1"),IPAddress.Parse("2606:4700::1")]);}
    [Fact]public void UserFormattingCannotEnableMentions(){var text=DiscordWebhookPolicy.PlainText("@everyone <@123> **test**\u202e\n",80);Assert.DoesNotContain("@",text);Assert.DoesNotContain("<",text);Assert.DoesNotContain("\u202e",text,StringComparison.Ordinal);using var message=JsonDocument.Parse(DiscordWebhookPolicy.Message("AeroScience: "+text));Assert.Empty(message.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());Assert.False(message.RootElement.GetProperty("allowed_mentions").GetProperty("replied_user").GetBoolean());Assert.Throws<ArgumentException>(()=>DiscordWebhookPolicy.Message(new string('x',2001)));}
    [Theory][InlineData("[]")][InlineData("null")][InlineData("{\"retry_after\":\"1\"}")][InlineData("{\"retry_after\":-1}")][InlineData("{\"retry_after\":99999}")]
    public void MalformedRateResponsesCannotAuthorizeAutomaticRetries(string json)=>Assert.Equal(DiscordAcknowledgment.Ambiguous,DiscordWebhookPolicy.Interpret(429,Encoding.UTF8.GetBytes(json)).State);
    [Theory][InlineData(200,"{\"id\":\"123456789012345678\"}",DiscordAcknowledgment.Delivered)][InlineData(204,"",DiscordAcknowledgment.Ambiguous)][InlineData(500,"{}",DiscordAcknowledgment.Ambiguous)][InlineData(200,"{}",DiscordAcknowledgment.Ambiguous)][InlineData(404,"{}",DiscordAcknowledgment.Rejected)][InlineData(429,"{\"retry_after\":1.25}",DiscordAcknowledgment.RateLimited)][InlineData(429,"{}",DiscordAcknowledgment.Ambiguous)]
    public void OnlyAnActualMessageAcknowledgmentCanBeDelivered(int code,string json,DiscordAcknowledgment expected)=>Assert.Equal(expected,DiscordWebhookPolicy.Interpret(code,Encoding.UTF8.GetBytes(json)).State);
}
