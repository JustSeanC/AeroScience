using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    // Entry metadata for existing card/mission types, not a second template engine.
    public static ResearchStarter[] ResearchStarters()=>[new("flight-regime",1,"Flight-Regime Validation",
        "Practice holding a pilot-chosen indicated airspeed and altitude using recorded simulator telemetry. This does not certify an aircraft.",
        ["Indicated airspeed (knots)","Altitude above mean sea level (feet)","Continuous recorded time (seconds)"],new(120,5,3000,100,30)),
        new(AtmosphericResearch.Template,1,"Vertical Atmospheric Profile",
        "Fly through different altitudes to investigate how atmospheric conditions change. Collect temperature, pressure, wind and air-density measurements as your aircraft climbs or descends.",
        ["Simulator air temperature (degC)","Pressure (inHg)","Wind (kt / degT)","Air density (slug/ft3)","Altitude (feet MSL)"],null,new())];

    public PreparedResearchStarter PrepareResearchStarter(ResearchStarterRequest request)
    {
        if(request.Version!=1||request.RequestId==Guid.Empty||request.Intent is not(ResearchEntryIntent.FlyNow or ResearchEntryIntent.Customize))
            throw new ArgumentException("Choose a supported research entry and preparation identity.");
        var starter=ResearchStarters().SingleOrDefault(s=>s.Id==request.StarterId)??throw new NotSupportedException("This starter is not available.");
        if(request.StarterVersion!=starter.Version)throw new ArgumentException("This starter changed. Refresh My Research before preparing it.");
        if(request.Card is {} input&&(input.Id==Guid.Empty||input.Revision<1))throw new ArgumentException("Choose an exact saved card revision.");
        var hash=PreflightReadiness.Hash(request);
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT payload_json FROM mission_plans WHERE id=$id";cmd.Parameters.AddWithValue("$id",request.RequestId.ToString());
        if(cmd.ExecuteScalar() is string previous){
            var plan=JsonSerializer.Deserialize<MissionPlan>(previous,WireJson.Options)!;
            if(plan.ResearchStarter is not {} origin||origin.Version!=1||origin.RequestId!=request.RequestId||origin.RequestSha256!=hash)
                throw new InvalidOperationException("This preparation identity already belongs to another selection. Start a new preparation.");
            var reference=SelectedResearchCards(plan).FirstOrDefault()??throw new InvalidOperationException("The prepared plan's card was removed. Review it in Mission Planner.");
            var retained=ResearchCard(reference.Id,reference.Revision);SupportedCard(retained);
            return new(plan,retained,true); // Preserve any later route, payload or card-revision edits.
        }
        var source=request.Card is {} selected?ResearchCard(selected.Id,selected.Revision):null;
        if(source is not null&&source.TemplateId!=starter.Id)throw new ArgumentException("The selected card belongs to a different experiment.");
        if(source is null&&request.Intent==ResearchEntryIntent.FlyNow){
            // Repeating the unmodified starter remains the same local experiment.
            // Search retained preparation provenance, not a capped browser page.
            cmd.CommandText="""
                SELECT c.payload_json FROM mission_plans p JOIN personal_research_cards c
                  ON c.id=json_extract(p.payload_json,'$.researchStarter.requestId')
                WHERE json_extract(p.payload_json,'$.researchStarter.starterId')=$starter
                  AND json_extract(p.payload_json,'$.researchStarter.intent')='flyNow'
                  AND json_extract(p.payload_json,'$.researchStarter.originalCard') IS NULL
                  AND c.revision=(SELECT MAX(revision) FROM personal_research_cards WHERE id=c.id)
                  AND json_extract(c.payload_json,'$.version')=$version AND json_extract(c.payload_json,'$.templateId')=$starter
                  AND (($starter='flight-regime' AND json_extract(c.payload_json,'$.flightRegime')=$rule)
                    OR ($starter='vertical-atmospheric-profile' AND json_extract(c.payload_json,'$.atmosphericProfile')=$rule))
                ORDER BY p.updated_utc DESC,p.id LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$starter",starter.Id);cmd.Parameters.AddWithValue("$version",starter.AtmosphericExample is null?1:2);
            cmd.Parameters.AddWithValue("$rule",starter.AtmosphericExample is {} atmospheric?JsonSerializer.Serialize(atmospheric,WireJson.Options):JsonSerializer.Serialize(starter.Example,WireJson.Options));
            if(cmd.ExecuteScalar() is string retained)source=JsonSerializer.Deserialize<PersonalResearchCard>(retained,WireJson.Options)!;
        }
        if(source is not null)SupportedCard(source);
        var copy=source is null||request.Intent==ResearchEntryIntent.Customize;
        PersonalResearchCard card;
        if(copy){
            cmd.CommandText="SELECT COUNT(*) FROM personal_research_cards WHERE id=$id";
            if((long)cmd.ExecuteScalar()!>0)throw new InvalidOperationException("This preparation's plan was removed or its identity is in use. Start a new preparation.");
            var rule=source?.FlightRegime??starter.Example;var atmosphere=source?.AtmosphericProfile??starter.AtmosphericExample;
            card=new(atmosphere is null?1:2,request.RequestId,1,source?.Name??starter.Name,starter.Id,rule,DateTimeOffset.UtcNow,atmosphere);SupportedCard(card);
            InsertResearchCard(db,tx,card);
        }else card=source!;
        var referenceCard=new ResearchCardReference(card.Id,card.Revision);
        var atmosphericCard=card.AtmosphericProfile;
        var prepared=MissionPlanning.Validate(new(request.RequestId,card.Name,atmosphericCard is null?MissionTemplateKind.ObservationFlight:MissionTemplateKind.AtmosphericProfile,
            Description:atmosphericCard is null?"Personal research using saved simulator flight telemetry; no aircraft settings are changed.":starter.Description,
            Objective:atmosphericCard?.Question??"Hold the selected indicated airspeed and MSL altitude within both card tolerances for its continuous hold duration.",
            Instruments:atmosphericCard is null?[]:ScienceCatalog.Templates.Single(t=>t.Id==MissionTemplateKind.AtmosphericProfile).Instruments,
            AtmosphericSettings:atmosphericCard is null?null:new(AltitudeIntervalMetres:atmosphericCard.BandWidthFeet*.3048,MaximumGapSeconds:atmosphericCard.MaximumGapSeconds),
            PreflightNotes:atmosphericCard is null?"Example targets require pilot review for the simulated aircraft. Free flight is supported; a route and extra scientific instruments are optional. Start mission, then Activate Card and Begin Attempt explicitly.":
                "Fly from anywhere at pilot-chosen achievable altitudes. Use normal flight and science controls; valid temperature observations in neighboring MSL bands establish coverage, not proof of a hypothesis. Pressure, wind and density are optional. Historical surface imagery is not air temperature.",
            ResearchCard:referenceCard,ResearchCards:[referenceCard],ResearchStarter:new(1,request.RequestId,hash,starter.Id,request.Intent,request.Card)));
        WritePlan(db,tx,prepared);EvidenceFault?.Invoke("research-starter-before-commit");tx.Commit();
        return new(prepared,card,false);
    }
}
