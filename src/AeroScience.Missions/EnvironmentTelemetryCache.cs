using AeroScience.Shared;

namespace AeroScience.Missions;

// Used only inside the existing coordinator gate. Samples keep auxiliary receipt times instead of implying simultaneous native packets.
public sealed class EnvironmentTelemetryCache
{
    private readonly Dictionary<string,EnvironmentReading> readings=[];
    private AtmosphericFrame? atmosphere;
    private string? aircraft;
    private DateTimeOffset? lastAircraftUtc;
    public void Apply(TelemetryUpdate update)
    {
        if(update.Connection.State!=ConnectionState.Connected){Clear();return;}
        if(update.Frame is {} f)
        {
            if(f.IsPaused || aircraft is not null&&aircraft!=f.Aircraft || lastAircraftUtc is {} utc&&f.TimestampUtc<utc)Clear();
            aircraft=f.Aircraft;lastAircraftUtc=f.TimestampUtc;
        }
        if(update.Environment is {} r && (!readings.TryGetValue(r.Id,out var old)||r.TimestampUtc>old.TimestampUtc))readings[r.Id]=r;
        if(update.Atmosphere is {} a && (atmosphere is null||a.TimestampUtc>atmosphere.TimestampUtc))atmosphere=a;
    }
    private void Clear(){readings.Clear();atmosphere=null;aircraft=null;lastAircraftUtc=null;}
    public TelemetryFrame Enrich(TelemetryFrame frame)
    {
        if(frame.IsPaused)return frame with{Environment=null,Atmosphere=null};
        bool Fresh(DateTimeOffset utc)=>(frame.TimestampUtc-utc).TotalSeconds is >=0 and <=3;
        var selected=readings.Values.Where(r=>Fresh(r.TimestampUtc)).OrderBy(r=>r.Id).ToArray();
        return frame with{Environment=selected.Length==0?null:new(selected),Atmosphere=atmosphere is {Quality:ObservationQuality.Valid} a&&Fresh(a.TimestampUtc)?a:null};
    }
}
