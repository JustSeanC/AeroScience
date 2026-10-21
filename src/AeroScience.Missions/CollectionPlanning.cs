using AeroScience.Shared;
namespace AeroScience.Missions;

public static class CollectionPlanning
{
    // Explicit Planner review operation, never a migration/read-time rewrite of old plans.
    public static CollectionPlan Bind(MissionPlan draft)
    {
        var collection=draft.Collection??throw new ArgumentException("Configure collection automation first.");
        var bound=collection with{SurveyAreas=(collection.SurveyAreas??[]).Select(a=>a with{GeometrySha256=EvidenceContracts.SurveyGeometryHash(
            draft.SurveyAreas?.ElementAtOrDefault(a.PlanAreaIndex)??throw new ArgumentException("Survey target no longer exists."))}).ToArray()};
        Validate(draft,bound);return bound;
    }
    public static void Validate(MissionPlan plan,CollectionPlan collection)
    {
        EvidenceContracts.ValidatePlan(collection,plan);
        if((collection.SurveyAreas??[]).Sum(a=>plan.SurveyAreas![a.PlanAreaIndex].Vertices.Length)>1024)
            throw new ArgumentException("Automatic boundary evaluation supports at most 1,024 total vertices. Split the automation area or use manual collection.");
        foreach(var area in collection.SurveyAreas??[])
        {
            var vertices=plan.SurveyAreas![area.PlanAreaIndex].Vertices;
            if(vertices.Length>256||vertices.Any(p=>Math.Abs(p.Latitude)>80||Geodesy.Distance(vertices[0],p)>100000))
                throw new ArgumentException("Automatic boundary geometry supports at most 256 vertices, below 80° latitude and within 100 km of its first vertex. Split this study area or use manual collection; saved geographic coordinates are unchanged.");
        }
        foreach(var trigger in collection.Triggers.Where(t=>t.Kind is CollectionTriggerKind.Waypoint or CollectionTriggerKind.DirectionalGate))
            if(Math.Abs(plan.Waypoints!.Single(w=>w.Id==trigger.TargetId).Latitude)>80)
                throw new ArgumentException("Automatic point/gate collection is unsupported above 80° latitude. Use manual collection.");
        foreach(var group in collection.Triggers.GroupBy(t=>(t.Kind,t.TargetId,t.DirectionTrueDegrees)))
            if(group.Select(t=>t.Pass).Distinct().Count()!=group.Count()||group.Select(t=>t.ToleranceMetres).Distinct().Count()!=1)
                throw new ArgumentException("At the same target/direction, configure one action per numbered pass with a consistent tolerance. Use separate explicit targets for conflicting actions.");
    }
}
