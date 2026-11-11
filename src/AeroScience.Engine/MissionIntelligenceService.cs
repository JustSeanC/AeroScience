using AeroScience.Shared;
using AeroScience.Missions;
namespace AeroScience.Engine;

// Bounded metadata only; the existing background performance worker owns live calculations.
// No archive replay, coverage union, trigger evaluation, or acquisition is performed by a read.
public sealed class MissionIntelligenceService(MissionCoordinator coordinator, MissionStore store,
    ResearchOperations research, MissionPerformanceService performance,ScienceStore? science=null)
{
    readonly object metadataGate=new();Guid? cachedMission;MissionPlan? cachedPlan;
    RetainedScienceProgress? retained;
    RetainedScienceProgress? Retained(Guid? id)
    {
        if(id is null)return null;
        // Reuse the indexed original-record counts, outside the recording path. Shared
        // displays reuse one short-lived projection; telemetry is never counted as science.
        lock(metadataGate){
            if(retained?.MissionId==id&&DateTimeOffset.UtcNow-retained.CheckedUtc<TimeSpan.FromSeconds(2))return retained;
            try{if(science is null)throw new IOException("Scientific Library is unavailable.");var rows=science.SessionCounts(id.Value);
                return retained=new(id.Value,DateTimeOffset.UtcNow,rows.Sum(r=>r.OriginalAtmosphericValues),rows.Sum(r=>r.DerivedAtmosphericValues),rows.Sum(r=>r.RemoteProducts),"Retained original records; counts are not scientific quality or completion.");}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){return retained=new(id.Value,DateTimeOffset.UtcNow,null,null,null,"Scientific records unavailable. No count was inferred from telemetry.");}
        }
    }
    MissionPlan? Plan(Guid id)
    {lock(metadataGate){if(cachedMission!=id){cachedPlan=store.Research(id).PlanSnapshot;cachedMission=id;}return cachedPlan;}}
    public ToolbarIntelligence Native(EngineSnapshot flight,CollectionOverview? collection)
    {
        var id=flight.ActiveMission?.Id;var plan=id is {} p?Plan(p):null;
        var consumed=(collection?.State?.FiredOccurrenceIds??[]).Concat(collection?.State?.SuppressedOccurrenceIds??[]).ToHashSet();
        var remaining=plan?.Collection?.Triggers.Count(t=>!consumed.Contains(t.OccurrenceId))??0;
        return ToolbarIntelligenceProjection.Create(flight,collection,id is {} current?performance.CachedProjection(current):null,research.OperatingReports,
            $"{remaining} configured occurrences remain. Next crossing/order/ETA not assessed; see dashboard.") with{RetainedScience=Retained(id)};
    }
    public MissionIntelligence Read()
    {
        var (flight,collection,sensors)=coordinator.OperatingIntelligence();var id=flight.ActiveMission?.Id;
        var plan=id is {} p?Plan(p):null;
        var state=collection?.State;
        var triggers=(plan?.Collection?.Triggers??[]).Select(t=>new TriggerIntelligence(t,
            state?.FiredOccurrenceIds?.Contains(t.OccurrenceId)==true?"Fired":state?.SuppressedOccurrenceIds?.Contains(t.OccurrenceId)==true?"Suppressed":"Configured",
            "Configured occurrence, not an ETA or predicted next crossing. See recorded events for actual outcomes.")).ToArray();
        var report=id is {} current?performance.CachedProjection(current):null;
        return new(1,DateTimeOffset.UtcNow,flight,collection,plan,triggers,research.OperatingReports,report,
            performance.ProjectionFault is not null?"Unavailable: "+performance.ProjectionFault:report is null?"Pending":"Provisional",
            ["Up to 100 sessions shown; complete session evidence remains in the archived reports and paged session API.",
             "Provider footprints, modeled footprints, flown paths and scientifically valid acquired coverage are distinct.",
             "No next-trigger order or active-area membership is inferred from map proximity; configured occurrences and backend event evidence are shown."],sensors,
             id is {} researchFlight?coordinator.PersonalResearch(researchFlight):null){RetainedScience=Retained(id)};
    }
}
