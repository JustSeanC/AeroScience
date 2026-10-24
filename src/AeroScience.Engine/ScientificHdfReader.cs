using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroScience.Engine;

// Internal capability contracts, not browser DTOs or a NASA product/import declaration.
// Only a trusted caller may supply a configured Library root and approved original hash.
public sealed record HdfOriginal(string RootDirectory,Guid LibraryId,string RelativePath,long Bytes,string Sha256);
public sealed record HdfWindow(string Dataset,ulong[]? Start=null,ulong[]? Count=null,string[]? Attributes=null);
public sealed record HdfReadRequest(int Version,HdfOriginal Original,HdfWindow[] Windows,string[]? RootAttributes=null);
public sealed record HdfAttributeValue(string State,string Type,ulong[] Dimensions,byte[]? LittleEndianValues=null,string[]? Strings=null);
public sealed record HdfWindowResult(string Dataset,string NumericType,ulong[] Dimensions,string Layout,ulong[] Chunks,ulong[]? Start,ulong[]? Count,byte[]? LittleEndianValues,Dictionary<string,HdfAttributeValue> Attributes);
public sealed record HdfBoundary(string Kind,long ProcessMemoryBytes,long ManagedHeapBytes,long AddressSpaceBytes,long ReadBytes,long ElapsedMilliseconds,long WorkingSetBytes);
public sealed record HdfReadResult(int Version,Guid LibraryId,string RelativePath,string OriginalSha256,long OriginalBytes,HdfWindowResult[] Windows,Dictionary<string,HdfAttributeValue> RootAttributes,HdfBoundary Boundary,string Method);
internal sealed record HdfFrame(string Phase,HdfReadResult? Result=null,string? Code=null);
public sealed class ScientificHdfException(string code,string message):IOException(message){public string Code{get;}=code;}
public sealed record HdfWorkerCommand(string FileName,string[] Arguments)
{
    public static HdfWorkerCommand Current()
    {
        var entry=Assembly.GetEntryAssembly()??throw new NotSupportedException("Scientific reader host is unavailable.");
        if(entry.GetName().Name is not ("AeroScience" or "AeroScience.Engine" or "AeroScience.TestHost"))throw new NotSupportedException("This entry point does not host the isolated scientific reader.");
        var executable=Environment.ProcessPath??throw new NotSupportedException("Scientific reader executable is unavailable.");
        return Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase)?new(executable,[entry.Location]):new(executable,[]);
    }
}

public sealed class ScientificHdfReader(HdfWorkerCommand? command=null)
{
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){MaxDepth=16,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    internal const int MaximumRequest=65536,MaximumReply=8*1024*1024;
    internal const long MaximumOriginal=64L*1024*1024*1024,MaximumRead=32L*1024*1024,MaximumChunk=4L*1024*1024,MaximumValues=4L*1024*1024;
    internal const long HeapBytes=128L*1024*1024,ProcessBytes=256L*1024*1024,AddressBytes=4L*1024*1024*1024;
    private static readonly SemaphoreSlim Work=new(1);
    public async Task<HdfReadResult> ReadAsync(HdfReadRequest request,CancellationToken token=default)
    {
        Validate(request);token.ThrowIfCancellationRequested();
        var body=JsonSerializer.SerializeToUtf8Bytes(request,Json);if(body.Length>MaximumRequest)throw new ArgumentException("Scientific reader request exceeds its bounded metadata limit.");
        await Work.WaitAsync(token);
        try
        {
            var selected=command??HdfWorkerCommand.Current();var start=new ProcessStartInfo(selected.FileName){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            var inherited=start.Environment.Where(pair=>new[]{"PATH","SystemRoot","WINDIR","TEMP","TMP","DOTNET_ROOT","DOTNET_ROOT_X64","LD_LIBRARY_PATH"}.Contains(pair.Key,StringComparer.OrdinalIgnoreCase)).ToArray();
            start.Environment.Clear();foreach(var pair in inherited)start.Environment[pair.Key]=pair.Value;
            foreach(var argument in selected.Arguments)start.ArgumentList.Add(argument);start.ArgumentList.Add(ScientificHdfWorker.Command);
            start.Environment["AEROSCIENCE_HDF_WORKER"]="1";start.Environment["AEROSCIENCE_HDF_PARENT_PID"]=Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);start.Environment["DOTNET_GCHeapHardLimit"]="0x8000000";start.Environment["DOTNET_GCConserveMemory"]="9";start.Environment["DOTNET_EnableDiagnostics"]="0";start.Environment["DOTNET_PROCESSOR_COUNT"]="1";start.Environment["DOTNET_gcServer"]="0";
            using var child=Process.Start(start)??throw new IOException("Isolated scientific reader could not start.");
            void Stop(){try{if(!child.HasExited)child.Kill(entireProcessTree:true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
            using var cancel=token.Register(Stop);using var output=new BufferedStream(child.StandardOutput.BaseStream,65536);var stderr=Drain(child.StandardError.BaseStream,8192,Stop);
            try
            {
                try{child.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(Exception error)when(error is InvalidOperationException or System.ComponentModel.Win32Exception){throw new ScientificHdfException("IsolationUnavailable","Scientific reader background priority could not be established.");}
                await child.StandardInput.BaseStream.WriteAsync(body,token);await child.StandardInput.WriteLineAsync();child.StandardInput.Close();
                async Task<HdfFrame> Frame(int seconds)
                {
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
                    try{var data=await Line(output,MaximumReply,deadline.Token);return JsonSerializer.Deserialize<HdfFrame>(data,Json)??throw new IOException("Empty scientific reader response.");}
                    catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new ScientificHdfException("Deadline","Scientific reader exceeded its bounded processing deadline; no result was published.");}
                }
                void Require(HdfFrame frame,string phase){if(frame.Phase=="error")throw Failure(frame.Code);if(frame.Phase!=phase)throw new ScientificHdfException("Protocol","Scientific reader response order is invalid.");}
                Require(await Frame(300),"verified");Require(await Frame(15),"rechecking");var final=await Frame(300);Require(final,"complete");
                using var exit=CancellationTokenSource.CreateLinkedTokenSource(token);exit.CancelAfter(TimeSpan.FromSeconds(5));await child.WaitForExitAsync(exit.Token);await stderr;
                var result=final.Result??throw new IOException("Scientific reader did not return a verified result.");
                if(child.ExitCode!=0||result.Version!=1||result.LibraryId!=request.Original.LibraryId||result.OriginalSha256!=request.Original.Sha256||result.OriginalBytes!=request.Original.Bytes||result.RelativePath!=request.Original.RelativePath||result.Windows.Length!=request.Windows.Length)throw new ScientificHdfException("Protocol","Scientific reader result identity differs from the approved original.");
                return result;
            }
            catch(Exception error)when(error is EndOfStreamException or JsonException){token.ThrowIfCancellationRequested();throw new ScientificHdfException("ResourceOrFormat","Isolated scientific reader stopped without a verified result. The source may be malformed or exceed reader resource limits; no data was imported.");}
            finally{Stop();try{await child.WaitForExitAsync();await stderr;}catch(Exception error)when(error is IOException or OperationCanceledException or InvalidOperationException){}}
        }
        finally{Work.Release();}
    }
    internal static ScientificHdfException Failure(string? code)=>new(code??"Format",code switch
    {
        "IsolationUnavailable"=>"This host cannot establish the required scientific-reader resource isolation. No in-process fallback is permitted.",
        "Ownership"=>"The original is outside its approved Library ownership boundary, is linked, or its identity changed.",
        "Integrity"=>"Original bytes differ from the approved size/hash or changed during reading. No result was published.",
        "ExternalStorage"=>"External/symbolic links and external/virtual HDF storage are unsupported and were rejected before resolution.",
        "Limit"=>"The requested metadata, numeric window, source chunk or input exceeds a bounded scientific-reader limit.",
        _=>"The original HDF structure, numeric type or storage encoding is unsupported or malformed. No data was imported."
    });
    internal static void Validate(HdfReadRequest request)
    {
        if(request.Version!=1)throw new NotSupportedException("Unsupported scientific-reader contract version.");
        if(request.Original is not {} f||f.LibraryId==Guid.Empty||f.Bytes is <=0 or >MaximumOriginal||f.Sha256 is null||f.Sha256.Length!=64||f.Sha256.Any(c=>!char.IsAsciiHexDigit(c)||char.IsUpper(c))||f.RootDirectory is null||f.RootDirectory.Length>4096||!Path.IsPathFullyQualified(f.RootDirectory))throw new ArgumentException("Use an approved original identity, absolute configured root, actual byte count and lowercase SHA-256.");
        PathParts(f.RelativePath,true);
        if(request.Windows is not{Length:>0 and <=16})throw new ArgumentException("Choose 1–16 bounded dataset operations, not an entire cube.");
        Attributes(request.RootAttributes);
        foreach(var w in request.Windows){if(w is null)throw new ArgumentException("Missing dataset operation.");PathParts(w.Dataset,false);Attributes(w.Attributes);if((w.Start is null)!=(w.Count is null)||w.Start is{Length:<1 or >4}||w.Count is{} count&&(count.Length!=w.Start!.Length||count.Any(n=>n==0)))throw new ArgumentException("Use a rank-matched nonempty numeric window or metadata-only inspection.");}
    }
    private static void Attributes(string[]? names){if(names?.Length>16||names?.Any(n=>string.IsNullOrWhiteSpace(n)||n.Length>80||n.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('_' or '-' or '.' or ' ')))==true)throw new ArgumentException("Choose at most 16 bounded attribute names.");}
    private static void PathParts(string path,bool original)
    {
        if(string.IsNullOrWhiteSpace(path)||path.Length>512||path.Contains('\\')||path.Contains(':')||path.StartsWith('/')||path.Split('/').Length>8||path.Split('/').Any(p=>p is "" or "." or ".."||p.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('_' or '-' or '.' or ' '))))throw new ArgumentException("Use a bounded source-relative or HDF dataset path without aliases or traversal.");
        if(original&&!path.StartsWith("Sources/",StringComparison.Ordinal)&&!path.StartsWith("Downloads/",StringComparison.Ordinal))throw new ArgumentException("Read only an approved managed original or owned staging file.");
    }
    internal static async Task<byte[]> Line(Stream input,int maximum,CancellationToken token)
    {
        using var output=new MemoryStream();var block=new byte[4096];
        // The caller retains one buffered stream across frames; never discard prefetched bytes.
        while(true){var n=await input.ReadAsync(block.AsMemory(0,1),token);if(n==0)throw new EndOfStreamException();if(block[0]==10)break;if(output.Length>=maximum)throw new ScientificHdfException("Protocol","Scientific reader response exceeds its byte bound.");output.WriteByte(block[0]);}
        return output.ToArray();
    }
    private static async Task Drain(Stream input,int maximum,Action stop)
    {var buffer=new byte[1024];var total=0;while(true){var n=await input.ReadAsync(buffer);if(n==0)return;total+=n;if(total>maximum){stop();throw new IOException("Scientific reader diagnostics exceeded their bound.");}}}
}
