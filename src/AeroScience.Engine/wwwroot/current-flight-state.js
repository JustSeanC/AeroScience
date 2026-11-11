import {missionPresentation} from './presentation-state.js';

// Read-only cockpit projection. Availability still belongs to the original controllers.
export function currentFlightPresentation(data, snapshot=data?.flight, {now=Date.now(),stale=false}={}) {
  const model=missionPresentation(data,{now}),flight=snapshot?.activeMission;
  const matched=!!snapshot&&(flight?.id||null)===(data?.flight?.activeMission?.id||null);
  const fresh=data?.version===1&&model.fresh&&matched&&!stale;
  const telemetry=snapshot?.telemetry,age=now-Date.parse(telemetry?.timestampUtc);
  const position=fresh&&snapshot.connection?.state==='connected'&&telemetry?.isValid!==false&&age>=-2000&&age<=6000;
  const connection=!fresh?'unknown':snapshot.connection?.state!=='connected'?'disconnected':!position?'stale':telemetry?.isPaused?'paused':'connected';
  let science=!fresh?'unknown':flight&&connection!=='connected'?'unavailable':model.science;
  const counts=data?.retainedScience?.missionId===flight?.id&&fresh?data.retainedScience:null;
  const session=data?.collection?.currentSession;
  const bound=fresh&&!!flight&&data?.collection?.state?.missionId===flight.id;
  if(science==='collecting'&&session?.id!==data?.collection?.state?.currentSessionId)science='unknown';
  if(science==='collecting'&&(data.collection?.state?.continuityHold||data.collection?.state?.holds?.length))science='unavailable';
  const started=Date.parse(flight?.startedUtc),elapsed=fresh&&Number.isFinite(started)&&now>=started?Math.floor((now-started)/1000):null;
  return Object.freeze({fresh,connection,position,flightId:fresh?flight?.id||null:null,
    recording:fresh?model.recording:'unknown',science,elapsedSeconds:elapsed,
    sessionId:bound&&session?.id===data?.collection?.state?.currentSessionId?session.id:null,
    sessionSequence:bound&&session?.id===data?.collection?.state?.currentSessionId?session.sequence:null,
    counts,scienceActions:connection==='connected'&&fresh&&!['unknown','unavailable'].includes(science)?model.scienceActions:[]});
}

// Bounded saved metadata only. This is center-to-center packet spacing, never
// valid-pixel coverage, traveled track length, detector sampling or a guarantee.
export function recordedScannerSpacing(products,{missionId,sessionId,now=Date.now()}={}) {
  if(!missionId||!sessionId)return null;
  const coordinate=p=>p&&Number.isFinite(p.latitude)&&Math.abs(p.latitude)<=90&&Number.isFinite(p.longitude)&&Math.abs(p.longitude)<=180;
  const rows=(Array.isArray(products)?products:[]).slice(0,30).filter(p=>p?.missionId===missionId&&p.sessionId===sessionId&&p.instrumentId==='terrain-scanner'&&p.id&&p.sourceId&&p.intentId&&Number.isSafeInteger(p.validSampleCount)&&p.validSampleCount>0&&coordinate(p.footprint?.center)&&Number.isFinite(Date.parse(p.flightPacketUtc))&&Date.parse(p.flightPacketUtc)<=now+2000)
    .sort((a,b)=>Date.parse(b.flightPacketUtc)-Date.parse(a.flightPacketUtc));
  const b=rows[0],a=rows.find(p=>p.id!==b?.id&&p.intentId!==b?.intentId&&p.sourceId===b?.sourceId&&Date.parse(p.flightPacketUtc)<Date.parse(b?.flightPacketUtc));
  if(!a||!b)return null;
  const rad=Math.PI/180,pa=a.footprint.center,pb=b.footprint.center;
  const h=Math.sin((pb.latitude-pa.latitude)*rad/2)**2+Math.cos(pa.latitude*rad)*Math.cos(pb.latitude*rad)*Math.sin((pb.longitude-pa.longitude)*rad/2)**2;
  return Object.freeze({firstId:a.id,lastId:b.id,sessionId,sourceId:b.sourceId,
    seconds:(Date.parse(b.flightPacketUtc)-Date.parse(a.flightPacketUtc))/1000,
    metres:6371008.8*2*Math.asin(Math.sqrt(Math.min(1,Math.max(0,h))))});
}
