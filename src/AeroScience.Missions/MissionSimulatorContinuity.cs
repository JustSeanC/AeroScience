using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed partial class MissionCoordinator
{
    private TelemetryFrame? xplaneBaseline;

    // Conservative P04 boundary: never join an unqualified gap or a new simulator
    // epoch to a continuing flight. Restarting recording is an explicit pilot action.
    private bool CheckXPlaneContinuity(ref TelemetryUpdate input)
    {
        if(active is null)return false;
        if(input.Connection.Origin is {} foreign&&foreign.SimulatorId!=simulatorPolicy.SimulatorId)return false;
        var next=input.Frame;var previous=xplaneBaseline;
        string? reason=null;
        if(!simulatorPolicy.RecordingAllowed||input.Connection.State!=ConnectionState.Connected||next is null)
            reason="Aircraft confirmation or connection became unavailable.";
        else if(active.SimulatorOrigin is not {} frozen||next.SimulatorOrigin is not {} origin||!SimulatorProvenance.SameRecording(frozen,origin))
            reason="A new connection epoch or source identity requires a separate recording.";
        else if(previous is not null)
        {
            if(next.TimestampUtc==previous.TimestampUtc)return true; // same confirmed sample, no new exposure
            var elapsed=(next.TimestampUtc-previous.TimestampUtc).TotalSeconds;
            var before=previous.SimulatorContext?.Clock;var after=next.SimulatorContext?.Clock;
            if(next.Aircraft!=previous.Aircraft||origin.AircraftType!=previous.SimulatorOrigin?.AircraftType||origin.AircraftRegistration!=previous.SimulatorOrigin?.AircraftRegistration)
                reason="Aircraft identity changed; the original flight is retained separately.";
            else if(elapsed<=0||elapsed>2||before is null||after is null||after.Replay
                ||after.RunningSeconds<before.RunningSeconds||after.FlightSeconds<before.FlightSeconds)
                reason="Clock, replay or receipt continuity is unqualified.";
            else if(after.RequestedRate!=before.RequestedRate||RateChanged(after.DistanceRate,before.DistanceRate)
                ||RateChanged(after.ActualRate,before.ActualRate)||RateChanged(after.DilationAwareRate,before.DilationAwareRate))
                reason="Simulation time rate changed; continuous research credit cannot be assumed.";
            else if(!after.Paused&&!before.Paused&&(after.FlightSeconds<=before.FlightSeconds||after.RunningSeconds<=before.RunningSeconds
                ||Math.Abs(after.FlightSeconds-before.FlightSeconds-elapsed)>Math.Max(.5,elapsed*.25)))
                reason="Simulator clock progress differs from qualified host receipt intervals.";
            else if(Geodesy.Distance(new(previous.Latitude,previous.Longitude),new(next.Latitude,next.Longitude))
                >Math.Max(previous.GroundspeedKnots,next.GroundspeedKnots)*.5144444444444445*Math.Max(0,elapsed)+1000)
                reason="Position discontinuity candidate; no route or survey segment was inferred.";
        }
        if(reason is null)
        {
            if(previous?.IsPaused==true&&next?.IsPaused==false)input=input with{Frame=next with{ContinuityBreak=true}};
            xplaneBaseline=input.Frame;return false;
        }
        var mission=active;var at=lastRecordedUtc??mission.StartedUtc;
        // Append source-qualified transition evidence without rewriting initial origin,
        // samples, acquisition hashes or earlier scientific sessions.
        store.AddEvent(mission.Id,Now,"connection",JsonSerializer.Serialize(new{reason,previous=previous?.SimulatorOrigin,
            next=input.Connection.Origin,input.SampleContext,input.Connection.State,input.Connection.Message},WireJson.Options));
        store.Finish(mission.Id,MissionStatus.Interrupted,at,reason+" Start a new recording after qualification.");
        research?.End();collection=null;geographicCollection=null;active=null;xplaneBaseline=null;
        recordingStop=new("simulatorContinuity",reason,mission.Id,Now);
        connection=input.Connection;frame=null;return true;
    }
    private static bool RateChanged(double? a,double? b)=>a is null||b is null?a!=b:Math.Abs(a.Value-b.Value)>.01;
}
