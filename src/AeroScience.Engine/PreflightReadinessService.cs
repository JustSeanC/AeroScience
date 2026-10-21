using AeroScience.Shared;
using AeroScience.Missions;
namespace AeroScience.Engine;
public sealed class PreflightReadinessService(ScienceStore science,ScientificAccessSettings access,StorageManagement storage)
{
    public ReadinessEnvironment Environment()
    {
        var s=storage.Snapshot(); SourceAvailability[] sources=[];var limitations=new List<string>();
        try{sources=science.Availability().ToArray();}catch(Exception e)when(e is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException){limitations.Add("Scientific Library is unavailable; no source coverage was assumed.");}
        return new(sources,access.Get().Preferences,s.Flight.Available,s.Science.Available,s.Flight.FreeBytes,s.Science.FreeBytes,
            s.Flight.LimitBytes-s.Flight.UsedBytes,s.Science.LimitBytes-s.Science.UsedBytes,limitations.ToArray());
    }
    public ReadinessAssessment Assess(MissionPlan draft)=>PreflightReadiness.Evaluate(draft,Environment(),DateTimeOffset.UtcNow);
}
