using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.TestHost;
// Explicit synthetic evidence for real-backend UI tests. Never shipped in the application.
internal static class FlightTestProcedureFixture
{
 public static object Create(Guid cardId,int revision,string scenario,MissionStore missions,MissionCoordinator host)
 {
  if(host.Snapshot().ActiveMission is not null)throw new InvalidOperationException("End the active flight first.");
  if(scenario is not("complete" or "missing-repeat" or "early" or "failure" or "paused" or "interrupted"))throw new ArgumentException("Unsupported synthetic procedure scenario.");
  var card=missions.ResearchCard(cardId,revision);var definition=card.Procedure??throw new ArgumentException("Use a multi-point card.");
  if(definition.Points.Any(p=>p.Conditions.HoldSeconds>10||p.Repetitions>3))throw new ArgumentException("Use bounded fixture holds up to 10 seconds and up to three repetitions.");
  var start=DateTimeOffset.UtcNow.AddMinutes(-30);var time=start;int tick=0;
  var coordinator=new MissionCoordinator(missions,1000,utcNow:()=>time);
  void Observe(int at,FlightRegimeDefinition rule,bool bad=false,bool pause=false){tick=at;time=start.AddSeconds(at);var frame=new TelemetryFrame(time,TelemetrySource.Simulated,"Explicit P11 controlled original fixture",40,-75,bad?rule.AltitudeMslFeet+rule.AltitudeToleranceFeet+20:rule.AltitudeMslFeet,null,bad?rule.AirspeedKnots+rule.AirspeedToleranceKnots+20:rule.AirspeedKnots,150,150,0,0,0,0,0,pause);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic fixture",time),frame));}
  Observe(0,definition.Points[0].Conditions);var flight=coordinator.Start(new("Retained multi-point "+scenario,PlanId:missions.CreateResearchPlan(card.Id,revision).Id));
  void Control(ResearchCardAction action,int? point=null){var state=coordinator.PersonalResearch(flight.Id);coordinator.ControlPersonalResearch(new(1,flight.Id,Guid.NewGuid(),state.Revision,action,card.Id,state.Cards.Single().OpenAttempt?.Id,point is {} i?definition.Points[i].Id:null));}
  Control(ResearchCardAction.Activate);
  void Attempt(int index){var point=definition.Points[index];var first=tick+1;Observe(first,point.Conditions);Control(ResearchCardAction.BeginAttempt,index);for(var i=1;i<=point.Conditions.HoldSeconds+1;i++)Observe(first+i,point.Conditions,scenario=="failure"&&index==0,scenario=="paused"&&index==0&&i==2);Control(ResearchCardAction.EndAttempt);}
  if(scenario=="early")Attempt(1);
  for(var index=0;index<definition.Points.Length;index++)for(var repeat=0;repeat<definition.Points[index].Repetitions-(index==1&&scenario is "missing-repeat" or "early"?1:0);repeat++)Attempt(index);
  if(scenario=="interrupted"){Observe(tick+1,definition.Points[0].Conditions);Control(ResearchCardAction.BeginAttempt,0);Observe(tick+1,definition.Points[0].Conditions);coordinator.Interrupt();}
  else{Observe(tick+1,definition.Points[0].Conditions);coordinator.End(flight.Id);}
  return new{flightId=flight.Id,startUtc=start,endUtc=time,cardId};
 }
}
