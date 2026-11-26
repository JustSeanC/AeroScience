using AeroScience.Shared;

namespace AeroScience.Missions;

// Host-selected source and P02 capability restrictions are authoritative. A
// provider cannot grant X-Plane recording/science by reporting its own flags true.
public sealed class SimulatorAccessPolicy
{
    public string SimulatorId { get; }
    public bool XPlane => SimulatorId == SimulatorPreferences.XPlane12;
    private int eligible;
    public SimulatorAccessPolicy(string simulatorId)
    {
        if (!SimulatorPreferences.Known(simulatorId)) throw new ArgumentException("Unsupported simulator identity.");
        SimulatorId = simulatorId; eligible = XPlane ? 0 : 1;
    }
    public bool RecordingAllowed => !XPlane && Volatile.Read(ref eligible) == 1;
    public bool ScienceAllowed => RecordingAllowed;
    public string ReasonKey => XPlane ? "simulator.recordingDisabled" : "simulator.sourceMismatch";
    public string Reason => PresentationText.Text(ReasonKey);
    public SimulatorAvailability Availability => new(1, SimulatorId, RecordingAllowed, ScienceAllowed, ReasonKey);
    public void Observe(TelemetryUpdate update)
    {
        if (XPlane || update.Connection.Origin is { } origin && origin.SimulatorId != SimulatorId
            || update.SampleContext is { } context && (!context.Capabilities.ProductionRecordingReady || !context.Capabilities.ScienceIntegrationReady))
            Volatile.Write(ref eligible, 0);
        else if (update.Connection.State == ConnectionState.Connected && update.Frame is { IsValid: true })
            Volatile.Write(ref eligible, 1);
    }
    public void RequireRecording()
    { if (!RecordingAllowed) throw new InvalidOperationException(Reason); }
    public void RequireScience()
    { if (!ScienceAllowed) throw new InvalidOperationException(Reason); }
}
