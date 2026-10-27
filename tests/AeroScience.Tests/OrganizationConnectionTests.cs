using System.Net;
using AeroScience.Engine;
using Xunit;
namespace AeroScience.Tests;

public sealed class OrganizationConnectionTests
{
    [Theory]
    [InlineData("http://example.org")][InlineData("https://user:secret@example.org")][InlineData("https://example.org/path")][InlineData("https://example.org/?token=secret")][InlineData("https://127.0.0.1")][InlineData("https://10.0.0.1")][InlineData("https://169.254.169.254")][InlineData("https://[::1]")][InlineData("https://example.org:444")][InlineData("file:///tmp/secret")][InlineData("https://example.org.")]
    public void UntrustedOrganizationAddressesFailClosed(string address)=>Assert.Throws<ArgumentException>(()=>OrganizationConnections.Origin(address));
    [Fact]public void EnrollmentLinksNormalizeOnlyToTheirSameHttpsOrigin()
    {
        Assert.Equal("https://research.example.org/",OrganizationConnections.Origin("https://research.example.org/#join=not-forwarded-to-desktop").AbsoluteUri);
        Assert.Equal("http://127.0.0.1:5193/",OrganizationConnections.Origin("http://127.0.0.1:5193/#connect=development",true).AbsoluteUri);
        Assert.Throws<ArgumentException>(()=>OrganizationConnections.Origin("http://example.org",true));
    }
    [Theory][InlineData("127.0.0.1")][InlineData("10.1.2.3")][InlineData("172.16.1.1")][InlineData("192.168.1.1")][InlineData("169.254.169.254")][InlineData("100.64.0.1")][InlineData("0.0.0.0")][InlineData("255.255.255.255")][InlineData("224.0.0.1")][InlineData("::1")][InlineData("fe80::1")][InlineData("fc00::1")][InlineData("::ffff:127.0.0.1")][InlineData("2002:7f00:1::1")][InlineData("2001::1")][InlineData("2001:db8::1")][InlineData("192.0.2.1")][InlineData("198.51.100.1")][InlineData("203.0.113.1")]
    public void ResolvedProtectedAddressesCannotReceiveOrganizationRequests(string address)=>Assert.False(OrganizationConnections.PermittedAddress(IPAddress.Parse(address)));
    [Fact]public void SecureVaultIsUnavailableRatherThanPlaintextFallbackOutsideTheCompanion()
    {
        var vault=new UnavailableOrganizationCredentials();Assert.False(vault.Available);Assert.Null(vault.Read(Guid.NewGuid()));Assert.Throws<IOException>(()=>vault.Save(Guid.NewGuid(),new string('a',64)));
    }
}
