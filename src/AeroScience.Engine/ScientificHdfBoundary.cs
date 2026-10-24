using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AeroScience.Engine;

internal sealed class ScientificHdfBoundary:IDisposable
{
    private SafeFileHandle? job;
    internal string Kind{get;private set;}="Unavailable";
    internal static ScientificHdfBoundary Enter()
    {
        if(Environment.GetEnvironmentVariable("AEROSCIENCE_HDF_WORKER")!="1")throw new ScientificHdfException("IsolationUnavailable","Scientific parsing is only allowed inside its resource-isolated worker.");
        if(!BitConverter.IsLittleEndian||RuntimeInformation.ProcessArchitecture!=Architecture.X64)throw new NotSupportedException("This decoder foundation supports little-endian x64 only.");
        var boundary=new ScientificHdfBoundary();
        try
        {
            if(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes>ScientificHdfReader.HeapBytes)throw new ScientificHdfException("IsolationUnavailable","Managed heap boundary was not applied before worker startup.");
            if(OperatingSystem.IsWindows())
            {
                boundary.job=CreateJobObject(IntPtr.Zero,null);if(boundary.job.IsInvalid)throw new ScientificHdfException("IsolationUnavailable","Windows worker job could not be created.");
                var limits=new JobLimits{Basic=new(){Flags=0x100|0x8,ActiveProcesses=1},ProcessMemory=(UIntPtr)(ulong)ScientificHdfReader.ProcessBytes};
                if(!SetInformationJobObject(boundary.job,9,ref limits,(uint)Marshal.SizeOf<JobLimits>())||!AssignProcessToJobObject(boundary.job,Process.GetCurrentProcess().Handle))throw new ScientificHdfException("IsolationUnavailable","Windows process memory/job boundaries could not be established.");
                boundary.Kind="Windows job: private-commit cap, one process";
            }
            else if(OperatingSystem.IsLinux())
            {
                Limit(2,(ulong)ScientificHdfReader.ProcessBytes); // RLIMIT_DATA: heap/private anonymous allocations.
                Limit(9,(ulong)ScientificHdfReader.AddressBytes); // RLIMIT_AS: all virtual address mappings.
                Limit(4,0); // No core dump containing source pixels or local metadata.
                boundary.Kind="Linux RLIMIT_DATA/RLIMIT_AS; core dumps disabled";
            }
            else throw new ScientificHdfException("IsolationUnavailable","OS scientific-reader memory isolation is unsupported; no unsafe fallback.");
            return boundary;
        }
        catch{boundary.Dispose();throw;}
    }
    private static void Limit(int kind,ulong maximum)
    {
        if(getrlimit(kind,out var prior)!=0)throw new ScientificHdfException("IsolationUnavailable","Linux worker limits could not be read.");
        var value=new RLimit{Current=Math.Min(prior.Current,maximum),Maximum=Math.Min(prior.Maximum,maximum)};
        if(setrlimit(kind,ref value)!=0)throw new ScientificHdfException("IsolationUnavailable","Linux worker limit could not be established.");
    }
    public void Dispose(){job?.Dispose();job=null;}
    [StructLayout(LayoutKind.Sequential)]private struct RLimit{public ulong Current,Maximum;}
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint PriorityClass,SchedulingClass;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct JobLimits{public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref JobLimits limits,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
    [DllImport("libc",SetLastError=true)]private static extern int getrlimit(int kind,out RLimit limit);
    [DllImport("libc",SetLastError=true)]private static extern int setrlimit(int kind,ref RLimit limit);
}
