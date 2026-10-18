using System.Runtime.InteropServices;

namespace AeroScience.Engine;

// Official Windows Network List Manager cost API. Unknown never implies unmetered.
public static class NetworkCost
{
    [ComImport,Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager { [PreserveSig] int GetCost(out uint cost,IntPtr destination); }
    public static bool? IsUnmetered()
    {
        if(!OperatingSystem.IsWindows())return null;
        object? instance=null;
        try
        {
            var type=Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"),throwOnError:true)!;
            instance=Activator.CreateInstance(type);var manager=(INetworkCostManager)instance!;
            if(manager.GetCost(out var cost,IntPtr.Zero)<0||cost==0)return null;
            return (cost&1)!=0&&(cost&0xF0006)==0;
        }
        catch(Exception e)when(e is COMException or InvalidCastException or TypeLoadException or PlatformNotSupportedException){return null;}
        finally{if(instance is not null&&Marshal.IsComObject(instance))Marshal.ReleaseComObject(instance);}
    }
}
