using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AeroScience.Missions;
namespace AeroScience.Desktop;

// Shared OS vault implementation. Separate targets isolate provider, profile, server and connection.
public sealed class WindowsEarthdataCredentials(string configurationDirectory):IEarthdataCredentials
{
    readonly WindowsCredentialStore vault=new();
    readonly string target="AeroScience/Earthdata/"+WindowsCredentialStore.Profile(configurationDirectory);
    public bool Available=>true;
    public string? ReadToken()=>vault.ReadToken(target);
    public void Save(string token){EarthdataConnection.ValidateToken(token);vault.Save(target,token);}
    public void Clear()=>vault.Clear(target);
}
internal sealed class WindowsCredentialStore
{
    internal static string Profile(string directory)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..32];
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags,Type;
        [MarshalAs(UnmanagedType.LPWStr)]public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;public IntPtr Blob;public uint Persist,AttributeCount;public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]public string? UserName;
    }
    [DllImport("advapi32.dll",EntryPoint="CredReadW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool Read(string name,uint type,int flags,out IntPtr value);
    [DllImport("advapi32.dll",EntryPoint="CredWriteW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool Write(ref Credential value,uint flags);
    [DllImport("advapi32.dll",EntryPoint="CredDeleteW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool Delete(string name,uint type,int flags);
    [DllImport("advapi32.dll")]private static extern void CredFree(IntPtr value);
    public string? ReadToken(string target)
    {
        if(!Read(target,1,0,out var pointer)){if(Marshal.GetLastWin32Error()==1168)return null;throw new IOException("Windows could not open the AeroScience credential vault. Reconnect on this PC.");}
        try{var value=Marshal.PtrToStructure<Credential>(pointer);if(value.BlobSize is 0 or >5120||value.BlobSize%2!=0)throw new IOException("Saved AeroScience credential is invalid; reconnect.");return Marshal.PtrToStringUni(value.Blob,(int)value.BlobSize/2);}
        finally{CredFree(pointer);}
    }
    public void Save(string target,string token)
    {
        if(string.IsNullOrEmpty(token)||token.Length>2560)throw new ArgumentException("Credential is outside vault bounds.");var pointer=Marshal.StringToCoTaskMemUni(token);
        try
        {
            var value=new Credential{Type=1,TargetName=target,UserName="AeroScience protected credential",BlobSize=(uint)(token.Length*2),Blob=pointer,Persist=2};
            if(!Write(ref value,0))throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows could not save the AeroScience token. No plaintext fallback was used.");
        }finally{Marshal.ZeroFreeCoTaskMemUnicode(pointer);}
    }
    public void Clear(string target){if(!Delete(target,1,0)&&Marshal.GetLastWin32Error()!=1168)throw new IOException("Windows could not remove the AeroScience credential. Use Windows Credential Manager to review it.");}
}
