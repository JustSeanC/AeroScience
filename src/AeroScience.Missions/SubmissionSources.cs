using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    public SubmissionChoice[] SubmissionChoices(Guid flight,Guid? card=null,int offset=0,bool includeNext=false)
    {
        if(offset<0)throw new ArgumentException("Choose a valid result page.");
        using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT payload_json FROM personal_research_attempts WHERE mission_id=$flight AND ($card IS NULL OR card_id=$card) AND json_extract(payload_json,'$.card.organizationAssignment.claimId') IS NOT NULL ORDER BY created_utc DESC,id LIMIT $limit OFFSET $offset";c.Parameters.AddWithValue("$flight",flight.ToString());c.Parameters.AddWithValue("$card",(object?)card?.ToString()??DBNull.Value);c.Parameters.AddWithValue("$offset",offset);c.Parameters.AddWithValue("$limit",includeNext?21:20);var rows=new List<PersonalResearchAttempt>();using(var r=c.ExecuteReader())while(r.Read())rows.Add(JsonSerializer.Deserialize<PersonalResearchAttempt>(r.GetString(0),WireJson.Options)!);
        return rows.Select(r=>{
            var s=r.Card.OrganizationAssignment!;return new SubmissionChoice(r.Id,r.Card.Id,r.Card.Name,s.ClaimId,s.OrganizationId,s.OrganizationName,s.CampaignId,s.CampaignName,s.Origin,r.StartUtc,r.EndUtc,r.Outcome,
                r.Card.Survey is not null||r.Card.ExperimentalSensor is not null?"Raster scenes, large binaries and modeled response products are Not Supported for Submission Yet. Selected metadata/original observations can be submitted only under an applicable incomplete/manual-review policy.":null);
        }).ToArray();
    }
    long SubmissionRecordedCount(Guid flight,Guid card)
    {
        using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT COUNT(*) FROM personal_card_attempts WHERE mission_id=$flight AND card_id=$card";c.Parameters.AddWithValue("$flight",flight.ToString());c.Parameters.AddWithValue("$card",card.ToString());return (long)c.ExecuteScalar()!;
    }
    public (SubmissionDocument Document,string[] Unsupported,bool PolicyVerified) MaterializeSubmission(SubmissionDraftRequest request,ScienceStore science,CancellationToken ct=default)
    {
        if(request.Version!=1||request.Id==Guid.Empty||request.ResultIds is not {Length:>=1 and <=16}||request.ResultIds.Distinct().Count()!=request.ResultIds.Length||request.ResultIds.Any(i=>i==Guid.Empty)
            ||request.SelectedKeys?.Length>SubmissionEvidenceProtocol.MaximumItems||string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>120||request.Name.Any(char.IsControl))throw new ArgumentException("Choose a named submission and 1–16 saved results from one completed flight.");
        var mission=Detail(request.FlightId)?.Mission??throw new IOException("The original flight is missing; no evidence can be reconstructed.");
        if(mission.Status==MissionStatus.Recording||mission.EndedUtc is null)throw new InvalidOperationException("End the recorded flight before selecting evidence for submission. Recording and science remain independent.");
        var results=request.ResultIds.Select(ResearchAttempt).ToArray();foreach(var r in results){ct.ThrowIfCancellationRequested();SupportedCard(r.Card);if(r.FlightId!=request.FlightId||r.Card.OrganizationAssignment is null)throw new ArgumentException("Only frozen organizational results from the selected flight can be shared here.");}
        var source=results[0].Card.OrganizationAssignment!;
        if(results.Any(r=>r.Card.OrganizationAssignment is not {} s||s.OrganizationId!=source.OrganizationId||s.Origin!=source.Origin||s.PilotId!=source.PilotId||s.CampaignId!=source.CampaignId))throw new ArgumentException("Group only cards from the same pilot, organization and campaign. Other destinations require independent drafts and approvals.");
        var groups=results.GroupBy(r=>r.Card.Id).ToArray();if(groups.Length>4)throw new ArgumentException("Choose at most four claimed cards in one bounded package; retained submissions are not limited.");
        var bindings=new List<SubmissionCardBinding>();var policies=new List<SubmissionPolicy>();var items=new List<SubmissionEvidenceItem>();var unsupported=new List<string>();
        var libraryAvailable=science.AvailabilityError() is null;
        using var analysis=results.Any(r=>r.Card.FlightRegime is null&&r.Card.Procedure is null&&(r.Card.ObservationFlight is null||r.Card.ObservationFlight.SelectedInstruments.Length>0&&libraryAvailable))?science.BeginAnalysis():null;
        foreach(var group in groups){
            var card=group.First().Card;var s=card.OrganizationAssignment!;var start=group.Min(r=>r.StartUtc);var end=group.Max(r=>r.EndUtc);var scoped=SubmissionEvidenceProtocol.ScopedCard(card);
            var frozen=ResearchOperations(mission.Id).Cards.SingleOrDefault(c=>c.Card.Id==card.Id)?.Card;
            if(frozen is null||PreflightReadiness.Hash(frozen)!=PreflightReadiness.Hash(card)||group.Any(r=>PreflightReadiness.Hash(r.Card)!=PreflightReadiness.Hash(card)))throw new IOException("The exact original frozen card/claim revision is missing or changed.");
            var binding=new SubmissionCardBinding(s.ClaimId,s.OpportunityId,s.CampaignId,s.IssuedRevision,s.IssuedSha256,card.Id,card.Revision,PreflightReadiness.Hash(scoped),start,end,SubmissionRecordedCount(mission.Id,card.Id));bindings.Add(binding);
            policies.Add(CachedSubmissionPolicy(s.Origin,s.OpportunityId)??new(1,s.OpportunityId,0,SubmissionPolicyMode.RequireMinimumEvidence,SubmissionEvidenceProtocol.MinimumRule,SubmissionEvidenceProtocol.MaximumPayloadBytes,default,SubmissionEvidenceProtocol.RetentionDisclosure));
            void Add(string key,SubmissionEvidenceKind kind,Guid result,string original,object sourceValue,SubmissionEvidenceData data,long records,DateTimeOffset from,DateTimeOffset to,string description,string privacy)
            {
                ct.ThrowIfCancellationRequested();if(items.Count>=SubmissionEvidenceProtocol.MaximumItems)throw new NotSupportedException("This result exceeds the 512-item bounded package. Evaluate a shorter recorded interval or submit cards separately; no original evidence was omitted or truncated.");
                if(items.Any(i=>i.Reference.Key==key))return;
                items.Add(new(new(key,kind,card.Id,result,mission.Id,original,PreflightReadiness.Hash(sourceValue),PreflightReadiness.Hash(data),SubmissionEvidenceProtocol.Bytes(data),records,from,to,description,privacy),data));
            }
            Add("definition:"+card.Id,SubmissionEvidenceKind.Definition,Guid.Empty,card.Id.ToString(),card,new(Definition:scoped),1,start,end,"Frozen issued research definition","Includes this card's requirements and original local revision; no device credentials or other organization identity.");
            var research=Research(mission.Id);var required=(SuggestedResearchPlan(card,Guid.Empty).Instruments??[]).Where(i=>i.Enabled).Select(i=>i.InstrumentId).ToHashSet();
            var configuration=new SubmissionConfiguration(mission.Id,PreflightReadiness.Hash(research.PlanSnapshot!),research.Template,research.Instruments.Where(i=>required.Contains(i.InstrumentId)).ToArray());
            Add("configuration:"+card.Id,SubmissionEvidenceKind.MissionConfiguration,Guid.Empty,mission.Id.ToString(),new{research.PlanSnapshot,research.Instruments},new(Configuration:configuration),configuration.Instruments.Length,start,end,"Scoped frozen instrument configuration","Includes relevant instrument/calibration settings and the original plan hash. Routes, private planning notes and other cards are excluded.");
            foreach(var result in group){
                ct.ThrowIfCancellationRequested();var window=new ResearchAttemptRequest(result.Id,result.FlightId,result.StartUtc,result.EndUtc);
                if(card.Procedure is not null){if(!ReviewProcedureEvidence(result,ct).MatchesSavedEvidence)throw new IOException("Original multi-point evidence is missing or changed.");}
                else if(card.FlightRegime is not null){if(!ReviewFlightTestEvidence(result,ct).MatchesSavedEvidence)throw new IOException("Original quantitative evidence is missing or changed.");}
                else{
                    if(ScienceDeleted(mission.Id))throw new IOException("Original collected science was permanently deleted. A submission cannot recreate it from flight context.");
                    var review=card.ObservationFlight is not null?ReviewObservationEvidence(result,libraryAvailable?science:null,ct):null;
                    var matches=review is not null?review.MatchesSavedEvidence&&(result.ObservationFlight!.SupportingMeasurements.All(q=>q.ValidReadings==0)||review.SupportingEvidenceMatches):card.Survey is not null?ReviewSurveyEvidence(result,science,ct).MatchesSavedEvidence:card.ExperimentalSensor is not null?ReviewExperimentalSensorEvidence(result,science,ct).MatchesSavedEvidence:ReviewAtmosphericEvidence(result,science,ct).MatchesSavedEvidence;
                    if(!matches)throw new IOException("Original scientific evidence is missing, changed or no longer verified.");
                }
                Add("result:"+result.Id,SubmissionEvidenceKind.Result,result.Id,result.Id.ToString(),result,new(Result:SubmissionEvidenceProtocol.ScopedResult(result)),1,result.StartUtc,result.EndUtc,"Saved local outcome and original evidence references","Includes the selected local result and its scientific limitations. Local Pass/Collection Complete is not organizational approval.");
                var marks=card.Procedure is not null?ReadSubmissionAttempts(mission.Id,card.Id,result.StartUtc,result.EndUtc):result.RecordedAttemptId is {} marked?[RecordedResearchAttempt(marked)]:[];
                var breaks=PersonalPauseTimes(mission.Id,result.StartUtc,result.EndUtc,card.FlightRegime is null&&card.Procedure is null).Take(SubmissionEvidenceProtocol.MaximumSamples+1).ToArray();
                if(breaks.Length>SubmissionEvidenceProtocol.MaximumSamples)throw new NotSupportedException("Choose a shorter original event interval.");
                var samples=card.FlightRegime is not null||card.Procedure is not null?ReadSubmissionTelemetry(result,ct):[];
                var telemetry=new SubmissionTelemetry(samples,breaks,marks,mission.Status);
                Add("telemetry:"+result.Id,SubmissionEvidenceKind.Telemetry,result.Id,result.Id.ToString(),telemetry,new(Telemetry:telemetry),samples.Length,result.StartUtc,result.EndUtc,samples.Length>0?"Original supporting telemetry interval":"Original collection/recording continuity","Telemetry includes the original recorded channels, aircraft identity and locations. Continuity includes pause/gap times and manual/automatic attempt identities; no unselected flight intervals are attached.");
                if(card.FlightRegime is null&&card.Procedure is null){
                    var observations=required.Count>0&&libraryAvailable?science.SubmissionObservations(mission.Id,result.StartUtc,result.EndUtc,required.ToArray(),ct):[];
                    if(observations.Length>SubmissionEvidenceProtocol.MaximumItems)throw new NotSupportedException("This original science window is too large for the first bounded submission format. Evaluate a shorter interval; no readings were truncated.");
                    foreach(var o in observations){
                        if(!required.Contains(o.InstrumentId))continue;
                        Add($"science:{card.Id}:{o.Id}",SubmissionEvidenceKind.ScientificObservations,result.Id,o.Id.ToString(CultureInfo.InvariantCulture),o,new(Observations:[o]),1,o.TimestampUtc,o.TimestampUtc,$"Original {o.MeasurementId} observation ({o.Unit})","Includes the actual value, original units/quality/provenance, timestamp, available recorded location and calibration metadata. It is not a historical raster scene or an invented reading.");
                    }
                    var sessionIds=observations.Select(o=>o.SessionId).Where(i=>i is not null).ToHashSet();
                    foreach(var id in sessionIds){ct.ThrowIfCancellationRequested();if(id is {} sid&&CollectionSession(mission.Id,sid) is {} session){var scopedSession=session with{Configurations=session.Configurations.Where(c=>required.Contains(c.Settings.InstrumentId)).ToArray(),Evidence=[]};Add($"session:{card.Id}:{session.Id}",SubmissionEvidenceKind.CollectionSessions,Guid.Empty,session.Id.ToString(),session,new(Sessions:[scopedSession]),1,start,end,"Scoped original science-session metadata","Includes the original session identity/times/status and selected instrument configuration. Unrelated packet references are excluded; its original source hash is retained.");}}
                    if(card.ObservationFlight is not null)foreach(var (original,entry) in ObservationDocuments(window,card.Id,card.Revision))
                        Add($"documentation:{card.Id}:{entry.Id}",SubmissionEvidenceKind.PilotDocumentation,result.Id,entry.Id.ToString(),original,new(Documentation:[entry with{SupportingObservationIds=[]}]),1,entry.TimestampUtc,entry.TimestampUtc,"Pilot Observation — "+entry.Category,"Includes the selected original written note and any recorded location. Sensor-context snapshots and references to other cards are excluded. This is a pilot account, not a sensor measurement or proof of scientific truth.");
                    if(card.Survey is not null||card.ExperimentalSensor is not null)unsupported.Add(card.Name+": large raster/binary/model-response products are Not Supported for Submission Yet. Only selected original readings, result metadata and definitions are included.");
                }
            }
        }
        var chosen=request.SelectedKeys is null?items:items.Where(i=>request.SelectedKeys.Contains(i.Reference.Key)||i.Reference.Kind==SubmissionEvidenceKind.Definition).ToList();
        if(request.SelectedKeys is not null&&request.SelectedKeys.Any(k=>!items.Any(i=>i.Reference.Key==k)))throw new IOException("A selected original evidence reference is missing. Nothing was substituted.");
        var manifest=new SubmissionManifest(1,request.Id,request.Name.Trim(),source.Origin,source.OrganizationId,source.OrganizationName,source.CampaignId,source.CampaignName,source.PilotId,mission.Id,request.ResultIds.Order().ToArray(),bindings.OrderBy(b=>b.CardId).ToArray(),policies.OrderBy(p=>p.OpportunityId).ToArray(),chosen.Select(i=>i.Reference).OrderBy(i=>i.Key,StringComparer.Ordinal).ToArray(),SubmissionEvidenceProtocol.HistoryDisclosure,SubmissionEvidenceProtocol.PrivacyDisclosure,request.ReviewLink);
        var document=new SubmissionDocument(manifest,chosen.OrderBy(i=>i.Reference.Key,StringComparer.Ordinal).ToArray());
        if(SubmissionEvidenceProtocol.Bytes(document)>SubmissionEvidenceProtocol.MaximumPayloadBytes)throw new NotSupportedException("The selected payload exceeds 2 MiB. Choose fewer optional items or a shorter evaluated interval.");
        return(document,unsupported.ToArray(),policies.All(p=>p.Revision>0));
    }
    RecordedResearchAttempt[] ReadSubmissionAttempts(Guid flight,Guid card,DateTimeOffset start,DateTimeOffset end)
    {
        using var db=Open();using var tx=db.BeginTransaction(deferred:true);var rows=ProcedureAttempts(db,tx,flight,card).Where(a=>a.StartUtc>=start&&a.EndUtc<=end).Take(1001).ToArray();if(rows.Length>1000)throw new NotSupportedException("Choose a shorter marked-attempt interval.");return rows;
    }
    SubmissionTelemetrySample[] ReadSubmissionTelemetry(PersonalResearchAttempt result,CancellationToken ct)
    {
        using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT sequence,payload_json FROM telemetry WHERE mission_id=$flight AND timestamp_utc >= $start AND timestamp_utc <= $end ORDER BY sequence LIMIT 10001";
        c.Parameters.AddWithValue("$flight",result.FlightId.ToString());c.Parameters.AddWithValue("$start",Stamp(result.StartUtc));c.Parameters.AddWithValue("$end",Stamp(result.EndUtc));
        if(result.Card.Procedure is null&&result.Evidence.SampleCount>0){c.CommandText="SELECT sequence,payload_json FROM telemetry WHERE mission_id=$flight AND sequence BETWEEN $first AND $last ORDER BY sequence LIMIT 10001";c.Parameters.AddWithValue("$first",result.Evidence.FirstSequence);c.Parameters.AddWithValue("$last",result.Evidence.LastSequence);}
        using var rows=c.ExecuteReader();var list=new List<SubmissionTelemetrySample>();while(rows.Read()){ct.ThrowIfCancellationRequested();list.Add(new(rows.GetInt64(0),JsonSerializer.Deserialize<TelemetryFrame>(rows.GetString(1),WireJson.Options)!));}if(list.Count>SubmissionEvidenceProtocol.MaximumSamples)throw new NotSupportedException("Choose a shorter original telemetry interval; no samples were truncated.");return list.ToArray();
    }
    public SubmissionPreview PreviewSubmission(SubmissionDraftRequest request,ScienceStore science,CancellationToken ct=default)
    {
        var built=MaterializeSubmission(request,science,ct);var d=built.Document;var assessment=built.PolicyVerified?SubmissionEvidenceProtocol.Assess(d,ct):new(false,false,new[]{"Submission policy is Unknown. Save the offline draft, then connect once to review this claim's actual policy before approving delivery."});
        return new(d.Manifest,PreflightReadiness.Hash(d.Manifest),PreflightReadiness.Hash(d),SubmissionEvidenceProtocol.Bytes(d),assessment,built.Unsupported,built.PolicyVerified,built.PolicyVerified&&SubmissionEvidenceProtocol.RequiredPoliciesMet(d,ct));
    }
}

public sealed partial class ScienceStore
{
    internal ScienceObservation[] SubmissionObservations(Guid flight,DateTimeOffset start,DateTimeOffset end,string[] instruments,CancellationToken ct)
    {
        if(instruments.Length is <1 or >32)throw new ArgumentException("Choose supported scientific instruments.");
        using var db=Open();using var c=db.CreateCommand();var names=instruments.Select((id,n)=>{var key="$instrument"+n;c.Parameters.AddWithValue(key,id);return key;}).ToArray();
        c.CommandText=$"SELECT id,payload_json FROM observations WHERE mission_id=$flight AND timestamp_utc >= $start AND timestamp_utc <= $end AND json_extract(payload_json,'$.instrumentId') IN ({string.Join(',',names)}) ORDER BY timestamp_utc,id LIMIT 513";
        c.Parameters.AddWithValue("$flight",flight.ToString());c.Parameters.AddWithValue("$start",start.ToString("O",CultureInfo.InvariantCulture));c.Parameters.AddWithValue("$end",end.ToString("O",CultureInfo.InvariantCulture));
        using var rows=c.ExecuteReader();var result=new List<ScienceObservation>();while(rows.Read()){ct.ThrowIfCancellationRequested();result.Add(JsonSerializer.Deserialize<ScienceObservation>(rows.GetString(1),WireJson.Options)! with{Id=rows.GetInt64(0)});}return result.ToArray();
    }
}
