using System.Collections.Concurrent;
using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

// Independent of the 1 Hz recorder/acquisition path. No queued samples, disk files or restart auto-consent.
public sealed class LiveFlightSharing(OrganizationConnections connections,MissionCoordinator coordinator,MissionStore store)
{
    sealed class Sharing(OrganizationSavedConnection connection,LiveSharingSession session,string label,Guid[] claims,bool truncated)
    {
        public readonly OrganizationSavedConnection Connection=connection;public readonly LiveSharingSession Session=session;
        public readonly Guid[] Claims=claims;public readonly bool Truncated=truncated;public readonly SemaphoreSlim SendGate=new(1,1);public readonly CancellationTokenSource Cancel=new();
        public volatile bool Enabled=true;public long Sequence;public readonly Dictionary<Guid,Guid> PreviousAttempts=[];
        public LocalLiveStatus Status=new(connection.Id,session.FlightId,connection.Name,label,"Sharing",session.Id,null,"Only current state is sent. ALL active members can view it. No scientific approval or evidence upload.");
    }
    static double? Supported(double value,double minimum,double maximum)=>double.IsFinite(value)&&value>=minimum&&value<=maximum?value:null;
    readonly ConcurrentDictionary<Guid,Sharing> sessions=[];
    readonly SemaphoreSlim control=new(1,1);
    public LocalLiveStatus[] Status()=>sessions.Values.Select(x=>x.Status).OrderBy(x=>x.OrganizationName).ToArray();
    ResearchFlightCard[] ScopedCards(OrganizationSavedConnection row,Guid flight)=>store.LiveResearchCards(flight).Cards
        .Where(c=>c.Card.OrganizationAssignment is {} a&&a.OrganizationId==row.OrganizationId&&a.Origin==row.Origin).ToArray();
    public async Task<object> Preview(Guid connection,CancellationToken ct)
    {
        var row=await connections.LiveConnection(connection,ct,true);var flight=coordinator.Snapshot().ActiveMission??throw new InvalidOperationException("Start normal flight recording before choosing Share Live Flight.");
        var dashboard=await connections.LiveDashboard(connection,ct);var cards=ScopedCards(row,flight.Id);
        return new{version=1,connectionId=connection,organization=row.Name,origin=row.Origin,flightId=flight.Id,flightStartedUtc=flight.StartedUtc,policy=dashboard.Policy,
            sharedCards=cards.Take(8).Select(c=>new{c.Card.Name,c.Card.OrganizationAssignment!.ClaimId}),cardsTruncated=cards.Length>8,
            includes="Current position, heading, feet MSL, knots IAS, aircraft type, original source UTC/freshness and bounded organization-card/science-session status. ALL active organization members may view it.",
            excludes="No private notes, raw science, flight tracks, source files, personal research archive, evidence submission or remote control.",
            limitations="Current state only, at most 8 card and 8 instrument summaries. Simulator and synthetic fixture sources are labeled separately. Sharing expires and does not resume after Engine restart."};
    }
    public async Task<LocalLiveStatus> Start(LocalLiveConsent consent,CancellationToken ct)
    {
        if(!consent.ConfirmAllActiveMembers)throw new ArgumentException("Confirm disclosure to ALL active members before Start Sharing.");
        await control.WaitAsync(ct);try{
            if(sessions.TryGetValue(consent.ConnectionId,out var existing)&&existing.Enabled)throw new InvalidOperationException("Stop the existing live share before starting another.");
            if(sessions.Values.Count(x=>x.Enabled)>=4)throw new InvalidOperationException("This companion supports four independently approved organization destinations at once.");
            var row=await connections.LiveConnection(consent.ConnectionId,ct,true);var flight=coordinator.Snapshot().ActiveMission;
            if(flight is null||flight.Id!=consent.FlightId||flight.Status!=MissionStatus.Recording)throw new InvalidOperationException("The active flight changed. Review its sharing preview again.");
            var allCards=ScopedCards(row,flight.Id);var claims=allCards.Take(8).Select(c=>c.Card.OrganizationAssignment!.ClaimId).Distinct().ToArray();
            var value=await connections.LiveStart(row.Id,new(1,Guid.NewGuid(),row.OrganizationId,flight.Id,flight.StartedUtc,consent.ExpectedPolicyRevision,consent.DisplayLabel,claims,true),ct);
            if(coordinator.Snapshot().ActiveMission?.Id!=flight.Id){await connections.LiveStop(row.Id,new(1,Guid.NewGuid(),value.Id,1,"flightEnded"),ct);throw new InvalidOperationException("That flight ended before sharing started. No later flight inherited consent.");}
            // Keep only current destinations; ended entries contain no coordinates and are replaced explicitly.
            foreach(var key in sessions.Where(x=>!x.Value.Enabled&&x.Key!=row.Id).Select(x=>x.Key).ToArray())if(sessions.Count>=8)sessions.TryRemove(key,out _);
            var share=new Sharing(row,value,consent.DisplayLabel,claims,allCards.Length>8);sessions[row.Id]=share;return share.Status;
        }finally{control.Release();}
    }
    public async Task<LocalLiveStatus?> Stop(Guid connection,CancellationToken ct,string reason="pilotStopped")
    {
        if(!sessions.TryGetValue(connection,out var item))return null;
        item.Enabled=false;item.Cancel.Cancel();item.Status=item.Status with{Status="Stopped locally",Message="Sending stopped. Waiting for the server to confirm revocation; unavailable servers remove the last position within 30 seconds."};
        await item.SendGate.WaitAsync(ct);try{
            try{await connections.LiveStop(connection,new(1,Guid.NewGuid(),item.Session.Id,1,reason),ct);item.Status=item.Status with{Status="Stopped",Message="Server confirmed live sharing stopped. Personal recording and science are unchanged."};}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidOperationException){/* Never restart, queue a sample or reuse another connection. */}
            return item.Status;
        }finally{item.SendGate.Release();}
    }
    public async Task Tick(CancellationToken ct)
    {
        await Task.WhenAll(sessions.Values.Where(s=>s.Enabled).Take(4).Select(s=>Send(s,ct)));
    }
    async Task Send(Sharing item,CancellationToken stopping)
    {
        if(!await item.SendGate.WaitAsync(0,stopping))return;
        var ended=false;
        try{
            if(!item.Enabled)return;
            var state=coordinator.OperatingIntelligence(includeHistory:false);var flight=state.Flight.ActiveMission;
            if(flight?.Id!=item.Session.FlightId||flight.Status!=MissionStatus.Recording){ended=true;return;}
            var frame=state.Flight.Telemetry;var now=DateTimeOffset.UtcNow;
            var source=state.Flight.Connection.State!=ConnectionState.Connected?LiveSourceState.Unavailable:frame?.IsPaused==true?LiveSourceState.Paused:frame is not {IsValid:true}||now-frame.TimestampUtc>TimeSpan.FromSeconds(6)||frame.TimestampUtc>now.AddSeconds(2)?LiveSourceState.Stale:LiveSourceState.Connected;
            var ready=source==LiveSourceState.Connected;
            var selected=ScopedCards(item.Connection,flight.Id).Where(c=>c.Card.OrganizationAssignment is {} a&&item.Claims.Contains(a.ClaimId)).ToArray();
            var statuses=new List<LiveCardStatus>();
            foreach(var card in selected.Take(8)){
                var attempt=card.OpenAttempt;var progress=card.Active?LiveResearchProgress.Activated:LiveResearchProgress.Attached;
                if(attempt is not null){item.PreviousAttempts[card.Card.Id]=attempt.Id;progress=LiveResearchProgress.AttemptInProgress;}
                else if(item.PreviousAttempts.TryGetValue(card.Card.Id,out var previous)){
                    try{attempt=store.RecordedResearchAttempt(previous);progress=attempt.PointEvidence is not null?LiveResearchProgress.LocallyEvaluated:attempt.Status==ResearchAttemptStatus.Interrupted?LiveResearchProgress.MissingEvidence:LiveResearchProgress.AttemptRecorded;}
                    catch(ArgumentException){item.PreviousAttempts.Remove(card.Card.Id);}
                }
                statuses.Add(new(card.Card.OrganizationAssignment!.ClaimId,card.Card.Id,card.Card.Revision,attempt?.TestPointId??card.Procedure?.NextPointId,attempt?.Id,progress,card.Active,attempt?.PointEvidence?.Outcome));
            }
            var instruments=state.Sensors.Instruments.Take(8).Select(x=>new LiveInstrumentStatus(x.InstrumentId,x.OperatingStatus,x.ScientificData.Status)).ToArray();
            var payload=new LiveFlightUpdate(1,item.Session.Id,1,++item.Sequence,frame?.TimestampUtc,frame?.Source,source,frame?.Aircraft is {Length:<=120} aircraft&&!aircraft.Any(char.IsControl)?aircraft:null,
                ready?frame!.Latitude:null,ready?frame!.Longitude:null,ready?Supported(frame!.HeadingDegrees,0,360):null,ready?Supported(frame!.AltitudeMslFeet,-5000,200000):null,ready?Supported(frame!.IndicatedAirspeedKnots,0,10000):null,
                statuses.ToArray(),new(state.Collection?.State?.CurrentSessionId,state.Collection?.State?.Phase,instruments,state.Sensors.Instruments.Length>8),item.Truncated);
            using var cancel=CancellationTokenSource.CreateLinkedTokenSource(stopping,item.Cancel.Token);
            var receipt=await connections.LiveUpdate(item.Connection.Id,payload,cancel.Token);
            if(item.Enabled)item.Status=item.Status with{Status=receipt.Applied?"Sharing":"Coalescing",LastSentUtc=receipt.ReceivedUtc,Message="ALL active members can view the permitted current state. Science status is not submission or approval."};
        }
        catch(OperationCanceledException)when(item.Cancel.IsCancellationRequested||stopping.IsCancellationRequested){}
        catch(UnauthorizedAccessException){item.Enabled=false;item.Status=item.Status with{Status="Authorization ended",Message="Sharing stopped. Review membership and authorize a new connection before explicitly starting again."};}
        catch(Exception e)when(e is IOException or InvalidOperationException or ArgumentException){item.Enabled=false;item.Status=item.Status with{Status="Disconnected — Start Sharing required",Message="No updates are queued. The server removes the last position within 30 seconds. Review the connection and start sharing explicitly again."};}
        finally{item.SendGate.Release();if(ended)await Stop(item.Connection.Id,stopping,"flightEnded");}
    }
}
public sealed class LiveFlightSharingWorker(LiveFlightSharing sharing,ILogger<LiveFlightSharingWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));
        while(await timer.WaitForNextTickAsync(stoppingToken))try{await sharing.Tick(stoppingToken);}
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}
        catch(Exception e){log.LogWarning("Live sharing status unavailable ({Type}); no flight data or credentials logged.",e.GetType().Name);}
    }
}
