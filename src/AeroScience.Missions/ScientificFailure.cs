namespace AeroScience.Missions;

public enum ScientificFailureKind { ProviderRedirect,Authorization,ProviderUnavailable,UnsupportedFormat,ScientificIntegrity }
public static class ScientificFailure
{
    private const string Key="AeroScience.ScientificFailureKind";
    public static IOException Create(ScientificFailureKind kind,string message)
    {var error=new IOException(message);error.Data[Key]=kind;return error;}
    public static ScientificFailureKind? Kind(Exception error)=>error.Data[Key] is ScientificFailureKind kind?kind:null;
}
