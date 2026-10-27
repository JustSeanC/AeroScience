using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.TestHost;
// Explicit original simulator fixtures, never a claim of installed MSFS acceptance.
internal static class AutomaticAttemptFixture
{
 public static object Create(Guid cardId,int revision,string scenario,MissionStore store,MissionCoordinator host)
 {
  if(host.Snapshot().ActiveMission is not null)throw new InvalidOperationException("End the active host flight first.");
  if(scenario is not("outcomes" or "procedure" or "manual" or "unarmed"))throw new ArgumentException("Unsupported controlled automatic fixture.");
  var card=store.ResearchCard(cardId,revision);var points=card.Procedure?.Points??[new FlightTestProcedurePoint(card.Id,card.FlightRegime??throw new ArgumentException("Choose a quantitative card."))];
  if(points.Any(p=>p.Conditions.HoldSeconds>5||p.Repetitions>3))throw new ArgumentException("Use short controlled fixture requirements.");
  var at=DateTimeOffset.UtcNow.AddHours(-1);var time=at;var tick=0;var coordinator=new MissionCoordinator(store,1000,utcNow:()=>time);
  void Observe(FlightTestProcedurePoint point,double factor){time=at.AddSeconds(++tick);var r=point.Conditions;var frame=new TelemetryFrame(time,TelemetrySource.Simulated,"Explicit P12 controlled original fixture",40,-75,r.AltitudeMslFeet+factor*r.AltitudeToleranceFeet,null,r.AirspeedKnots+factor*r.AirspeedToleranceKnots,150,150,0,0,0,0,0);coordinator.Apply(new(new(ConnectionState.Connected,frame.Source,"Synthetic fixture",time),frame));}
  Observe(points[0],4);var flight=coordinator.Start(new("Automatic original fixture "+scenario,PlanId:store.CreateResearchPlan(cardId,revision).Id));
  void Control(ResearchCardAction action,Guid? point=null){for(var retry=0;;retry++){var state=coordinator.PersonalResearch(flight.Id);try{coordinator.ControlPersonalResearch(new(1,flight.Id,Guid.NewGuid(),state.Revision,action,cardId,state.Cards.Single().OpenAttempt?.Id,point));return;}catch(InvalidOperationException ex)when(ex.Message.StartsWith("Flight or test-card state changed")&&retry<4){}}}
  void Read(FlightTestProcedurePoint p,double factor,int count){for(var i=0;i<count;i++){Observe(p,factor);store.ProcessAutomaticAttempts(flight.Id);}}
  Control(ResearchCardAction.Activate);if(scenario!="unarmed")Control(ResearchCardAction.EnableAutomatic,card.Procedure is null?null:points[0].Id);
  if(scenario=="procedure"){
   for(var index=0;index<points.Length;index++){if(index>0)Control(ResearchCardAction.EnableAutomatic,points[index].Id);for(var repeat=0;repeat<points[index].Repetitions;repeat++){if(repeat>0)Read(points[index],4,3);Read(points[index],0,(int)points[index].Conditions.HoldSeconds+2);Read(points[index],0,3);}}
  }else if(scenario=="outcomes"){
   Read(points[0],0,(int)points[0].Conditions.HoldSeconds+2);Read(points[0],0,3);Read(points[0],4,3);Read(points[0],1.6,(int)Math.Max(5,2*points[0].Conditions.HoldSeconds)+2);Read(points[0],4,3);Read(points[0],0,2);
  }else if(scenario=="manual"){
   Read(points[0],0,2);Control(ResearchCardAction.BeginAttempt,card.Procedure is null?null:points[0].Id);Read(points[0],0,(int)points[0].Conditions.HoldSeconds+2);Control(ResearchCardAction.EndAttempt);Read(points[0],0,3);Read(points[0],4,3);Read(points[0],0,(int)points[0].Conditions.HoldSeconds+2);
  }else Read(points[0],0,(int)points[0].Conditions.HoldSeconds+2);
  time=at.AddSeconds(++tick);if(scenario=="outcomes")coordinator.Interrupt();else coordinator.End(flight.Id);
  for(var i=0;i<8;i++){store.ProcessAutomaticAttempts(flight.Id);store.EvaluateAutomaticResults(flight.Id);}
  return new{flightId=flight.Id,cardId,startUtc=flight.StartedUtc,endUtc=time};
 }
}
