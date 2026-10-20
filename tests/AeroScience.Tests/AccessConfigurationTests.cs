using AeroScience.Engine;
using Xunit;
namespace AeroScience.Tests;

// Configuration/cryptographic state validation needs no running server or database.
public sealed class AccessConfigurationTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("8.8.8.8")]
    [InlineData("127.0.0.1")]
    public void LanBindingRefusesPublicAndWildcardAddresses(string address) => Assert.Throws<InvalidOperationException>(() =>
        new EngineOptions { EnableLan = true, LanAddress = address }.Validate());

    [Fact]
    public void RemotePairingRequiresCodeExpiresAcrossRestartAndLimitsAttempts()
    {
        var access = new AccessControl(); Assert.Null(access.Pair(false, "WRONG")); Assert.Null(access.Pair(false, null));
        var session = access.Pair(false, access.PairingCode); Assert.NotNull(session);
        Assert.NotEqual(access.PairingCode, new AccessControl().PairingCode);
        for (var i = 0; i < 5; i++) Assert.True(access.AllowAttempt("192.168.1.2"));
        Assert.False(access.AllowAttempt("192.168.1.2"));
        Assert.True(access.AllowAttempt("192.168.1.3"));
    }
}
