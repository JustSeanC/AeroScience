using AeroScience.Shared;
using System.Text.Json;

namespace AeroScience.Missions;

// All mutations pass through this gate, including telemetry and commands from every display.
public sealed class MissionCoordinator(MissionStore store, int samplingIntervalMs, bool requireFreshTelemetry = false,
    Action? ensureRecordingCapacity = null, ResearchOperations? research = null,Func<DateTimeOffset>? utcNow=null,Func<MissionPlan,ReadinessAssessment>? assessPreflight=null)
{
    internal MissionStore Store => store;
    private readonly object gate = new();
    private ScienceCollectionController? collection;
    private GeographicCollectionEngine? geographicCollection;
    private bool collectionFault;
    private bool simulatorPaused;
    private DateTimeOffset Now => utcNow?.Invoke()??DateTimeOffset.UtcNow;
    private DateTimeOffset ControlTime => !requireFreshTelemetry&&frame is {} f&&f.TimestampUtc>Now?f.TimestampUtc:Now;
    private string SelectionPath => Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "selected-plan.json");
    private Guid? SelectedPlan()
    {
        if (!File.Exists(SelectionPath)) return null;
        return JsonSerializer.Deserialize<Guid?>(File.ReadAllText(SelectionPath), WireJson.Options);
    }
    public Guid? SelectPlan(Guid? id)
    {
        lock (gate)
        {
            if (active is not null || maintenance) throw new InvalidOperationException("Finish recording or maintenance before selecting a plan.");
            if (id is {} plan && store.Plan(plan) is null) throw new ArgumentException("Saved plan no longer exists. Refresh missions; no unplanned flight was selected.");
            PreferenceStore.WriteAtomic(SelectionPath, id); return id;
        }
    }
    private readonly EnvironmentTelemetryCache environment = new();
    private TelemetryFrame? frame;
    private Mission? active;
    private DateTimeOffset? lastRecordedUtc;
    private string? lastAircraft;
    private DateTimeOffset? samplingAnchor;
    private long lastSlot = -1, received, skipped, missing;
    private DateTimeOffset? firstRecordedUtc;
    private bool maintenance;
    private RecordingStopInfo? recordingStop = store.LastRecordingStop();
    private ConnectionInfo connection = new(ConnectionState.Initializing, TelemetrySource.Simulator,
        "Waiting for telemetry provider", DateTimeOffset.UtcNow);

    public EngineSnapshot Snapshot()
    {
        lock (gate)
        {
            var count = active?.SampleCount ?? 0;
            var seconds = (lastRecordedUtc - firstRecordedUtc)?.TotalSeconds;
            return new(connection, frame, active, samplingIntervalMs, Sampling: new(received, count, skipped, missing,
                count > 1 && seconds > 0 ? (count - 1) / seconds : null, firstRecordedUtc, lastRecordedUtc,
                active is not null && active.DurationSeconds >= 1 ? count / active.DurationSeconds : null,
                active is not null && firstRecordedUtc is not null ? Math.Max(0, (firstRecordedUtc.Value - active.StartedUtc).TotalSeconds) : null),
                Research: research?.Snapshot(connection, frame), RecordingStop: recordingStop, SelectedPlanId: SelectedPlan());
        }
    }

    public (EngineSnapshot Flight, CollectionOverview? Collection, SensorOperatingOverview Sensors) OperatingIntelligence()
    {
        lock(gate)
        {
            var flight=Snapshot();var overview=active is {} mission?ScienceCollection(mission.Id):null;
            var sensors=SensorOperatingProjection.Create(flight,overview?.State,research?.Configurations??[],
                research?.EffectiveConfigurations??[],research?.OperatingReports??[],research?.OperatingContextUtc,Now,overview?.Fault==true);
            return (flight,overview,sensors);
        }
    }

    public void Apply(TelemetryUpdate update)
    {
        lock(gate)
        {
            if(active is not null&&connection.State!=update.Connection.State)store.AddEvent(active.Id,Now,"connection",update.Connection.Message);
            connection=update.Connection;environment.Apply(update);
            if(update.Frame is {IsValid:true} incoming)update=update with{Frame=environment.Enrich(incoming)};
            frame=connection.State!=ConnectionState.Connected?null:update.Frame is {IsValid:true}?update.Frame:update.Frame is not null?null:frame;
            if(active is null){research?.Observe(update,frame);return;}
            var pauseTransition = update.Frame is { IsValid: true } pauseFrame && pauseFrame.IsPaused != simulatorPaused ? pauseFrame : null;
            TelemetryFrame? sample=null;long slot=-1;var capacityOkay=true;
            if(update.Frame is not null&&frame is not null)
            {
                received++;
                if(frame.IsPaused||lastRecordedUtc is not null&&frame.TimestampUtc<=lastRecordedUtc)skipped++;
                else
                {
                    samplingAnchor??=frame.TimestampUtc;
                    slot=(long)Math.Floor(((frame.TimestampUtc-samplingAnchor.Value).TotalMilliseconds+Math.Min(100,samplingIntervalMs*.1))/samplingIntervalMs);
                    if(slot<=lastSlot)skipped++;
                    else sample=frame with{Guidance=research?.CurrentGuidance(connection,frame)};
                }
            }
            try
            {
                if(sample is not null){capacityOkay=false;ensureRecordingCapacity?.Invoke();capacityOkay=true;}
                if (pauseTransition is not null)
                {
                    store.AddEvent(active.Id, pauseTransition.TimestampUtc, pauseTransition.IsPaused ? "simulator-paused" : "simulator-resumed",
                        pauseTransition.IsPaused ? "Simulator paused; this interval cannot establish a research hold." : "Simulator resumed.");
                    simulatorPaused = pauseTransition.IsPaused;
                }
                var intents=new List<ScienceAcquisitionIntent>();
                if(collection is not null&&!collectionFault)
                {
                    geographicCollection?.Observe(update,Now);
                    var eligible=sample is null?update:update with{Frame=sample};
                    if(eligible.Frame is null&&frame?.IsPaused==true)eligible=eligible with{Atmosphere=null};
                    intents.AddRange(collection.Ingest(eligible,sample));
                    if(update.Frame is not null&&update.Atmosphere is not null&&frame?.IsPaused!=true)
                        intents.AddRange(collection.Ingest(update with{Frame=null,Guidance=null,Environment=null}));
                    research?.ApplyCollection(collection.State,collection.Session,intents.ToArray());
                }
                else if(sample is not null)store.Append(active.Id,sample);
                if(sample is not null)
                {
                    if(lastSlot>=0&&slot>lastSlot+1){missing+=slot-lastSlot-1;store.AddEvent(active.Id,sample.TimestampUtc,"telemetry-gap",$"No committed observation in {slot-lastSlot-1} configured recording intervals.");}
                    if(lastAircraft is not null&&lastAircraft!=sample.Aircraft)store.AddEvent(active.Id,sample.TimestampUtc,"aircraft-changed",$"Aircraft changed to {sample.Aircraft}.");
                    firstRecordedUtc??=sample.TimestampUtc;lastRecordedUtc=sample.TimestampUtc;lastSlot=slot;lastAircraft=sample.Aircraft;
                    active=active with{Aircraft=active.Aircraft??sample.Aircraft,Source=active.Source??sample.Source,
                        Departure=active.Departure??new(sample.Latitude,sample.Longitude),Arrival=new(sample.Latitude,sample.Longitude),SampleCount=active.SampleCount+1,
                        MinimumAltitudeFeet=Math.Min(active.MinimumAltitudeFeet??sample.AltitudeMslFeet,sample.AltitudeMslFeet),MaximumAltitudeFeet=Math.Max(active.MaximumAltitudeFeet??sample.AltitudeMslFeet,sample.AltitudeMslFeet)};
                }
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                collectionFault=true;research?.HoldCollection();
                // If the independent evidence operation fails but the flight row remains writable,
                // continue raw recording safely; no unaudited scientific work is dispatched.
                try
                {
                    if(!capacityOkay)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
                    if(sample is not null&&!store.HasTelemetryTime(active.Id,sample.TimestampUtc))store.Append(active.Id,sample);
                    active=store.Detail(active.Id)!.Mission;
                    if(sample is not null){lastRecordedUtc=sample.TimestampUtc;lastSlot=slot;firstRecordedUtc??=sample.TimestampUtc;lastAircraft=sample.Aircraft;}
                    store.AddEvent(active.Id,DateTimeOffset.UtcNow,"collection-storage-hold",error.Message);
                }
                catch(Exception fatal)when(fatal is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    recordingStop=new(fatal is StorageLimitException?"storageLimitReached":"storageUnavailable",fatal.Message,active.Id,DateTimeOffset.UtcNow);
                    try{store.AddEvent(active.Id,recordingStop.TimestampUtc,recordingStop.Code,fatal.Message);store.Finish(active.Id,MissionStatus.Interrupted,lastRecordedUtc??active.StartedUtc,"Recording storage unavailable.");}catch(Exception e)when(e is IOException or Microsoft.Data.Sqlite.SqliteException){}
                    research?.End();active=null;
                }
                return;
            }
            try{if(!collectionFault)research?.Observe(sample is null?update:update with{Frame=sample},frame);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {ScientificDiagnostics.Record("science-processing-error",active.Id.ToString(),"flight",RemoteKind.Terrain,result:error.Message);}
        }
    }

    public Mission Start(string? name, ScienceMode mode = ScienceMode.Universal) => Start(new StartMissionRequest(name ?? "", mode));

    public Mission Start(StartMissionRequest request)
    {
        var name = request.Name;
        var mode = request.Mode;
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Choose Universal or Realistic mode.");
        if (name?.Any(char.IsControl) == true) throw new ArgumentException("Mission names cannot contain control characters.");
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Use a mission name of 1–100 characters without control characters.");
        lock (gate)
        {
            if (maintenance) throw new InvalidOperationException("Storage/settings maintenance is in progress. Try again after it completes.");
            if (active is not null) throw new InvalidOperationException("A mission is already recording.");
            if(request.CheckSelectedPlan&&SelectedPlan()!=request.PlanId)throw new InvalidOperationException("The selected mission plan changed. Review the current selection before starting.");
            var plan = request.PlanId is { } planId ? store.Plan(planId) ?? throw new ArgumentException("Saved mission plan not found.") : null;
            if(request.ExpectedPlanRevision is not null&&(plan is null||ScientificComposites.Identity(plan)!=request.ExpectedPlanRevision))
                throw new InvalidOperationException("The saved mission plan changed. Review its current revision before starting.");
            if (plan is not null) mode = plan.Mode;
            var configurations = InstrumentProfiles.Freeze(ScienceCatalog.Normalize(request.Instruments ?? plan?.Instruments ?? research?.Configurations));
            try { ensureRecordingCapacity?.Invoke(); }
            catch (Exception error) when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { throw new InvalidOperationException(error.Message, error); }
            if (requireFreshTelemetry && (connection.State != ConnectionState.Connected || frame is null
                || DateTimeOffset.UtcNow - frame.TimestampUtc > TimeSpan.FromSeconds(6) || frame.IsPaused))
                throw new InvalidOperationException("Live aircraft telemetry is unavailable. Mission History remains accessible.");
            var armed=request.ArmScience||plan?.StartScienceArmed==true||plan?.Collection?.InitialPolicy==InitialCollectionPolicy.Armed;
            if(!armed)research?.EnsureCanStart(configurations);
            var readiness=plan is null?null:assessPreflight?.Invoke(plan);
            var now = Now;
            var id = store.Create(name, frame, now, mode, plan, configurations, request.Objective);
            active = store.Detail(id)!.Mission;
            recordingStop = null;
            research?.Begin(active, store.Research(id));
            if(research is not null)
            {
                try
                {
                    store.InitializeCollectionEvidence(new(1,id,0,CollectionPhase.Armed,plan?.Collection?.InitialPolicy==InitialCollectionPolicy.Armed?CollectionAuthority.Automatic:CollectionAuthority.Manual,ControlRevision:0),plan?.Collection,
                        MissionRubrics.Freeze(id,now,mode,store.RecordedPlanHash(id),plan,configurations,request.Objective,readiness));
                    store.AddEvent(id,now,"science-armed","Flight recording active; science awaits an explicit session start.");
                    collection=new(store,id,()=>research!.Configurations);collectionFault=false;
                    geographicCollection=plan?.Collection is null?null:new(plan,collection);
                    research.PublicationLease=AcquireSciencePublication;
                    if(!armed&&configurations.Any(c=>c.Enabled))
                        collection.Control(new(1,id,Guid.NewGuid(),0,CollectionAction.StartData,null),now);
                    research.ApplyCollection(collection.State,collection.Session,[]);
                    research.ManagedPause=SetManagedPaused;
                }
                catch
                {
                    research.HoldCollection();collectionFault=true;collection=null;geographicCollection=null;
                    try{store.Finish(id,MissionStatus.Interrupted,now,"Collection initialization failed; no scientific acquisition was enabled.");}
                    catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){ScientificDiagnostics.Record("collection-initialization-error",id.ToString(),"flight",RemoteKind.Terrain,result:error.Message);}
                    try{research.End();}finally{active=null;}
                    throw;
                }
            }
            lastRecordedUtc = null;
            simulatorPaused = false;
            firstRecordedUtc = samplingAnchor = null;
            lastSlot = -1; received = skipped = missing = 0;
            lastAircraft = null;
            // Take a fresh sample on the next provider update; do not backdate the first sample.
            return active;
        }
    }

    public void ConfigureInstruments(InstrumentConfiguration[] configurations)
    {
        lock (gate)
        {
            if (maintenance) throw new InvalidOperationException("Storage maintenance is in progress.");
            if (research is null) throw new InvalidOperationException("Scientific acquisition is unavailable in this host.");
            var next=InstrumentProfiles.Freeze(ScienceCatalog.Normalize(configurations));
            if(JsonSerializer.Serialize(next,WireJson.Options)==JsonSerializer.Serialize(research.Configurations,WireJson.Options))return;
            research.EnsureCanStart(next);
            collection?.ConfigurationChanged(ControlTime,next);
            try{research.Configure(next);if(collection is not null)research.ApplyCollection(collection.State,collection.Session,[]);}
            catch{collectionFault=true;research.HoldCollection();throw;}
        }
    }
    public void ConfigureInstrument(string id,bool? enabled,int? intervalMs)
    {
        lock(gate)
        {
            if(research is null)throw new InvalidOperationException("Scientific acquisition is unavailable in this host.");
            var current=research.Configurations;if(!current.Any(c=>c.InstrumentId==id))throw new ArgumentException("Unknown instrument.");
            ConfigureInstruments(current.Select(c=>c.InstrumentId==id?c with{Enabled=enabled??c.Enabled,IntervalMs=intervalMs??c.IntervalMs}:c).ToArray());
        }
    }
    private bool SetManagedPaused(bool paused,DateTimeOffset? timestamp)
    {
        lock(gate)
        {
            var state=collection?.State??throw new InvalidOperationException("No active collection controller.");
            if((state.Phase==CollectionPhase.Paused)==paused)return false;
            var result=collection!.Control(new(1,state.MissionId,Guid.NewGuid(),state.Revision,paused?CollectionAction.PauseData:CollectionAction.ResumeData,state.CurrentSessionId),timestamp??ControlTime);
            research?.ApplyCollection(collection.State,collection.Session,[],result.Event.InputUtc);
            return result.Event.Outcome==CollectionEventOutcome.Applied;
        }
    }
    public ResearchState SetSciencePaused(Guid id, bool paused)
    {
        lock (gate)
        {
            if (maintenance || active?.Id != id) throw new InvalidOperationException("This mission is not currently recording.");
            if (research is null) throw new InvalidOperationException("Science Collection is unavailable in this host.");
            var state=collection?.State??throw new InvalidOperationException("No collection controller.");
            ControlScience(new(1,id,Guid.NewGuid(),state.Revision,paused?CollectionAction.PauseData:CollectionAction.ResumeData,state.CurrentSessionId));
            return research.Snapshot(connection, frame);
        }
    }
    public CollectionOverview ScienceCollection(Guid id)
    {
        lock(gate)
        {
            var state=store.CollectionCheckpoint(id);var recording=active?.Id==id&&!maintenance;
            var current=state?.CurrentSessionId is {} sid?store.CollectionSession(id,sid):null;
            var actions=Enum.GetValues<CollectionAction>().Where(a=>a!=CollectionAction.Unknown).Select(a=>
            {
                var allowed=recording&&state is not null&&research is not null&&a switch
                {
                    CollectionAction.StartData=>state.CurrentSessionId is null&&state.Phase!=CollectionPhase.Stopped&&research.Configurations.Any(c=>c.Enabled),
                    CollectionAction.PauseData=>state.Phase==CollectionPhase.Collecting,
                    CollectionAction.ResumeData=>state.Phase==CollectionPhase.Paused&&!state.ContinuityHold,
                    CollectionAction.EndData=>state.CurrentSessionId is not null,
                    CollectionAction.RearmAutomation=>true,
                    _=>false
                };
                return new CollectionActionAvailability(a,allowed,allowed?"Session-bound command; state is checked again when applied.":!recording?"Flight is not recording.":a==CollectionAction.StartData&&research?.Configurations.Any(c=>c.Enabled)!=true?"Select an enabled instrument before starting science.":"Not available in the current session/hold state. Resolve holds and explicitly rearm when appropriate.");
            }).ToArray();
            return new(state,store.CollectionSessions(id),recording&&collectionFault,["multi-session-1","session-bound-controls-1","control-revision-1"],actions,current,recording?geographicCollection?.Areas(Now):null);
        }
    }

    public CollectionCommitResult ControlScience(CollectionControlRequest request)
    {
        lock(gate)
        {
            if(store.CollectionControlReceipt(request) is {} previous)return previous;
            if(maintenance||active?.Id!=request.MissionId||collection is null)throw new InvalidOperationException("This flight is not recording.");
            if(request.Action==CollectionAction.StartData)research?.EnsureCanStart(research.Configurations);
            if(request.Action==CollectionAction.RearmAutomation&&collectionFault&&collection.Session is {} retained)
                research?.Configure(retained.Configurations.GroupBy(c=>c.Settings.InstrumentId).Select(g=>g.OrderByDescending(c=>c.Revision).First().Settings).ToArray());
            var result=collection.Control(request,ControlTime);
            if(request.Action==CollectionAction.RearmAutomation&&result.Event.Outcome==CollectionEventOutcome.Applied){collectionFault=false;geographicCollection?.Rearm();}
            research?.ApplyCollection(collection.State,collection.Session,[],result.Event.InputUtc);return result;
        }
    }
    private IDisposable AcquireSciencePublication(RemoteAcquisitionRequest request)
    {
        Monitor.Enter(gate);
        try
        {
            if(request.SessionId is {} session&&request.IntentId is {} intent)
            {
                var state=collection?.State;
                if(collectionFault||active?.Id!=request.Mission.Id||state?.Phase!=CollectionPhase.Collecting||state.CurrentSessionId!=session
                    ||store.ScienceIntent(intent) is not {State:"Pending"} saved||saved.Intent.SessionId!=session)
                    throw new OperationCanceledException("The original session/exposure is no longer eligible for live publication.");
            }
            return new SciencePublicationLease(gate);
        }
        catch{Monitor.Exit(gate);throw;}
    }
    private sealed class SciencePublicationLease(object gate):IDisposable{public void Dispose()=>Monitor.Exit(gate);}

    public MissionMarker AddMarker(Guid id, MarkerRequest request)
    {
        lock (gate)
        {
            if(maintenance)throw new InvalidOperationException("Data maintenance is in progress.");
            if(store.MarkerReceipt(id,request) is {} receipt)return receipt;
            if(active?.Id!=id)throw new InvalidOperationException("Observation markers require an active mission.");
            var available = connection.State == ConnectionState.Connected && frame is { IsValid:true, IsPaused: false }
                && (DateTimeOffset.UtcNow-frame.TimestampUtc).TotalSeconds is >=0 and <=6 ? frame : null;
            return store.AddMarker(id, request, DateTimeOffset.UtcNow, available,
                JsonSerializer.Serialize(research?.Snapshot(connection, frame), WireJson.Options));
        }
    }
    public MissionNote AddResearchNote(Guid id, NoteRequest request)
    {
        lock (gate)
        {
            if (maintenance) throw new InvalidOperationException("Data maintenance is in progress.");
            if(store.NoteReceipt(id,request) is {} receipt)return receipt;
            if (request.Phase == "in-flight" && active?.Id != id) throw new InvalidOperationException("In-flight notes require that active mission.");
            return store.AddNote(id, request);
        }
    }

    public IDisposable EnterMaintenance()
    {
        lock (gate)
        {
            if (active is not null) throw new InvalidOperationException("End the active mission before storage/settings maintenance.");
            if (maintenance) throw new InvalidOperationException("Storage/settings maintenance is already running.");
            maintenance = true;
            return new MaintenanceLease(this);
        }
    }
    private sealed class MaintenanceLease(MissionCoordinator owner) : IDisposable
    {
        public void Dispose() { lock (owner.gate) owner.maintenance = false; }
    }

    public Mission End(Guid id)
    {
        lock (gate)
        {
            if (active?.Id != id) throw new InvalidOperationException("This mission is not currently recording.");
            store.Finish(id, MissionStatus.Completed, ControlTime, "Mission completed by operator.");
            research?.End();
            geographicCollection=null;collection=null;active = null;
            return store.Detail(id)!.Mission;
        }
    }

    public ResearchFlightState PersonalResearch(Guid id)
    {
        lock(gate){
            var state=store.ResearchOperations(id);
            var canBegin=active?.Id==id&&connection.State==ConnectionState.Connected&&frame is {IsValid:true,IsPaused:false}
                && Now-frame.TimestampUtc>=TimeSpan.FromSeconds(-2)&&Now-frame.TimestampUtc<=TimeSpan.FromSeconds(6);
            return state with{GeneratedUtc=Now,CanBeginAttempt=canBegin};
        }
    }
    public ResearchCardResult ControlPersonalResearch(ResearchCardControl request)
    {
        lock(gate){
            // Durable receipts may be replayed after End Flight; a new command cannot
            // mutate an old flight. Store rechecks recording/revision transactionally.
            if(maintenance)throw new InvalidOperationException("Storage maintenance is in progress.");
            if(store.ResearchReceipt(request) is {} prior)return new(prior,PersonalResearch(request.FlightId));
            if(active?.Id!=request.FlightId)throw new InvalidOperationException("This flight is not currently recording. Synchronize before issuing card controls.");
            if(request.Action is ResearchCardAction.BeginAttempt or ResearchCardAction.EnableAutomatic&&!PersonalResearch(request.FlightId).CanBeginAttempt)
                throw new InvalidOperationException("Begin Attempt needs fresh, unpaused aircraft telemetry for this active flight.");
            var receipt=store.ControlResearchCard(request,ControlTime);
            return new(receipt,PersonalResearch(request.FlightId));
        }
    }

    public void Interrupt()
    {
        lock (gate)
        {
            if (active is null) return;
            store.Finish(active.Id, MissionStatus.Interrupted, lastRecordedUtc ?? active.StartedUtc,
                "Engine stopped before the mission was completed.");
            research?.End();
            geographicCollection=null;collection=null;active = null;
        }
    }
}
