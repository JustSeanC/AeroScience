using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.SimConnect;
using AeroScience.Telemetry;

namespace AeroScience.Engine;

public static class EngineApplication
{
    public static WebApplication Create(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        builder.WebHost.ConfigureKestrel((context, server) =>
        {
            var options = context.Configuration.GetSection("AeroScience").Get<EngineOptions>() ?? new();
            options.Validate();
            server.Limits.MaxRequestBodySize = 1048576;
            server.Listen(IPAddress.Loopback, options.Port);
            if (options.EnableLan) server.Listen(IPAddress.Parse(options.LanAddress!), options.Port);
        });
        builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(sp =>
            new FileLogProvider(Path.Combine(sp.GetRequiredService<EngineOptions>().DataDirectory, "logs")));
        // Request paths and session/pairing payloads are not logged.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IConfiguration>().GetSection("AeroScience").Get<EngineOptions>() ?? new();
            options.Validate();
            return options;
        });
        builder.Services.AddSingleton<AccessControl>();
        builder.Services.AddSingleton<DataDirectoryLease>();
        builder.Services.AddSingleton(sp =>
        {
            _ = sp.GetRequiredService<DataDirectoryLease>();
            var store = new MissionStore(Path.Combine(sp.GetRequiredService<EngineOptions>().DataDirectory, "missions.db"));
            store.Initialize(); store.RecoverInterrupted(); return store;
        });
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<EngineOptions>();
            var directory = settings.ScienceLibraryDirectory ?? Path.Combine(settings.DataDirectory, "ScienceLibrary");
            if (settings.ScienceLibraryDirectory is null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                PreferenceStore.WriteAtomic(Path.Combine(directory, StorageManager.LibraryManifest), new { formatVersion = 1, libraryId = Guid.NewGuid() });
            }
            var science = new ScienceStore(directory);
            var missions = sp.GetRequiredService<MissionStore>(); missions.ScienceLibrary = science;
            if (science.AvailabilityError() is null)
                try { missions.ReconcileDatasetReferences(science.LibraryId, science.ContainsDataset); }
                catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
                { sp.GetRequiredService<ILogger<ScienceStore>>().LogWarning("Science references need review: {Message}", error.Message); }
            return science;
        });
        builder.Services.AddSingleton(sp => new ScienceUnitSettings(sp.GetRequiredService<EngineOptions>().ConfigurationDirectory ?? sp.GetRequiredService<EngineOptions>().DataDirectory));
        builder.Services.AddSingleton(sp => new ResearchOperations(sp.GetRequiredService<MissionStore>(), sp.GetRequiredService<ScienceStore>(), sp.GetRequiredService<ScienceUnitSettings>().Get));
        builder.Services.AddSingleton(sp => new MissionCoordinator(sp.GetRequiredService<MissionStore>(), sp.GetRequiredService<EngineOptions>().SamplingIntervalMs,
            requireFreshTelemetry: true, ensureRecordingCapacity: () =>
            {
                var path = sp.GetRequiredService<EngineOptions>().DataDirectory;
                if (new LocalStorageCapacity().FreeBytes(path) < StorageManager.MinimumFreeBytes)
                    throw new IOException("Insufficient disk space for recording. Free space or choose another storage location; existing history is retained.");
            }, research: sp.GetRequiredService<ResearchOperations>()));
        builder.Services.AddSingleton<ITelemetryProvider, SimConnectTelemetryProvider>();
        configureServices?.Invoke(builder.Services);
        builder.Services.AddHostedService<TelemetryWorker>();
        builder.Services.AddSingleton<ToolbarCommandHandler>();
        builder.Services.AddHostedService<ToolbarWorker>();

        var app = builder.Build();
        var options = app.Services.GetRequiredService<EngineOptions>();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob: https://tile.openstreetmap.org; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (!AccessControl.ValidHost(context.Request, options))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "Unrecognized Host header." });
                return;
            }
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!HttpMethods.IsGet(context.Request.Method) && !AccessControl.ValidOrigin(context.Request))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { error = "A same-origin request is required." });
                    return;
                }
                if (!HttpMethods.IsGet(context.Request.Method) && !context.Request.HasJsonContentType())
                {
                    context.Response.StatusCode = 415;
                    return;
                }
                var path = context.Request.Path.Value;
                if (path is not ("/api/health" or "/api/access" or "/api/session"))
                {
                    var session = context.RequestServices.GetRequiredService<AccessControl>().Get(context.Request);
                    if (session is null) { context.Response.StatusCode = 401; return; }
                    if (!HttpMethods.IsGet(context.Request.Method)
                        && !AccessControl.Equal(context.Request.Headers["X-AeroScience-CSRF"].ToString(), session.Csrf))
                    { context.Response.StatusCode = 403; return; }
                }
            }
            try { await next(context); }
            catch (ArgumentException e) when (!context.Response.HasStarted)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (InvalidOperationException e) when (!context.Response.HasStarted)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (Exception e) when (!context.Response.HasStarted && e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "Storage unavailable. Existing data was not replaced. " + e.Message }); }
        });
        app.UseDefaultFiles();
        var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        contentTypes.Mappings[".geojson"] = "application/geo+json";
        app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = "0.2.1" }));
        app.MapGet("/api/access", (HttpContext c) => new { requiresPairing = !AccessControl.IsLoopback(c) });
        app.MapPost("/api/session", (HttpContext c, PairRequest request, AccessControl access) =>
        {
            // Tabs in the same browser share their HttpOnly session cookie. Reuse its CSRF token
            // so opening a second display does not invalidate commands in the first display.
            if (request.Code is null && access.Get(c.Request) is { } existing)
                return Results.Ok(new { csrf = existing.Csrf, expiresUtc = existing.ExpiresUtc });
            var loopback = AccessControl.IsLoopback(c);
            if (!loopback && !access.AllowAttempt(c.Connection.RemoteIpAddress?.ToString() ?? "unknown")) return Results.StatusCode(429);
            var session = access.Pair(loopback, request.Code);
            if (session is null) return Results.Unauthorized();
            c.Response.Cookies.Append(AccessControl.CookieName, session.Token, new CookieOptions
            { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = c.Request.IsHttps, MaxAge = TimeSpan.FromHours(12), IsEssential = true });
            return Results.Ok(new { csrf = session.Csrf, expiresUtc = session.ExpiresUtc });
        });
        app.MapGet("/api/state", (MissionCoordinator missions) => missions.Snapshot());
        app.MapPost("/api/missions", (StartMissionRequest request, MissionCoordinator missions) =>
        {
            var mission = missions.Start(request);
            return Results.Created($"/api/missions/{mission.Id}", mission);
        });
        app.MapPost("/api/missions/{id:guid}/end", (Guid id, MissionCoordinator missions) => Results.Ok(missions.End(id)));
        app.MapGet("/api/missions", (int? limit, int? offset, MissionStore missions) =>
            missions.List(Math.Clamp(limit ?? 50, 1, 100), Math.Max(offset ?? 0, 0)));
        app.MapGet("/api/missions/{id:guid}", (Guid id, MissionStore missions) =>
            missions.Detail(id) is { } detail ? Results.Ok(detail) : Results.NotFound());
        app.MapGet("/api/missions/{id:guid}/telemetry", (Guid id, int? limit, int? offset, MissionStore missions) =>
            missions.Detail(id) is null ? Results.NotFound() : Results.Ok(missions.Samples(id, Math.Clamp(limit ?? 1000, 1, 5000), Math.Max(offset ?? 0, 0))));
        app.MapGet("/api/missions/{id:guid}/export", (Guid id, MissionStore missions, HttpContext context) =>
        {
            var detail = missions.Detail(id);
            if (detail is null) return Results.NotFound();
            if (detail.Mission.Status == MissionStatus.Recording) return Results.Conflict(new { error = "End the mission before exporting." });
            return Results.Stream(stream => missions.ExportJsonAsync(id, stream, context.RequestAborted), "application/json", $"AeroScience-mission-{id}.json");
        });
        app.MapResearchEndpoints();
        app.MapGet("/api/settings", (HttpContext c, AccessControl access) => new
        {
            version = "0.2.1",
            telemetry = options.Telemetry,
            options.SamplingIntervalMs,
            options.EnableLan,
            options.EnableToolbarBridge,
            lanUrl = options.EnableLan ? $"http://{options.LanAddress}:{options.Port}" : null,
            pairingCode = options.EnableLan && AccessControl.IsLoopback(c) ? access.PairingCode : null,
            dataDirectory = AccessControl.IsLoopback(c) ? options.DataDirectory : null,
            desktopAvailable = AccessControl.IsLoopback(c) && c.RequestServices.GetService<IDesktopActions>() is not null,
            missionStorage = AccessControl.IsLoopback(c) ? c.RequestServices.GetService<IDesktopActions>()?.MissionStorage : null,
            scienceStorage = AccessControl.IsLoopback(c) ? c.RequestServices.GetService<IDesktopActions>()?.ScienceStorage : null,
            toolbarStatus = AccessControl.IsLoopback(c) ? c.RequestServices.GetService<IDesktopActions>()?.Toolbar : null,
            simConnect = OperatingSystem.IsWindows() ? "Windows x64 native SDK runtime required in SimConnect mode" : "Live telemetry requires Windows x64; offline history remains available",
        });
        app.MapPost("/api/desktop/{action}", (string action, HttpContext context) =>
        {
            if (!AccessControl.IsLoopback(context)) return Results.StatusCode(403);
            var desktop = context.RequestServices.GetService<IDesktopActions>();
            if (desktop is null) return Results.Conflict(new { error = "Open the AeroScience Windows application to manage native settings." });
            switch (action)
            {
                case "settings": desktop.OpenSettings(); break;
                case "toolbar": desktop.OpenToolbar(); break;
                case "storage": desktop.OpenStorage(); break;
                case "data-management": desktop.OpenDataManagement(); break;
                case "feedback": desktop.CollectFeedback(); break;
                default: return Results.NotFound();
            }
            return Results.Accepted(value: new { opened = true });
        });
        // Server-sent events are a one-way, bounded latest-state stream. Commands use the shared API.
        app.MapGet("/api/events", async (HttpContext context, MissionCoordinator missions) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                do
                {
                    var access = context.RequestServices.GetRequiredService<AccessControl>();
                    if (access.Get(context.Request) is null) break;
                    var json = JsonSerializer.Serialize(missions.Snapshot(), WireJson.Options);
                    await context.Response.WriteAsync($"event: state\ndata: {json}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                } while (await timer.WaitForNextTickAsync(context.RequestAborted));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (IOException) { /* A display disconnected; recording continues in TelemetryWorker. */ }
        });
        app.Logger.LogInformation("AeroScience 0.2.1 — {Source} telemetry; dashboard http://127.0.0.1:{Port}; data {Directory}", options.Telemetry, options.Port, options.DataDirectory);
        return app;
    }
}
