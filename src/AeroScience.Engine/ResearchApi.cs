using Microsoft.Data.Sqlite;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed record EstimateRequest(InstrumentConfiguration[]? Instruments, double? DurationMinutes);
public static class ResearchApi
{
    public static void MapResearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/templates", () => ScienceCatalog.Templates);
        app.MapGet("/api/instruments", () => ScienceCatalog.Definitions);
        app.MapPost("/api/instruments", (InstrumentConfiguration[] input, MissionCoordinator missions) =>
        { missions.ConfigureInstruments(input); return Results.Ok(missions.Snapshot().Research); });
        app.MapPost("/api/science/estimate", (EstimateRequest request, ScienceStore science) => science.Estimate(request.Instruments, request.DurationMinutes));
        app.MapGet("/api/plans", (int? limit, int? offset, MissionStore missions) => missions.Plans(limit ?? 50, offset ?? 0));
        app.MapGet("/api/plans/{id:guid}", (Guid id, MissionStore missions) => missions.Plan(id) is { } plan ? Results.Ok(plan) : Results.NotFound());
        app.MapPost("/api/plans", (MissionPlan plan, MissionStore missions) => Results.Ok(missions.SavePlan(plan)));
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
