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
        ["Simulator air temperature (degC)","Pressure (inHg)","Wind (kt / degT)","Air density (slug/ft3)","Altitude (feet MSL)"],null,new()),
        new(AtmosphericTransectResearch.Template,1,"Atmospheric Transect",
        "Fly across a region to investigate changing air temperature, pressure, wind and density at your aircraft. Repeated locations and measurement gaps remain visible.",
        ["Simulator air temperature (degC)","Pressure (inHg)","Wind (kt / degT)","Air density (slug/ft3)","Recorded geographic position"],null,null,new()),
        new(SurveyResearch.Template,1,"Terrain & Temperature Survey","Investigate DEM-derived terrain elevation, historical Landsat land surface temperature or simulator air temperature. Choose one or several separate measurements.",["Terrain elevation (m, source datum)","Historical land surface temperature (K)","Simulator air temperature (degC)"],null,null,null,SurveyResearchDefinition.Default),
        new(ObservationResearch.Template,1,"Scientific Observation Flight","Investigate an area or event by recording what you observe during your flight. Add notes, mark interesting locations and collect optional supporting measurements.",["Pilot observations and research notes","Original host UTC and available recorded position","Optional recorded simulator measurements"],null,ObservationExample:new()),
        new(ExperimentalSensorResearch.Template,1,"Experimental Sensor Checkout","Test a fictional temperature sensor response using original recorded simulator air temperature. Model-derived outputs are not hardware measurements.",["Recorded simulator air temperature (degC)","Fictional model-derived response (degC)","Declared tracking criterion"],null,SensorExample:new())];

    public PreparedResearchStarter PrepareResearchStarter(ResearchStarterRequest request)
    {
        if(request.Version!=1||request.RequestId==Guid.Empty||request.Intent is not(ResearchEntryIntent.FlyNow or ResearchEntryIntent.Customize))
            throw new ArgumentException("Choose a supported research entry and preparation identity.");
        var starter=ResearchStarters().SingleOrDefault(s=>s.Id==request.StarterId)??throw new NotSupportedException("This starter is not available.");
        if(request.Survey is not null&&(starter.Id!=SurveyResearch.Template||request.Card is not null))throw new ArgumentException("Choose measurements for a new survey, or edit a saved survey in Mission Planner.");
        var observation=starter.ObservationExample;var sensor=starter.SensorExample;
        var survey=request.Survey??starter.SurveyExample;if(survey is not null)SurveyResearch.Validate(survey);
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
                    OR ($starter='vertical-atmospheric-profile' AND json_extract(c.payload_json,'$.atmosphericProfile')=$rule)
                    OR ($starter='atmospheric-transect' AND json_extract(c.payload_json,'$.atmosphericTransect')=$rule)
                    OR ($starter='terrain-temperature-survey' AND json_extract(c.payload_json,'$.survey')=$rule)
                    OR ($starter='scientific-observation-flight' AND json_extract(c.payload_json,'$.observationFlight')=$rule)
                    OR ($starter='experimental-sensor-checkout' AND json_extract(c.payload_json,'$.experimentalSensor')=$rule))
                ORDER BY p.updated_utc DESC,p.id LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$starter",starter.Id);cmd.Parameters.AddWithValue("$version",sensor is not null?6:observation is not null?5:survey is not null?4:starter.TransectExample is not null?3:starter.AtmosphericExample is null?1:2);
            cmd.Parameters.AddWithValue("$rule",sensor is not null?JsonSerializer.Serialize(sensor,WireJson.Options):observation is not null?JsonSerializer.Serialize(observation,WireJson.Options):survey is not null?JsonSerializer.Serialize(survey,WireJson.Options):starter.TransectExample is {} transect?JsonSerializer.Serialize(transect,WireJson.Options):starter.AtmosphericExample is {} atmospheric?JsonSerializer.Serialize(atmospheric,WireJson.Options):JsonSerializer.Serialize(starter.Example,WireJson.Options));
            if(cmd.ExecuteScalar() is string retained)source=JsonSerializer.Deserialize<PersonalResearchCard>(retained,WireJson.Options)!;
        }
        if(source is not null)SupportedCard(source);
        var copy=source is null||request.Intent==ResearchEntryIntent.Customize;
        PersonalResearchCard card;
        if(copy){
            cmd.CommandText="SELECT COUNT(*) FROM personal_research_cards WHERE id=$id";
            if((long)cmd.ExecuteScalar()!>0)throw new InvalidOperationException("This preparation's plan was removed or its identity is in use. Start a new preparation.");
            var rule=source?.FlightRegime??starter.Example;var atmosphere=source?.AtmosphericProfile??starter.AtmosphericExample;var transect=source?.AtmosphericTransect??starter.TransectExample;
            card=new(sensor is not null?6:observation is not null?5:survey is not null?4:transect is not null?3:atmosphere is null?1:2,request.RequestId,1,source?.Name??starter.Name,starter.Id,rule,DateTimeOffset.UtcNow,atmosphere,transect,source?.Survey??survey,source?.ObservationFlight??observation,source?.ExperimentalSensor??sensor);SupportedCard(card);
            InsertResearchCard(db,tx,card);
        }else card=source!;
        var referenceCard=new ResearchCardReference(card.Id,card.Revision);
        var atmosphericCard=card.AtmosphericProfile;var geographicCard=card.AtmosphericTransect;var collection=AtmosphericResearch.IsCollectionCard(card)||card.Survey is not null;
        var template=card.Survey is not null?MissionTemplateKind.GeographicSurvey:geographicCard is not null?MissionTemplateKind.AtmosphericTransect:collection?MissionTemplateKind.AtmosphericProfile:MissionTemplateKind.ObservationFlight;
        var prepared=MissionPlanning.Validate(new(request.RequestId,card.Name,template,
            Description:card.ExperimentalSensor is not null?starter.Description:card.ObservationFlight is not null?starter.Description:!collection?"Personal research using saved simulator flight telemetry; no aircraft settings are changed.":starter.Description,
            Objective:card.ExperimentalSensor?.Question??card.ObservationFlight?.Question??card.Survey?.Question??geographicCard?.Question??atmosphericCard?.Question??"Hold the selected indicated airspeed and MSL altitude within both card tolerances for its continuous hold duration.",
            Instruments:card.ExperimentalSensor is not null?[new InstrumentConfiguration("ambient-temperature",true,1000)]:card.ObservationFlight is {} observations?observations.SelectedInstruments.Select(i=>new InstrumentConfiguration(i,true)).ToArray():card.Survey is {} selection?selection.Measurements.Select(m=>new InstrumentConfiguration(SurveyResearch.Instrument(m),true,m==SurveyMeasurement.AirTemperature?1000:2000)).ToArray():!collection?[]:ScienceCatalog.Templates.Single(t=>t.Id==template).Instruments,
            AtmosphericSettings:!collection||card.Survey is not null?null:new(AltitudeIntervalMetres:(atmosphericCard?.BandWidthFeet??500)*.3048,MaximumGapSeconds:geographicCard?.MaximumGapSeconds??atmosphericCard!.MaximumGapSeconds),
            StartScienceArmed:geographicCard is null&&card.Survey is null&&card.ObservationFlight is null&&card.ExperimentalSensor is null?null:true,
            PreflightNotes:card.ExperimentalSensor is not null?"Fictional modeled response requires original ambient-temperature science readings in degC. Start mission, then Start new science session. Card activation/attempt controls are separate. Change temperature conditions in normal flight; missing/paused collection cannot pass. No external scene, internet or account is needed.":card.ObservationFlight is not null?"Record Pilot Observations with a category and short note, or Save in-flight note. Optional instruments need an explicit science start to retain supporting readings. Written accounts are not sensor measurements or scientific proof; missing positions remain unavailable.":!collection?"Example targets require pilot review for the simulated aircraft. Free flight is supported; a route and extra scientific instruments are optional. Start mission, then Activate Card and Begin Attempt explicitly.":
                card.Survey is not null?"Choose terrain elevation, historical land surface temperature and/or simulator air temperature. Use existing source readiness/preparation and Library reuse; no download is approved here. Start mission, then explicitly Start new science session. Missing external data never substitutes for another objective.":geographicCard is not null?"Fly from anywhere along a free-flight or pilot-reviewed route. Collect distinct sampling locations within a supported leg. Review geographic extent, sampling gaps, altitude and changing weather; no external scene or fixed airport is required.":"Fly from anywhere at pilot-chosen achievable altitudes. Use normal flight and science controls; valid temperature observations in neighboring MSL bands establish coverage, not proof of a hypothesis. Pressure, wind and density are optional. Historical surface imagery is not air temperature.",
            ResearchCard:referenceCard,ResearchCards:[referenceCard],ResearchStarter:new(1,request.RequestId,hash,starter.Id,request.Intent,request.Card)));
        WritePlan(db,tx,prepared);EvidenceFault?.Invoke("research-starter-before-commit");tx.Commit();
        return new(prepared,card,false);
    }
}
