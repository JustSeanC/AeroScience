using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Tests;

// Disposable synthetic originals and recorded observations; never genuine FAA/NASA acceptance.
public static class SurveyResearchFixture
{
    public static async Task<object> Create(bool cloudy,string root,MissionStore missions,ScienceStore science,MissionCoordinator coordinator)
    {
        if(coordinator.Snapshot().ActiveMission is not null)throw new InvalidOperationException("End the active fixture flight first.");
        var prepared=missions.PrepareResearchStarter(new(1,Guid.NewGuid(),SurveyResearch.Template,ResearchEntryIntent.FlyNow,Survey:new(1,Enum.GetValues<SurveyMeasurement>())));
        var at=DateTimeOffset.UtcNow.AddMinutes(-1);var frame=new TelemetryFrame(at,TelemetrySource.Simulated,"Synthetic survey / not MSFS",40.001,-75.004,1000,null,100,100,100,0,0,0,0,0);
        var id=missions.Create("Synthetic terrain-temperature survey — "+(cloudy?"cloudy":"valid"),frame,at,plan:prepared.Plan);
        for(var i=0;i<=20;i++)missions.Append(id,frame with{TimestampUtc=at.AddSeconds(i),Longitude=-75.004+i*.001});
        missions.Finish(id,MissionStatus.Completed,at.AddSeconds(20),"Synthetic fixture end");
        var dataset=science.EnsureDataset(missions.Detail(id)!.Mission,new("ambient-temperature",true),missions.Research(id));
        science.Append(Enumerable.Range(1,4).Select(i=>new ScienceObservation(0,dataset.Id,id,"ambient-temperature",at.AddSeconds(i),"temperature",15+i,"degC",ScienceProvenance.SimulatorReported,"Synthetic simulator air temperature",40.001,-75.004+i*.001,1000,ObservationQuality.Valid,at.AddSeconds(i))).ToArray());
        var sources=new List<Guid>();foreach(var kind in new[]{RemoteKind.Terrain,RemoteKind.Thermal}){var source=await SurveyFixture.Source(science,Path.Combine(root,"synthetic-survey-originals",Guid.NewGuid().ToString("N")),kind,cloudy:cloudy);sources.Add(source.Id);SurveyFixture.Publish(science,missions,id,source,at.AddSeconds(5));}
        return new{synthetic=true,missionId=id,cardId=prepared.Card.Id,startUtc=at,endUtc=at.AddSeconds(20),sourceIds=sources};
    }
}
