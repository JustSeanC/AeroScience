using AeroScience.Missions;
using AeroScience.Shared;

// Retained synthetic simulator observations; only compiled into the disposable test host.
public static class AtmosphericTransectFixture
{
    public static object Create(int locations,MissionStore missions,ScienceStore science,MissionCoordinator coordinator)
    {
        if(coordinator.Snapshot().ActiveMission is not null)throw new InvalidOperationException("End the fixture flight before creating retained transects.");
        if(locations is <0 or >3)throw new ArgumentException("Use zero to three synthetic sampling locations.");
        var prepared=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),AtmosphericTransectResearch.Template,ResearchEntryIntent.FlyNow));
        var start=DateTimeOffset.UtcNow.AddMinutes(-1);var end=start.AddSeconds(20);var origin=new Coordinates(0,179.999);
        var frame=new TelemetryFrame(start,TelemetrySource.Simulated,"Synthetic transect / not MSFS",origin.Latitude,origin.Longitude,1000,null,100,100,100,0,0,0,0,0);
        var id=missions.Create("Synthetic geographic transect — "+locations+" locations",frame,start,plan:prepared.Plan);
        for(var i=0;i<=20;i++){var p=Geodesy.Direct(origin,90,Math.Min(i/5,2)*600);missions.Append(id,frame with{TimestampUtc=start.AddSeconds(i),Latitude=p.Latitude,Longitude=p.Longitude,AltitudeMslFeet=1000+i*10});}
        missions.Finish(id,MissionStatus.Completed,end,"Synthetic end");var mission=missions.Detail(id)!.Mission;var context=missions.Research(id);var rows=new List<ScienceObservation>();var session=Guid.NewGuid();
        foreach(var q in AtmosphericResearch.Quantities){var dataset=science.EnsureDataset(mission,new(q.Instrument,true),context);
            for(var site=0;site<locations;site++)for(var n=0;n<3;n++){var at=start.AddSeconds(site*3+n+1);var p=Geodesy.Direct(origin,90,site*600);var value=q.Measurement switch{"temperature"=>20-site*2,"pressure"=>29.8-site*.1,"wind-speed"=>8+site,"wind-direction"=>270,_=>.0023};
                rows.Add(new(0,dataset.Id,id,q.Instrument,at,q.Measurement,value,q.Unit,ScienceProvenance.SimulatorReported,"Synthetic geographic fixture / "+q.Instrument,p.Latitude,p.Longitude,1000+site*100,ObservationQuality.Valid,at,SessionId:session));}}
        science.Append(rows);return new{synthetic=true,missionId=id,cardId=prepared.Card.Id,startUtc=start,endUtc=end};
    }
}
