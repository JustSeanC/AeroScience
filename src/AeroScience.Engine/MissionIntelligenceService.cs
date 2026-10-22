using AeroScience.Shared;
using AeroScience.Missions;
namespace AeroScience.Engine;

// Bounded metadata only; the existing background performance worker owns live calculations.
// No archive replay, coverage union, trigger evaluation, or acquisition is performed by a read.
public sealed class MissionIntelligenceService(MissionCoordinator coordinator, MissionStore store,
    ResearchOperations research, MissionPerformanceService performance)
{
    readonly object metadataGate=new();Guid? cachedMission;MissionPlan? cachedPlan;
    MissionPlan? Plan(Guid id)
    {lock(metadataGate){if(cachedMission!=id){cachedPlan=store.Research(id).PlanSnapshot;cachedMission=id;}return cachedPlan;}}
    public ToolbarIntelligence Native(EngineSnapshot flight,CollectionOverview? collection)
    {
        var id=flight.ActiveMission?.Id;var plan=id is {} p?Plan(p):null;
        var consumed=(collection?.State?.FiredOccurrenceIds??[]).Concat(collection?.State?.SuppressedOccurrenceIds??[]).ToHashSet();
        var remaining=plan?.Collection?.Triggers.Count(t=>!consumed.Contains(t.OccurrenceId))??0;
        return ToolbarIntelligenceProjection.Create(flight,collection,id is {} current?performance.CachedProjection(current):null,research.OperatingReports,
            $"{remaining} configured occurrences remain. Next crossing/order/ETA not assessed; see dashboard.");
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
             "No next-trigger order or active-area membership is inferred from map proximity; configured occurrences and backend event evidence are shown."],sensors);
    }
}
