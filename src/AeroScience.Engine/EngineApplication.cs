using System.Net;
using System.Reflection;
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
        builder.Services.AddSingleton(sp => new ScientificAccessSettings(sp.GetRequiredService<EngineOptions>().ConfigurationDirectory ?? sp.GetRequiredService<EngineOptions>().DataDirectory, NetworkCost.IsUnmetered));
        builder.Services.AddSingleton(sp => { _ = sp.GetRequiredService<StorageManagement>(); return new AutomaticScientificData(sp.GetRequiredService<ScienceStore>(),sp.GetRequiredService<ScientificAccessSettings>()); });
        builder.Services.AddSingleton<ScientificCatalog>();
        builder.Services.AddSingleton<IEarthdataCredentials,UnavailableEarthdataCredentials>();
        builder.Services.AddSingleton<ScientificLibraryJobs>();
        builder.Services.AddHostedService<ScientificLibraryWorker>();
        builder.Services.AddSingleton(sp=>new ScientificImportSession(sp.GetRequiredService<ScienceStore>(),sp.GetRequiredService<AutomaticScientificData>().RefreshLocalReadiness));
        builder.Services.AddSingleton<ScientificPlanData>();
        builder.Services.AddSingleton<ScientificFlightPrediction>();
        builder.Services.AddHostedService<ScientificPreparationWorker>();
        builder.Services.AddSingleton<ScientificDownlinkSignals>();
        builder.Services.AddHostedService<ScientificDownlinkWorker>();
        builder.Services.AddSingleton(sp => new AviationReference(
            sp.GetRequiredService<EngineOptions>().ConfigurationDirectory ?? sp.GetRequiredService<EngineOptions>().DataDirectory,
            sp.GetRequiredService<EngineOptions>().DataDirectory,
            flightActive: () => sp.GetRequiredService<MissionCoordinator>().Snapshot().ActiveMission is not null));
        builder.Services.AddHostedService<AviationAcquisitionWorker>();
        builder.Services.AddSingleton(sp => new MapSettings(sp.GetRequiredService<EngineOptions>().ConfigurationDirectory ?? sp.GetRequiredService<EngineOptions>().DataDirectory));
        builder.Services.AddSingleton(sp => new ApplicationUpdates(sp.GetRequiredService<EngineOptions>().ConfigurationDirectory ?? sp.GetRequiredService<EngineOptions>().DataDirectory));
        builder.Services.AddHostedService<ApplicationUpdateWorker>();
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
                try { science.Initialize(); missions.ReconcileDatasetReferences(science.LibraryId, science.ContainsDataset); }
                catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
                { sp.GetRequiredService<ILogger<ScienceStore>>().LogWarning("Science references need review: {Message}", error.Message); }
            return science;
        });
        builder.Services.AddSingleton(sp => new ScienceUnitSettings(sp.GetRequiredService<EngineOptions>().ConfigurationDirectory ?? sp.GetRequiredService<EngineOptions>().DataDirectory));
        builder.Services.AddSingleton(sp => new InstrumentCalibrationSettings(sp.GetRequiredService<EngineOptions>().ConfigurationDirectory ?? sp.GetRequiredService<EngineOptions>().DataDirectory));
        builder.Services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<EngineOptions>(); var science = sp.GetRequiredService<ScienceStore>();
            var management = new StorageManagement(o.ConfigurationDirectory ?? o.DataDirectory, sp.GetRequiredService<MissionStore>(), science);
            science.Management = management; science.MissionIsProtected = sp.GetRequiredService<MissionStore>().IsProtected; return management;
        });
        builder.Services.AddSingleton(sp=>new ScientificCache(sp.GetRequiredService<ScienceStore>(),sp.GetRequiredService<MissionStore>(),sp.GetRequiredService<StorageManagement>(),log:message=>sp.GetRequiredService<ILogger<ScientificCache>>().LogInformation("{CleanupResult}",message)));
        builder.Services.AddSingleton(sp => { _ = sp.GetRequiredService<StorageManagement>(); return new ResearchOperations(sp.GetRequiredService<MissionStore>(), sp.GetRequiredService<ScienceStore>(), sp.GetRequiredService<ScienceUnitSettings>().Get,()=>sp.GetRequiredService<ScientificAccessSettings>().Get().Preferences); });
        builder.Services.AddSingleton(sp => new MissionCoordinator(sp.GetRequiredService<MissionStore>(), sp.GetRequiredService<EngineOptions>().SamplingIntervalMs,
            requireFreshTelemetry: true, ensureRecordingCapacity: () =>
            {
                sp.GetRequiredService<StorageManagement>().RequireFlightCapacity();
            }, research: sp.GetRequiredService<ResearchOperations>(),assessPreflight:sp.GetRequiredService<PreflightReadinessService>().Assess));
        builder.Services.AddSingleton<ITelemetryProvider, SimConnectTelemetryProvider>();
        configureServices?.Invoke(builder.Services);
        builder.Services.AddHostedService<TelemetryWorker>();
        builder.Services.AddSingleton(sp=>new ToolbarCommandHandler(sp.GetRequiredService<MissionCoordinator>(),sp.GetRequiredService<RouteMapService>(),
            (snapshot,collection)=>sp.GetRequiredService<MissionIntelligenceService>().Native(snapshot,collection), ApplicationProfile.Current.AuthorizeToolbar));
        builder.Services.AddSingleton<RouteMapService>();
        builder.Services.AddSingleton<ToolbarBridgeStatus>();
        builder.Services.AddHostedService<ToolbarWorker>();
        builder.Services.AddHostedService<StorageManagementWorker>();
        builder.Services.AddSingleton<ScientificAnalysisJobs>();
        builder.Services.AddSingleton<PreflightReadinessService>();
        builder.Services.AddSingleton<MissionPerformanceService>();
        builder.Services.AddSingleton<MissionIntelligenceService>();
        builder.Services.AddHostedService(sp=>sp.GetRequiredService<MissionPerformanceService>());
        builder.Services.AddHostedService(sp=>sp.GetRequiredService<ScientificAnalysisJobs>());

        var app = builder.Build();
        if(!app.Environment.IsEnvironment("Testing")){var diagnosticsOptions=app.Services.GetRequiredService<EngineOptions>();ScientificDiagnostics.Initialize(diagnosticsOptions.ConfigurationDirectory??diagnosticsOptions.DataDirectory);app.Lifetime.ApplicationStopping.Register(ScientificDiagnostics.Flush);}

        var options = app.Services.GetRequiredService<EngineOptions>();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob: https://tile.openstreetmap.org https://ibasemaps-api.arcgis.com https://basemaps-api.arcgis.com https://static-map-tiles-api.arcgis.com https://services.arcgisonline.com https://js.arcgis.com; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
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
        contentTypes.Mappings[".md"] = "text/plain; charset=utf-8";
        app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes, OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache" });
        var helpDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot", "help", "remote-sensing");
        if (Directory.Exists(helpDirectory)) app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = "/help/remote-sensing", ContentTypeProvider = contentTypes,
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(helpDirectory)
        });
        app.MapGet("/api/toolbar/bridge",(ToolbarBridgeStatus status)=>status.Snapshot());
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = ApplicationUpdates.CurrentVersion }));
        app.MapGet("/api/build", () => new {
            profile = ApplicationProfile.Current.Validation ? "validation" : "production",
            name = ApplicationProfile.Current.Name,
            sourceCommit = typeof(EngineApplication).Assembly.GetCustomAttributes<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion.Split('+').LastOrDefault(),
            port = options.Port,
            toolbarChannel = ApplicationProfile.Current.CommandEvent
        });
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
            c.Response.Cookies.Append(access.SessionCookieName, session.Token, new CookieOptions
            { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = c.Request.IsHttps, MaxAge = TimeSpan.FromHours(12), IsEssential = true });
            return Results.Ok(new { csrf = session.Csrf, expiresUtc = session.ExpiresUtc });
        });
        EngineSnapshot DisplaySnapshot(MissionCoordinator missions)
        {
            var map = app.Services.GetRequiredService<MapSettings>().Get();
            return missions.Snapshot() with { Maps = new(map.Revision, map.Preferences.OnlineEnabled, map.Preferences.Layer) };
        }
        app.MapGet("/api/state", (MissionCoordinator missions) => DisplaySnapshot(missions));
        app.MapGet("/api/mission-intelligence", (MissionIntelligenceService intelligence) => intelligence.Read());
        app.MapPost("/api/missions", (StartMissionRequest request, MissionCoordinator missions) =>
        {
            var mission = missions.Start(request);
            return Results.Created($"/api/missions/{mission.Id}", mission);
        });
        app.MapPost("/api/missions/{id:guid}/end", (Guid id, MissionCoordinator missions) => Results.Ok(missions.End(id)));
        app.MapGet("/api/missions", (int? limit, int? offset, string? search, string? status, string? sort, MissionStore missions) =>
            missions.List(Math.Clamp(limit ?? 50, 1, 100), Math.Max(offset ?? 0, 0), search:search,status:status,sort:sort));
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
            version = ApplicationUpdates.CurrentVersion,
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
                case "science-import": desktop.ImportScientificSource(); break;
                case "terrain-import": desktop.ChooseScientificDataset("terrain"); break;
                case "thermal-import": desktop.ChooseScientificDataset("thermal"); break;
                case "multispectral-import": desktop.ChooseScientificDataset("multispectral"); break;
                case "hyperspectral-header": desktop.ChooseHyperspectral(false); break;
                case "hyperspectral-folder": desktop.ChooseHyperspectral(true); break;
                case "earthdata-connect": desktop.ConnectEarthdata(); break;
                case "compact-flight": desktop.CompactStorage(false); break;
                case "compact-science": desktop.CompactStorage(true); break;
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
                    var json = JsonSerializer.Serialize(DisplaySnapshot(missions), WireJson.Options);
                    await context.Response.WriteAsync($"event: state\ndata: {json}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                } while (await timer.WaitForNextTickAsync(context.RequestAborted));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (IOException) { /* A display disconnected; recording continues in TelemetryWorker. */ }
        });
        app.Logger.LogInformation("AeroScience {Version} — {Source} telemetry; dashboard http://127.0.0.1:{Port}; data {Directory}", ApplicationUpdates.CurrentVersion, options.Telemetry, options.Port, options.DataDirectory);
        return app;
    }
}
