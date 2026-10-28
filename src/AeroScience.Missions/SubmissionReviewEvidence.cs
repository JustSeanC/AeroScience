using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

// Interprets ONLY received P17 documents. Uses existing scientific/continuous-hold evaluators.
public static partial class SubmissionReviewEvidence
{
    public const int PolicyVersion=1,MaximumPackages=8;
    public static ReviewRequirementDefinition[] Requirements(SubmissionDocument d)
    {
        return d.Manifest.Cards.SelectMany(b=>{
            var c=d.Evidence.Single(i=>i.Reference.CardId==b.CardId&&i.Data.Definition is not null).Data.Definition!;
            return Requirements(c,b.IssuedRevision,b.DefinitionSha256);
        }).ToArray();
    }
    public static ReviewRequirementDefinition[] Requirements(PersonalResearchCard c,int issuedRevision,string definitionSha256)
    {
            ReviewRequirementDefinition R(string key,string name,string question,string criteria,Guid? point=null,bool supported=true)=>new(c.Id,key,c.Name,name,question,criteria,issuedRevision,definitionSha256,point,supported);
            static string Rule(FlightRegimeDefinition r){var parts=new List<string>();if(FlightRegimeEvaluation.RequiresAirspeed(r))parts.Add($"{r.AirspeedKnots} ± {r.AirspeedToleranceKnots} knots IAS");if(FlightRegimeEvaluation.RequiresAltitude(r))parts.Add($"{r.AltitudeMslFeet} ± {r.AltitudeToleranceFeet} feet MSL");return string.Join(r.TestPoint?.Policy=="any"?" OR ":" AND ",parts)+$" for {r.HoldSeconds} continuous seconds. Gaps and pauses do not qualify.";}
            if(c.Procedure is {} p)return p.Points.Select(v=>R("point:"+v.Id,v.Conditions.TestPoint!.Name,v.Conditions.TestPoint.Description,Rule(v.Conditions)+$" {v.Repetitions} distinct attempt(s). "+(p.Ordered?"Required earlier points must qualify first in the same flight.":"Independent point execution."),v.Id)).ToArray();
            if(c.FlightRegime is {} f)return[R("conditions",f.TestPoint?.Name??"Flight-Regime Validation",f.TestPoint?.Description??"Establish the issued operating conditions.",Rule(f))];
            if(c.AtmosphericProfile is {} a)return[R("collection","Vertical Atmospheric Profile",a.Question,$"{a.RequiredBands} neighboring {a.BandWidthFeet}-foot MSL bands; {a.MinimumReadingsPerBand} consecutive valid original temperature readings per band; maximum gap {a.MaximumGapSeconds} seconds. Optional pressure, wind and density remain distinct.")];
            if(c.AtmosphericTransect is {} t)return[R("collection","Atmospheric Transect",t.Question,"Meet the exact published geographic sampling and original-temperature coverage requirements. Flight path alone does not prove collection.")];
            if(c.ObservationFlight is {} o)return[R("documentation","Pilot Observation Documentation",o.Question,$"{o.MinimumObservations} qualifying documented observations; categories {string.Join(", ",o.SelectedCategories)}. "+(o.RequireEveryCategory?"Every category is required. ":"")+(o.RequirePosition?"Supported recorded positions are required. ":"")+"Documentation is not proof of a hypothesis.")];
            if(c.Survey is {} s)return s.Measurements.Select(m=>R("survey:"+m,m.ToString(),s.Question,$"{s.MinimumValidSamples} valid samples at {s.MinimumLocations} distinct supported locations. Full original raster/QA/source-context completeness is not verifiable from P17's bounded scalar format.",supported:false)).ToArray();
            return[R("sensor","Modeled Sensor Evidence",c.ExperimentalSensor?.Question??c.Name,"Modeled response series are not supported in the P17 format. Original inputs and submitted metadata can be inspected; incomplete products cannot be approved as complete.",supported:false)];
    }
    public static SubmissionDocument Scoped(SubmissionDocument d,Guid card)
    {
        var b=d.Manifest.Cards.Single(x=>x.CardId==card);return d with{Manifest=d.Manifest with{Cards=[b],Policies=d.Manifest.Policies.Where(p=>p.OpportunityId==b.OpportunityId).ToArray(),Items=d.Manifest.Items.Where(i=>i.CardId==card).ToArray()},Evidence=d.Evidence.Where(i=>i.Reference.CardId==card).ToArray()};
    }
    public static ResearchReviewAssessment Assess(SubmissionDocument root,SubmissionDocument[] received,Guid card,string key,CancellationToken ct=default)
    {
        if(received.Length is <1 or >MaximumPackages)throw new ArgumentException("Select 1–8 received packages for this bounded requirement review.");
        var requirement=Requirements(root).SingleOrDefault(r=>r.CardId==card&&r.Key==key)??throw new ArgumentException("Choose an original published requirement.");
        var binding=root.Manifest.Cards.Single(c=>c.CardId==card);var blockers=new List<string>();var limitations=new List<string>{"Human review concerns only expressly received evidence. Client outcomes, received-byte integrity and human findings are separate; this is not hardware attestation, aircraft certification or proof of a hypothesis.","Collection goals and procedure repetitions are evaluated within each original flight. Different flights are not silently merged into one atmosphere, hold or ordered procedure."};
        foreach(var d in received){ct.ThrowIfCancellationRequested();SubmissionEvidenceProtocol.Validate(d,ct);if(d.Manifest.PilotId!=root.Manifest.PilotId||d.Manifest.OrganizationId!=root.Manifest.OrganizationId||d.Manifest.CampaignId!=root.Manifest.CampaignId||!d.Manifest.Cards.Any(c=>c.CardId==card&&c.ClaimId==binding.ClaimId&&c.IssuedSha256==binding.IssuedSha256&&c.DefinitionSha256==binding.DefinitionSha256))throw new ArgumentException("Received evidence differs from this exact original claim and definition.");}
        var original=received.SelectMany(d=>d.Evidence).Where(i=>i.Reference.CardId==card&&i.Data.Result is not null).Select(i=>i.Data.Result!).ToArray();bool eligible=false;FlightTestProcedureSummary? procedure=null;AtmosphericResearchSummary? atmosphere=null;
        var definition=root.Evidence.Single(i=>i.Reference.CardId==card&&i.Data.Definition is not null).Data.Definition!;
        if(!requirement.ApprovalSupported)blockers.Add(requirement.Criteria);
        else foreach(var group in received.GroupBy(d=>d.Manifest.FlightId)){
            ct.ThrowIfCancellationRequested();var first=Scoped(group.First(),card);var items=new Dictionary<string,SubmissionEvidenceItem>(StringComparer.Ordinal);
            foreach(var item in group.SelectMany(d=>d.Evidence).Where(i=>i.Reference.CardId==card)){
                if(items.TryGetValue(item.Reference.Key,out var previous)&&PreflightReadiness.Hash(previous)!=PreflightReadiness.Hash(item)){blockers.Add("Conflicting versions of the same original received evidence identity cannot be combined.");continue;}items[item.Reference.Key]=item;
            }
            if(blockers.Count>0)continue;var all=items.Values.OrderBy(i=>i.Reference.Key,StringComparer.Ordinal).ToArray();var min=group.SelectMany(d=>d.Manifest.Cards).Where(c=>c.CardId==card).Min(c=>c.StartUtc);var max=group.SelectMany(d=>d.Manifest.Cards).Where(c=>c.CardId==card).Max(c=>c.EndUtc);
            var merged=first with{Manifest=first.Manifest with{Cards=[binding with{StartUtc=min,EndUtc=max}],ResultIds=group.SelectMany(d=>d.Manifest.ResultIds).Distinct().ToArray(),Items=all.Select(i=>i.Reference).ToArray()},Evidence=all};
            if(all.Length>SubmissionEvidenceProtocol.MaximumItems||SubmissionEvidenceProtocol.Bytes(merged)>SubmissionEvidenceProtocol.MaximumPayloadBytes){blockers.Add("The selected combined flight exceeds the bounded assessment size. Select fewer received packages; no evidence was truncated.");continue;}
            if(definition.Procedure is {} rules){
                var samples=all.SelectMany(i=>i.Data.Telemetry?.Samples??[]).GroupBy(s=>s.Sequence).OrderBy(g=>g.Key).ToArray();if(samples.Any(g=>g.Select(s=>PreflightReadiness.Hash(s)).Distinct().Count()>1)){blockers.Add("Conflicting original telemetry at one sequence cannot qualify.");continue;}
                var raw=samples.Select(g=>g.First()).ToArray();if(raw.Length>SubmissionEvidenceProtocol.MaximumSamples){blockers.Add("Too many selected original samples for one assessment.");continue;}
                var pauses=all.SelectMany(i=>i.Data.Telemetry?.Breaks??[]).Distinct().Order().ToArray();var attempts=all.SelectMany(i=>i.Data.Telemetry?.Attempts??[]).GroupBy(a=>a.Id).ToArray();if(attempts.Any(g=>g.Select(a=>PreflightReadiness.Hash(a)).Distinct().Count()>1)){blockers.Add("Conflicting marked-attempt identities cannot qualify.");continue;}
                var progress=new FlightProcedureProgress(rules);foreach(var attempt in attempts.Select(g=>g.First()).OrderBy(a=>a.StartUtc).ThenBy(a=>a.Id)){
                    var point=rules.Points.SingleOrDefault(p=>p.Id==attempt.TestPointId);if(point is null||attempt.PointEvidence is null){limitations.Add("A received attempt has no supported point identity/evidence and cannot qualify.");continue;}
                    var verified=MissionStore.ReceivedPointEvidence(attempt,point,raw,pauses,ct);if(verified is null){blockers.Add("A received point outcome/hash disagrees with its selected original telemetry or interruption evidence.");continue;}progress.Add(attempt with{PointEvidence=verified});
                }
                var summary=progress.Summary(all.Any(i=>i.Data.Telemetry is {} t&&t.FlightStatus!=MissionStatus.Completed));procedure=summary;eligible|=summary.Points.Single(p=>p.PointId==requirement.PointId).Outcome==ResearchAttemptOutcome.Pass;
            }else{
                if(AtmosphericResearch.IsCollectionCard(definition)){
                    var sessions=all.SelectMany(i=>i.Data.Sessions??[]).ToArray();
                    if(all.SelectMany(i=>i.Data.Observations??[]).Any(o=>o.SessionId is null||!sessions.Any(s=>s.Id==o.SessionId&&s.MissionId==group.Key&&s.StartedUtc<=o.TimestampUtc&&s.EndedUtc is {} end&&end>=o.TimestampUtc))){blockers.Add("Original collection-session identity and time bounds are missing for selected scientific observations. Request the session metadata; a receipt cannot waive this traceability gap.");continue;}
                }
                try{var assessment=SubmissionEvidenceProtocol.Assess(merged,ct);limitations.AddRange(assessment.Warnings);if(definition.FlightRegime is not null)eligible|=assessment.RuleSupported&&assessment.MinimumMet&&all.Any(i=>i.Data.Result?.Outcome==ResearchAttemptOutcome.Pass);
                    else {eligible|=assessment.RuleSupported&&assessment.MinimumMet;if(AtmosphericResearch.IsCollectionCard(definition)){var obs=all.SelectMany(i=>i.Data.Observations??[]).OrderBy(o=>o.TimestampUtc).ThenBy(o=>o.Id);var breaks=all.SelectMany(i=>i.Data.Telemetry?.Breaks??[]).Distinct().Order();atmosphere=AtmosphericResearch.Calculate(obs,definition,Guid.Empty,breaks,ct,new(Guid.Empty,group.Key,min,max)).Summary;}}}
                catch(ArgumentException){blockers.Add("Selected original evidence is inconsistent with the frozen result or published collection requirement.");}
            }
        }
        if(!eligible)blockers.Add("The received supported evidence does not fulfill this published requirement. Request more evidence or reject it; P18 cannot waive missing or failed requirements.");
        return new(requirement.ApprovalSupported,eligible&&blockers.Count==0,blockers.Distinct().ToArray(),limitations.Distinct().ToArray(),original.Select(r=>r.Outcome).Distinct().ToArray(),received.Select(d=>d.Manifest.Id).ToArray(),procedure,atmosphere);
    }
}

public sealed partial class MissionStore
{
    public static IEnumerable<SubmissionTelemetrySample> ReceivedPointSamples(RecordedResearchAttempt attempt,IEnumerable<SubmissionTelemetrySample> samples)=>samples.Where(s=>attempt.Automatic is {} a?s.Sequence>=a.FirstSequence&&s.Sequence<=a.LastSequence:attempt.ManualSource is {} m?s.Sequence>m.AfterSequence&&s.Sequence<=m.ThroughSequence:s.Frame.TimestampUtc>=attempt.StartUtc&&s.Frame.TimestampUtc<=attempt.EndUtc);
    public static FlightTestPointEvidence? ReceivedPointEvidence(RecordedResearchAttempt attempt,FlightTestProcedurePoint point,SubmissionTelemetrySample[] samples,DateTimeOffset[] breaks,CancellationToken ct)
    {
        if(attempt.EndUtc is not {} end||attempt.PointEvidence is not {} saved||end<attempt.StartUtc||end-attempt.StartUtc>TimeSpan.FromHours(2))return null;
        var selected=ReceivedPointSamples(attempt,samples).OrderBy(s=>s.Sequence).ToArray();
        var pauses=breaks.Where(b=>b>=attempt.StartUtc&&b<=end).Order().ToArray();var window=new FlightRegimeEvaluation.PointWindow(point.Conditions,attempt.StartUtc,end);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);int n=0;long first=0,last=0,count=0,gaps=0,invalid=0,edges=0;DateTimeOffset? prior=null;TelemetrySource? source=null;string? aircraft=null;
        foreach(var s in selected){ct.ThrowIfCancellationRequested();while(n<pauses.Length&&pauses[n]<=s.Frame.TimestampUtc){window.Break();edges++;AppendResearchBreak(hash,pauses[n++]);}if(prior is {} p){if((s.Frame.TimestampUtc-p).TotalSeconds is <=0 or >FlightRegimeEvaluation.MaximumGapSeconds)gaps++;}else if((s.Frame.TimestampUtc-attempt.StartUtc).TotalSeconds>FlightRegimeEvaluation.MaximumGapSeconds)gaps++;prior=s.Frame.TimestampUtc;if(!FlightRegimeEvaluation.SupportedSample(s.Frame,point.Conditions))invalid++;window.Add(s.Frame);AppendResearchSample(hash,s.Sequence,JsonSerializer.Serialize(s.Frame,WireJson.Options));if(count++==0)first=s.Sequence;last=s.Sequence;source??=s.Frame.Source;aircraft??=s.Frame.Aircraft;}
        while(n<pauses.Length){window.Break();edges++;AppendResearchBreak(hash,pauses[n++]);}if(prior is null||(end-prior.Value).TotalSeconds>FlightRegimeEvaluation.MaximumGapSeconds)gaps++;
        var actual=window.Finish(point.Id,new(first,last,count,Convert.ToHexStringLower(hash.GetHashAndReset()),saved.Evidence.PlanSha256,source),attempt.Status!=ResearchAttemptStatus.Recorded) with{SampleGaps=gaps,UnsupportedReadings=invalid,RecordedBreaks=edges,Aircraft=aircraft};return PreflightReadiness.Hash(actual)==PreflightReadiness.Hash(saved)?actual:null;
    }
}
