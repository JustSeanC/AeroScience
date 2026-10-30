using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace AeroScience.Organization;

internal static class DiscordCredentialProtection
{
    internal static IDataProtector Protector(IDataProtectionProvider provider,Guid organization,Guid destination)
        =>provider.CreateProtector("AeroScience/Discord/v1",organization.ToString(),destination.ToString());

    // Recovery validates protected settings without sending, enabling a destination,
    // changing disclosure consent or returning its reusable secret.
    internal static void VerifyRestored(DiscordDestinationRecord destination,Guid organization,IDataProtectionProvider provider)
    {
        const string unavailable="Restored notification credentials do not match the paired organization identity. No delivery or replacement credential was created.";
        if(destination.OrganizationId!=organization||destination.PublicId==Guid.Empty)throw new InvalidOperationException(unavailable);
        if(destination.Deleted){if(destination.ProtectedCredential.Length!=0)throw new InvalidOperationException(unavailable);return;}
        try{
            var address=DiscordWebhookAddress.Parse(Protector(provider,organization,destination.PublicId).Unprotect(destination.ProtectedCredential));
            if(address.MaskedIdentifier!=destination.MaskedIdentifier)throw new InvalidOperationException(unavailable);
        }catch(Exception ex)when(ex is CryptographicException or ArgumentException){throw new InvalidOperationException(unavailable);}
    }
}
