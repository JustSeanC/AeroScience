import {node, numeric, dateTime, link, newIdentity} from './workstation-ui.js';
import {renderSensorOperating,markSensorOperatingStale} from './sensor-operating-status.js';
const labels={startData:'Start new science session',pauseData:'Pause science collection',resumeData:'Resume science collection',endData:'End current science session',rearmAutomation:'Rearm automatic collection'};
export function metricText(m){
 const supported=['supported','caution'].includes(m.status)&&Number.isFinite(m.numerator)&&Number.isFinite(m.denominator)&&m.denominator>0;
 return `${m.id}: ${m.status}${supported?` · ${numeric(m.numerator)} / ${numeric(m.denominator)} ${m.unit} (${numeric(100*m.numerator/m.denominator)}%)`:Number.isFinite(m.numerator)?` · ${numeric(m.numerator)} ${m.unit}`:''}. Missing ${m.missing}; unassessed ${m.unassessed}. ${m.method}`;
}
const semantic=s=>JSON.stringify([s?.missionId,s?.controlRevision,s?.currentSessionId,s?.phase,s?.authority,s?.manualAutomationHold,s?.continuityHold,s?.holds]);
const elapsed=(from,to)=>from?`${Math.max(0,Math.floor(((to?Date.parse(to):Date.now())-Date.parse(from))/1000))} s`:'Unknown';

// Shared desktop/paired-browser presentation. Backend services own all decisions.
export function missionIntelligence(parent,api,{id='mission-intelligence',after=false,sensorParent=null,onState=()=>{},onStale=()=>{},acceptState=()=>true}={}){
 const panel=node('article',undefined,'panel research-panel mission-intelligence');panel.id=id;
 if(after)parent.after(panel);else parent.append(panel);panel.append(node('h2','Mission intelligence'));
 const status=node('p','Loading authoritative mission state…');status.setAttribute('role','status');
 const summary=node('p'),sessionLine=node('p'),authority=node('p'),message=node('p');message.setAttribute('role','status');
 const actions=node('div',undefined,'mission-actions'),reasonLabel=node('label','Pause rationale '),reason=node('select');reason.setAttribute('aria-label','Science pause rationale');
 for(const [value,title]of [['','Unspecified (evidence stays uncertain)'],['safety','Pilot-declared safety pause']]){const option=node('option',title);option.value=value;reason.append(option);}reasonLabel.append(reason);
 const controls=new Map();let current=null,busy=false,inflight=false,generation=0,disposed=false,lastSuccess=0;
 for(const [action,label]of Object.entries(labels)){const button=node('button',label);button.type='button';button.disabled=true;button.onclick=()=>control(action);actions.append(button);controls.set(action,button);}
 panel.append(status,summary,sessionLine,authority,reasonLabel,actions,message,node('p','End current science session preserves its results; Start begins another session. End Flight is a separate recording control. Rearm does not start or resume science.'));
 const sections={};for(const [key,title]of [['sessions','Science sessions'],['instruments','Effective instruments & operating evidence'],['performance','Provisional flight performance & scientific yield'],['obligations','Frozen research objectives & obligations'],['triggers','Configured automation & recorded events']]){const detail=node('details');detail.append(node('summary',title));const body=node('div');detail.append(body);panel.append(detail);sections[key]=body;}
 const links=node('nav');links.setAttribute('aria-label','Mission intelligence tools');link(links,'Find & Download External Data','#library/download');link(links,'NASA AVIRIS Download & Import','#library/download?science=hyperspectral');link(links,'Review offline flight reports','#history');panel.append(links);
 const sensors=sensorParent||node('section');if(!sensorParent){sensors.id=id+'-sensors';panel.querySelector('details').before(sensors);}
 const historyList=node('ul');panel.append(historyList);
 const eventList=node('ol'),more=node('button','Load more collection events'),reloadEvents=node('button','Refresh collection event evidence');more.type=reloadEvents.type='button';sections.triggers.append(eventList,more,reloadEvents);let eventAfter=0,eventMission=null,eventBusy=false;
 reloadEvents.onclick=()=>{if(eventBusy)return;eventAfter=0;eventList.replaceChildren();more.hidden=false;more.click();};
 more.onclick=async()=>{if(eventBusy||!current?.flight.activeMission)return;eventBusy=true;more.disabled=true;const mission=current.flight.activeMission.id;
  try{const rows=await api(`/api/missions/${mission}/collection/events?after=${eventAfter}&limit=50`);if(eventMission!==mission||disposed)return;for(const e of rows){eventAfter=e.sequence;eventList.append(node('li',`${dateTime(e.inputUtc)} · ${e.action} · ${e.origin} · ${e.outcome} · session ${e.sessionId||'none'} · occurrence ${e.occurrenceId||'none'} · ${e.reason}. Evidence: ${e.evidence.map(v=>v.id).join(', ')}`));}more.hidden=rows.length<50;}catch(e){message.textContent='Event evidence unavailable: '+e.message;}finally{eventBusy=false;more.disabled=false;}};
 function replace(body,key,render){if(body.dataset.key===key)return;body.dataset.key=key;body.replaceChildren();render(body);}
 function draw(data){
  current=data;lastSuccess=Date.now();renderSensorOperating(sensors,data.sensorOperating,{compact:!sensorParent});const {flight,collection,plan,performance}=data,s=collection?.state,m=flight.activeMission;
  const age=flight.telemetry?(Date.now()-Date.parse(flight.telemetry.timestampUtc))/1000:null;
  status.textContent=`${flight.connection.state} · ${flight.connection.source==='simulated'?'SIMULATED TEST FIXTURE — no MSFS acceptance':'Read-only simulator connection'} · ${age===null?'Position unavailable':age< -2||age>6?'Stale position — no crossing inferred':`Position age ${Math.max(0,Math.floor(age))} s`} · refreshed ${dateTime(data.generatedUtc)}`;
  summary.textContent=m?`${m.name} · ${plan?.objective||'Objective not declared'} · ${plan?.template||flight.research?.template||'General research'} · ${m.aircraft||'Aircraft unknown'} · ${m.mode} · Flight recording ${elapsed(m.startedUtc)} · ${m.sampleCount} flight samples`:'Start a flight to arm science or begin a session. Flight recording and scientific collection are separate.';
  const session=collection?.currentSession;
  sessionLine.textContent=s?`Science: ${s.phase} · ${session?`Session ${session.sequence} · ${session.id} · started ${dateTime(session.startedUtc)} · ${elapsed(session.startedUtc)}`:'No open science session. Flight recording continues.'}`:'No active science session.';
  authority.textContent=s?`Automation: ${s.authority}${s.manualAutomationHold?' · pilot override — rearm required':''}${s.continuityHold?' · continuity hold':''}${collection.fault?' · storage hold; resolve storage before rearm':''}. ${(s.holds||[]).map(h=>h.reason).join(' ')}`:'';
  for(const [action,button]of controls){const available=collection?.actions?.find(a=>a.action===action);button.disabled=busy||!available?.available;button.title=available?.reason||'No active flight.';}
  replace(sections.sessions,JSON.stringify(collection?.sessions),body=>{for(const item of collection?.sessions||[])body.append(node('p',`Session ${item.sequence} · ${item.id} · ${item.status} · ${dateTime(item.startedUtc)} → ${item.endedUtc?dateTime(item.endedUtc):'open'} · ${item.endReason||'not ended'}`));});
  replace(historyList,JSON.stringify((collection?.sessions||[]).map(v=>[v.id,v.status])),body=>{for(const item of collection?.sessions||[])body.append(node('li',`Session ${item.sequence} · ${item.id} · ${item.status}`));});
  replace(sections.instruments,JSON.stringify([flight.research?.instruments,data.operating]),body=>{
   for(const i of flight.research?.instruments||[]){const effective=session?.configurations?.filter(c=>c.settings.instrumentId===i.instrumentId).sort((a,b)=>b.revision-a.revision)[0];const report=data.operating.find(r=>r.configuration.settings.instrumentId===i.instrumentId&&r.assessment.missionId===m?.id&&r.assessment.sessionId===session?.id&&(!effective||r.configuration.id===effective.id)),config=effective||report?.configuration;
    body.append(node('h3',`${i.instrumentId} · ${i.enabled?'enabled':'disabled'} · ${i.quality}`),node('p',`${i.acquisitionSets} acquired sets; ${i.observationCount} measurement rows; ${i.rejectedObservations} rejected; ${i.missingIntervals} missing intervals. ${i.message||''}`),node('p',`Configuration ${config?.id||'Not yet assessed'} revision ${config?.revision??'Unknown'}; profile ${i.payloadProfileId||'Unknown'}; calibration ${config?.calibration?.status||'Unknown'} ${config?.calibration?.id||''}. Last success ${dateTime(i.lastSuccessfulUtc)}.`));
    for(const r of report?.assessment.rules||[])body.append(node('p',`${r.parameter}: ${r.observedValue??'Unknown'} ${r.unit} · Operating ${r.compliance}; derived validity ${r.derivationValidity}; source QA ${r.sourceQuality}. ${r.reasons.join(' ')}`));
   }body.append(node('p','Measurement rows and acquisition sets have distinct denominators. Historical external source observations are not live aircraft sensor images. Unknown operating evidence is not successful compliance.'));
  });
  replace(sections.performance,JSON.stringify(performance),body=>{
   if(!performance){body.append(node('p',`${data.performanceState}. Performance awaits the bounded background evaluator. Good Data and exact geographic coverage are not yet assessed.`));return;}
   const g=performance.grade;body.append(node('p',`${g.status} · ${g.letter?'Policy grade '+g.letter:'No final letter grade'} · supported bounds ${numeric(performance.supportedScoreLower)}–${numeric(performance.supportedScoreUpper)} / 100 · applicable evidence ${g.evidenceWeightFraction==null?'Unknown':numeric(g.evidenceWeightFraction*100)+'%'} · evidence as of ${dateTime(performance.input.asOfUtc)}${Date.now()-Date.parse(performance.input.asOfUtc)>15000?' · Stale evidence cutoff; not current real-time performance.':''}`));
   for(const c of performance.categories)body.append(node('p',`${c.category}: ${c.applicable?numeric(c.scoreLower)+'–'+numeric(c.scoreUpper):'Inapplicable'} · weight ${c.effectiveWeight}% (frozen ${c.originalWeight}%) · evidence ${numeric(c.evidenceFraction*100)}%. ${c.method}`));
   for(const y of performance.input.yield||[])for(const metric of y.metrics)body.append(node('p',`${y.instrumentId} · ${y.sessionId?'Session '+y.sessionId:'Whole flight'} · ${metricText(metric)}`));
   if(!performance.input.yield.length)body.append(node('p','Good Data, source QA and exact valid-pixel coverage pending archived scientific reconciliation. Actual current acquisition counts appear under Instruments; route adherence is not scientific coverage.'));
   for(const text of [...(g.limitations||[]),...(performance.input.limitations||[])])body.append(node('p',text));
  });
  replace(sections.obligations,JSON.stringify([performance?.input.context,performance?.input.evidence]),body=>{
   const context=performance?.input.context;if(!context){body.append(node('p','Frozen grading obligations unavailable; Universal/legacy missions are not assigned invented grades.'));return;}
   body.append(node('p',`Frozen ${dateTime(context.frozenUtc)} · rubric ${context.rubric.id} revision ${context.rubric.revision}. Obligations apply across every session; ending or restarting science does not erase intended work.`));
   for(const o of context.obligations){const e=performance.input.evidence.find(v=>v.obligationId===o.id);body.append(node('p',`${o.id} · ${o.criterionId} · ${o.scope||'Mission'} · planned session ${o.plannedSessionKey||'not specified'} · target ${o.targetAmount??'Unknown'} ${o.unit||''}. ${e?.reasons.join(' ')||'Evidence pending'} ${e?.recommendations.join(' ')||''}`));}
  });
  let configured=sections.triggers.querySelector('[data-configured]');if(!configured){configured=node('div');configured.dataset.configured='';sections.triggers.prepend(configured);}
  replace(configured,JSON.stringify([data.triggers,collection?.areas]),body=>{body.append(node('p','Configured occurrences are not predicted next crossings. No ETA or final-waypoint End is inferred. End requires an explicit point/gate; area exit may only pause.'));
   for(const area of collection?.areas||[])body.append(node('p',`${area.name} · ${area.id} · ${area.inside===null?'Area membership unknown':area.inside?'Inside research area':'Outside research area'} · position evidence ${dateTime(area.positionUtc)}`));
   for(const t of data.triggers)body.append(node('p',`${t.trigger.action} · ${t.trigger.kind} · target ${t.trigger.targetId} · occurrence ${t.trigger.occurrenceId} · planned session ${t.trigger.plannedSessionKey||'none'} · ${t.status}`));if(!data.triggers.length)body.append(node('p','No automatic triggers configured. Use manual science controls.'));
  });
  if(eventMission!==m?.id){eventMission=m?.id;eventAfter=0;eventList.replaceChildren();more.hidden=!m;if(m)more.click();}
  onState(data);
 }
 function invalidate(){generation++;markSensorOperatingStale(sensors,'Collection or configuration changed; waiting for current sensor evidence.');}
 async function control(action){if(busy||!current?.collection?.state)return;const visible=current.collection.state,mission=visible.missionId;busy=true;invalidate();for(const button of controls.values())button.disabled=true;
  try{const fresh=await api(`/api/missions/${mission}/collection`);if(semantic(fresh.state)!==semantic(visible))throw Error('Science session or authority changed; no command was applied.');
   const request={version:1,missionId:mission,requestId:newIdentity(),expectedRevision:fresh.state.revision,expectedControlRevision:fresh.state.controlRevision??null,expectedSessionId:visible.currentSessionId??null,action,...(action==='pauseData'&&reason.value?{justification:reason.value}:{})};
   let result;try{result=await api(`/api/missions/${mission}/collection/control`,request);}catch(error){if(!(error instanceof TypeError))throw error;result=await api(`/api/missions/${mission}/collection/control`,request);}message.textContent=result.event.reason;
  }catch(error){message.textContent=error.message+' Synchronize and issue a new command if still intended.';}finally{busy=false;await refresh();}
 }
 async function refresh(){if(inflight||busy||disposed||document.hidden||!panel.offsetParent)return;inflight=true;const own=++generation;
  try{const data=await api('/api/mission-intelligence');if(own===generation&&panel.isConnected){if(acceptState(data))draw(data);else{for(const button of controls.values())button.disabled=true;onStale("The recording flight changed while this response was in flight. Synchronizing the current flight.");}}}catch(error){if(own===generation){status.textContent='Mission state unavailable or stale: '+error.message;markSensorOperatingStale(sensors);for(const button of controls.values())button.disabled=true;onStale(error.message);}}finally{inflight=false;}
 }
 const wake=()=>refresh();window.addEventListener('hashchange',wake);document.addEventListener('visibilitychange',wake);
 const timer=setInterval(()=>{if(!panel.isConnected){dispose();return;}if(lastSuccess&&Date.now()-lastSuccess>6000){status.textContent='Mission state stale — reconnect before issuing collection commands.';for(const button of controls.values())button.disabled=true;onStale('Current mission state has not been confirmed for more than six seconds.');}refresh();},2000);
 function dispose(){disposed=true;generation++;clearInterval(timer);window.removeEventListener('hashchange',wake);document.removeEventListener('visibilitychange',wake);}
 refresh();return {refresh,dispose,invalidate};
}
