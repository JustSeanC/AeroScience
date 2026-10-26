using AeroScience.Missions;
using AeroScience.Shared;

// Explicit synthetic retained observations for browser/restart tests, never part of production.
public static class AtmosphericResearchFixture
{
    public static object Create(int bands,MissionStore missions,ScienceStore science,MissionCoordinator coordinator)
    {
        if(coordinator.Snapshot().ActiveMission is not null)throw new InvalidOperationException("End the actual fixture recording before seeding retained profiles.");
        if(bands is <0 or >3)throw new ArgumentException("Use zero to three synthetic fixture bands.");
        var prepared=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),AtmosphericResearch.Template,ResearchEntryIntent.FlyNow));
        var start=DateTimeOffset.UtcNow.AddMinutes(-1);var end=start.AddSeconds(20);
        var frame=new TelemetryFrame(start,TelemetrySource.Simulated,"Synthetic retained atmospheric fixture",40,-75,1100,null,100,100,100,0,0,0,0,0);
        var id=missions.Create("Synthetic atmospheric profile — "+bands+" bands",frame,start,plan:prepared.Plan);
        for(var i=0;i<=20;i++)missions.Append(id,frame with{TimestampUtc=start.AddSeconds(i),AltitudeMslFeet=1100+i*50});
        missions.Finish(id,MissionStatus.Completed,end,"Synthetic fixture end");var mission=missions.Detail(id)!.Mission;var research=missions.Research(id);var session=Guid.NewGuid();var rows=new List<ScienceObservation>();
        foreach(var quantity in AtmosphericResearch.Quantities){var dataset=science.EnsureDataset(mission,new(quantity.Instrument,true),research);
            for(var band=0;band<bands;band++)for(var i=0;i<3;i++){var at=start.AddSeconds(1+band*4+i);var value=quantity.Measurement switch{"temperature"=>20-band*3+i*.1,"pressure"=>29-band,"wind-speed"=>5+band,"wind-direction"=>270,_=>.002};
                rows.Add(new(0,dataset.Id,id,quantity.Instrument,at,quantity.Measurement,value,quantity.Unit,ScienceProvenance.SimulatorReported,"Synthetic test fixture / "+ScienceCatalog.Definitions.Single(d=>d.Id==quantity.Instrument).Measurements.Single(m=>m.Id==quantity.Measurement).SimulatorVariable,40,-75,1100+band*500,ObservationQuality.Valid,at,SessionId:session));}}
        science.Append(rows);return new{synthetic=true,missionId=id,cardId=prepared.Card.Id,startUtc=start,endUtc=end};
    }
}
