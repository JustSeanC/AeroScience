using AeroScience.Engine;
namespace AeroScience.Desktop;

public sealed class WindowsOrganizationCredentials(string configurationDirectory):IOrganizationCredentials
{
    readonly WindowsCredentialStore vault=new();
    readonly string prefix="AeroScience/Organization/"+WindowsCredentialStore.Profile(configurationDirectory)+"/";
    public bool Available=>true;
    public string? Read(Guid connection)=>vault.ReadToken(prefix+connection.ToString("N"));
    public void Save(Guid connection,string credential)
    {
        if(connection==Guid.Empty||credential is not {Length:64}||credential.Any(c=>!char.IsAsciiHexDigit(c)))throw new ArgumentException("Unsupported organization credential.");
        vault.Save(prefix+connection.ToString("N"),credential);
    }
    public void Remove(Guid connection)=>vault.Clear(prefix+connection.ToString("N"));
}
