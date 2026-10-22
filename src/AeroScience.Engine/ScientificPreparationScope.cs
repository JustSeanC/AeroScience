using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed record PreflightScope(string Mode="entire",int[]? AreaIndices=null,int[]? SegmentIndices=null,string? PlanRevision=null);
public sealed record PreparationScopePreview(string PlanRevision,bool FirstHalfAvailable,string FirstHalfExplanation,
    PreflightScope Selection,GeographicBounds[] Areas,int FullRegions,int InstrumentUnits,int FullInstrumentUnits);
public static class ScientificPreparationScope
{
    public static object Review(MissionPlan plan,ScienceStore science,ScientificAccessSettings settings,PreflightScope? scope=null)
    {
        var preview=Preview(plan,scope);var areas=preview.Areas;var kinds=ScientificPlanPreparation.Kinds(plan);var sources=science.Availability();var p=settings.Get().Preferences;
        var retained=kinds.ToDictionary(k=>k.ToString(),k=>areas.Take(128).Count(a=>ScientificSourceSelection.Select(sources.Select(s=>s with{CoversPoint=ScientificSourceSelection.CoversArea(s.Source,a)}),k,p) is not null));
        return new{planName=plan.Name,areas,reviewedRegions=Math.Min(128,areas.Length),instruments=kinds,retained,allowance=p.PreflightTransferBytes,estimatedBytes=(long?)null,
            limitations="Native source grids/masks remain authoritative. Retained footprint candidates require re-verification. Transfer size and duration unknown before source/subset selection. Scope changes preserve verified sources; no additional preflight work starts until approved.",plan,scope=preview};
    }
    public static string Revision(MissionPlan plan)=>ScientificComposites.Identity(plan);
    // Order comes from geodesic route traversal and explicit route collection actions,
    // or the portions of that route inside declared research polygons. It is never cell sort order.
    private static List<(Coordinates A,Coordinates B,double Length)>? OrderedCollection(MissionPlan plan)
    {
        var points=plan.Waypoints??[];if(points.Length<2)return null;
        var collection=plan.Collection;var polygons=plan.SurveyAreas??[];
        if(collection is not null&&(collection.Boundary?.PauseOnExit==true||collection.Triggers.Any(t=>t.Kind!=CollectionTriggerKind.Waypoint||t.Pass!=1)))return null;
        if(collection is null&&polygons.Length==0)return null;
        var result=new List<(Coordinates,Coordinates,double)>();bool collecting=collection?.InitialPolicy==InitialCollectionPolicy.Manual;
        int inspected=0;
        for(int i=0;i<points.Length-1;i++){
            if(collection is not null){var actions=collection.Triggers.Where(t=>t.TargetId==points[i].Id).ToArray();
                if(actions.Length>1)return null;
                if(actions.SingleOrDefault() is {} trigger)collecting=trigger.Action switch{CollectionAction.StartData or CollectionAction.ResumeData=>true,CollectionAction.PauseData or CollectionAction.EndData=>false,_=>collecting};}
            var a=Geodesy.Point(points[i]);var b=Geodesy.Point(points[i+1]);var length=Geodesy.Distance(a,b);if(length<=0)continue;
            var count=(int)Math.Ceiling(length/350);if(count>20000||inspected+count>100000)return null;inspected+=count;
            var bearing=Geodesy.Bearing(a,b);
            for(int n=0;n<count;n++){
                var from=Geodesy.Direct(a,bearing,length*n/count);var to=Geodesy.Direct(a,bearing,length*(n+1)/count);var mid=Geodesy.Direct(a,bearing,length*(n+.5)/count);
                if(collection is not null?!collecting:!polygons.Any(p=>MissionAnalysis.Inside(mid,p.Vertices)))continue;
                result.Add((from,to,length/count));
            }
        }
        return result.Count>0?result:null;
    }
    public static PreparationScopePreview Preview(MissionPlan plan,PreflightScope? input=null)
    {
        var revision=Revision(plan);var request=input??new();if(request.PlanRevision is {} supplied&&supplied!=revision)throw new InvalidOperationException("The saved plan revision changed. Review preparation scope again.");
        var ordered=OrderedCollection(plan);var explanation=ordered is null?"First Half needs an ordered route with unambiguous waypoint collection actions, or route portions inside declared survey areas. Boundary pause/resume, area/gate triggers and repeated occurrences need explicit research area/segment selection or Entire Mission; no continuous collection interval is assumed.":"First 50% by WGS84 geodesic length of the ordered collection route. Transit outside declared research areas or explicit collection intervals is excluded. Polygon-edge membership is bounded at 350 m; requested source cells can extend past the route boundary.";
        var full=ScientificPlanPreparation.Cells(plan,.04).Distinct().ToArray();GeographicBounds[] selected;
        if(request.Mode=="entire")selected=full;
        else if(request.Mode=="first-half"){
            if(ordered is null)throw new ArgumentException(explanation);var remaining=ordered.Sum(s=>s.Length)/2;var cells=new List<GeographicBounds>();
            foreach(var segment in ordered){if(remaining<=0)break;var length=Math.Min(segment.Length,remaining);var end=Geodesy.Direct(segment.A,Geodesy.Bearing(segment.A,segment.B),length);cells.AddRange(ScientificGeographicWork.Cells([segment.A,end],[],.04));remaining-=length;}
            selected=cells.Distinct().ToArray();
        }
        else if(request.Mode=="areas"){
            var ai=request.AreaIndices??[];var si=request.SegmentIndices??[];
            if(ai.Length+si.Length==0||ai.Length+si.Length>4096||ai.Distinct().Count()!=ai.Length||si.Distinct().Count()!=si.Length||ai.Any(i=>i<0||i>=(plan.SurveyAreas?.Length??0))||si.Any(i=>i<0||i>=(plan.Waypoints?.Length??0)-1))throw new ArgumentException("Choose existing research areas or ordered route segments from this saved plan.");
            selected=ai.SelectMany(i=>ScientificGeographicWork.Cells([],[(plan.SurveyAreas??[])[i].Vertices],.04)).Concat(si.SelectMany(i=>ScientificGeographicWork.Cells([Geodesy.Point(plan.Waypoints![i]),Geodesy.Point(plan.Waypoints[i+1])],[],.04))).Distinct().Take(4097).ToArray();
        }
        else throw new ArgumentException("Choose First Half of Mission, Entire Mission or Choose Research Areas.");
        if(selected.Length>4096)throw new ArgumentException("Preparation exceeds 4,096 source regions. Select smaller research areas.");
        // Scope cells must belong to the plan inventory, including boundary-adjacent route cells.
        selected=selected.Where(full.Contains).ToArray();
        if(selected.Length==0&&full.Length>0&&request.Mode!="entire")throw new ArgumentException("Selected collection scope has no supported source cells in the saved plan inventory. Choose another area or Entire Mission.");
        var kinds=ScientificPlanPreparation.Kinds(plan).Length;
        return new(revision,ordered is not null,explanation,request with{PlanRevision=revision},selected,full.Length,selected.Length*kinds,full.Length*kinds);
    }
}
