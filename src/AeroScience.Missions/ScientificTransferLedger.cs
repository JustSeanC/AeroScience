namespace AeroScience.Missions;

public sealed class ScientificAllowanceWaitException(string message) : IOException(message);
public sealed record TransferBalance(long Spent,long Reserved,long? Available,int Leases);

// Per-flight rolling hour and preflight session volume use independent ledgers.
// Reservations cover the next bounded read, never a speculative whole-scene cap.
public sealed class ScientificTransferLedger(bool rolling=true,Func<DateTimeOffset>? clock=null)
{
    private readonly object gate=new();
    private readonly Dictionary<Guid,long> held=[];
    private readonly Queue<(DateTimeOffset At,long Bytes)> entries=[];
    private long spent,total;
    private bool Rolling=>rolling;
    private void RecordTransfer(long bytes){spent+=bytes;total+=bytes;if(rolling)entries.Enqueue((Now,bytes));}
    private DateTimeOffset Now => clock?.Invoke()??DateTimeOffset.UtcNow;
    public long TotalBytes {get{lock(gate)return total;}}
    private void Trim(){if(rolling)while(entries.TryPeek(out var e)&&e.At<=Now.AddHours(-1)){entries.Dequeue();spent-=e.Bytes;}}
    public TransferBalance Snapshot(long? limit){lock(gate){Trim();var reserved=held.Values.Sum();return new(spent,reserved,limit is {} cap?Math.Max(0,cap-spent-reserved):null,held.Count);}}
    public Lease Acquire(Func<long?> limit){lock(gate){var id=Guid.NewGuid();held.Add(id,0);return new(this,id,limit);}}
    public sealed class Lease(ScientificTransferLedger owner,Guid id,Func<long?> limit):IDisposable
    {
        private long charged;
        private bool disposed;
        public long HeldBytes {get{lock(owner.gate)return owner.held.GetValueOrDefault(id);}}
        public void EnsureTotal(long nextTotal)
        {
            lock(owner.gate)
            {
                ObjectDisposedException.ThrowIf(disposed,this);owner.Trim();
                var wanted=Math.Max(0,nextTotal-charged);var extra=Math.Max(0,wanted-owner.held[id]);
                if(limit() is {} cap&&owner.spent+owner.held.Values.Sum()+extra>cap)
                    throw new ScientificAllowanceWaitException(owner.held.Values.Sum()>0?"Waiting for download allowance reserved by active transfers. Recording continues.":owner.Rolling?"Waiting for download allowance: flight volume limit reached. Rolling-hour usage expires automatically; change the limit or use retained data.":"Waiting for download allowance: preflight session volume limit reached. Change the preflight limit or use retained data.");
                owner.held[id]+=extra;
            }
        }
        public void Charge(long cumulativeBytes)
        {
            lock(owner.gate)
            {
                ObjectDisposedException.ThrowIf(disposed,this);
                var delta=cumulativeBytes-charged;
                if(delta<0||delta>owner.held[id])throw new InvalidOperationException("Scientific transfer accounting requires a reserved read and monotonic actual bytes.");
                owner.held[id]-=delta;charged=cumulativeBytes;owner.RecordTransfer(delta);
            }
        }
        public void Dispose(){lock(owner.gate){if(disposed)return;disposed=true;owner.held.Remove(id);}}
    }
}

public sealed record ScientificDiagnostic(DateTimeOffset Utc,string Event,string Key,string Scope,string Instrument,string? Provider,long Bytes,string? Result);
public static class ScientificDiagnostics
{
    private static readonly object gate=new();
    private static readonly Queue<ScientificDiagnostic> entries=[];
    private static string? file;
    private static Timer? flushTimer;
    private static long revision,flushed=-1;
    public static void Initialize(string directory)
    {
        lock(gate){file=Path.Combine(directory,"logs","scientific-diagnostics.json");flushTimer?.Dispose();flushTimer=new Timer(_=>Flush(),null,TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5));}
    }
    public static void Flush()
    {
        string? destination;ScientificDiagnostic[] snapshot;long snapshotRevision;lock(gate){destination=file;if(revision==flushed)return;snapshot=entries.ToArray();snapshotRevision=revision;}
        if(destination is null||snapshot.Length==0)return;
        try{PreferenceStore.WriteAtomic(destination,snapshot);lock(gate)flushed=snapshotRevision;}catch(Exception e)when(e is IOException or UnauthorizedAccessException){}
    }
    public static void Record(string action,string key,string scope,AeroScience.Shared.RemoteKind kind,string? provider=null,long bytes=0,string? result=null)
    {lock(gate){revision++;if(entries.Count>=512)entries.Dequeue();entries.Enqueue(new(DateTimeOffset.UtcNow,action,key.Length>256?key[..256]:key,scope,kind.ToString(),provider,bytes,result?.Length>2048?result[..2048]:result));}}
    public static ScientificDiagnostic[] Snapshot(){lock(gate)return entries.ToArray();}
}
