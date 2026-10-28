using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed record ApprovedEvidenceInterpretation(ApprovedRequirementSummary Requirement,ApprovedMeasurementSummary[] Measurements,ApprovedResearchLocation[] Locations);
public static partial class SubmissionReviewEvidence
{
    // Sanitized interpretation of the exact evidence references accepted by P18; no notes, aircraft names,
    // original paths, raw samples, credentials or reviewer conversation enter the member projection.
    public static ApprovedEvidenceInterpretation DescribeApproved(SubmissionDocument root,SubmissionDocument[] documents,ReviewRequirementDefinition requirement,ReviewFinding finding,CancellationToken ct=default)
    {
        var assessment=Assess(root,documents,requirement.CardId,requirement.Key,ct);
        if(finding.State!=ResearchReviewState.Approved||!assessment.CanApprove)throw new InvalidOperationException("The accepted requirement no longer has verifiable supported received evidence.");
        var card=root.Evidence.Single(i=>i.Reference.CardId==requirement.CardId&&i.Data.Definition is not null).Data.Definition!;
        var groups=new Dictionary<string,Measure>();var locations=new Dictionary<string,ApprovedResearchLocation>();var outcomes=new List<ApprovedOutcomeCount>();
        var selected=documents.SelectMany(d=>d.Evidence.Where(i=>i.Reference.CardId==card.Id).Select(i=>(d.Manifest.FlightId,Item:i))).ToArray();
        var originals=selected.Where(v=>v.Item.Data.Result is not null).Select(v=>v.Item.Data.Result!).GroupBy(v=>v.Id).Select(g=>g.First()).ToArray();
        outcomes.AddRange(originals.GroupBy(r=>r.Outcome).Select(g=>new ApprovedOutcomeCount(g.Key,g.LongCount(),"Original client interval evaluation; not a reviewer decision")));
        string Key(Guid flight,string instrument,string measurement,string unit,string provenance,string source,DateTimeOffset at)=>PreflightReadiness.Hash(new{requirement=requirement.Key,flight,instrument,measurement,unit,provenance,source,date=at.UtcDateTime.Date});
        string Category(TelemetrySource? source)=>source==TelemetrySource.Simulated?"Synthetic/demo recorded inputs; not installed-MSFS evidence":"Declared simulator-reported inputs; not hardware attestation";
        void Add(Guid flight,string instrument,string measurement,string unit,string provenance,string identity,string category,DateTimeOffset at,double value,double? altitude,double? lat=null,double? lon=null)
        {
            if(!double.IsFinite(value))return;var key=Key(flight,instrument,measurement,unit,provenance,identity,at);if(!groups.TryGetValue(key,out var group)){group=new(key,requirement.Key,measurement,unit,instrument,provenance,category,PreflightReadiness.Hash(identity),"Flight "+PreflightReadiness.Hash(flight)[..12],at);groups.Add(key,group);}group.Add(at,value,altitude,lat is not null&&lon is not null);
            if(lat is {} a&&lon is {} b){var location=new ApprovedResearchLocation(requirement.Key,key,at,a,b,altitude,"Submitted observation location; not sensor coverage",provenance);locations.TryAdd(PreflightReadiness.Hash(location),location);}
        }
        if(card.FlightRegime is not null||card.Procedure is not null){
            var rule=card.FlightRegime??card.Procedure!.Points.Single(p=>p.Id==requirement.PointId).Conditions;
            foreach(var flight in selected.GroupBy(v=>v.FlightId)){
                ct.ThrowIfCancellationRequested();var data=flight.Select(v=>v.Item.Data.Telemetry).OfType<SubmissionTelemetry>().ToArray();var samples=data.SelectMany(t=>t.Samples).GroupBy(s=>s.Sequence).Select(g=>g.First()).ToArray();var attempts=data.SelectMany(t=>t.Attempts).Where(a=>a.TestPointId==requirement.PointId).GroupBy(a=>a.Id).Select(g=>g.First()).ToArray();
                if(card.Procedure is not null){outcomes.AddRange(attempts.Where(a=>a.PointEvidence is not null).GroupBy(a=>a.PointEvidence!.Outcome).Select(g=>new ApprovedOutcomeCount(g.Key,g.LongCount(),"Original marked point attempt; ordering/repetition credit is separate")));samples=attempts.SelectMany(a=>MissionStore.ReceivedPointSamples(a,samples)).GroupBy(s=>s.Sequence).Select(g=>g.First()).ToArray();}
                foreach(var sample in samples){var f=sample.Frame;if(f.TimestampUtc.Offset!=TimeSpan.Zero||f.IsPaused||!Enum.IsDefined(f.Source))continue;var category=Category(f.Source);var source=f.Source.ToString();if(FlightRegimeEvaluation.RequiresAirspeed(rule)&&double.IsFinite(f.IndicatedAirspeedKnots)&&f.IndicatedAirspeedKnots>=0)Add(flight.Key,"recorded-telemetry","Indicated airspeed","knots IAS",source,source,category,f.TimestampUtc,f.IndicatedAirspeedKnots,null);if(FlightRegimeEvaluation.RequiresAltitude(rule)&&double.IsFinite(f.AltitudeMslFeet))Add(flight.Key,"recorded-telemetry","MSL altitude","feet MSL",source,source,category,f.TimestampUtc,f.AltitudeMslFeet,f.AltitudeMslFeet);}
            }
        }
        else if(AtmosphericResearch.IsCollectionCard(card)||card.ObservationFlight is not null){
            foreach(var flight in selected.GroupBy(v=>v.FlightId)){
                ct.ThrowIfCancellationRequested();var source=originals.FirstOrDefault(r=>r.FlightId==flight.Key)?.Evidence.Source;var seen=new Dictionary<(Guid,long),string>();
                foreach(var observation in flight.SelectMany(v=>v.Item.Data.Observations??[])){
                    var identity=(observation.DatasetId,observation.Id);var hash=PreflightReadiness.Hash(observation);if(seen.TryGetValue(identity,out var old)){if(old!=hash)throw new InvalidOperationException("Conflicting original measurement identities cannot form an approved summary.");continue;}seen.Add(identity,hash);
                    var q=AtmosphericResearch.Quantities.SingleOrDefault(q=>q.Instrument==observation.InstrumentId&&q.Measurement==observation.MeasurementId&&q.Unit==observation.Unit);if(q==default||observation.Quality!=ObservationQuality.Valid||observation.Provenance!=ScienceProvenance.SimulatorReported||observation.Calibration is not null||!double.IsFinite(observation.Value)||string.IsNullOrWhiteSpace(observation.Source))continue;
                    if(card.ObservationFlight is {} notes&&!notes.SelectedInstruments.Contains(observation.InstrumentId))continue;
                    var located=AtmosphericResearch.Usable(observation,q.Unit);if(AtmosphericResearch.IsCollectionCard(card)&&!located)continue;
                    var category=observation.Source.StartsWith("Synthetic",StringComparison.OrdinalIgnoreCase)?Category(TelemetrySource.Simulated):Category(source);
                    Add(flight.Key,q.Instrument,q.Measurement,q.Unit,observation.Provenance.ToString(),observation.Source+" / dataset "+observation.DatasetId,category,observation.TimestampUtc,observation.Value,located?observation.AltitudeMslFeet:null,located?observation.Latitude:null,located?observation.Longitude:null);
                }
                if(card.ObservationFlight is {} rule){var docs=flight.SelectMany(v=>v.Item.Data.Documentation??[]).GroupBy(n=>n.Id).Select(g=>g.First()).Where(n=>rule.SelectedCategories.Contains(n.Category)&&!string.IsNullOrWhiteSpace(n.Text)&&(!rule.RequirePosition||ObservationResearch.SupportedPosition(n))).ToArray();foreach(var category in docs.GroupBy(n=>n.Category)){
                    foreach(var n in category){var located=ObservationResearch.SupportedPosition(n);Add(flight.Key,"pilot-documentation","Documented observations: "+category.Key,"documented entries","Pilot Observation",category.Key,"Pilot-written documentation, not a sensor measurement or proof of a hypothesis",n.TimestampUtc,1,located?n.AltitudeMslFeet:null,located?n.Latitude:null,located?n.Longitude:null);}
                }}
            }
        }
        var limitations=new[]{SubmissionEvidenceProtocol.HistoryDisclosure,"Human approval, original measured outcomes and received-byte integrity remain separate. A measured failure is never changed into a Pass.","Source groups preserve original units, instrument, source identity, recorded flight and UTC observation date. Ranges describe selected original inputs, not continuous coverage or a proved trend.","Account names, private notes/reviewer messages, raw telemetry, aircraft details and raw received packages are excluded. Locations describe only supported submitted observations; flight paths are not sensor coverage.","Distinct contributors mean distinct authorized pilot identities; physical/person/aircraft independence is not hardware-attested."};
        var diagnostics=originals.Where(r=>r.AtmosphericProfile is not null).Select(r=>$"Original client interval {r.StartUtc:O} to {r.EndUtc:O}: {r.AtmosphericProfile!.ValidTemperatureReadings} valid temperature readings, {r.AtmosphericProfile.ExcludedReadings} excluded readings, {r.AtmosphericProfile.MajorGaps} major gaps; {r.AtmosphericProfile.Bands.Count(b=>b.Covered)} covered of {r.AtmosphericProfile.Bands.Length} reported altitude bands. These are original client interval diagnostics; approved source-group counts describe the actually selected received readings. Different flights are not combined.").ToArray();
        return new(new(requirement.Key,requirement.Name,requirement.Objective,requirement.Criteria,finding.EventId,finding.PolicyVersion,finding.TimestampUtc,finding.Evidence.Select(e=>e.ReceiptId).Distinct().ToArray(),outcomes.ToArray(),limitations.Concat(diagnostics).ToArray()),groups.Values.Select(g=>g.View()).OrderBy(g=>g.GroupId,StringComparer.Ordinal).ToArray(),locations.Values.OrderBy(p=>p.TimestampUtc).ThenBy(p=>p.SourceGroupId,StringComparer.Ordinal).ToArray());
    }
    sealed class Measure(string key,string requirement,string measurement,string unit,string instrument,string provenance,string source,string sourceHash,string flight,DateTimeOffset at)
    {
        DateTimeOffset start=at,end=at;long count,located;double min=double.PositiveInfinity,max=double.NegativeInfinity;double? low,high;
        public void Add(DateTimeOffset time,double value,double? altitude,bool location){start=time<start?time:start;end=time>end?time:end;count++;min=Math.Min(min,value);max=Math.Max(max,value);if(altitude is {} a&&double.IsFinite(a)){low=Math.Min(low??a,a);high=Math.Max(high??a,a);}if(location)located++;}
        public ApprovedMeasurementSummary View()=>new(key,requirement,measurement,unit,instrument,provenance,source,sourceHash,flight,start,end,count,instrument=="pilot-documentation"?null:min,instrument=="pilot-documentation"?null:max,low,high,located,instrument=="pilot-documentation"?"Usable pilot documentation; not sensor QA":"Valid supported original readings; no fabricated no-data values");
    }
}
