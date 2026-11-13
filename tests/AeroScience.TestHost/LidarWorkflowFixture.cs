using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.TestHost;

internal static class LidarWorkflowFixture
{
    internal sealed record Fixture(Guid MissionId,Guid GeometryId,Guid SourceId,Guid JobId,bool SyntheticFlight,bool SyntheticSource);
    internal static ScientificArtifact Flight(MissionStore missions,ScienceStore science,Coordinates center)
    {
        var utc=DateTimeOffset.UtcNow.AddMinutes(-5);InstrumentConfiguration[] settings=[new("terrain-scanner",true,1000,new(InstrumentProfiles.Generic("terrain-scanner")))];
        var id=missions.Create("SYNTHETIC P05c flight corridor; historical points are separate",null,utc,instruments:settings);missions.InitializeCollectionEvidence(new(1,id,0,CollectionPhase.Armed,CollectionAuthority.Manual,ControlRevision:0));var controller=new ScienceCollectionController(missions,id,()=>settings);
        void Control(CollectionAction action,int second){var s=controller.State;controller.Control(new(1,id,Guid.NewGuid(),s.Revision,action,s.CurrentSessionId),utc.AddSeconds(second));}Control(CollectionAction.StartData,0);
        for(var i=0;i<3;i++){
            var p=Geodesy.Direct(center,0,(i-1)*100);var time=utc.AddSeconds(i);var frame=new TelemetryFrame(time,TelemetrySource.Simulated,"SYNTHETIC archived P05c flight",p.Latitude,p.Longitude,5000,1000,200,200,400,0,0,0,0,0);
            var packet=new CollectionInputReceipt("synthetic-p05c-"+i,new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic fixture",time),frame));var state=controller.State;var next=state with{Revision=state.Revision+1};var e=new CollectionEvent(1,id,next.Revision,time,time,state.CurrentSessionId,CollectionAction.Unknown,CollectionEventOrigin.Engine,CollectionEventOutcome.Applied,state.Phase,state.Phase,state.Authority,MissionStore.IngressReason,[MissionStore.DescribeCollectionInput(packet)],ExpectedSessionId:state.CurrentSessionId);missions.CommitCollectionEvidence(new(next,e,Inputs:[packet],FlightSample:frame,EngineDecisionId:Guid.NewGuid()));
        }
        Control(CollectionAction.EndData,3);missions.Finish(id,MissionStatus.Completed,utc.AddSeconds(3),"Synthetic archived collection fixture");return ContinuousTerrainSurveys.Build(science,missions,id);
    }
    internal static async Task<Fixture> Create(MissionStore missions,ScienceStore science,string root)
    {
        var original=AeroScience.Tests.LidarFixture.Write(Path.Combine(root,"synthetic-p05c"),points:[(0,0,12345),(0,450,23456),(100000,100000,99999)]);
        var source=await LidarImports.Import(science,original,new("SYNTHETIC historical LiDAR edge fixture","synthetic-lidar-"+Guid.NewGuid(),"Synthetic testing only","Not genuine source acceptance"));var geometry=Flight(missions,science,new(40,-75));
        var job=LidarProcessing.Create(science,missions,geometry.MissionId,new(geometry.Id,[new(source.Id,source.ManifestSha256,true)],new(RecordsPerTile:2,MaximumScratchBytes:4194304),Guid.NewGuid()));return new(geometry.MissionId,geometry.Id,source.Id,job.Id,true,true);
    }
}
