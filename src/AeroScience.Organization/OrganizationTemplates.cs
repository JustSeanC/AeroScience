using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Organization;

public static class OrganizationTemplates
{
    public static OrganizationTemplate[] All()
    {
        var templates=MissionStore.ResearchStarters().Select(s=>{
            var reference=new OrganizationTemplateReference(s.Id,s.Version);
            return new OrganizationTemplate(reference,s.Name,s.Description,new(1,reference,s.Example,s.AtmosphericExample,s.TransectExample,s.SurveyExample,s.ObservationExample,s.SensorExample));
        }).ToList();
        var custom=new OrganizationTemplateReference(FlightRegimeEvaluation.CustomTemplate,1);
        var rule=new FlightRegimeDefinition(120,5,3000,100,30,new());
        templates.Add(new(custom,"Custom Flight-Test Conditions","One named IAS/MSL test point, using the existing quantitative definition. Example targets are not aircraft operating limits.",new(1,custom,FlightRegime:rule)));
        var multi=new OrganizationTemplateReference(FlightRegimeEvaluation.ProcedureTemplate,1);
        templates.Add(new(multi,"Multi-Point Flight-Test Procedure","Ordered IAS hold followed by two distinct MSL holds. Uses the existing multi-point contract; no flights or assignments are created.",new(1,multi,Procedure:new(1,true,[
            new(Guid.Parse("11111111-1111-4111-8111-111111111111"),rule with{HoldSeconds=10,TestPoint=new(Name:"Low-Speed Stability",AltitudeRequired:false)}),
            new(Guid.Parse("22222222-2222-4222-8222-222222222222"),rule with{HoldSeconds=10,TestPoint=new(Name:"Altitude Demonstration",AirspeedRequired:false)},2)]))));
        return templates.ToArray();
    }
    public static OrganizationResearchDefinition Resolve(OrganizationTemplateReference reference)
    {
        var template=All().SingleOrDefault(t=>t.Reference==reference)??throw new ArgumentException("Choose a currently supported research approach and its exact version.");
        var d=template.Definition;
        if(d.Procedure is not null)FlightRegimeEvaluation.ValidateProcedure(d.Procedure);
        else if(d.FlightRegime is not null)FlightRegimeEvaluation.Validate(d.FlightRegime);
        else if(d.AtmosphericProfile is not null)AtmosphericResearch.Validate(d.AtmosphericProfile);
        else if(d.AtmosphericTransect is not null)AtmosphericTransectResearch.Validate(d.AtmosphericTransect);
        else if(d.Survey is not null)SurveyResearch.Validate(d.Survey);
        else if(d.ObservationFlight is not null)ObservationResearch.Validate(d.ObservationFlight);
        else if(d.ExperimentalSensor is not null)ExperimentalSensorResearch.Validate(d.ExperimentalSensor);
        else throw new ArgumentException("The research approach has no supported definition.");
        return d;
    }
}
