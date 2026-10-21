namespace AeroScience.Shared;

// Read-only projections of retained evidence. They never evaluate geometry or create grades.
public sealed record CollectionActionAvailability(CollectionAction Action, bool Available, string Reason);
public sealed record CollectionOverview(CollectionState? State, ScienceCollectionSession[] Sessions,
    bool Fault, string[] Capabilities, CollectionActionAvailability[] Actions, ScienceCollectionSession? CurrentSession,
    CollectionAreaStatus[]? Areas=null);
public sealed record CollectionAreaStatus(Guid Id,string Name,bool? Inside,DateTimeOffset? PositionUtc);
public sealed record TriggerIntelligence(CollectionTrigger Trigger, string Status, string Explanation);
public sealed record MissionIntelligence(int Version, DateTimeOffset GeneratedUtc, EngineSnapshot Flight,
    CollectionOverview? Collection, MissionPlan? Plan, TriggerIntelligence[] Triggers,
    InstrumentOperatingReport[] Operating, MissionPerformanceReport? Performance, string PerformanceState,
    string[] Limitations);
