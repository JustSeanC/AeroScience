using Microsoft.Data.Sqlite;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed record PatternSurveyRequest(PatternDefinition Definition, SurveyArea Area,string FitMode="polygon");
public sealed record EstimateRequest(InstrumentConfiguration[]? Instruments, double? DurationMinutes);
public sealed record PerformanceRevisionRequest(string Reason);
public sealed record SciencePauseRequest(bool Paused);
public sealed record MapLayerRequest(string Layer);
public sealed record MapPrivacyRequest(bool Online);
public sealed record ProtectionRequest(bool Protected);
public sealed record SourceSelectionRequest(Guid SourceId,bool Enabled);
public sealed record SourceModeRequest(RemoteKind Kind,string Mode);
public sealed record HyperspectralAssociation(string FileName);
public sealed record PreparationControl(string Action);
public sealed record MissionSelection(Guid? PlanId);
public sealed record PreparationCancel(string? Key=null);
public sealed record CleanupRequest(Guid PreviewId,bool Confirmed);
public static class ResearchApi
{
    public static void MapResearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/storage/management", (StorageManagement storage) => storage.Snapshot());
        app.MapPost("/api/storage/refresh", (StorageManagement storage, HttpContext c) => AccessControl.IsLoopback(c) ? Results.Ok(storage.Snapshot(true)) : Results.StatusCode(403));
        app.MapPost("/api/storage/preferences", (StorageLimits preferences, StorageManagement storage, HttpContext c) => AccessControl.IsLoopback(c) ? Results.Ok(storage.Configure(preferences)) : Results.StatusCode(403));
        app.MapPost("/api/storage/cleanup/preview", (ScientificCache cache, HttpContext c) => AccessControl.IsLoopback(c) ? Results.Ok(new { id=cache.Preview().Id }) : Results.StatusCode(403));
        app.MapGet("/api/storage/cleanup/{id:guid}", (Guid id,int? offset,string? sort,string? filter,string? status,ScientificCache cache,HttpContext c) => AccessControl.IsLoopback(c) ? Results.Ok(cache.Page(id,offset??0,sort??"created",filter??"",status??"")) : Results.StatusCode(403));
        app.MapPost("/api/storage/cleanup", (CleanupRequest request,ScientificCache cache,HttpContext c) => AccessControl.IsLoopback(c) ? Results.Ok(cache.Clean(request.PreviewId,request.Confirmed)) : Results.StatusCode(403));
        app.MapGet("/api/storage/cleanup/history", (ScientificCache cache,HttpContext c) => AccessControl.IsLoopback(c) ? Results.Ok(cache.History()) : Results.StatusCode(403));
        app.MapGet("/api/missions/{id:guid}/protection", (Guid id,MissionStore store) => new { isProtected=store.IsProtected(id) });
        app.MapPost("/api/missions/{id:guid}/protection", (Guid id,ProtectionRequest request,MissionStore store) => {store.Protect(id,request.Protected);return new {isProtected=store.IsProtected(id)};});
        app.MapGet("/api/science/datasets/{id:guid}/protection", (Guid id,ScienceStore science) => new { isProtected=science.DatasetProtected(id) });
        app.MapPost("/api/science/datasets/{id:guid}/protection", (Guid id,ProtectionRequest request,ScienceStore science) => {science.ProtectDataset(id,request.Protected);return new {isProtected=science.DatasetProtected(id)};});
        app.MapGet("/api/science/products/{id:guid}/preview.png", (Guid id,ScientificCache cache) => Results.File(cache.PreviewPng(id),"image/png"));
        app.MapGet("/api/updates", (ApplicationUpdates updates) => updates.Get());
        app.MapPost("/api/updates/preferences", (UpdatePreferences preferences, ApplicationUpdates updates, HttpContext c) => AccessControl.IsLoopback(c) ? Results.Ok(updates.Configure(preferences)) : Results.StatusCode(403));
        app.MapPost("/api/updates/check", (ApplicationUpdates updates, HttpContext c) => updates.Check(true, c.RequestAborted));
        app.MapPost("/api/updates/remind", (ApplicationUpdates updates) => updates.RemindLater());
        app.MapGet("/api/science/access", (ScientificAccessSettings settings,HttpContext c)=>new{configuration=settings.Get(),canConfigure=AccessControl.IsLoopback(c)});
        app.MapPost("/api/science/access",(ScientificAccessPreferences preferences,AutomaticScientificData automatic,HttpContext c)=>AccessControl.IsLoopback(c)?Results.Ok(automatic.Configure(preferences)):Results.StatusCode(403));
        app.MapPost("/api/science/source-selection",(SourceSelectionRequest request,ScientificAccessSettings settings,AutomaticScientificData automatic,ScienceStore science,HttpContext c)=>
        {if(!AccessControl.IsLoopback(c))return Results.StatusCode(403);if(!science.Sources().Any(s=>s.Id==request.SourceId))throw new ArgumentException("Source not found.");var p=settings.Get().Preferences;var excluded=(p.ExcludedSourceIds??[]).ToHashSet();if(request.Enabled)excluded.Remove(request.SourceId);else excluded.Add(request.SourceId);return Results.Ok(automatic.Configure(p with{ExcludedSourceIds=excluded.ToArray()}));});
        app.MapPost("/api/science/source-mode",(SourceModeRequest request,ScientificAccessSettings settings,AutomaticScientificData automatic,HttpContext c)=>
        {if(!AccessControl.IsLoopback(c))return Results.StatusCode(403);var p=settings.Get().Preferences;var modes=new Dictionary<RemoteKind,string>(p.SourceModes??[]){[request.Kind]=request.Mode};return Results.Ok(automatic.Configure(p with{SourceModes=modes}));});
        app.MapGet("/api/plans/{id:guid}/preparation/instruments",(Guid id,ScientificPlanData plans)=>plans.Instruments(id));
        app.MapGet("/api/plans/{id:guid}/preparation/regions",(Guid id,int? offset,ScientificPlanData plans)=>plans.Regions(id,offset??0));
        app.MapGet("/api/science/plan-readiness",(ScientificPlanData preparation)=>preparation.Snapshot());
        app.MapGet("/api/science/prediction",(ScientificFlightPrediction prediction)=>prediction.Snapshot());
        app.MapPost("/api/science/preparation/control",(PreparationControl request,AutomaticScientificData automatic)=>{if(request.Action is not("pause" or "resume"))throw new ArgumentException("Choose pause or resume.");automatic.Pause(request.Action=="pause");return Results.Ok(new { action=request.Action,queue=automatic.QueueSummary() });});
        app.MapPost("/api/plans/{id:guid}/preparation/control",(Guid id,PreparationControl request,ScientificPlanData plans)=>{plans.Control(id,request.Action);return Results.Ok(plans.Snapshot().Single(s=>s.PlanId==id));});
        app.MapGet("/api/science/activity",(AutomaticScientificData automatic,ScientificPlanData plans)=>new{jobs=automatic.Snapshot(),queue=automatic.QueueSummary(),plans=plans.Snapshot(),plansWarning=plans.Warning,network=automatic.NetworkDiagnostics()});
        app.MapGet("/api/science/diagnostics",(AutomaticScientificData automatic,HttpContext c)=>AccessControl.IsLoopback(c)?Results.Ok(new{privacy="Geographic regions and provider/instrument activity are included. Review before sharing. No credentials or original scientific files.",events=ScientificDiagnostics.Snapshot(),network=automatic.NetworkDiagnostics(),queue=automatic.QueueSummary()}):Results.StatusCode(403));
        app.MapGet("/api/science/library/providers",()=>ScientificCatalog.Providers);
        app.MapGet("/api/science/library/local",(ScientificCatalog catalog)=>catalog.Local());
        app.MapPost("/api/science/library/catalog",(ScientificCatalogQuery query,ScientificCatalog catalog,HttpContext c)=>catalog.Search(query,c.RequestAborted));
        app.MapGet("/api/science/readiness",(AutomaticScientificData automatic)=>automatic.Snapshot());
        app.MapPost("/api/science/prepare",(ScientificPreparationRequest request,AutomaticScientificData automatic)=>automatic.Prepare(GeographicBounds.Around(new(request.Latitude,request.Longitude)),request.Kinds??[RemoteKind.Thermal,RemoteKind.Multispectral,RemoteKind.Terrain],retry:true,samplePoint:new(request.Latitude,request.Longitude)));
        app.MapPost("/api/science/prepare/cancel",(PreparationCancel request,AutomaticScientificData automatic)=>{automatic.Cancel(request.Key);return Results.Ok(new{cancelled=true});});
        app.MapGet("/api/plans/{id:guid}/preparation/preview",(Guid id,MissionStore store,ScienceStore science,ScientificAccessSettings settings)=>
        {
            var plan=store.Plan(id)??throw new ArgumentException("Plan not found.");var areas=ScientificPlanPreparation.Cells(plan,.04).Take(4096).ToArray();var kinds=ScientificPlanPreparation.Kinds(plan);var sources=science.Availability();var p=settings.Get().Preferences;
            var retained=kinds.ToDictionary(k=>k.ToString(),k=>areas.Take(128).Count(a=>ScientificSourceSelection.Select(sources.Select(s=>s with{CoversPoint=ScientificSourceSelection.CoversArea(s.Source,a)}),k,p) is not null));
            return new{planName=plan.Name,areas,reviewedRegions=Math.Min(128,areas.Length),instruments=kinds,retained,allowance=p.PreflightTransferBytes,estimatedBytes=(long?)null,limitations="Native source grids and masks remain authoritative. Local footprint candidates are checked again before use. Transfer size is unknown before provider selection. Up to 4096 bounded regions; no whole-scene download.",plan};
        });
        app.MapPost("/api/plans/{id:guid}/prepare",(Guid id,MissionStore store,ScientificPlanData preparation)=>store.Plan(id) is {} plan?Results.Ok(preparation.Prepare(plan,true)):Results.NotFound());
        app.MapGet("/api/science/hyperspectral/import",(ScientificImportSession session,HttpContext c)=>AccessControl.IsLoopback(c)?Results.Ok(session.Snapshot()):Results.StatusCode(403));
        app.MapPost("/api/science/hyperspectral/metadata",async (HyperspectralUserMetadata metadata,ScientificImportSession session,HttpContext c)=>{if(!AccessControl.IsLoopback(c))return Results.StatusCode(403);await session.Configure(metadata);return Results.Ok(session.Snapshot());});
        app.MapPost("/api/science/hyperspectral/associate",async (HyperspectralAssociation request,ScientificImportSession session,HttpContext c)=>{if(!AccessControl.IsLoopback(c))return Results.StatusCode(403);await session.Associate(request.FileName);return Results.Ok(session.Snapshot());});
        app.MapPost("/api/science/hyperspectral/import",(ScientificImportSession session,HttpContext c)=>{if(!AccessControl.IsLoopback(c))return Results.StatusCode(403);session.Import();return Results.Accepted();});
        app.MapPost("/api/science/hyperspectral/cancel",(ScientificImportSession session,HttpContext c)=>{if(!AccessControl.IsLoopback(c))return Results.StatusCode(403);session.Cancel();return Results.Ok();});
        app.MapGet("/api/science/sources", (double? latitude, double? longitude, ScienceStore science) => science.Availability(latitude is { } lat && longitude is { } lon ? new Coordinates(lat, lon) : null));
        app.MapGet("/api/science/sources/{id:guid}/coverage",(Guid id,ScienceStore science)=>
        {
            var source=science.Sources().FirstOrDefault(s=>s.Id==id)??throw new ArgumentException("Source not found.");var g=source.Grids.Values.First();
            return ScientificRaster.GridCoverage(g);
        });
        app.MapPost("/api/science/workspace/atmosphere",(ScienceMapQuery query,ScienceStore science)=>science.AtmosphereMap(query));
        app.MapPost("/api/science/workspace/map",(ScienceMapQuery query,ScienceStore science)=>science.MapPage(query));
        app.MapPost("/api/science/workspace/overview",(ScienceMapQuery query,ScienceStore science,HttpContext context)=>Task.Run(()=>science.MapOverview(query,context.RequestAborted),context.RequestAborted));
        app.MapPost("/api/science/workspace/compare",(ScientificComparisonRequest request,ScienceStore science)=>science.Compare(request));
        app.MapGet("/api/missions/{id:guid}/science-interpretation",(Guid id,MissionStore missions)=>ScientificInterpretation.Describe(missions.StreamSamples(id)));
        app.MapGet("/api/missions/{id:guid}/science-summary",(Guid id,MissionStore missions,ScienceStore science)=>ScientificResearchSummary.Build(missions,science,id));
        app.MapGet("/api/missions/{id:guid}/archive-summary",(Guid id,MissionStore missions)=>new { instruments=missions.Research(id).Instruments.Where(i=>i.Enabled).Select(i=>i.InstrumentId).ToArray(),review=missions.ScientificReview(id) });
        app.MapGet("/api/science/products/page",(Guid? missionId,int? offset,string? instrumentId,ScienceStore science)=>new{items=science.Products(missionId,30,offset??0,instrumentId),total=science.ProductCount(missionId,instrumentId),offset=offset??0});
        app.MapGet("/api/missions/{id:guid}/scientific-results",(Guid id,MissionStore store,ScienceStore science)=>store.Detail(id) is {} detail?Results.Ok(new{mission=detail.Mission,diagnostics=store.Research(id).Diagnostics,datasets=science.Datasets(id,200)}):Results.NotFound());
        app.MapGet("/api/missions/{id:guid}/scientific-atmosphere-locations",(Guid id,string? instrumentId,long? after,ScienceStore science)=>
        {var all=science.StreamObservations(id,after:after??0,limit:1001,instrumentId:instrumentId).ToArray();var rows=all.Take(1000).ToArray();return new{items=rows,next=rows.LastOrDefault()?.Id??after??0,hasMore=all.Length>1000};});
        app.MapGet("/api/missions/{id:guid}/scientific-locations",(Guid id,string? instrumentId,int? offset,ScienceStore science)=>science.ProductLocations(id,instrumentId,Math.Max(0,offset??0)));
        app.MapGet("/api/science/products", (Guid? missionId, int? limit, int? offset, string? instrumentId, ScienceStore science) => science.Products(missionId, limit ?? 50, offset ?? 0,instrumentId));
        app.MapGet("/api/science/products/{id:guid}/map-layer",(Guid id,string measurement,ScienceStore science)=>ScientificMapLayer.Create(science.ReadProduct(id),measurement));
        app.MapGet("/api/science/products/{id:guid}", (Guid id, ScienceStore science) => science.ReadProduct(id));
        app.MapGet("/api/science/products/{id:guid}/export/{format}", (Guid id, string format, ScienceStore science) =>
        {
            var content = RemoteExports.Content(science.ReadProduct(id), format);
            var type = format == "csv" ? "text/csv" : format == "kml" ? "application/vnd.google-earth.kml+xml" : format == "gpx" ? "application/gpx+xml" : "application/json";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(content), type, $"AeroScience-product-{id}.{format}");
        });
        app.MapGet("/api/maps/settings", (MapSettings maps, HttpContext c) => new { configuration = maps.Get(), canConfigure = AccessControl.IsLoopback(c), privacy = MapSettings.Privacy });
        app.MapPost("/api/maps/privacy", (MapPrivacyRequest request, MapSettings maps) => maps.ChoosePrivacy(request.Online));
        app.MapPost("/api/maps/settings", (MapSettingsRequest request, MapSettings maps, HttpContext c) => AccessControl.IsLoopback(c) ? Results.Ok(maps.Save(request)) : Results.StatusCode(403));
        app.MapPost("/api/maps/layer", (MapLayerRequest request, MapSettings maps) => maps.SelectLayer(request.Layer));
        app.MapGet("/api/maps/client", (string? layer, MapSettings maps, HttpContext c) => maps.Client(layer, c.RequestAborted));
        app.MapPost("/api/maps/test", async (MapSettings maps, HttpContext c) => AccessControl.IsLoopback(c) ? Results.Ok(await maps.Client("satellite", c.RequestAborted)) : Results.StatusCode(403));
        app.MapPost("/api/routes/export", (RouteExportRequest request) => RouteExports.Generate(request));
        app.MapGet("/api/maps/active", (RouteMapService maps) => maps.State(null, null, includeTrack: false));
        app.MapGet("/api/display/units", (ScienceUnitSettings settings) => new { preferences = settings.Get(), options = ScienceUnits.Options, aviation = ScienceUnits.Aviation, scientific = ScienceUnits.Scientific, warning = settings.Warning });
        app.MapPost("/api/display/units", (ScienceUnitPreferences preferences, ScienceUnitSettings settings) => settings.Set(preferences));
        app.MapPost("/api/missions/{id:guid}/science/state", (Guid id, SciencePauseRequest request, MissionCoordinator missions) => missions.SetSciencePaused(id, request.Paused));
        app.MapGet("/api/missions/{id:guid}/performance",(Guid id,MissionPerformanceService performance)=>performance.Read(id));
        app.MapGet("/api/missions/{id:guid}/performance/{revision:int}",(Guid id,int revision,MissionStore missions)=>new{state="Available",reports=new[]{missions.PerformanceRevision(id,revision)},revisions=missions.PerformanceRevisions(id),projection=(MissionPerformanceReport?)null});
        app.MapPost("/api/missions/{id:guid}/performance/recompute",(Guid id,PerformanceRevisionRequest request,MissionPerformanceService performance,HttpContext c)=>AccessControl.IsLoopback(c)?Results.Accepted(value:performance.Recompute(id,request.Reason)):Results.StatusCode(403));
        app.MapGet("/api/missions/{id:guid}/performance/export.csv",(Guid id,MissionStore missions)=>Results.File(System.Text.Encoding.UTF8.GetBytes(ResearchExports.PerformanceCsv(missions,id)),"text/csv",$"AeroScience-performance-{id}.csv"));
        app.MapGet("/api/missions/{id:guid}/performance/export",(Guid id,MissionStore missions,HttpContext c)=>Results.Stream(stream=>missions.ExportPerformanceJsonAsync(id,stream,c.RequestAborted),"application/json",$"AeroScience-performance-{id}.json"));
        app.MapGet("/api/missions/{id:guid}/collection/counts",(Guid id,ScienceStore science)=>science.SessionCounts(id));
        app.MapGet("/api/missions/{id:guid}/collection",(Guid id,MissionCoordinator missions)=>missions.ScienceCollection(id));
        app.MapPost("/api/missions/{id:guid}/collection/control",(Guid id,CollectionControlRequest request,MissionCoordinator missions)=>
        {
            if(id!=request.MissionId)throw new ArgumentException("Flight identity does not match command.");
            var result=missions.ControlScience(request);
            return result.Event.Outcome==CollectionEventOutcome.Conflict?Results.Conflict(result):Results.Ok(result);
        });
        app.MapPost("/api/plans/readiness", (MissionPlan draft, PreflightReadinessService readiness) => readiness.Assess(draft));
        app.MapGet("/api/plans/readiness/context", (PreflightReadinessService readiness) => new { contextSha256=PreflightReadiness.Hash(readiness.Environment()) });
        app.MapGet("/api/plans/{id:guid}/readiness", (Guid id, MissionStore store, PreflightReadinessService readiness) => readiness.Assess(store.Plan(id)??throw new ArgumentException("Plan not found.")));
        app.MapPost("/api/plans/collection/bind",(MissionPlan draft)=>CollectionPlanning.Bind(draft));
        app.MapGet("/api/patterns", () => Enum.GetNames<FlightPatternKind>());
        app.MapPost("/api/patterns/fit-survey", (PatternSurveyRequest request) => FlightPatterns.FitSurvey(request.Definition,request.Area,request.FitMode));
        app.MapPost("/api/patterns/preview", (PatternDefinition definition) => FlightPatterns.Generate(definition));
        app.MapGet("/api/presets", (MissionStore store) => store.Presets());
        app.MapPost("/api/presets", (InstrumentPreset preset, MissionStore store) => store.SavePreset(preset));
        app.MapPost("/api/presets/resolve", (InstrumentPreset preset) => MissionStore.ResolvePreset(preset));
        app.MapPost("/api/presets/{id:guid}/delete", (Guid id, MissionStore store) => store.DeletePreset(id) ? Results.Ok(new { deleted = true }) : Results.NotFound());
        app.MapGet("/api/missions/{id:guid}/analysis", (Guid id, MissionStore store) => store.Detail(id) is null ? Results.NotFound() : Results.Ok(MissionAnalysis.Compare(store.Research(id).PlanSnapshot, store.StreamSamples(id), store.GetAnalysisSettings(id))));
        app.MapPost("/api/missions/{id:guid}/analysis", (Guid id, AnalysisSettings settings, MissionStore store) => store.SaveAnalysisSettings(id, settings));
        app.MapGet("/api/templates", () => ScienceCatalog.Templates);
        app.MapGet("/api/instruments", () => ScienceCatalog.Definitions);
        app.MapGet("/api/instruments/configuration", (ResearchOperations research) => research.Configurations);
        app.MapGet("/api/instruments/operating",(ResearchOperations research)=>research.OperatingReports);
        app.MapGet("/api/missions/{id:guid}/operating",(Guid id,DateTimeOffset? at,MissionStore store)=>store.OperatingReports(id,at));
        app.MapGet("/api/instruments/calibration", (InstrumentCalibrationSettings settings, HttpContext context) =>
        {
            var definitions = settings.Get();
            return new { instruments = ScienceCatalog.Definitions.Select(d => InstrumentCalibration.Describe(d.Id)), definitions,
                warning = settings.Warning, canConfigure = AccessControl.IsLoopback(context) };
        });
        app.MapPost("/api/instruments/calibration", (CalibrationDefinition definition, InstrumentCalibrationSettings settings, HttpContext context) =>
        {
            if (!AccessControl.IsLoopback(context)) return Results.StatusCode(403);
            try { return Results.Ok(settings.Save(definition)); }
            catch (NotSupportedException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (System.Text.Json.JsonException error) { return Results.Conflict(new { error = "Saved calibration settings are invalid and were preserved. " + error.Message }); }
        });
        app.MapGet("/api/instrument-profiles", () => new { profiles=InstrumentProfiles.Catalog, generic=RemoteAcquisition.Instruments.Keys.Select(InstrumentProfiles.Generic) });
        app.MapPost("/api/sensor-planning", (SensorPlanningRequest request,ScienceStore science,HttpContext context) => TerrainPlanning.Apply(request,InstrumentProfiles.Plan(request),science,context.RequestAborted));
        app.MapGet("/api/science/source-capabilities", (ScienceStore science) => science.Sources().Select(ScientificCapabilities.Describe));
        app.MapGet("/api/missions/{id:guid}/science-review",(Guid id,MissionStore store)=>store.ScientificReview(id));
        app.MapGet("/api/missions/{id:guid}/downlink",(Guid id,int? offset,MissionStore store)=>new {review=store.ScientificReview(id),items=store.Recovery(id,100,offset??0)});
        app.MapPost("/api/missions/{id:guid}/downlink/control",(Guid id,PreparationControl request,MissionStore store,AutomaticScientificData automatic,ScientificDownlinkSignals signals)=>{store.ControlRecovery(id,request.Action);if(request.Action!="resume")signals.Cancel(id);automatic.PauseScope("plan:downlink:"+id,request.Action!="resume");if(request.Action=="cancel")automatic.CancelScope("plan:downlink:"+id);return store.ScientificReview(id);});
        app.MapGet("/api/missions/{id:guid}/replay", (Guid id,long? after,DateTimeOffset? at,int? limit,MissionStore store) => store.Replay(id,after??0,at,limit??200));
        app.MapGet("/api/missions/{id:guid}/replay/events", (Guid id,long? after,MissionStore store) => store.ReplayEvents(id,after??0));
        app.MapGet("/api/missions/{id:guid}/replay/acquisitions", (Guid id,DateTimeOffset at,ScienceStore science) => science.ReplayAcquisitions(id,at));
        app.MapGet("/api/missions/{id:guid}/replay/context",(Guid id,DateTimeOffset at,MissionStore missions,ScienceStore science)=>
        {
            var instruments=missions.ReplayInstruments(id,at);
            try{return Results.Ok(new{instruments,observations=science.ReplayObservations(id,at),error=(string?)null});}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){return Results.Ok(new{instruments,observations=(ScienceObservation[]?)null,error=e.Message});}
        });
        app.MapGet("/api/missions/{id:guid}/transect-passes",(Guid id,MissionStore store)=>store.Research(id).PlanSnapshot?.Patterns?.Where(p=>p.Kind==FlightPatternKind.StraightTransect).Select(p=>new{p.Id,passes=TransectPasses.Recognize(p,store.StreamSamples(id))}).ToArray()??[]);
        app.MapGet("/api/missions/{id:guid}/atmospheric-analysis", (Guid id,MissionStore store,HttpContext context) => AtmosphericAnalysis.Calculate(store.StreamSamples(id),store.Research(id).PlanSnapshot?.AtmosphericSettings,token:context.RequestAborted));
        app.MapPost("/api/missions/{id:guid}/coverage",(Guid id,ScientificCoverageRequest request,ScientificAnalysisJobs jobs)=>jobs.Enqueue(id,coverage:request));
        app.MapPost("/api/missions/{id:guid}/composites",(Guid id,CompositeRequest request,ScientificAnalysisJobs jobs)=>jobs.Enqueue(id,composite:request));
        app.MapGet("/api/analysis/jobs/{id:guid}",(Guid id,ScientificAnalysisJobs jobs)=>jobs.Get(id));
        app.MapPost("/api/analysis/jobs/{id:guid}/cancel",(Guid id,ScientificAnalysisJobs jobs)=>jobs.Cancel(id));
        app.MapGet("/api/missions/{id:guid}/artifacts",(Guid id,ScienceStore science)=>science.Artifacts(id).Take(1000).Select(a=>new{a.Id,a.Kind,a.Format,a.Bytes,a.CreatedUtc,a.InputIdentity}));
        app.MapGet("/api/science/artifacts/{id:guid}/metadata",(Guid id,ScienceStore science)=>Results.Content(science.Artifact(id).MetadataJson,"application/json"));
        app.MapGet("/api/science/artifacts/{id:guid}/preview",(Guid id,ScienceStore science,HttpContext context)=>{var preview=ScientificArtifactPreview.Create(science,science.Artifact(id),context.RequestAborted);return new{image="data:image/png;base64,"+Convert.ToBase64String(preview.Png),preview.Bounds,preview.Minimum,preview.Maximum,preview.Units,preview.Bands,preview.Method};});
        app.MapGet("/api/science/artifacts/{id:guid}",(Guid id,ScienceStore science)=>{var artifact=science.Artifact(id);return Results.File(science.VerifiedArtifactPath(artifact),artifact.Format=="tif"?"image/tiff":"application/json",$"AeroScience-{artifact.Kind}-{id:N}.{artifact.Format}");});
        app.MapPost("/api/instruments", (InstrumentConfiguration[] input, MissionCoordinator missions) =>
        { missions.ConfigureInstruments(input); return Results.Ok(missions.Snapshot().Research); });
        app.MapPost("/api/science/estimate", (EstimateRequest request, ScienceStore science) => science.Estimate(request.Instruments, request.DurationMinutes));
        app.MapPost("/api/mission-selection", (MissionSelection request, MissionCoordinator missions) => new { planId=missions.SelectPlan(request.PlanId) });
        app.MapGet("/api/plans", (int? limit, int? offset, MissionStore missions) => missions.Plans(limit ?? 50, offset ?? 0));
        app.MapGet("/api/plans/{id:guid}", (Guid id, MissionStore missions) => missions.Plan(id) is { } plan ? Results.Ok(plan) : Results.NotFound());
        app.MapPost("/api/plans", (MissionPlan plan, MissionStore missions,ScientificPlanData preparation) => {var saved=missions.SavePlan(plan);return Results.Ok(saved);});
        app.MapPost("/api/plans/{id:guid}/delete", (Guid id, MissionStore missions) => missions.DeletePlan(id) ? Results.Ok(new { deleted = true }) : Results.NotFound());
        app.MapGet("/api/missions/{id:guid}/research", (Guid id, MissionStore missions) => missions.Detail(id) is null ? Results.NotFound() : Results.Ok(missions.Research(id)));
        app.MapGet("/api/missions/{id:guid}/track", (Guid id, int? limit, long? after, MissionStore missions) =>
            missions.Detail(id) is null ? Results.NotFound() : Results.Ok(missions.Track(id, limit ?? 1500, after)));
        app.MapGet("/api/missions/{id:guid}/markers", (Guid id, int? limit, int? offset, MissionStore missions) => missions.Markers(id, limit ?? 200, offset ?? 0));
        app.MapPost("/api/missions/{id:guid}/markers", (Guid id, MarkerRequest request, MissionCoordinator missions) => Results.Ok(missions.AddMarker(id, request)));
        app.MapGet("/api/missions/{id:guid}/notes", (Guid id, int? limit, int? offset, MissionStore missions) => missions.Notes(id, limit ?? 200, offset ?? 0));
        app.MapPost("/api/missions/{id:guid}/notes", (Guid id, NoteRequest request, MissionCoordinator missions) => Results.Ok(missions.AddResearchNote(id, request)));
        app.MapGet("/api/missions/{id:guid}/review", (Guid id, MissionStore missions, ScienceStore science) =>
        {
            if (missions.Detail(id) is null) return Results.NotFound();
            var error = science.AvailabilityError(); IReadOnlyList<MeasurementSummary> summary = [];
            if (error is null) try { summary = science.Summary(id); } catch (Exception failure) when (failure is IOException or SqliteException or UnauthorizedAccessException) { error = failure.Message; }
            return Results.Ok(new
            {
                flight = missions.Statistics(id),
                sciencePauses = missions.SciencePauses(id),
                research = missions.Research(id),
                datasets = missions.ResolvedDatasetReferences(id),
                scienceAvailable = error is null,
                scienceError = error,
                measurements = summary
            });
        });
        app.MapGet("/api/science/datasets", (Guid? missionId, int? limit, int? offset, ScienceStore science) =>
        {
            var error = science.AvailabilityError();
            return Results.Ok(new { available = error is null, error, datasets = error is null ? science.Datasets(missionId, limit ?? 50, offset ?? 0) : [] });
        });
        app.MapGet("/api/missions/{id:guid}/science", (Guid id, long? after, int? limit, ScienceStore science) =>
        {
            var error = science.AvailabilityError();
            ScienceObservation[] rows = [];
            if (error is null) try { rows = science.StreamObservations(id, after: after ?? 0, limit: limit ?? 1000).ToArray(); } catch (Exception failure) when (failure is IOException or SqliteException or UnauthorizedAccessException) { error = failure.Message; }
            return Results.Ok(new { available = error is null, error, observations = rows, next = rows.LastOrDefault()?.Id ?? after ?? 0 });
        });
        app.MapGet("/api/science/datasets/{id:guid}/observations", (Guid id, long? after, int? limit, ScienceStore science) =>
        {
            var count=Math.Clamp(limit??1000,1,1000);var all=science.StreamObservations(datasetId:id,after:after??0,limit:count+1).ToArray();var rows=all.Take(count).ToArray();
            return Results.Ok(new { observations = rows, next = rows.LastOrDefault()?.Id ?? after ?? 0,hasMore=all.Length>count });
        });
        app.MapGet("/api/missions/{id:guid}/export/{format}", (Guid id, string format, HttpContext context, MissionStore missions, ScienceStore science) =>
        {
            if (missions.Detail(id) is null) return Results.NotFound(); ResearchExports.RequireCompleted(missions, id);
            var token = context.RequestAborted;
            if (format is "science.csv" or "atmosphere.csv" && science.AvailabilityError() is { } error) return Results.Conflict(new { error });
            return format switch
            {
                "flight.csv" => Results.Stream(stream => ResearchExports.CsvAsync(missions, id, "flight", stream, token), "text/csv; charset=utf-8", $"AeroScience-{id}-flight.csv"),
                "science.csv" or "atmosphere.csv" => Results.Stream(stream => ResearchExports.CsvAsync(missions, id, "science", stream, token), "text/csv; charset=utf-8", $"AeroScience-{id}-atmosphere.csv"),
                "events.csv" => Results.Stream(stream => ResearchExports.CsvAsync(missions, id, "events", stream, token), "text/csv; charset=utf-8", $"AeroScience-{id}-events.csv"),
                "gpx" => Results.Stream(stream => ResearchExports.GpxAsync(missions, id, stream, token), "application/gpx+xml", $"AeroScience-{id}.gpx"),
                "kml" => Results.Stream(stream => ResearchExports.KmlAsync(missions, id, stream, token), "application/vnd.google-earth.kml+xml", $"AeroScience-{id}.kml"),
                "report.txt" => Results.Stream(stream => ResearchExports.ReportAsync(missions, id, stream, token), "text/plain; charset=utf-8", $"AeroScience-{id}-research-summary.txt"),
                "research.zip" => Results.Stream(stream=>ResearchPackage.WriteAsync(missions,science,id,stream,token),"application/zip",$"AeroScience-{id}-research.zip"),
                _ => Results.NotFound()
            };
        });
    }
}
