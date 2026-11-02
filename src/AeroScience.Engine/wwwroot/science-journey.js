import {node,link,button} from './workstation-ui.js';
import {researchCardTargets} from './personal-research-start.js';

// A view of existing recording/collection controllers, never a recorder or evaluator.
export function scienceJourney(parent,api){
 const root=node('section',undefined,'science-journey');root.id='science-journey';root.setAttribute('aria-label','Your research flight');
 const title=node('h2','Ready to Start'),steps=node('ol'),summary=node('p'),connection=node('p'),recording=node('p'),collection=node('p'),count=node('p'),next=node('p'),actions=node('div',undefined,'action-row'),results=node('div',undefined,'action-row');
 for(const label of ['Choose Research','Prepare for Flight','Start Recording','Collect Science','Review Results'])steps.append(node('li',label));
 recording.setAttribute('role','status');collection.setAttribute('role','status');count.dataset.retainedScience='';
 root.append(title,steps,summary,connection,recording,collection,count,next,actions,results);parent.prepend(root);
 link(actions,'Choose Research','#my-research');const instruments=link(actions,'Review Instruments','#science');
 const show=button(actions,'Show Science Controls',()=>{const panel=document.getElementById('mission-intelligence');panel?.scrollIntoView({block:'center'});const control=[...panel?.querySelectorAll('button')||[]].find(b=>!b.disabled);control?.focus({preventScroll:true});});
 let flight=null,intelligence=null,plan=null,card=null,last=null,stale=false,recovered=false;
 function paint(){
  const active=flight?.activeMission,telemetry=flight?.telemetry,age=telemetry?Date.now()-Date.parse(telemetry.timestampUtc):Infinity;
  const fresh=flight?.connection?.state==='connected'&&telemetry?.isValid!==false&&age>=-2000&&age<=6000;
  const same=intelligence?.flight?.activeMission?.id===active?.id&&active;
  const state=same&&!stale?intelligence.collection?.state:null,enabled=(same?intelligence.flight.research?.instruments:plan?.instruments)||[];
  const needed=enabled.filter(i=>i.enabled),fault=same&&intelligence.collection?.fault;
  const unavailable=active&&(!fresh||telemetry?.isPaused||stale||!state||fault);
  title.textContent=active?'Your Research Flight':last?'Flight Saved — Review Results':'Ready to Start';
  summary.textContent=card?`${card.name} · ${card.atmosphericProfile?.question||card.atmosphericTransect?.question||card.survey?.question||card.observationFlight?.question||card.experimentalSensor?.question||'Follow the saved research requirements.'} · ${researchCardTargets(card)}`:plan?plan.name:'Choose a research starter, select a saved mission below, or record an Unplanned Flight.';
  connection.textContent=`Aircraft telemetry: ${fresh?(flight.connection.source==='simulated'?'Synthetic test fixture — not MSFS':telemetry.isPaused?'MSFS paused':'Connected to MSFS'):'Unavailable or stale'}. Telemetry on this screen is not a saved scientific observation.`;
  recording.textContent=active?`Flight recording: Recording · ${active.sampleCount} retained flight samples`:'Flight recording: Not recording. Preparing a mission does not start recording.';
  const phases={armed:'Armed — science has not started',collecting:'Collecting',paused:'Paused',betweenSessions:'Session ended — ready for another session',stopped:'Stopped'};
  collection.textContent=`Science collection: ${!active?'Not started':unavailable?'Unavailable — check the connection, session or storage hold':phases[state.phase]||'Unavailable'}`;
  const retained=same?intelligence.retainedScience:null;
  count.textContent=!active?'Stored scientific observations appear here during your flight.':retained?.missionId===active.id&&Number.isFinite(retained.originalObservations)?`Retained original scientific observations: ${retained.originalObservations} · modeled/derived observations: ${retained.derivedObservations} · saved remote products: ${retained.remoteProducts}. ${stale?'Last checked count; connection is stale.':'Counts do not establish quality or research completion.'}`:'Retained scientific observations: unavailable. No count is inferred from flight samples.';
  next.textContent=!active?plan?`Mission prepared — Start Recording next. ${needed.length} instrument(s) enabled. ${card?.atmosphericProfile||card?.atmosphericTransect||card?.experimentalSensor?'Onboard atmospheric observations need no NASA download, organization or cloud account.':'Review the saved instrument and source requirements.'}`:'Next: Choose Research or select a mission below.':fault?'Resolve the scientific storage/session hold in Science Controls. Flight recording remains separate.':!fresh||telemetry?.isPaused?'Load or unpause the aircraft and wait for fresh simulator data. No measurements are invented.':!needed.length?'No instruments enabled. Review Instruments to choose supported measurements. Recording alone does not collect science.':stale||!state?'Waiting for authoritative collection status. Do not assume science is collecting.':state.phase==='armed'||state.phase==='betweenSessions'?'Next: use Start new science session in Science Controls.':state.phase==='paused'?'Next: use Resume science collection when ready.':state.phase==='collecting'&&retained?.originalObservations===0?'Collecting is enabled, but no original scientific observations have been retained yet. Check instrument/source readiness if the count stays at zero.':'Watch the retained observation count. End mission when finished, then open this flight’s results.';
  show.hidden=!active;instruments.textContent=needed.length?'Review Instruments':'Choose Instruments';
  const step=active?3:last?4:plan?2:0;[...steps.children].forEach((item,i)=>{if(i===step)item.setAttribute('aria-current','step');else item.removeAttribute('aria-current');});
  const identity=last?.id||'';if(results.dataset.flight!==identity){results.dataset.flight=identity;results.replaceChildren();if(last){results.append(node('p',`Saved flight: ${last.name}`));link(results,'Review Results',`#workspace/map?missions=${last.id}`);link(results,'Open Scientific Analysis',`#workspace/analysis?missions=${last.id}`);link(results,'Original Evidence Timeline',`#operations?flight=${last.id}`);}}
 }
 async function recover(){if(recovered)return;recovered=true;try{const rows=await api('/api/missions?limit=1');if(!flight?.activeMission&&rows[0]?.status!=='recording'){last=rows[0]||null;paint();}}catch{recovered=false;}}
 window.addEventListener('aeroscience-intelligence',event=>{intelligence=event.detail;stale=false;paint();});
 window.addEventListener('aeroscience-intelligence-stale',()=>{stale=true;paint();});
 return {selection(p,c){plan=p;card=c;paint();},update(value){if(flight?.activeMission&&!value.activeMission)last=flight.activeMission;flight=value;paint();if(!value.activeMission)recover();}};
}
