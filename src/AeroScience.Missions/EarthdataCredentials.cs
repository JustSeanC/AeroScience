using AeroScience.Shared;
namespace AeroScience.Missions;

// Implemented by the desktop OS vault. No HTTP endpoint accepts or returns a token.
public interface IEarthdataCredentials
{
    bool Available {get;}
    string? ReadToken();
    void Clear();
}
public sealed class UnavailableEarthdataCredentials:IEarthdataCredentials
{
    public bool Available=>false;
    public string? ReadToken()=>null;
    public void Clear(){}
}
public static class EarthdataConnection
{
    public const string ProfileUrl="https://urs.earthdata.nasa.gov/profile";
    public static void ValidateToken(string token)
    {
        if(token.Length is <20 or >2560||token.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '.' or '_' or '-')))
            throw new ArgumentException("Paste the complete NASA-generated User Token, not your password, a URL or a browser cookie.");
    }
    public static EarthdataConnectionStatus Status(IEarthdataCredentials credentials)
    {
        var connected=credentials.ReadToken() is not null;
        return new(credentials.Available,connected,connected?"Earthdata credential saved on this PC. NASA checks its current authorization during preview/download; expired or denied access requires reconnection.":"Connect on the companion PC using NASA's official User Token page. No NASA password is requested by AeroScience.");
    }
}
