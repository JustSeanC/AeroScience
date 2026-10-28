using System.Text.Json;
using System.Text;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record ToolbarCommand(int Protocol, Guid RequestId, string Op, string? Name = null, Guid? MissionId = null,
    ScienceMode Mode = ScienceMode.Universal, string? InstrumentId = null, bool? Enabled = null, int? IntervalMs = null,
    string? Category = null, string? Notes = null, Guid? PlanId = null, string? Revision = null, int Offset = 0, long? After = null, int? WaypointIndex = null,
    CollectionControlRequest? Control=null,ResearchCardControl? ResearchControl=null);
public sealed record ToolbarReply(int Protocol, Guid RequestId, bool Ok, string? Error, EngineSnapshot State,
    string[]? Capabilities=null,ToolbarIntelligence? Intelligence=null,ToolbarControlReceipt? Receipt=null,Guid? EngineInstance=null);

// The simulator-native channel reaches the same coordinator as the authenticated browser API.
public sealed class ToolbarCommandHandler(MissionCoordinator coordinator, RouteMapService? maps = null,
    Func<EngineSnapshot,CollectionOverview?,ToolbarIntelligence>? intelligence=null, Func<string,string?>? authorize=null)
{
    // Native JSON is transported as UTF-8, never inserted as HTML. Preserve safe
    // JSON/HTML escaping while avoiding needless ASCII escapes for research text.
    private static readonly JsonSerializerOptions NativeResearchJson=new(WireJson.Options){Encoder=System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)};
    readonly Guid engineInstance=Guid.NewGuid();
    private readonly RouteMapService navigation = maps ?? new(coordinator);
    private readonly Dictionary<Guid, (string Request, string Reply)> completed = [];
    private readonly Queue<Guid> order = new();
    private readonly object gate = new();

    public string Handle(string json)
    {
        lock (gate)
        {
            ToolbarCommand? command = null;
            try
            {
                if (Encoding.UTF8.GetByteCount(json) > 1024) throw new ArgumentException("Toolbar command is too large.");
                command = JsonSerializer.Deserialize<ToolbarCommand>(json, WireJson.Options);
                if (command is null || command.Protocol != 1 || command.RequestId == Guid.Empty)
                    throw new ArgumentException("Invalid toolbar protocol or request identifier.");
                if (authorize?.Invoke(json) is { } rejection) throw new ArgumentException(rejection);
                if(command.Op is "research-state" or "research-control")
                {
                    if(command.Offset<0||command.Offset>MissionStore.MaximumFlightCards)throw new ArgumentException("Use bounded personal-card pages.");
                    var id=command.MissionId??coordinator.Snapshot().ActiveMission?.Id;
                    var state=id is {} flight?coordinator.PersonalResearch(flight):null;
                    // Validate page identity before any durable control side effect.
                    var pageSize=state?.Cards.Any(c=>(c.Card.OrganizationAssignment is not null||AtmosphericResearch.IsCollectionCard(c.Card)||c.Card.Survey is not null||c.Card.ObservationFlight is not null||c.Card.ExperimentalSensor is not null||c.Card.FlightRegime?.TestPoint is not null||c.Card.Procedure is not null))==true?1:4;
                    if(command.Offset%pageSize!=0)throw new ArgumentException("Use bounded four-card pages.");
                    ResearchCardReceipt? receipt=null;
                    if(command.Op=="research-control"){
                        var control=command.ResearchControl??throw new ArgumentException("Missing flight-bound card command.");
                        if(control.RequestId!=command.RequestId)throw new ArgumentException("Native and card command identities must match.");
                        if(command.MissionId!=control.FlightId)throw new ArgumentException("Native and card flight identities must match.");
                        var result=coordinator.ControlPersonalResearch(control);receipt=result.Receipt;state=result.State;
                    }
                    // A full atmospheric question may contain 500 Unicode characters.
                    // Keep exact definitions and the same byte boundary by paging one card.
                    if(state is not null)state=state with{ActiveCardCount=state.Cards.Count(c=>c.Active),ActiveCardNames=state.Cards.Where(c=>c.Active).Take(3).Select(c=>c.Card.Name.Length>32?c.Card.Name[..32]+"…":c.Card.Name).ToArray(),Cards=state.Cards.Skip(command.Offset).Take(pageSize).Select(CompactProcedureCard).ToArray(),NextOffset=command.Offset+pageSize<state.TotalCards?command.Offset+pageSize:null,PageSize=pageSize};
                    var encoded=JsonSerializer.Serialize(new{protocol=1,command.RequestId,ok=true,research=state,receipt},NativeResearchJson);
                    if(Encoding.UTF8.GetByteCount(encoded)>8000&&state is not null){state=state with{ActiveCardNames=[]};encoded=JsonSerializer.Serialize(new{protocol=1,command.RequestId,ok=true,research=state,receipt},NativeResearchJson);}
                    if(Encoding.UTF8.GetByteCount(encoded)>8000)throw new InvalidOperationException("Personal card reply exceeds the native packet bound. Use the dashboard.");
                    return encoded;
                }
                if (command.Op is "map-state" or "map-chunk" or "map-plans")
                {
                    object value = command.Op switch {
                        "map-state" => navigation.State(command.PlanId, command.After, waypointIndex: command.WaypointIndex),
                        "map-chunk" => navigation.Chunk(command.PlanId, command.Revision ?? "", command.Offset),
                        _ => navigation.Plans(command.Offset) };
                    var encoded = JsonSerializer.Serialize(new { protocol = 1, command.RequestId, ok = true, map = value }, WireJson.Options);
                    if (Encoding.UTF8.GetByteCount(encoded) > 8000) throw new InvalidOperationException("Map response exceeds its safe packet bound. Resynchronize the map.");
                    return encoded;
                }
                if(command.Op=="science-control")
                {
                    var control=command.Control??throw new ArgumentException("Missing session-bound control request.");
                    if(control.RequestId!=command.RequestId)throw new ArgumentException("Native and durable request identities must match.");
                    var result=coordinator.ControlScience(control);
                    return Reply(command.RequestId,result.Event.Outcome!=CollectionEventOutcome.Conflict,
                        result.Event.Outcome==CollectionEventOutcome.Conflict?result.Event.Reason:null,
                        new(result.Event.Sequence,result.Event.Outcome,result.Event.SessionId,result.Event.Reason));
                }
                if(command.Op=="marker"&&command.MissionId is {} markerMission){
                    // The existing marker row is the durable receipt. Do not retain
                    // personal note contents in a second in-memory command archive.
                    // Bounded transient replies retain only a request digest, not its text.
                    if(completed.TryGetValue(command.RequestId,out var prior)&&prior.Request!=Digest(json))return Reply(command.RequestId,false,"Request identifier was already used.");
                    coordinator.AddMarker(markerMission,new(command.Category??"observation",command.Notes,command.RequestId));
                    if(prior.Reply is {} previousReply&&coordinator.Snapshot().ActiveMission?.Id==markerMission)return previousReply;
                    var markerReply=Reply(command.RequestId,true,null);Remember(command.RequestId,json,markerReply);return markerReply;
                }
                if (completed.TryGetValue(command.RequestId, out var previous))
                    return previous.Request == Digest(json) ? previous.Reply : Reply(command.RequestId, false, "Request identifier was already used.");
                switch (command.Op)
                {
                    case "state": return Reply(command.RequestId, true, null);
                    case "select-plan": coordinator.SelectPlan(command.PlanId); break;
                    case "start": coordinator.Start(new StartMissionRequest(command.Name ?? "", command.Mode, PlanId: command.PlanId)); break;
                    case "end" when command.MissionId is { } id: coordinator.End(id); break;
                    case "science-pause" when command.MissionId is { } pauseMission: coordinator.SetSciencePaused(pauseMission, true); break;
                    case "science-resume" when command.MissionId is { } resumeMission: coordinator.SetSciencePaused(resumeMission, false); break;
                    case "instrument" when command.InstrumentId is { } instrument:
                        coordinator.ConfigureInstrument(instrument,command.Enabled,command.IntervalMs);break;
                    default: throw new ArgumentException("Unknown toolbar command or missing mission ID.");
                }
                var reply = Reply(command.RequestId, true, null);
                Remember(command.RequestId, json, reply);
                return reply;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException or NotSupportedException)
            {
                var reply = Reply(command?.RequestId ?? Guid.Empty, false, e is JsonException ? "Malformed toolbar JSON." : e.Message);
                if (command is { RequestId: var id, Op: not ("state" or "science-control" or "marker") } && id != Guid.Empty) Remember(id, json, reply);
                return reply;
            }
        }
    }
    private string Reply(Guid id, bool ok, string? error,ToolbarControlReceipt? receipt=null)
    {
        static string? Short(string? value, int length = 100) => value?.Length > length ? string.Concat(value.EnumerateRunes().Take(length).Select(r=>r.ToString())) + "…" : value;
        var state = coordinator.Snapshot();
        var collection=state.ActiveMission is {} flight?coordinator.ScienceCollection(flight.Id):null;
        var summary=intelligence?.Invoke(state,collection)??ToolbarIntelligenceProjection.Create(state,collection);
        // Display-only text bounds keep native CommBus below 8 KiB; database/export values stay intact.
        state = state with
        {
            Connection = state.Connection with { Message = Short(state.Connection.Message, 160)! },
            Telemetry = state.Telemetry is { } t ? t with { Aircraft = Short(t.Aircraft, 80)!, Environment=null, Atmosphere=null, Guidance=null } : null,
            ActiveMission = state.ActiveMission is { } m ? m with { Name = Short(m.Name, 80)!, Aircraft = Short(m.Aircraft, 80) } : null,
            Research = state.Research is { } r ? r with
            {
                ScienceStorageError = Short(r.ScienceStorageError, 120),
                Instruments = r.Instruments.Select(i => i with { Message = Short(i.Message, 80) }).ToArray()
            } : null
        };
        string Encode()=>JsonSerializer.Serialize(new ToolbarReply(1,id,ok,Short(error,180),state,
            ["mission-intelligence-1","session-bound-controls-1","control-revision-1","personal-research-controls-1"],summary,receipt is null?null:receipt with{Reason=Short(receipt.Reason,120)!},engineInstance),WireJson.Options);
        var encoded=Encode();
        if(Encoding.UTF8.GetByteCount(encoded)>8000)
        {
            // Scientific diagnostics remain on the dashboard; avoid losing the entire native bridge.
            state=state with{Research=state.Research is {} research?research with{Units=null,Guidance=null,Atmosphere=null,Instruments=research.Instruments.Select(i=>i with{Measurements=[],Message=null,PayloadProfileName=null,PayloadProfileId=null}).ToArray()}:null};
            summary=summary with{DetailsOmitted=true};encoded=Encode();
        }
        if(Encoding.UTF8.GetByteCount(encoded)>8000){summary=summary with{Metrics=[],Warnings=[],TriggerStatus="Open dashboard for details.",DetailsOmitted=true};encoded=Encode();}
        if(Encoding.UTF8.GetByteCount(encoded)>8000)throw new InvalidOperationException("Native reply exceeds 8 KiB even after display-only reduction. Open bridge diagnostics.");
        return encoded;
    }
    private static string Digest(string json)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    private void Remember(Guid id, string json, string reply)
    {
        if (completed.ContainsKey(id)) return;
        completed.Add(id, (Digest(json), reply)); order.Enqueue(id);
        while (order.Count > 128) completed.Remove(order.Dequeue());
    }
    private static ResearchFlightCard CompactProcedureCard(ResearchFlightCard item)
    {
        if(item.Card.OrganizationAssignment is {} organization)item=item with{Card=item.Card with{OrganizationAssignment=null},Organization=new(string.Concat(organization.OrganizationName.EnumerateRunes().Take(60)),organization.ClaimId,organization.IssuedRevision,"Local monitoring; eligibility pending; Not Submitted")};
        if(item.Card.Procedure is null)return item;
        static string Clip(string value,int maximum)=>string.Concat(value.EnumerateRunes().Take(maximum));
        // Display projection only. Commands always resolve the complete frozen definition in the store.
        var definition=item.Card.Procedure with{Points=item.Card.Procedure.Points.Select(p=>p with{Conditions=p.Conditions with{TestPoint=p.Conditions.TestPoint! with{Name=Clip(p.Conditions.TestPoint.Name,40),Description="Full immutable definition in My Research"}}}).ToArray()};
        var progress=item.Procedure is null?null:item.Procedure with{Explanation="Provisional progress; verify originals after flight.",Points=item.Procedure.Points.Select(p=>p with{Explanation=Clip(p.Explanation,80)}).ToArray()};
        return item with{Card=item.Card with{Procedure=definition},Procedure=progress,OpenAttempt=item.OpenAttempt is null?null:item.OpenAttempt with{PointEvidence=null}};
    }

}
