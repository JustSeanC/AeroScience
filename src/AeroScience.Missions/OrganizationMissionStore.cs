using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    void MigrateOrganizationPlanning(SqliteConnection db)
    {
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE organization_mission_preparations(
              request_id TEXT PRIMARY KEY,request_sha256 TEXT NOT NULL,plan_id TEXT NOT NULL,
              card_id TEXT NOT NULL,audit_json TEXT NOT NULL);
            CREATE INDEX organization_preparations_card ON organization_mission_preparations(card_id,request_id);
            CREATE TABLE organization_claim_checks(
              id INTEGER PRIMARY KEY AUTOINCREMENT,card_id TEXT NOT NULL,payload_json TEXT NOT NULL);
            CREATE INDEX organization_checks_card ON organization_claim_checks(card_id,id DESC);
            PRAGMA user_version=11;
            """;
        cmd.ExecuteNonQuery();EvidenceFault?.Invoke("organization-planning-migration-before-commit");tx.Commit();
    }
    static OrganizationOpportunitySnapshot OrganizationOriginal(OrganizationAssignmentIdentity s)
    {
        if(s is null||s.Version!=1||new[]{s.ConnectionId,s.OrganizationId,s.CampaignId,s.OpportunityId,s.ClaimId,s.PilotId}.Any(i=>i==Guid.Empty)
            ||s.IssuedRevision<1||s.VerifiedClaimRevision<1||s.IssuedSha256 is not {Length:64}||s.IssuedSha256.Any(c=>!char.IsAsciiHexDigit(c))
            ||s.IssuedJson is not {Length:>0 and <=65536}||s.AcceptedUtc==default||s.VerifiedUtc<s.AcceptedUtc
            ||!Uri.TryCreate(s.Origin,UriKind.Absolute,out var origin)||origin.UserInfo!=""||origin.Query!=""||origin.Fragment!=""||origin.AbsolutePath!="/"
            ||origin.GetLeftPart(UriPartial.Authority)!=s.Origin||!(origin.Scheme=="https"&&origin.Port==443||origin.Scheme=="http"&&IPAddress.TryParse(origin.Host.Trim('[',']'),out var ip)&&IPAddress.IsLoopback(ip)))
            throw new ArgumentException("The assignment's original identity or verified source boundary is invalid.");
        if(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s.IssuedJson)))!=s.IssuedSha256)
            throw new ArgumentException("The original issued publication hash does not match. No assignment was attached.");
        OrganizationOpportunitySnapshot original;
        try{original=JsonSerializer.Deserialize<OrganizationOpportunitySnapshot>(s.IssuedJson,new JsonSerializerOptions(WireJson.Options){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})!;}
        catch(JsonException){throw new ArgumentException("The issued publication contains an unsupported contract. Its requirements cannot be discarded.");}
        if(original is null||original.Version!=1||original.Status!=OrganizationPublication.Published||original.Id!=s.OpportunityId||original.OrganizationId!=s.OrganizationId||original.CampaignId!=s.CampaignId||original.Revision!=s.IssuedRevision||original.Definition is null||original.Definition.Version!=1||original.Definition.Template?.Version!=1)
            throw new ArgumentException("The issued definition does not belong to this exact organization claim.");
        return original;
    }
    static Guid OrganizationCardId(OrganizationAssignmentIdentity s)=>new(SHA256.HashData(Encoding.UTF8.GetBytes($"organization-card-1:{s.Origin}:{s.OrganizationId}:{s.PilotId}:{s.ClaimId}:{s.IssuedSha256}"))[..16]);
    static PersonalResearchCard OrganizationCard(OrganizationAssignmentIdentity s)
    {
        var original=OrganizationOriginal(s);var d=original.Definition;
        if(new object?[]{d.FlightRegime,d.AtmosphericProfile,d.AtmosphericTransect,d.Survey,d.ObservationFlight,d.ExperimentalSensor,d.Procedure}.Count(v=>v is not null)!=1)
            throw new ArgumentException("An assignment must contain exactly one supported research definition.");
        var version=d.Procedure is not null?8:d.FlightRegime?.TestPoint is not null?7:d.ExperimentalSensor is not null?6:d.ObservationFlight is not null?5:d.Survey is not null?4:d.AtmosphericTransect is not null?3:d.AtmosphericProfile is not null?2:1;
        var card=new PersonalResearchCard(version,OrganizationCardId(s),1,original.Name,d.Template.Id,d.FlightRegime,s.AcceptedUtc,d.AtmosphericProfile,d.AtmosphericTransect,d.Survey,d.ObservationFlight,d.ExperimentalSensor,d.Procedure);
        SupportedCard(card);return card with{OrganizationAssignment=s};
    }
    static void ValidateOrganizationCard(PersonalResearchCard card)
    {
        if(card.OrganizationAssignment is not {} source)return;
        var expected=OrganizationCard(source);
        if(PreflightReadiness.Hash(expected)!=PreflightReadiness.Hash(card))throw new ArgumentException("An issued organizational card is immutable. Edited or cloned personal cards cannot qualify as this claim.");
    }
    public static OrganizationAssignmentIdentity VerifiedOrganizationAssignment(Guid connection,string origin,Guid organization,OrganizationClaimOriginal envelope)
    {
        var claim=envelope.Claim;
        if(claim is null||claim.OrganizationId!=organization||claim.State!=OrganizationClaimState.Claimed||claim.AcceptedUtc is null||claim.EndedUtc is not null||!envelope.AvailableForPreparation||envelope.CheckedUtc>DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidOperationException("This assignment is not currently available for new preparation. Accept it first, or review its release, membership and publication status.");
        var source=new OrganizationAssignmentIdentity(1,connection,origin,organization,envelope.OrganizationName,claim.IssuedCard.CampaignId,envelope.CampaignName,claim.OpportunityId,claim.Id,claim.PilotId,envelope.PilotName,claim.IssuedRevision,claim.IssuedSha256,envelope.IssuedJson,claim.AcceptedUtc.Value,envelope.CheckedUtc,claim.Revision);
        var original=OrganizationOriginal(source);
        if(PreflightReadiness.Hash(original)!=PreflightReadiness.Hash(claim.IssuedCard))throw new ArgumentException("The displayed claim and original publication differ.");
        _=OrganizationCard(source);return source;
    }
    public OrganizationMissionPreview PreviewOrganizationMission(OrganizationAssignmentIdentity source,Guid? planId)
    {
        var card=OrganizationCard(source);
        var proposed=SuggestedResearchPlan(card,Guid.Empty);
        var before=planId is {} id?Plan(id)??throw new ArgumentException("Select an existing saved mission plan."):proposed;
        var changes=new List<OrganizationMissionChange>();
        if(planId is not null){
            foreach(var instrument in (proposed.Instruments??[]).Where(i=>i.Enabled)){
                var current=before.Instruments?.SingleOrDefault(i=>i.InstrumentId==instrument.InstrumentId);
                if(current?.Enabled!=true)changes.Add(new("instrument:"+instrument.InstrumentId,"Enable "+ScienceCatalog.Definitions.Single(i=>i.Id==instrument.InstrumentId).Name,current is null?"Not selected":"Disabled","Enabled; existing payload/calibration/sampling settings are preserved"));
            }
            if(before.Template!=proposed.Template)changes.Add(new("template","Suggested mission template",before.Template.ToString(),proposed.Template.ToString()));
            if(proposed.AtmosphericSettings is not null&&before.AtmosphericSettings is null)changes.Add(new("atmospheric-settings","Atmospheric sampling approach","No profile/transect settings","Sampling interval and gap settings from this issued definition"));
            if(proposed.StartScienceArmed==true&&before.StartScienceArmed!=true)changes.Add(new("science-start","Science collection start",before.StartScienceArmed==false?"Existing automatic start policy":"Existing plan's default policy","Wait for an explicit Start new science session"));
        }
        var limitations=new[]{"Recording, science collection and card activation remain independent. Nothing starts or downloads here.",
            "Review the existing Mission Planner's Science and Storage Readiness. Source footprints do not establish valid scientific coverage.",
            "Organization eligibility requires later claim-time verification. Local results are Not Submitted; no evidence is uploaded."};
        return new(source,card,before,proposed,changes.ToArray(),limitations,planId is null?"suggested":PreflightReadiness.Hash(before),planId is not null);
    }
    static string OrganizationSaveHash(Guid connection,Guid claim,OrganizationMissionSave request)=>PreflightReadiness.Hash(new{connection,claim,request});
    public PreparedOrganizationMission? RetainedOrganizationPreparation(Guid connection,Guid claim,OrganizationMissionSave request)
    {
        using var db=Open();return ReadOrganizationPreparation(db,null,connection,claim,request);
    }
    PreparedOrganizationMission? ReadOrganizationPreparation(SqliteConnection db,SqliteTransaction? tx,Guid connection,Guid claim,OrganizationMissionSave request)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT request_sha256,plan_id,card_id FROM organization_mission_preparations WHERE request_id=$id";cmd.Parameters.AddWithValue("$id",request.RequestId.ToString());
        using var row=cmd.ExecuteReader();if(!row.Read())return null;
        if(row.GetString(0)!=OrganizationSaveHash(connection,claim,request))throw new InvalidOperationException("This preparation identity belongs to a different review. Start a new review.");
        var planId=Guid.Parse(row.GetString(1));var cardId=Guid.Parse(row.GetString(2));row.Close();
        cmd.CommandText="SELECT payload_json FROM mission_plans WHERE id=$plan";cmd.Parameters.AddWithValue("$plan",planId.ToString());
        var plan=cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<MissionPlan>(json,WireJson.Options)!:throw new InvalidOperationException("The prepared plan was removed. It will not be recreated by a retry.");
        cmd.CommandText="SELECT payload_json FROM personal_research_cards WHERE id=$card AND revision=1";cmd.Parameters.AddWithValue("$card",cardId.ToString());var card=JsonSerializer.Deserialize<PersonalResearchCard>((string)cmd.ExecuteScalar()!,WireJson.Options)!;SupportedCard(card);
        if(!SelectedResearchCards(plan).Any(r=>r.Id==card.Id&&r.Revision==card.Revision))throw new InvalidOperationException("The assignment was removed from this plan. A retry cannot reattach it or rewrite your edits.");
        return new(plan,card,true,["Previously committed preparation retained. Current claim eligibility was not rechecked by this retry."]);
    }
    public PreparedOrganizationMission SaveOrganizationMission(Guid connection,Guid claim,OrganizationAssignmentIdentity source,OrganizationMissionSave request)
    {
        if(request.Version!=1||request.RequestId==Guid.Empty||request.AcceptedChanges is null||request.AcceptedChanges.Length>32||request.AcceptedChanges.Distinct().Count()!=request.AcceptedChanges.Length||request.SaveMode is not("update" or "copy")||request.PlanId is null&&request.SaveMode!="copy")throw new ArgumentException("Review individual changes and explicitly choose Save Changes or Save as New Plan.");
        if(connection!=source.ConnectionId||claim!=source.ClaimId||request.IssuedSha256!=source.IssuedSha256||request.ClaimRevision!=source.VerifiedClaimRevision)throw new InvalidOperationException("The claim or issued definition changed. Review it again.");
        using var db=Open();using var tx=db.BeginTransaction();var retained=ReadOrganizationPreparation(db,tx,connection,claim,request);if(retained is not null)return retained;
        var preview=PreviewOrganizationMission(source,request.PlanId);
        if(request.PlanSha256!=preview.PlanSha256||request.AcceptedChanges.Any(id=>!preview.Changes.Any(c=>c.Id==id)))throw new InvalidOperationException("The plan or recommendations changed after review. Reload before saving; no changes were made.");
        // The SQLite writer transaction is already held. Re-read before the guarded update.
        if(request.PlanId is {} originalId){using var check=db.CreateCommand();check.Transaction=tx;check.CommandText="SELECT payload_json FROM mission_plans WHERE id=$id";check.Parameters.AddWithValue("$id",originalId.ToString());var json=check.ExecuteScalar() as string;if(json is null||PreflightReadiness.Hash(JsonSerializer.Deserialize<MissionPlan>(json,WireJson.Options))!=request.PlanSha256)throw new InvalidOperationException("The selected mission changed. Review the current plan before saving.");}
        var card=preview.Card;using var find=db.CreateCommand();find.Transaction=tx;find.CommandText="SELECT payload_json FROM personal_research_cards WHERE id=$id AND revision=1";find.Parameters.AddWithValue("$id",card.Id.ToString());
        if(find.ExecuteScalar() is string existing){var original=JsonSerializer.Deserialize<PersonalResearchCard>(existing,WireJson.Options)!;SupportedCard(original);if(original.OrganizationAssignment?.IssuedSha256!=source.IssuedSha256)throw new InvalidOperationException("The retained assignment identity differs.");card=original;}else InsertResearchCard(db,tx,card);
        var plan=preview.Before;
        foreach(var change in request.AcceptedChanges){
            if(change.StartsWith("instrument:",StringComparison.Ordinal)){var id=change[11..];var current=(plan.Instruments??[]).ToList();var index=current.FindIndex(i=>i.InstrumentId==id);if(index>=0)current[index]=current[index] with{Enabled=true};else current.Add(preview.Suggested.Instruments!.Single(i=>i.InstrumentId==id));plan=plan with{Instruments=current.ToArray()};}
            else plan=change switch{"template"=>plan with{Template=preview.Suggested.Template},"atmospheric-settings"=>plan with{AtmosphericSettings=preview.Suggested.AtmosphericSettings},"science-start"=>plan with{StartScienceArmed=true},_=>throw new ArgumentException("Unsupported recommendation.")};
        }
        var refs=SelectedResearchCards(plan).Where(r=>r.Id!=card.Id).Append(new(card.Id,card.Revision)).ToArray();
        plan=MissionPlanning.Validate(plan with{Id=request.SaveMode=="update"?request.PlanId!.Value:request.RequestId,ResearchCard=plan.ResearchCard??new(card.Id,card.Revision),ResearchCards=refs,ResearchStarter=request.SaveMode=="copy"?null:plan.ResearchStarter});
        _=SelectedResearchCards(plan);
        if(request.SaveMode=="copy"){using var collision=db.CreateCommand();collision.Transaction=tx;collision.CommandText="SELECT COUNT(*) FROM mission_plans WHERE id=$id";collision.Parameters.AddWithValue("$id",plan.Id.ToString());if((long)collision.ExecuteScalar()!>0)throw new InvalidOperationException("This preparation identity is already in use. The existing plan was not overwritten.");}
        WritePlan(db,tx,plan);
        var rejected=preview.Changes.Where(c=>!request.AcceptedChanges.Contains(c.Id)).Select(c=>c.Id).ToArray();
        var audit=new OrganizationMissionAudit(request.RequestId,plan.Id,card.Id,OrganizationSaveHash(connection,claim,request),request.PlanSha256,PreflightReadiness.Hash(plan),request.AcceptedChanges,rejected,DateTimeOffset.UtcNow,preview.Changes);
        using var receipt=db.CreateCommand();receipt.Transaction=tx;receipt.CommandText="INSERT INTO organization_mission_preparations VALUES($id,$hash,$plan,$card,$audit)";receipt.Parameters.AddWithValue("$id",request.RequestId.ToString());receipt.Parameters.AddWithValue("$hash",audit.RequestSha256);receipt.Parameters.AddWithValue("$plan",plan.Id.ToString());receipt.Parameters.AddWithValue("$card",card.Id.ToString());receipt.Parameters.AddWithValue("$audit",JsonSerializer.Serialize(audit,WireJson.Options));receipt.ExecuteNonQuery();
        EvidenceFault?.Invoke("organization-preparation-before-commit");tx.Commit();
        return new(plan,card,false,preview.Limitations.Concat(rejected.Select(c=>"Recommendation rejected: "+preview.Changes.Single(r=>r.Id==c).Title+". Existing configuration retained; collection readiness may remain incomplete.")).ToArray());
    }
    public OrganizationMissionAudit[] OrganizationPreparationHistory(Guid card,int offset=0)
    {
        if(offset<0)throw new ArgumentException("Choose a valid history page.");using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT audit_json FROM organization_mission_preparations WHERE card_id=$id ORDER BY rowid DESC LIMIT 20 OFFSET $offset";cmd.Parameters.AddWithValue("$id",card.ToString());cmd.Parameters.AddWithValue("$offset",offset);using var rows=cmd.ExecuteReader();var result=new List<OrganizationMissionAudit>();while(rows.Read())result.Add(JsonSerializer.Deserialize<OrganizationMissionAudit>(rows.GetString(0),WireJson.Options)!);return result.ToArray();
    }
    public PersonalResearchCard[] OrganizationResearchCards(int offset=0)
    {
        if(offset<0)throw new ArgumentException("Choose a valid prepared-card page.");using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM personal_research_cards WHERE json_extract(payload_json,'$.organizationAssignment') IS NOT NULL ORDER BY name,id LIMIT 20 OFFSET $offset";cmd.Parameters.AddWithValue("$offset",offset);using var rows=cmd.ExecuteReader();var result=new List<PersonalResearchCard>();while(rows.Read()){var card=JsonSerializer.Deserialize<PersonalResearchCard>(rows.GetString(0),WireJson.Options)!;SupportedCard(card);result.Add(card);}return result.ToArray();
    }
    public OrganizationAssignmentIdentity OrganizationAssignmentForCard(Guid cardId)
    {
        var card=ResearchCard(cardId);SupportedCard(card);return card.OrganizationAssignment??throw new ArgumentException("Choose an issued organization card.");
    }
    public void RecordOrganizationClaimCheck(Guid cardId,OrganizationClaimCheck check)
    {
        var card=ResearchCard(cardId);SupportedCard(card);var source=card.OrganizationAssignment??throw new ArgumentException("This is a personal card, not an organization assignment.");
        if(check.Original is {} original&&(original.Claim.Id!=source.ClaimId||original.Claim.PilotId!=source.PilotId||original.Claim.OrganizationId!=source.OrganizationId||original.Claim.IssuedSha256!=source.IssuedSha256||original.IssuedJson!=source.IssuedJson))throw new ArgumentException("The refreshed claim does not match the original assignment. No evidence was changed.");
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO organization_claim_checks(card_id,payload_json) VALUES($id,$json)";cmd.Parameters.AddWithValue("$id",cardId.ToString());cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(check,WireJson.Options));cmd.ExecuteNonQuery();
    }
    public OrganizationClaimCheck? LatestOrganizationClaimCheck(Guid cardId)
    {
        _=ResearchCard(cardId);using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM organization_claim_checks WHERE card_id=$id ORDER BY id DESC LIMIT 1";cmd.Parameters.AddWithValue("$id",cardId.ToString());return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<OrganizationClaimCheck>(json,WireJson.Options):null;
    }
}
