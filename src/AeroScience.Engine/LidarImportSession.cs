using System.Security.Cryptography;
using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed record LidarImportState(string State,string Message,Guid? SelectionId=null,string? File=null,string? Sha256=null,LidarLasHeader? Header=null,bool Busy=false,Guid? SourceId=null);
public sealed record LidarImportCommand(Guid SelectionId,LidarImportMetadata Metadata);
// Typed local importer, using the original Library copy/verification/lease path.
// Selection/inspection never imports or authorizes network access.
public sealed class LidarImportSession(ScienceStore science):IDisposable
{
    readonly object gate=new();LidarImportState state=new("Choose","Choose a local historical LAS/LAZ original. It remains protected.");string? selected;CancellationTokenSource? cancel;Task? active;
    public bool IsBusy{get{lock(gate)return state.Busy;}}
    public LidarImportState Snapshot(){lock(gate)return state;}
    public async Task Select(string path)
    {
        StorageManager.ValidateLocalPath(path);if(Path.GetExtension(path).ToLowerInvariant() is not(".las" or ".laz"))throw new ArgumentException("Choose an original LAS/LAZ file. COPC indexing is not currently supported.");
        lock(gate){if(state.Busy)throw new InvalidOperationException("Finish or cancel the current point import before choosing another original.");state=new("Inspecting","Reading bounded original LAS metadata; nothing is imported.",Busy:true);selected=null;}
        try
        {
            var result=await Task.Run(()=>{var h=LidarLas.Inspect(path);using var f=File.OpenRead(path);return(h,sha:Convert.ToHexStringLower(SHA256.HashData(f)));});
            lock(gate){selected=Path.GetFullPath(path);state=new("Review","Review original project, license, acquisition dates and vertical limitations. All records will be verified before import publication.",Guid.NewGuid(),selected,result.sha,result.h);}
        }
        catch{lock(gate)state=new("Failed","The selected original could not be verified. Unsupported CRS/format/COPC is refused; choose a supported source.");throw;}
    }
    public void Import(LidarImportCommand request)
    {
        lock(gate)
        {
            if(state.Busy||state.State!="Review"||selected is null||state.SelectionId!=request.SelectionId)throw new InvalidOperationException("Review the current original selection before importing; a stale selection cannot import another file.");
            var path=selected;var sha=state.Sha256;cancel=new();var token=cancel.Token;state=state with{State="Importing",Busy=true,Message="Copying and verifying original historical records under Library storage limits. No flight measurement credit."};
            active=Task.Run(async()=>{
                try{using(var input=File.OpenRead(path))if(Convert.ToHexStringLower(SHA256.HashData(input))!=sha)throw new IOException("Selected original changed after review. Choose it again; no replacement is imported.");var source=await LidarImports.Import(science,path,request.Metadata,token,new Progress<string>(message=>{lock(gate)if(state.Busy)state=state with{Message=message};}));lock(gate)state=state with{State="Completed",Busy=false,SourceId=source.Id,Message="Verified historical point original retained. Choose it explicitly in corridor processing; no recording or collection action was started."};}
                catch(OperationCanceledException){lock(gate)state=state with{State="Cancelled",Busy=false,Message="Local import stopped. Existing retained sources and the user's original file remain protected; managed partial work is visible in Storage."};}
                catch(Exception e)when(e is not OutOfMemoryException){lock(gate)state=state with{State="Failed",Busy=false,Message=e.Message};}
            });
        }
    }
    public void Cancel(){lock(gate){cancel?.Cancel();if(state.Busy)state=state with{Message="Stopping safely; waiting for the original copy/verification to stop."};}}
    public void Dispose(){Cancel();active?.GetAwaiter().GetResult();cancel?.Dispose();}
}
