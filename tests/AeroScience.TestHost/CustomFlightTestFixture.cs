using AeroScience.Missions;
using AeroScience.Shared;

// Disposable original synthetic telemetry in the existing recorder, not fabricated MSFS acceptance.
public static class CustomFlightTestFixture
{
    public static object Create(Guid cardId,int revision,string scenario,MissionStore missions,MissionCoordinator coordinator)
    {
        if(coordinator.Snapshot().ActiveMission is not null)throw new InvalidOperationException("End the active flight first.");
        if(scenario is not("pass" or "fail" or "gap" or "paused" or "interrupted" or "no-ias"))throw new ArgumentException("Choose a supported custom test fixture.");
        var card=missions.ResearchCard(cardId,revision);if(card.TemplateId!=FlightRegimeEvaluation.CustomTemplate)throw new ArgumentException("Use a saved custom card.");var rule=card.FlightRegime!;
        var start=DateTimeOffset.UtcNow.AddMinutes(-2);var end=start.AddSeconds(6);var plan=missions.CreateResearchPlan(card.Id,revision);
        var frame=new TelemetryFrame(start,TelemetrySource.Simulated,"Explicit retained custom-card fixture",40,-75,rule.AltitudeMslFeet,null,scenario=="no-ias"?-1:rule.AirspeedKnots,150,150,0,0,0,0,0);
        var id=missions.Create("Retained custom test — "+scenario,frame,start,plan:plan);
        for(var i=0;i<=6;i++){
            if(scenario=="gap"&&i==3)continue;
            missions.Append(id,frame with{TimestampUtc=start.AddSeconds(i),IsPaused=scenario=="paused"&&i==3,
                AltitudeMslFeet=scenario=="fail"?rule.AltitudeMslFeet+rule.AltitudeToleranceFeet+10:rule.AltitudeMslFeet,
                IndicatedAirspeedKnots=scenario=="no-ias"?-1:scenario=="fail"?rule.AirspeedKnots+rule.AirspeedToleranceKnots+10:rule.AirspeedKnots});
        }
        missions.Finish(id,scenario=="interrupted"?MissionStatus.Interrupted:MissionStatus.Completed,end,"Synthetic retained evidence window");
        return new{missionId=id,cardId,revision,startUtc=start,endUtc=end};
    }
}
