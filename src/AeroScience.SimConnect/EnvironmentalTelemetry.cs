using AeroScience.Shared;

namespace AeroScience.SimConnect;

// Each variable is an independent optional FLOAT64 request. SDK rejection cannot invalidate aircraft, atmosphere or another scalar.
public static class EnvironmentalTelemetry
{
    public sealed record Definition(string Id,string Name,string Unit,string Meaning);
    public static readonly Definition[] Variables =
    [
        new("ground-altitude","GROUND ALTITUDE","meters","Surface beneath user aircraft; SDK does not specify a geodetic vertical datum."),
        new("in-cloud","AMBIENT IN CLOUD","bool","Local aircraft cloud occupancy, not a regional cloud volume."),
        new("cloud-density","ENV CLOUD DENSITY","percent over 100","Local cloud density 0–1, not percent 0–100."),
        new("visibility","AMBIENT VISIBILITY","meters","Ambient particle visibility; not a complete optical propagation model."),
        new("precipitation","AMBIENT PRECIP RATE","millimeters of water","SDK precipitation quantity; no documented time denominator. Do not label mm/h."),
        new("precipitation-state","AMBIENT PRECIP STATE","mask","SDK mask: 2 none, 4 rain, 8 snow; unknown bits retained."),
        new("wind-x","AMBIENT WIND X","meters per second","East/west component. The cited SDK entry does not define the positive-axis sign; no sign conversion applied."),
        new("wind-y","AMBIENT WIND Y","meters per second","Vertical component. The cited SDK entry does not define the positive-axis sign; no sign conversion applied."),
        new("wind-z","AMBIENT WIND Z","meters per second","North/south component. The cited SDK entry does not define the positive-axis sign; no sign conversion applied.")
    ];
    public static EnvironmentReading Decode(int index,double raw,DateTimeOffset utc,bool paused=false)
    {
        var d=Variables[index];
        var valid=double.IsFinite(raw) && (d.Id switch
        {
            "in-cloud"=>raw is 0 or 1,
            "cloud-density"=>raw is >=0 and <=1,
            "visibility" or "precipitation"=>raw>=0,
            "precipitation-state"=>raw>=0&&raw<=uint.MaxValue&&raw==Math.Truncate(raw),
            _=>true
        });
        return new(d.Id,utc.ToUniversalTime(),valid?raw:null,d.Unit,d.Name,
            paused?ObservationQuality.Paused:valid?ObservationQuality.Valid:ObservationQuality.Invalid,d.Meaning);
    }
    public static EnvironmentReading Unavailable(int index,DateTimeOffset utc)
    {
        var d=Variables[index];return new(d.Id,utc.ToUniversalTime(),null,d.Unit,d.Name,ObservationQuality.Unavailable,"The SDK rejected this optional variable. Other telemetry continues.");
    }
}
