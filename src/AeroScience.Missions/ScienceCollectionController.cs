using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

// Owned by the coordinator command gate. Decisions are durable before acquisition is enabled.
public sealed class ScienceCollectionController(MissionStore store, Guid missionId, Func<InstrumentConfiguration[]> settings)
{
    private Dictionary<string,(DateTimeOffset Anchor,long Slot)> schedule=[];
    private long epoch;
    private readonly Dictionary<string,DateTimeOffset> lastExposure=[];
    public CollectionState State => store.CollectionCheckpoint(missionId) ?? throw new InvalidOperationException("No initialized collection controller.");
    public ScienceCollectionSession? Session => State.CurrentSessionId is {} id ? store.CollectionSession(missionId,id) : null;
    public CollectionCommitResult Control(CollectionControlRequest request, DateTimeOffset at,
        CollectionEventOrigin origin=CollectionEventOrigin.Manual, Guid? occurrence=null,
        CollectionInputReceipt[]? inputs=null, string? plannedSession=null, CollectionHold[]? automaticHolds=null,EvidenceReference? policy=null,string? suppression=null)
    {
        if(origin==CollectionEventOrigin.Manual)EvidenceContracts.Validate(request);
        var current=State;
        var old=origin==CollectionEventOrigin.Manual?store.CollectionControlReceipt(request):null;
        if(old is not null)return old;
        var automatic=origin is CollectionEventOrigin.RouteTrigger or CollectionEventOrigin.Boundary;
        var conflict=!EvidenceContracts.ControlMatches(request,current);
        var next=current with{Revision=current.Revision+1,ControlRevision=conflict?current.ControlRevision:current.Revision+1};
        var session=Session;
        var outcome=conflict?CollectionEventOutcome.Conflict:CollectionEventOutcome.Applied;
        var reason=conflict?"Stale controller revision/session; synchronize before a new command.":request.Action.ToString();
        if(!conflict && automatic && (current.Authority!=CollectionAuthority.Automatic||current.ManualAutomationHold||current.ContinuityHold))
        {outcome=CollectionEventOutcome.Suppressed;reason="Manual authority or continuity hold suppresses automatic action; explicit rearm is required.";}
        if(!conflict&&suppression is not null){outcome=CollectionEventOutcome.Suppressed;reason=suppression;}
        ScienceCollectionSession? changed=null;
        if(outcome==CollectionEventOutcome.Applied)
        {
            var manual=!automatic;
            switch(request.Action)
            {
                case CollectionAction.StartData:
                    if(session is not null || current.Phase==CollectionPhase.Stopped || !settings().Any(c=>c.Enabled))
                    {outcome=CollectionEventOutcome.NoOp;reason="Start requires no open session and an enabled instrument in an active flight.";break;}
                    var id=Guid.NewGuid();
                    var configurations=settings().Select(c=>InstrumentCalibration.Effective(c,Guid.NewGuid(),1,at)).ToArray();
                    changed=new(1,id,missionId,store.CollectionSessionCount(missionId)+1,at,CollectionSessionStatus.Collecting,configurations,[],plannedSession);
                    next=next with{Phase=CollectionPhase.Collecting,CurrentSessionId=id,Authority=manual?CollectionAuthority.Manual:CollectionAuthority.Automatic,
                        ManualAutomationHold=manual||current.ManualAutomationHold,Holds=(current.Holds??[]).Where(h=>h.Kind is CollectionHoldKind.Continuity).ToArray()};
                    break;
                case CollectionAction.PauseData:
                    if(session is null){outcome=CollectionEventOutcome.NoOp;reason="There is no current science session to pause.";break;}
                    next=next with{Phase=CollectionPhase.Paused,Authority=manual?CollectionAuthority.Manual:current.Authority,
                        ManualAutomationHold=manual||current.ManualAutomationHold,
                        Holds=manual?[..(current.Holds??[]).Where(h=>h.Kind!=CollectionHoldKind.Manual),new(CollectionHoldKind.Manual,"Pilot pause",session.Id)]:automaticHolds??current.Holds};
                    changed=session with{Status=CollectionSessionStatus.Paused};break;
                case CollectionAction.ResumeData:
                    if(session is null||current.Phase!=CollectionPhase.Paused){outcome=CollectionEventOutcome.NoOp;reason="Resume requires the same open paused session; ended sessions never reopen.";break;}
                    var remaining=manual?(current.Holds??[]).Where(h=>h.Kind==CollectionHoldKind.Continuity).ToArray():automaticHolds??current.Holds??[];
                    next=next with{Holds=remaining};
                    if(current.ContinuityHold||remaining.Length>0){outcome=CollectionEventOutcome.Suppressed;reason="A continuity, route or boundary hold remains. Resolve/rearm it explicitly.";break;}
                    next=next with{Phase=CollectionPhase.Collecting,Authority=manual?CollectionAuthority.Manual:current.Authority,
                        ManualAutomationHold=manual||current.ManualAutomationHold,Holds=remaining};
                    changed=session with{Status=CollectionSessionStatus.Collecting};break;
                case CollectionAction.EndData:
                    next=next with{Authority=manual?CollectionAuthority.Manual:current.Authority,ManualAutomationHold=manual||current.ManualAutomationHold};
                    if(session is null){outcome=CollectionEventOutcome.NoOp;reason="No current session; automatic starts held after manual End.";break;}
                    next=next with{Phase=CollectionPhase.BetweenSessions,CurrentSessionId=null,LastSessionId=session.Id,
                        Holds=(current.Holds??[]).Where(h=>h.Kind==CollectionHoldKind.Continuity).ToArray()};
                    changed=session with{Status=CollectionSessionStatus.Ended,EndedUtc=at,EndReason=manual?CollectionEndReason.Manual:CollectionEndReason.Trigger};break;
                case CollectionAction.RearmAutomation:
                    next=next with{Authority=CollectionAuthority.Automatic,ManualAutomationHold=false,ContinuityHold=false,
                        Holds=(current.Holds??[]).Where(h=>h.Kind is CollectionHoldKind.Boundary or CollectionHoldKind.Route).ToArray()};
                    reason="Pilot explicitly rearmed future automation; no missed occurrence or session was started.";break;
            }
        }
        if(automatic&&occurrence is {} key)
            next=outcome==CollectionEventOutcome.Applied?next with{FiredOccurrenceIds=[..current.FiredOccurrenceIds??[],key]}:
                next with{SuppressedOccurrenceIds=[..current.SuppressedOccurrenceIds??[],key]};
        var links=(inputs??[]).Select(MissionStore.DescribeCollectionInput).ToArray();
        var transition=new CollectionEvent(1,missionId,next.Revision,DateTimeOffset.UtcNow,at,changed?.Id??session?.Id,request.Action,origin,outcome,
            current.Phase,next.Phase,next.Authority,reason,links,automatic?null:request.RequestId,occurrence,request.ExpectedSessionId,
            PlannedSessionKey:plannedSession,CollectionPolicy:policy,Justification:automatic?null:request.Justification);
        var result=store.CommitCollectionEvidence(new(next,transition,changed,automatic?null:request,inputs));
        if(changed is not null || request.Action==CollectionAction.RearmAutomation){schedule.Clear();epoch=next.Revision;}
        return result;
    }

    public void ContinuityHold(string reason,CollectionInputReceipt[] inputs,DateTimeOffset at)
    {
        var current=State;if(current.ContinuityHold)return;var session=Session;
        var next=current with{Revision=current.Revision+1,ControlRevision=current.Revision+1,ContinuityHold=true,Phase=session is null?current.Phase:CollectionPhase.Paused,
            Holds=[..(current.Holds??[]).Where(h=>h.Kind!=CollectionHoldKind.Continuity),new(CollectionHoldKind.Continuity,reason,session?.Id)]};
        var transition=new CollectionEvent(1,missionId,next.Revision,DateTimeOffset.UtcNow,at,session?.Id,CollectionAction.Unknown,CollectionEventOrigin.Engine,
            CollectionEventOutcome.Applied,current.Phase,next.Phase,next.Authority,reason,inputs.Select(MissionStore.DescribeCollectionInput).ToArray(),ExpectedSessionId:session?.Id);
        store.CommitCollectionEvidence(new(next,transition,session is null?null:session with{Status=CollectionSessionStatus.Paused},Inputs:inputs,EngineDecisionId:Guid.NewGuid()));
        schedule.Clear();epoch=next.Revision;
    }

    public ScienceAcquisitionIntent[] Ingest(TelemetryUpdate input, TelemetryFrame? flightSample=null)
    {
        var current=State;var session=Session;
        var packet=new CollectionInputReceipt("ingress:"+Guid.NewGuid().ToString("N"),input);
        var intents=new List<ScienceAcquisitionIntent>();var nextSchedule=new Dictionary<string,(DateTimeOffset Anchor,long Slot)>(schedule);
        if(current.Phase==CollectionPhase.Collecting&&session is not null&&input.Connection.State==ConnectionState.Connected)
        foreach(var config in session.Configurations.GroupBy(c=>c.Settings.InstrumentId).Select(g=>g.OrderByDescending(c=>c.Revision).First()).Where(c=>c.Settings.Enabled))
        {
            var remote=RemoteAcquisition.Instruments.ContainsKey(config.Settings.InstrumentId);
            if(!remote&&input.Frame is not null)continue; // caller splits auxiliary ingress explicitly
            var time=remote?input.Frame is {IsValid:true,IsPaused:false} frame?frame.TimestampUtc:(DateTimeOffset?)null:
                input.Atmosphere is {Quality:ObservationQuality.Valid} atmosphere?atmosphere.TimestampUtc:null;
            if(time is not {} utc||utc<session.StartedUtc||input.Frame?.IsPaused==true||remote&&(DateTimeOffset.UtcNow-utc).TotalSeconds is < -2 or >6
                ||lastExposure.TryGetValue(config.Settings.InstrumentId,out var previous)&&utc<=previous)continue;
            var run=schedule.GetValueOrDefault(config.Settings.InstrumentId,(utc,-1));
            var slot=(long)Math.Floor(((utc-run.Anchor).TotalMilliseconds+Math.Min(100,config.Settings.IntervalMs*.1))/config.Settings.IntervalMs);
            if(slot<=run.Slot)continue;
            nextSchedule[config.Settings.InstrumentId]=(run.Anchor,slot);
            // Separate atmosphere and position packets retain their own exact receipt/time.
            intents.Add(new(Guid.NewGuid(),missionId,session.Id,config.Settings.InstrumentId,slot,config.Id,epoch,utc,packet.Id));
        }
        if(flightSample is null&&intents.Count==0)return [];
        var next=current with{Revision=current.Revision+1};var link=MissionStore.DescribeCollectionInput(packet);
        var transition=new CollectionEvent(1,missionId,next.Revision,DateTimeOffset.UtcNow,link.ObservedUtc,current.CurrentSessionId,
            CollectionAction.Unknown,CollectionEventOrigin.Engine,CollectionEventOutcome.Applied,current.Phase,current.Phase,current.Authority,
            "Committed exact ingress and eligible acquisition opportunities; no interpolated observations.",[link],ExpectedSessionId:current.CurrentSessionId);
        store.CommitCollectionEvidence(new(next,transition,Inputs:[packet],Intents:intents.ToArray(),FlightSample:flightSample,EngineDecisionId:Guid.NewGuid()));
        schedule=nextSchedule;foreach(var intent in intents)lastExposure[intent.InstrumentId]=intent.ExposureUtc;return intents.ToArray();
    }

    public void ConfigurationChanged(DateTimeOffset at,InstrumentConfiguration[]? updated=null)
    {
        var current=State;var session=Session;
        var added=session is null?[]:(updated??settings()).Select(c=>InstrumentCalibration.Effective(c,Guid.NewGuid(),
            session.Configurations.Where(p=>p.Settings.InstrumentId==c.InstrumentId).Max(p=>p.Revision)+1,at)).ToArray();
        var next=current with{Revision=current.Revision+1,ControlRevision=current.Revision+1};
        var transition=new CollectionEvent(1,missionId,next.Revision,at,at,session?.Id,CollectionAction.Unknown,CollectionEventOrigin.Engine,
            CollectionEventOutcome.Applied,current.Phase,current.Phase,current.Authority,"Frozen effective instrument configuration changed.",[],ExpectedSessionId:session?.Id);
        store.CommitCollectionEvidence(new(next,transition,session is null?null:session with{Configurations=[..session.Configurations,..added]},EngineDecisionId:Guid.NewGuid()));
        schedule.Clear();epoch=next.Revision;
    }
}
