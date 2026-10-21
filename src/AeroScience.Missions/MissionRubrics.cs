using AeroScience.Shared;
namespace AeroScience.Missions;

// Versioned pilot policy, not a sensor accuracy standard. Changes require a new revision.
public static class MissionRubrics
{
    public const string Policy="aeroscience-performance-1";
    public static GradingRubric Define(MissionTemplateKind template,bool operatingApplicable=true)=>new(1,Policy,1,template,
        [new(RubricCategory.MissionDesign,20),new(RubricCategory.SensorOperatingCompliance,30),new(RubricCategory.FlightPlanExecution,25),new(RubricCategory.AcquisitionDiscipline,25)],
        [new("design",RubricCategory.MissionDesign,1,true,["frozen-plan"],"Explicit objective, enabled instruments and template-appropriate bounded research work."),
         new("operating",RubricCategory.SensorOperatingCompliance,1,true,["operating-assessment"],"Documented applicable P04 rules during frozen research work, with unknown outcomes retained.",operatingApplicable,operatingApplicable?"Remote geometry/overlap rules are applicable.":"Atmospheric simulator channels have no documented physical sensor operating envelope. Category excluded before flight; remaining weights normalized explicitly."),
         new("execution",RubricCategory.FlightPlanExecution,1,true,["flight-position"],template==MissionTemplateKind.AtmosphericProfile?"Frozen altitude targets, not polygon overlap.":"Ordered research route/region evidence; no interpolation through telemetry gaps."),
         new("discipline",RubricCategory.AcquisitionDiscipline,1,true,["collection-intent"],"Frozen expected work and actual eligible acquisition attempts, independently of source success or science-session boundaries.")],
        new(90,80,70,60),.8,true,["AeroScience pilot-performance policy; initial thresholds require representative live validation.","Cloud/nodata/provider failures affect factual scientific yield, not automatically pilot compliance.","Unknown evidence is bounded, never silently scored perfect or discarded."]);
    public static MissionAssessmentContext Freeze(Guid mission,DateTimeOffset at,ScienceMode mode,string planHash,MissionPlan? plan,InstrumentConfiguration[] settings,string? objective=null,ReadinessAssessment? readiness=null)
    {
        var configs=settings.Where(c=>c.Enabled).Select((c,i)=>new EffectiveInstrumentConfiguration(MissionPlanning.StableWaypointId(mission,10000+i),1,at,c,c.Calibration,OperatingPolicy:new("hybrid-realistic-1",1,PreflightReadiness.Hash("hybrid-realistic-1")),Specification:InstrumentCalibration.Describe(c.InstrumentId).Specification)).ToArray();
        var template=plan?.Template??MissionTemplateKind.GeneralResearch;
        var rubric=Define(template,configs.Any(c=>RemoteAcquisition.Instruments.ContainsKey(c.Settings.InstrumentId)));
        var obligations=new List<MissionObligation>();
        void Add(string criterion,string scope,string? instrument=null,int? region=null,Guid? waypoint=null,Guid? area=null,string? session=null,double? amount=null,string? unit=null,double? targetAltitude=null)
        {obligations.Add(new(MissionPlanning.StableWaypointId(mission,20000+obligations.Count),criterion,1,session,waypoint,area,TargetAmount:amount,Unit:unit,RepeatRuleId:"first-eligible-slot-1",InstrumentId:instrument,Scope:scope,RegionIndex:region,TargetAltitudeFeet:targetAltitude));}
        Add("design","plan");
        var regions=plan?.SurveyAreas??[];
        var sessions=(plan?.Collection?.Triggers??[]).Where(t=>t.Action==CollectionAction.StartData&&t.PlannedSessionKey is not null).Select(t=>t.PlannedSessionKey!).Distinct().ToArray();
        var scopes=regions.Length>0?regions.Select((_,i)=>(Scope:"region",Index:(int?)i,Key:(string?)null)).ToArray():sessions.Length>0?sessions.Select(k=>(Scope:"planned-session",Index:(int?)null,Key:(string?)k)).ToArray():[(Scope:"flight",Index:(int?)null,Key:(string?)null)];
        if(regions.Length>0&&sessions.Length>0)scopes=[..scopes,..sessions.Select(k=>(Scope:"planned-session",Index:(int?)null,Key:(string?)k))];
        foreach(var scope in scopes)
        {
            var area=scope.Index is {} index?plan?.Collection?.SurveyAreas?.FirstOrDefault(a=>a.PlanAreaIndex==index)?.Id:null;
            foreach(var config in configs)
            {
                var duration=scope.Scope=="flight"?plan?.EstimatedDurationMinutes*60:null;
                var expected=duration>0?Math.Ceiling(duration.Value/(config.Settings.IntervalMs/1000d)):(double?)null;
                Add("discipline",scope.Scope,config.Settings.InstrumentId,scope.Index,area:area,session:scope.Key,amount:expected,unit:expected is null?null:"acquisition sets");
                if(RemoteAcquisition.Instruments.ContainsKey(config.Settings.InstrumentId))Add("operating",scope.Scope,config.Settings.InstrumentId,scope.Index,area:area,session:scope.Key);
            }
        }
        if(template==MissionTemplateKind.AtmosphericProfile&&plan?.MinimumAltitudeFeet is {} low&&plan.MaximumAltitudeFeet is {} high&&high>low)
        {
            var step=(plan.AtmosphericSettings?.AltitudeIntervalMetres??100)/.3048;
            var bins=Math.Min(128,Math.Max(2,(int)Math.Ceiling((high-low)/step)+1));
            for(var i=0;i<bins;i++)Add("execution","altitude",region:i,targetAltitude:low+(high-low)*i/(bins-1));
        }
        else if(plan?.Waypoints is {Length:>0} route)foreach(var (w,i) in route.Select((w,i)=>(w,i)))Add("execution","waypoint",region:i,waypoint:w.Id??MissionPlanning.StableWaypointId(plan.Id,i));
        else foreach(var scope in scopes)Add("execution",scope.Scope,region:scope.Index,session:scope.Key);
        // Normalize obligation work weights once; later session creation cannot dilute them.
        foreach(var group in obligations.GroupBy(o=>o.CriterionId).ToArray())
            foreach(var obligation in group.ToArray()){var i=obligations.IndexOf(obligation);obligations[i]=obligation with{Weight=1d/group.Count()};}
        var context=new MissionAssessmentContext(1,mission,at,mode,planHash,rubric,obligations.ToArray(),configs,readiness);
        EvidenceContracts.Validate(context);return context;
    }
}
