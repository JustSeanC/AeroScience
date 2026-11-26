using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed record SimulatorSettingsView(int Version, Guid InstanceId, SimulatorPreferences? Preferences,
    SimulatorPreferences Effective, bool RestartRequired, bool CanWrite, string StatusKey,
    ConnectionState ConnectionState, string TechnicalReason, bool ProductionRecordingReady,
    bool ScienceIntegrationReady, string? Warning = null);

public sealed class SimulatorSettings(PreferenceStore preferences, EngineOptions options, MissionCoordinator coordinator,
    ScientificImportSession imports, LidarImportSession lidar)
{
    private readonly SimulatorPreferences effective = options.Simulator;
    private readonly Guid instance = Guid.NewGuid();
    public SimulatorSettingsView View(bool localOwner)
    {
        SimulatorPreferences? saved = null; string? warning = null;
        try { saved = preferences.Simulator(); }
        catch(Exception error) when(error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { warning=PresentationText.Text("simulator.invalidSaved"); }
        var protectedOperation=!coordinator.CanChangeSimulator||imports.IsBusy||lidar.IsBusy||File.Exists(preferences.JournalPath);
        if(localOwner&&protectedOperation&&warning is null)warning=PresentationText.Text("simulator.maintenance");
        var snapshot = coordinator.Snapshot();
        return new(1,instance,saved,effective,saved is not null&&!effective.SameConnection(saved),
            localOwner&&warning is null&&!protectedOperation,
            StatusKey(snapshot),snapshot.Connection.State,snapshot.Connection.Message,
            coordinator.LiveOperationsAllowed,coordinator.LiveOperationsAllowed,warning);
    }
    public SimulatorSettingsView Save(SimulatorChange change)
    {
        if(imports.IsBusy||lidar.IsBusy)throw new InvalidOperationException(PresentationText.Text("simulator.maintenance"));
        // Uses the original acquisition/command gate. No worker is stopped or
        // replaced; the new choice is read only on a later companion startup.
        try { using(var lease=coordinator.EnterMaintenance())preferences.SetSimulator(change); }
        catch(JsonException error){throw new InvalidOperationException(PresentationText.Text("simulator.invalidSaved"),error);}
        return View(localOwner:true);
    }
    private string StatusKey(EngineSnapshot snapshot)
    {
        var connection=snapshot.Connection;
        if(connection.Message=="SimulatorSourceMismatch")return "simulator.sourceMismatch";
        if(connection.Message=="ApiAccessDenied")return "simulator.accessDenied";
        if(connection.Message=="UnsupportedApiVersion")return "simulator.unsupportedApi";
        if(connection.State==ConnectionState.Connected&&snapshot.Telemetry is not null)
            return effective.Id==SimulatorPreferences.XPlane12?(coordinator.LiveOperationsAllowed?"simulator.qualifiedConnected":"simulator.readOnlyConnected"):"simulator.connected";
        if(connection.Message.Contains("Stale",StringComparison.Ordinal)||connection.Message is "SnapshotSpanExceeded" or "ConfirmationContinuityLost")
            return "simulator.stale";
        if(connection.Message is "WaitingForLoadedFlight" or "WaitingForInitialSnapshot" or "CoreMeasurementUnavailable" or "FlightProgressUnverified"
            or "PauseChangeAwaitingConfirmation" or "ClockUnavailable" or "ReplayNotLiveFlight" or "RequiredDatarefIdentityLost")
            return "simulator.waitingData";
        if(connection.State==ConnectionState.Unavailable)return "simulator.invalidApi";
        if(connection.State==ConnectionState.Disconnected)return coordinator.HadQualifiedTelemetry?"simulator.reconnecting":"simulator.waiting";
        return "simulator.connecting";
    }
}

public static class SimulatorSettingsApi
{
    public static void MapSimulatorSettings(this WebApplication app)
    {
        // Global API authentication, same-origin and CSRF checks run first.
        app.MapGet("/api/settings/simulator",(HttpContext c,SimulatorSettings settings)=>settings.View(AccessControl.IsLoopback(c)));
        app.MapPost("/api/settings/simulator",(HttpContext c,SimulatorChange change,SimulatorSettings settings)=>
            AccessControl.IsLoopback(c)?Results.Ok(settings.Save(change)):Results.StatusCode(403));
    }
}
