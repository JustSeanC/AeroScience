using AeroScience.Missions;
using AeroScience.Shared;

// Disposable synthetic documentation; no actual aircraft or scientific-phenomenon acceptance.
public static class ObservationResearchFixture
{
    public static object Create(int count,MissionStore missions,MissionCoordinator coordinator)
    {
        if(coordinator.Snapshot().ActiveMission is not null)throw new InvalidOperationException("End active flight before retained fixture creation.");if(count is <0 or >3)throw new ArgumentException("Use zero to three synthetic records.");
        var prepared=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),ObservationResearch.Template,ResearchEntryIntent.FlyNow));var start=DateTimeOffset.UtcNow.AddMinutes(-1);var end=start.AddSeconds(20);
        var frame=new TelemetryFrame(start,TelemetrySource.Simulated,"Synthetic retained pilot observation",0,179.999,1000,null,100,100,100,0,0,0,0,0);var id=missions.Create("Retained pilot investigation — "+count+" records",frame,start,plan:prepared.Plan);
        for(var i=0;i<=20;i++)missions.Append(id,frame with{TimestampUtc=start.AddSeconds(i)});
        for(var i=0;i<count;i++){var at=start.AddSeconds(i+1);var located=i%2==0?frame with{TimestampUtc=at,Longitude=i==0?179.999:-179.999}:null;missions.AddMarker(id,new(i==2?"finding":"observation","Synthetic original account "+i,Guid.NewGuid()),at,located,null);}
        missions.Finish(id,MissionStatus.Completed,end,"Synthetic fixture end");return new{synthetic=true,missionId=id,cardId=prepared.Card.Id,startUtc=start,endUtc=end};
    }
}
