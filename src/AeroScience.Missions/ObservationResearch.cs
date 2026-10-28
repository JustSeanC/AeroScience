using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public static class ObservationResearch
{
    public const string Template="scientific-observation-flight",Evaluator="pilot-documentation-1";
    public static void Validate(ObservationResearchDefinition? r)
    {
        if(r is null||r.Version!=1||new[]{r.Question,r.IntendedObservations,r.Approach}.Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>500||s.Any(c=>char.IsControl(c)&&c is not ('\n' or '\r' or '\t')))
            ||r.MinimumObservations is <1 or >10000||r.SelectedCategories.Length is <1 or >7||r.SelectedCategories.Distinct().Count()!=r.SelectedCategories.Length||r.SelectedCategories.Any(c=>!ScienceCatalog.MarkerCategories.Contains(c))
            ||r.SelectedInstruments.Length>4||r.SelectedInstruments.Distinct().Count()!=r.SelectedInstruments.Length||r.SelectedInstruments.Any(i=>!AtmosphericResearch.Quantities.Any(q=>q.Instrument==i)))
            throw new ArgumentException("Choose a question, observation description and approach (up to 500 characters each), known categories, a goal of 1–10,000 documented observations, and optional supported atmospheric instruments.");
    }
    public sealed record Calculation(ObservationResearchSummary Summary,PilotObservationEntry[] Preview,ScienceObservation[] SupportingPreview,bool Downsampled);
    public static bool SupportedPosition(PilotObservationEntry entry)=>entry.Latitude is {} lat&&entry.Longitude is {} lon&&MissionPlanning.Position(lat,lon)&&entry.PositionTimestampUtc is {} at&&(entry.TimestampUtc-at).TotalSeconds is >=0 and <=6;
    public static Calculation Calculate(MissionStore missions,ScienceStore? science,PersonalResearchCard card,ResearchAttemptRequest window,CancellationToken token=default)
    {
        Validate(card.ObservationFlight);token.ThrowIfCancellationRequested();var rule=card.ObservationFlight!;using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Hash(object o)=>hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o,WireJson.Options)+"\n"));
        Hash(Evaluator);Hash(card);Hash(new{window.FlightId,window.StartUtc,window.EndUtc});var docs=new List<PilotObservationEntry>();var counts=rule.SelectedCategories.ToDictionary(c=>c,_=>(Documented:0L,Qualified:0L));long documented=0,qualified=0,located=0,excluded=0,work=0;
        foreach(var (original,entry) in missions.ObservationDocuments(window,card.Id,card.Revision)){
            token.ThrowIfCancellationRequested();if(++work>10000)throw new InvalidOperationException("This analysis exceeds 10,000 documentation records. Choose a shorter interval; retained research is not limited.");Hash(original);
            if(!counts.ContainsKey(entry.Category)||string.IsNullOrWhiteSpace(entry.Text)){excluded++;docs.Add(entry with{ExclusionReason=!counts.ContainsKey(entry.Category)?"Category not selected for this objective":"Location marker only; no written observation"});continue;}
            documented++;var hasPosition=SupportedPosition(entry);if(hasPosition)located++;
            var valid=!rule.RequirePosition||hasPosition;var c=counts[entry.Category];counts[entry.Category]=(c.Documented+1,c.Qualified+(valid?1:0));if(valid)qualified++;
            docs.Add(entry with{Qualified=valid,ExclusionReason=valid?null:"Required recorded location is unavailable"});
        }
        var docHash=Convert.ToHexStringLower(hash.GetHashAndReset());
        var qs=AtmosphericResearch.Quantities.Where(q=>rule.SelectedInstruments.Contains(q.Instrument)).ToArray();var summaries=qs.ToDictionary(q=>q.Measurement,_=>(Valid:0L,Excluded:0L,Min:(double?)null,Max:(double?)null));var preview=docs.Where((_,i)=>i%Math.Max(1,(int)Math.Ceiling(docs.Count/100d))==0).ToArray();var referenced=new Dictionary<long,ScienceObservation>();var latest=new Dictionary<string,ScienceObservation>();var next=0;long supportingWork=0;
        void AssociateBefore(DateTimeOffset? before){while(next<preview.Length&&(before is null||preview[next].TimestampUtc<before)){
            var doc=preview[next];var near=latest.Values.Where(o=>(doc.TimestampUtc-o.TimestampUtc).TotalSeconds is >=0 and <=3).OrderBy(o=>o.Id).ToArray();foreach(var o in near)referenced.TryAdd(o.Id,o);preview[next++]=doc with{SupportingObservationIds=near.Select(o=>o.Id).ToArray()};
        }}
        Guid? library=null;var issues=new List<string>();
        if(qs.Length>0){
            if(science is null||science.AvailabilityError() is not null){Hash("Optional supporting Library unavailable");issues.Add("Optional recorded measurements are unavailable. Pilot documentation remains separate and reviewable.");}
            else{
                library=science.LibraryId;Hash(library);foreach(var o in science.ResearchObservationWindow(window.FlightId,window.StartUtc,window.EndUtc)){
                    token.ThrowIfCancellationRequested();if(++supportingWork>1000000)throw new InvalidOperationException("Choose a shorter supporting-evidence window.");AssociateBefore(o.TimestampUtc);var q=qs.FirstOrDefault(q=>q.Instrument==o.InstrumentId&&q.Measurement==o.MeasurementId);if(q==default)continue;Hash(o);var c=summaries[q.Measurement];
                    if(o.Quality!=ObservationQuality.Valid||o.Provenance!=ScienceProvenance.SimulatorReported||o.Calibration is not null||o.Unit!=q.Unit||!double.IsFinite(o.Value)||string.IsNullOrWhiteSpace(o.Source)){summaries[q.Measurement]=(c.Valid,c.Excluded+1,c.Min,c.Max);continue;}
                    summaries[q.Measurement]=(c.Valid+1,c.Excluded,Math.Min(c.Min??o.Value,o.Value),Math.Max(c.Max??o.Value,o.Value));
                    latest[o.MeasurementId]=o;
                }
            }
        }else Hash("No supporting measurements requested");
        var supportHash=Convert.ToHexStringLower(hash.GetHashAndReset());AssociateBefore(null);
        issues.AddRange(["Pilot Observation is a person's written account, not a sensor measurement or verified scientific fact. A completed documentation goal never proves a hypothesis.",
            "Each count is a documentation record, not an independent physical measurement. Repeated notes, shared evidence and overlapping attempts are not independent trials.",
            "Missing or stale positions remain unavailable; locations are never reconstructed from nearby flight tracks. Maps show individual recorded points, not continuous coverage.",
            "Optional supporting values come only from valid original simulator-reported science records. Live marker-context snapshots, raw flight atmosphere, modeled and historical external values are not substituted. Near-note associations use original readings from the preceding three seconds, not causal attribution."]);
        var summary=new ObservationResearchSummary(1,documented,qualified,located,excluded,counts.Select(c=>new ObservationCategorySummary(c.Key,c.Value.Documented,c.Value.Qualified)).ToArray(),docHash,library,supportHash,
            qs.Select(q=>{var s=summaries[q.Measurement];return new AtmosphericQuantitySummary(q.Instrument,q.Measurement,q.Unit,s.Valid,s.Excluded,s.Min,s.Max);}).ToArray(),issues.ToArray());
        return new(summary,preview,referenced.Values.OrderBy(o=>o.TimestampUtc).ThenBy(o=>o.Id).ToArray(),docs.Count>preview.Length);
    }
    public static (ResearchAttemptOutcome Outcome,string Explanation) Outcome(ObservationResearchSummary s,ObservationResearchDefinition r,bool interrupted)
    {
        if(s.Documented==0)return(ResearchAttemptOutcome.InsufficientEvidence,"Insufficient Evidence: no usable pilot documentation in the selected categories and recorded interval. Sensor readings or a flight path cannot substitute for observations.");
        if(!interrupted&&s.Qualified>=r.MinimumObservations&&(!r.RequireEveryCategory||s.Categories.All(c=>c.Qualified>0)))return(ResearchAttemptOutcome.CollectionComplete,"Collection Complete: the selected documentation requirements were met. This records a pilot's observations; it does not establish scientific truth or prove a hypothesis.");
        return(ResearchAttemptOutcome.PartialCollection,"Partial Collection: usable pilot documentation exists, but the goal, required categories/positions or uninterrupted-attempt requirement was not met. Optional measurements never replace missing documentation.");
    }
}
public sealed partial class MissionStore
{
    internal IEnumerable<(object Original,PilotObservationEntry Entry)> ObservationDocuments(ResearchAttemptRequest window,Guid card,int revision)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="""
            SELECT kind,payload_json FROM (
              SELECT 'marker' kind,id,timestamp_utc,payload_json FROM research_markers WHERE mission_id=$flight AND timestamp_utc >= $start AND timestamp_utc <= $end
                AND EXISTS(SELECT 1 FROM json_each(payload_json,'$.pilotObservation.cards') WHERE json_extract(value,'$.id')=$card AND json_extract(value,'$.revision')=$revision)
              UNION ALL
              SELECT 'note',id,timestamp_utc,payload_json FROM research_notes WHERE mission_id=$flight AND timestamp_utc >= $start AND timestamp_utc <= $end
                AND json_extract(payload_json,'$.phase')='in-flight'
                AND EXISTS(SELECT 1 FROM json_each(payload_json,'$.observationCards') WHERE json_extract(value,'$.id')=$card AND json_extract(value,'$.revision')=$revision)
            ) ORDER BY timestamp_utc,id LIMIT 10001
            """;
        cmd.Parameters.AddWithValue("$flight",window.FlightId.ToString());cmd.Parameters.AddWithValue("$start",window.StartUtc.ToString("O",CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$end",window.EndUtc.ToString("O",CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$card",card.ToString());cmd.Parameters.AddWithValue("$revision",revision);
        using var rows=cmd.ExecuteReader();while(rows.Read()){
            if(rows.GetString(0)=="marker"){var m=JsonSerializer.Deserialize<MissionMarker>(rows.GetString(1),WireJson.Options)!;yield return(m,new(m.Id,"Pilot Observation",m.Category,m.TimestampUtc,m.Notes??"",m.Latitude,m.Longitude,m.AltitudeMslFeet,m.PilotObservation?.PositionTimestampUtc,false,[]));}
            else{var n=JsonSerializer.Deserialize<MissionNote>(rows.GetString(1),WireJson.Options)!;yield return(n,new(n.Id,"Pilot Observation — in-flight note","note",n.TimestampUtc,n.Text,null,null,null,null,false,[]));}
        }
    }
    public ObservationResearchReview ReviewObservationEvidence(PersonalResearchAttempt result,ScienceStore? science,CancellationToken token=default)
    {
        if(result.ObservationFlight is null||result.Card.ObservationFlight is null||result.EvaluatorVersion!=ObservationResearch.Evaluator||result.EndUtc<result.StartUtc||result.EndUtc-result.StartUtc>TimeSpan.FromHours(24))throw new NotSupportedException("Select a supported retained observation result of up to 24 hours.");
        if(ScienceDeleted(result.FlightId))throw new InvalidOperationException("Collected pilot research and dependent results were permanently deleted; raw flight context cannot recreate them.");
        using var lease=science?.AvailabilityError() is null?science?.BeginAnalysis():null;
        var c=ObservationResearch.Calculate(this,science,result.Card,new(result.Id,result.FlightId,result.StartUtc,result.EndUtc),token);
        var interrupted=Detail(result.FlightId)?.Mission.Status!=MissionStatus.Completed||result.RecordedAttemptId is {} a&&RecordedResearchAttempt(a).Status==ResearchAttemptStatus.Interrupted;
        var matches=c.Summary.DocumentationSha256==result.Evidence.Sha256&&PreflightReadiness.Hash(Research(result.FlightId).PlanSnapshot!)==result.Evidence.PlanSha256
            &&ObservationResearch.Outcome(c.Summary,result.Card.ObservationFlight,interrupted).Outcome==result.Outcome;
        var support=c.Summary.SupportingSha256==result.ObservationFlight.SupportingSha256&&c.Summary.LibraryId==result.ObservationFlight.LibraryId;
        return new(matches,support,result.ObservationFlight,matches?c.Preview:[],matches&&support?c.SupportingPreview:[],c.Downsampled,matches
            ?"Original pilot documentation, definition and frozen plan hashes match. Documentation completeness is separate from scientific truth." :"Pilot documentation changed or is unavailable. Saved results are historical; no verified observation map is shown.");
    }
}
