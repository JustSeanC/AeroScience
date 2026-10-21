using AeroScience.Shared;
namespace AeroScience.Missions;

// Display-only measurements, independent of durable transfer allowance accounting.
// Retry network bytes never masquerade as unique original-file completion.
public sealed class ScientificTransferProgress(Func<DateTimeOffset>? clock=null)
{
    private readonly object gate=new();private readonly Dictionary<int,long> files=[];private readonly Queue<(DateTimeOffset Time,long Bytes)> observations=[];
    private long network;private string stage="Preparing";private DateTimeOffset changed;private long? total;
    private double? smooth,current,lastReliableRate;private DateTimeOffset lastRateTime,lastNetworkAt;private long lastRateBytes;private int fileCount;private readonly Dictionary<int,long> lengths=[];private int? currentFile;
    private DateTimeOffset Now=>clock?.Invoke()??DateTimeOffset.UtcNow;
    public DateTimeOffset LastMeasuredAt {get{lock(gate)return lastNetworkAt;}}
    public double? LastReliableRate {get{lock(gate)return lastReliableRate;}}
    public void Begin(long? expected,int expectedFiles=0){lock(gate){files.Clear();lengths.Clear();observations.Clear();network=0;total=expected;fileCount=expectedFiles;stage="Preparing";changed=Now;lastRateTime=lastNetworkAt=Now;lastRateBytes=0;smooth=current=null;observations.Enqueue((Now,0));}}
    public void Stage(string value){lock(gate){if(stage!=value){observations.Clear();smooth=current=null;lastRateTime=Now;lastRateBytes=network;if(value=="Downloading")observations.Enqueue((Now,network));}stage=value;changed=Now;}}
    public void Network(long cumulative){lock(gate){if(cumulative<network||Now-changed>TimeSpan.FromSeconds(8)){observations.Clear();smooth=current=null;lastRateTime=Now;lastRateBytes=cumulative;}network=cumulative;changed=lastNetworkAt=Now;var seconds=(Now-lastRateTime).TotalSeconds;if(seconds>=.25){current=Math.Max(0,cumulative-lastRateBytes)/seconds;smooth=smooth is {} prior ? .25*current.Value+.75*prior : current;lastRateTime=Now;lastRateBytes=cumulative;}observations.Enqueue((Now,cumulative));while(observations.Count>256||observations.Count>0&&Now-observations.Peek().Time>TimeSpan.FromSeconds(15))observations.Dequeue();if(observations.Count>=3&&(Now-observations.Peek().Time).TotalSeconds>=2&&cumulative-observations.Peek().Bytes>=65536)lastReliableRate=smooth;}}
    public void File(int index,long prepared,long? expected=null){lock(gate){files[index]=Math.Max(0,prepared);if(expected is {} size)lengths[index]=size;currentFile=index+1;changed=Now;}}
    public LibraryTransferProgress Snapshot(string jobState)
    {
        lock(gate){var now=Now;var samples=observations.ToArray();double? rate=null,low=null,high=null;var active=jobState=="Downloading"&&stage=="Downloading";
            if(active&&samples.Length>=3&&now-samples[^1].Time<TimeSpan.FromSeconds(8)){
                var seconds=(samples[^1].Time-samples[0].Time).TotalSeconds;var bytes=samples[^1].Bytes-samples[0].Bytes;
                if(seconds>=2&&bytes>=65536){rate=smooth??bytes/seconds;lastReliableRate=rate;}
            }
            var prepared=files.Values.Sum();if(total is {} known&&prepared>known){total=null;}
            if(active&&rate is >0&&total is {} expected&&prepared<expected){var seconds=(expected-prepared)/rate.Value;low=Math.Max(1,seconds*.75);high=Math.Max(1,seconds*1.5);}
            var label=jobState switch{"Paused"=>"Paused by User","Queued"=>"Queued — waiting for a shared transfer slot","Interrupted"=>"Interrupted — resume explicitly","Failed"=>"Failed","Completed"=>"Ready",_=>active&&now-changed>=TimeSpan.FromSeconds(8)?"Waiting for Network":stage};
            return new(label,total,total is null?null:prepared,network,rate,low,high,changed,low is null?"Time estimate available after download starts and sustained transfer is measured. Verification/import time is unknown.":"Approximate range from recent measured network throughput (EWMA smoothing) and remaining verified original-file length. Excludes queue, verification and import time.",active&&rate is not null?current:null,lengths.Count(p=>files.GetValueOrDefault(p.Key)==p.Value),fileCount,currentFile);
        }
    }
}
