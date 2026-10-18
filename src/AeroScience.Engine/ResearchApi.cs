using Microsoft.Data.Sqlite;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed record EstimateRequest(InstrumentConfiguration[]? Instruments, double? DurationMinutes);
public sealed record SciencePauseRequest(bool Paused);
public sealed record MapLayerRequest(string Layer);
public sealed record MapPrivacyRequest(bool Online);
public sealed record ProtectionRequest(bool Protected);
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
        app.MapGet("/api/science/plan-readiness",(ScientificPlanData preparation)=>preparation.Snapshot());
        app.MapGet("/api/science/readiness",(AutomaticScientificData automatic)=>automatic.Snapshot());
        app.MapPost("/api/science/prepare",(ScientificPreparationRequest request,AutomaticScientificData automatic)=>automatic.Prepare(GeographicBounds.Around(new(request.Latitude,request.Longitude)),request.Kinds??[RemoteKind.Thermal,RemoteKind.Multispectral,RemoteKind.Terrain],retry:true));
        app.MapPost("/api/science/prepare/cancel",(AutomaticScientificData automatic)=>{automatic.Cancel();return Results.Ok(new{cancelled=true});});
        app.MapPost("/api/plans/{id:guid}/prepare",(Guid id,MissionStore store,ScientificPlanData preparation)=>store.Plan(id) is {} plan?Results.Ok(preparation.Prepare(plan,true)):Results.NotFound());
        app.MapGet("/api/science/sources", (double? latitude, double? longitude, ScienceStore science) => science.Availability(latitude is { } lat && longitude is { } lon ? new Coordinates(lat, lon) : null));
        app.MapGet("/api/science/products", (Guid? missionId, int? limit, int? offset, string? instrumentId, ScienceStore science) => science.Products(missionId, limit ?? 50, offset ?? 0,instrumentId));
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
        app.MapGet("/api/patterns", () => Enum.GetNames<FlightPatternKind>());
        app.MapPost("/api/patterns/preview", (PatternDefinition definition) => FlightPatterns.Generate(definition));
        app.MapGet("/api/presets", (MissionStore store) => store.Presets());
        app.MapPost("/api/presets", (InstrumentPreset preset, MissionStore store) => store.SavePreset(preset));
        app.MapPost("/api/presets/resolve", (InstrumentPreset preset) => MissionStore.ResolvePreset(preset));
        app.MapPost("/api/presets/{id:guid}/delete", (Guid id, MissionStore store) => store.DeletePreset(id) ? Results.Ok(new { deleted = true }) : Results.NotFound());
        app.MapGet("/api/missions/{id:guid}/analysis", (Guid id, MissionStore store) => store.Detail(id) is null ? Results.NotFound() : Results.Ok(MissionAnalysis.Compare(store.Research(id).PlanSnapshot, store.StreamSamples(id), store.GetAnalysisSettings(id))));
        app.MapPost("/api/missions/{id:guid}/analysis", (Guid id, AnalysisSettings settings, MissionStore store) => store.SaveAnalysisSettings(id, settings));
        app.MapGet("/api/templates", () => ScienceCatalog.Templates);
        app.MapGet("/api/instruments", () => ScienceCatalog.Definitions);
        app.MapPost("/api/instruments", (InstrumentConfiguration[] input, MissionCoordinator missions) =>
        { missions.ConfigureInstruments(input); return Results.Ok(missions.Snapshot().Research); });
        app.MapPost("/api/science/estimate", (EstimateRequest request, ScienceStore science) => science.Estimate(request.Instruments, request.DurationMinutes));
        app.MapGet("/api/plans", (int? limit, int? offset, MissionStore missions) => missions.Plans(limit ?? 50, offset ?? 0));
        app.MapGet("/api/plans/{id:guid}", (Guid id, MissionStore missions) => missions.Plan(id) is { } plan ? Results.Ok(plan) : Results.NotFound());
        app.MapPost("/api/plans", (MissionPlan plan, MissionStore missions,ScientificPlanData preparation) => {var saved=missions.SavePlan(plan); _=preparation.Prepare(saved);return Results.Ok(saved);});
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
            var rows = science.StreamObservations(datasetId: id, after: after ?? 0, limit: limit ?? 1000).ToArray();
            return Results.Ok(new { observations = rows, next = rows.LastOrDefault()?.Id ?? after ?? 0 });
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
                _ => Results.NotFound()
            };
        });
    }
}
