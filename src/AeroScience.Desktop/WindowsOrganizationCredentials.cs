using AeroScience.Engine;
namespace AeroScience.Desktop;

public sealed class WindowsOrganizationCredentials(string configurationDirectory, Func<string>? effectiveLocale = null):IOrganizationCredentials
{
    string Text(string key) => AeroScience.Shared.PresentationText.Text(key, locale: effectiveLocale?.Invoke() ?? "en-US");
    readonly WindowsCredentialStore vault=new(effectiveLocale);
    readonly string prefix="AeroScience/Organization/"+WindowsCredentialStore.Profile(configurationDirectory)+"/";
    public bool Available=>true;
    public string? Read(Guid connection)=>vault.ReadToken(prefix+connection.ToString("N"));
    public void Save(Guid connection,string credential)
    {
        if(connection==Guid.Empty||credential is not {Length:64}||credential.Any(c=>!char.IsAsciiHexDigit(c)))throw new ArgumentException(Text("windows.error.unsupportedOrganizationCredential"));
        vault.Save(prefix+connection.ToString("N"),credential);
    }
    public void Remove(Guid connection)=>vault.Clear(prefix+connection.ToString("N"));
    public bool OwnerKeyAvailable=>true;
    public string? ReadOwnerKey(Guid intent)=>vault.ReadToken(prefix+"Owner/"+intent.ToString("N"));
    public void SaveOwnerKey(Guid intent,string privateKey)
    {
        if(intent==Guid.Empty||privateKey.Length is <100 or >512)throw new ArgumentException(Text("windows.error.unsupportedOwnerKey"));
        byte[] bytes;try{bytes=Convert.FromBase64String(privateKey);}catch(FormatException){throw new ArgumentException(Text("windows.error.unsupportedOwnerKey"));}
        try{using var key=System.Security.Cryptography.ECDsa.Create();key.ImportPkcs8PrivateKey(bytes,out var read);if(read!=bytes.Length||!AeroScience.Shared.OrganizationOwnerProtocol.ValidPublicKey(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())))throw new ArgumentException(Text("windows.error.unsupportedOwnerKey"));vault.Save(prefix+"Owner/"+intent.ToString("N"),privateKey);}
        finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);}
    }
    public void RemoveOwnerKey(Guid intent)=>vault.Clear(prefix+"Owner/"+intent.ToString("N"));
}
