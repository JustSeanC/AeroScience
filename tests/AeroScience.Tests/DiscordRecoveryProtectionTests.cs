using AeroScience.Organization;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace AeroScience.Tests;

public sealed class DiscordRecoveryProtectionTests
{
    const string Webhook="https://discord.com/api/webhooks/123456789012345678/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static DiscordDestinationRecord Destination(IDataProtectionProvider provider,Guid organization)
    {
        var destination=new DiscordDestinationRecord{PublicId=Guid.NewGuid(),OrganizationId=organization,MaskedIdentifier=DiscordWebhookAddress.Parse(Webhook).MaskedIdentifier};
        destination.ProtectedCredential=DiscordCredentialProtection.Protector(provider,organization,destination.PublicId).Protect(Webhook);return destination;
    }
    [Fact]public void PairedKeysVerifyAProtectedDestinationWithoutEnablingOrChangingIt()
    {
        var provider=new EphemeralDataProtectionProvider();var organization=Guid.NewGuid();var destination=Destination(provider,organization);var protectedValue=destination.ProtectedCredential;
        DiscordCredentialProtection.VerifyRestored(destination,organization,provider);
        Assert.False(destination.Enabled);Assert.Equal(protectedValue,destination.ProtectedCredential);Assert.NotEqual(Webhook,protectedValue);
    }
    [Theory][InlineData("key")][InlineData("organization")][InlineData("destination")][InlineData("ciphertext")][InlineData("masked-identity")]
    public void IncompatibleProtectedSettingsCannotBeCalledVerifiedOrExposeASecret(string changed)
    {
        IDataProtectionProvider provider=new EphemeralDataProtectionProvider();var organization=Guid.NewGuid();var destination=Destination(provider,organization);
        switch(changed){
            case "key":provider=new EphemeralDataProtectionProvider();break;
            case "organization":organization=Guid.NewGuid();break;
            case "destination":destination.PublicId=Guid.NewGuid();break;
            case "ciphertext":destination.ProtectedCredential="invalid-protected-value";break;
            case "masked-identity":destination.MaskedIdentifier="other";break;
        }
        var failure=Assert.Throws<InvalidOperationException>(()=>DiscordCredentialProtection.VerifyRestored(destination,organization,provider));
        Assert.DoesNotContain(Webhook,failure.ToString());Assert.DoesNotContain("invalid-protected-value",failure.ToString());Assert.Null(failure.InnerException);Assert.False(destination.Enabled);
    }
    [Fact]public void DeletedCredentialsStayDeletedAndAnUnexpectedRetainedSecretFailsVerification()
    {
        var provider=new EphemeralDataProtectionProvider();var organization=Guid.NewGuid();var destination=Destination(provider,organization);destination.Deleted=true;
        Assert.Throws<InvalidOperationException>(()=>DiscordCredentialProtection.VerifyRestored(destination,organization,provider));
        destination.ProtectedCredential="";destination.MaskedIdentifier="";DiscordCredentialProtection.VerifyRestored(destination,organization,provider);Assert.Empty(destination.ProtectedCredential);
    }
}
