using System.Security.Claims;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

public sealed partial class OrganizationCampaignProgress
{
    sealed record CoverageSlot(int Band,int Row,int Column,long Observations,long Flights,long Dates,long Pilots,long Located)
    {public CoverageCounts Counts=>new(Observations,Flights,Dates,Pilots,Located);}
    public Task<CoverageGoalAssessment> AssessCoverageGoal(Guid id,int? revision,string? compatibility,ClaimsPrincipal actor,CancellationToken ct,Guid? devicePilot=null)=>Snapshot<CoverageGoalAssessment>(actor,devicePilot,async r=>{
        var goal=await Goal(id,revision,ct);var d=goal.Definition;
        var filter=new CampaignCoverageFilter(compatibility,d.OpportunityId,d.PublicationRevision,d.RequirementKey,d.Variable,d.Instrument,d.Category,d.FromUtc,d.ThroughUtc);CoverageFilter(filter);
        var candidates=CoverageFacts(goal.CampaignId,filter).Where(o=>o.PublicationSha256==d.PublicationSha256&&o.Unit==d.Unit);
        if(d.SourceSemanticsSha256 is {} semantics)candidates=candidates.Where(o=>o.SourceSemanticsSha256==semantics);
        var groups=await candidates.Select(o=>o.CompatibilityId).Distinct().LongCountAsync(ct);
        var selected=groups==0?null:compatibility??(groups==1?await candidates.Select(o=>o.CompatibilityId).FirstAsync(ct):null);
        var limitations=new List<string>{"Counts describe accepted original point samples, not continuous altitude profiles or complete-area sensor coverage. Distinct pilot accounts do not prove physical independence.","Each configured band/cell combination must independently meet all minimum counts. Different compatibility groups are never summed."};
        if(goal.HistoricalPublication)limitations.Add("This goal retains an earlier published card revision. Current card completion remains separate.");
        if(await MissingCoverage(goal.CampaignId,ct)>0)limitations.Add("Some current approved evidence could not be indexed or verified and contributes no coverage credit.");
        if(selected is null&&groups>1)return new(goal,"Insufficient Compatible Evidence",false,false,null,new(0,0,0,0),[],[],["Choose one compatible source group; these distinct units, source semantics or requirement revisions cannot be combined."],limitations.ToArray(),groups);
        var q=UniqueCoverage(candidates.Where(o=>o.CompatibilityId==selected));
        var a=d.Altitude;var grid=d.Grid;int bands=a is null?1:CampaignCoverageRules.BandCount(a),rows=grid?.Rows??1,columns=grid?.Columns??1;
        if(a is not null)q=q.Where(o=>o.AltitudeFeetMsl>=a.MinimumFeetMsl&&o.AltitudeFeetMsl<=a.MaximumFeetMsl);
        if(grid is not null)q=q.Where(o=>o.Latitude>=grid.South&&o.Latitude<=grid.North&&o.Longitude>=grid.West&&o.Longitude<=grid.East);
        // All operations remain SQL aggregates. Result cardinality is bounded by the validated goal,
        // never by an arbitrary cap on the campaign's retained observations.
        var indexed=q.Select(o=>new{O=o,Band=a==null?0:Math.Min(bands-1,(int)Math.Floor((o.AltitudeFeetMsl!.Value-a.MinimumFeetMsl)/a.BandFeet)),Row=grid==null?0:Math.Min(rows-1,(int)Math.Floor((o.Latitude!.Value-grid.South)/(grid.North-grid.South)*rows)),Column=grid==null?0:Math.Min(columns-1,(int)Math.Floor((o.Longitude!.Value-grid.West)/(grid.East-grid.West)*columns))});
        var slots=await indexed.GroupBy(v=>new{v.Band,v.Row,v.Column}).Select(g=>new CoverageSlot(g.Key.Band,g.Key.Row,g.Key.Column,g.LongCount(),g.Select(v=>v.O.FlightId).Distinct().LongCount(),g.Select(v=>v.O.Date).Distinct().LongCount(),g.Select(v=>v.O.PilotId).Distinct().LongCount(),g.LongCount(v=>v.O.Latitude!=null&&v.O.Longitude!=null))).ToArrayAsync(ct);
        var bandTotals=await indexed.GroupBy(v=>v.Band).Select(g=>new CoverageSlot(g.Key,0,0,g.LongCount(),g.Select(v=>v.O.FlightId).Distinct().LongCount(),g.Select(v=>v.O.Date).Distinct().LongCount(),g.Select(v=>v.O.PilotId).Distinct().LongCount(),g.LongCount(v=>v.O.Latitude!=null&&v.O.Longitude!=null))).ToArrayAsync(ct);
        var cellTotals=await indexed.GroupBy(v=>new{v.Row,v.Column}).Select(g=>new CoverageSlot(0,g.Key.Row,g.Key.Column,g.LongCount(),g.Select(v=>v.O.FlightId).Distinct().LongCount(),g.Select(v=>v.O.Date).Distinct().LongCount(),g.Select(v=>v.O.PilotId).Distinct().LongCount(),g.LongCount(v=>v.O.Latitude!=null&&v.O.Longitude!=null))).ToArrayAsync(ct);
        CoverageCounts Zero()=>new(0,0,0,0);CoverageCounts Slot(int b,int y,int x)=>slots.FirstOrDefault(v=>v.Band==b&&v.Row==y&&v.Column==x)?.Counts??Zero();
        bool Meets(int b,int y,int x)=>CampaignCoverageRules.Satisfied(Slot(b,y,x),d);
        var missing=new List<string>();bool complete=selected is not null;
        for(var b=0;b<bands;b++)for(var y=0;y<rows;y++)for(var x=0;x<columns;x++)if(!Meets(b,y,x)){complete=false;var count=Slot(b,y,x);missing.Add($"{(a is null?"Goal":$"Altitude band {b+1}")}{(grid is null?"":$", sampling cell {y+1}/{x+1}")}: {count.Observations}/{d.MinimumObservations} observations, {count.Flights}/{d.MinimumFlights} flights, {count.Dates}/{d.MinimumDates} UTC dates, {count.Pilots}/{d.MinimumPilots} distinct approved pilots.");}
        var altitude=new List<CoverageBand>();if(a is not null)for(var b=0;b<bands;b++){var ok=Enumerable.Range(0,rows).All(y=>Enumerable.Range(0,columns).All(x=>Meets(b,y,x)));var count=bandTotals.FirstOrDefault(v=>v.Band==b)?.Counts??Zero();altitude.Add(new(b,a.MinimumFeetMsl+b*a.BandFeet,Math.Min(a.MaximumFeetMsl,a.MinimumFeetMsl+(b+1)*a.BandFeet),b==bands-1,count,ok,ok?"Observed sampling meets this target":"More accepted sampling required"));}
        var cells=new List<CoverageCell>();if(grid is not null)for(var y=0;y<rows;y++)for(var x=0;x<columns;x++){var ok=Enumerable.Range(0,bands).All(b=>Meets(b,y,x));var count=cellTotals.FirstOrDefault(v=>v.Row==y&&v.Column==x)?.Counts??Zero();cells.Add(new(y,x,grid.South+(grid.North-grid.South)*y/rows,grid.West+(grid.East-grid.West)*x/columns,grid.South+(grid.North-grid.South)*(y+1)/rows,grid.West+(grid.East-grid.West)*(x+1)/columns,count,ok,ok?"Qualifying point samples present":"More accepted samples required — not an area footprint"));}
        if(selected is null)missing.Insert(0,"No accepted compatible evidence. Unsupported or unavailable source products cannot establish coverage.");
        return new(goal,complete?"Sampling Targets Met":selected is null?"Insufficient Compatible Evidence":"More Research Needed",complete,selected is not null,selected,await CoverageCount(q,ct),altitude.ToArray(),cells.ToArray(),missing.ToArray(),limitations.ToArray(),Math.Max(0,groups-(selected is null?0:1)));
    },ct);
}
