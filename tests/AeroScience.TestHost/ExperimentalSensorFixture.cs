using AeroScience.Missions;
using AeroScience.Shared;

// Original synthetic science rows in real stores, not a production acquisition option.
public static class ExperimentalSensorFixture
{
    public static object Create(string scenario,MissionStore missions,ScienceStore science,MissionCoordinator coordinator)
    {
        if(coordinator.Snapshot().ActiveMission is not null)throw new InvalidOperationException("End active flight before fixture creation.");
        if(scenario is not("complete" or "missing" or "gap" or "changed"))throw new ArgumentException("Choose a supported synthetic sensor scenario.");
        var prepared=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),ExperimentalSensorResearch.Template,ResearchEntryIntent.FlyNow));
        var card=prepared.Card;
        if(scenario=="changed"){card=missions.SaveResearchCard(new(card.Name,null,card.Id,card.Revision,ExperimentalSensor:card.ExperimentalSensor! with{ResponseSeconds=2,Gain=1.1}));prepared=new(missions.CreateResearchPlan(card.Id,card.Revision),card,false);}
        var start=DateTimeOffset.UtcNow.AddMinutes(-2);var end=start.AddSeconds(30);
        var frame=new TelemetryFrame(start,TelemetrySource.Simulated,"Synthetic retained sensor input",40,-75,1000,null,100,100,100,0,0,0,0,0);
        var id=missions.Create("Retained sensor checkout — "+scenario,frame,start,plan:prepared.Plan);
        for(var i=0;i<=30;i++)missions.Append(id,frame with{TimestampUtc=start.AddSeconds(i)});
        missions.Finish(id,MissionStatus.Completed,end,"Synthetic input fixture end");
        if(scenario!="missing"){
            var d=science.EnsureDataset(missions.Detail(id)!.Mission,new("ambient-temperature",true),missions.Research(id));var session=Guid.NewGuid();
            science.Append(Enumerable.Range(0,31).Where(i=>scenario!="gap"||i<12||i>20).Select(i=>new ScienceObservation(0,d.Id,id,"ambient-temperature",start.AddSeconds(i),"temperature",i<10?15:16,"degC",ScienceProvenance.SimulatorReported,"Synthetic retained MSFS-like input; not installed acceptance",40,-75,1000,ObservationQuality.Valid,start.AddSeconds(i),SessionId:session)).ToArray());
        }
        return new{missionId=id,cardId=card.Id,startUtc=start,endUtc=end};
    }
}
