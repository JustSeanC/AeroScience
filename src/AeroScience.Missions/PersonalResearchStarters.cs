using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    // Entry metadata for existing card/mission types, not a second template engine.
    public static ResearchStarter[] ResearchStarters()=>[new("flight-regime",1,"Flight-Regime Validation",
        "Practice holding a pilot-chosen indicated airspeed and altitude using recorded simulator telemetry. This does not certify an aircraft.",
        ["Indicated airspeed (knots)","Altitude above mean sea level (feet)","Continuous recorded time (seconds)"],new(120,5,3000,100,30))];

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
                  AND json_extract(c.payload_json,'$.version')=1 AND json_extract(c.payload_json,'$.templateId')=$starter
                  AND json_extract(c.payload_json,'$.flightRegime')=$rule
                ORDER BY p.updated_utc DESC,p.id LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$starter",starter.Id);cmd.Parameters.AddWithValue("$rule",JsonSerializer.Serialize(starter.Example,WireJson.Options));
            if(cmd.ExecuteScalar() is string retained)source=JsonSerializer.Deserialize<PersonalResearchCard>(retained,WireJson.Options)!;
        }
        if(source is not null)SupportedCard(source);
        var copy=source is null||request.Intent==ResearchEntryIntent.Customize;
        PersonalResearchCard card;
        if(copy){
            cmd.CommandText="SELECT COUNT(*) FROM personal_research_cards WHERE id=$id";
            if((long)cmd.ExecuteScalar()!>0)throw new InvalidOperationException("This preparation's plan was removed or its identity is in use. Start a new preparation.");
            var rule=source?.FlightRegime??starter.Example;FlightRegimeEvaluation.Validate(rule);
            card=new(1,request.RequestId,1,source?.Name??starter.Name,starter.Id,rule,DateTimeOffset.UtcNow);
            InsertResearchCard(db,tx,card);
        }else card=source!;
        var referenceCard=new ResearchCardReference(card.Id,card.Revision);
        var prepared=MissionPlanning.Validate(new(request.RequestId,card.Name,MissionTemplateKind.ObservationFlight,
            Description:"Personal research using saved simulator flight telemetry; no aircraft settings are changed.",
            Objective:"Hold the selected indicated airspeed and MSL altitude within both card tolerances for its continuous hold duration.",
            Instruments:[],PreflightNotes:"Example targets require pilot review for the simulated aircraft. Free flight is supported; a route and extra scientific instruments are optional. Start mission, then Activate Card and Begin Attempt explicitly.",
            ResearchCard:referenceCard,ResearchCards:[referenceCard],ResearchStarter:new(1,request.RequestId,hash,starter.Id,request.Intent,request.Card)));
        WritePlan(db,tx,prepared);EvidenceFault?.Invoke("research-starter-before-commit");tx.Commit();
        return new(prepared,card,false);
    }
}
