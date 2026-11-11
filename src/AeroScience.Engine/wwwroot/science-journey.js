import {node,link,button,informationDetails,scientificNotice} from './workstation-ui.js';
import {researchCardTargets} from './personal-research-start.js';

// A view of existing recording/collection controllers, never a recorder or evaluator.
export function scienceJourney(parent,api){
 const root=node('section',undefined,'science-journey');root.id='science-journey';root.setAttribute('aria-label','Your research flight');
 const title=node('h2','Ready to Start'),steps=node('ol'),summary=node('p'),connection=node('p'),recording=node('p'),collection=node('p'),count=node('p'),next=node('p'),actions=node('div',undefined,'action-row'),results=node('div',undefined,'action-row');
 // The existing collection controller moves its own command buttons here.
 // This is one command authority, not another recorder or optimistic shortcut.
 const collectionControls=node('div');collectionControls.id='science-journey-controls';collectionControls.setAttribute('role','group');collectionControls.setAttribute('aria-label','Science collection actions');
 for(const label of ['Choose Research','Prepare for Flight','Start Recording','Collect Science','Review Results'])steps.append(node('li',label));
 recording.setAttribute('role','status');collection.setAttribute('role','status');count.dataset.retainedScience='';
 const explanation=informationDetails(root,'Recording, source & count details',node('p','Flight samples are recorded aircraft telemetry. Original scientific observations, modeled/derived observations and remote products are separate retained records. Counts alone do not establish quality or research completion.'));
 root.prepend(title,steps,summary,connection,recording,collection,count,next,collectionControls,actions,results);parent.prepend(root);
 link(actions,'Choose Research','#my-research');const instruments=link(actions,'Review Instruments','#science');
  const show=button(actions,'Science Details',()=>{const panel=document.getElementById('mission-intelligence');panel?.scrollIntoView({block:'center'});const details=panel?.querySelector('details');if(details){details.open=true;details.querySelector('summary')?.focus({preventScroll:true});}});
 let flight=null,intelligence=null,plan=null,card=null,last=null,stale=false,recovered=false,recoveryEpoch=0;
 function paint(){
  const active=flight?.activeMission,telemetry=flight?.telemetry,age=telemetry?Date.now()-Date.parse(telemetry.timestampUtc):Infinity;
  const fresh=flight?.connection?.state==='connected'&&telemetry?.isValid!==false&&age>=-2000&&age<=6000;
  const same=intelligence?.flight?.activeMission?.id===active?.id&&active;
  const state=same&&!stale?intelligence.collection?.state:null,enabled=(same?intelligence.flight.research?.instruments:(plan?.instruments??flight?.research?.instruments))||[];
  const needed=enabled.filter(i=>i.enabled),fault=same&&intelligence.collection?.fault;
  const unavailable=active&&(!fresh||telemetry?.isPaused||stale||!state||fault);
  title.textContent=active?'Your Research Flight':last?'Flight Saved — Review Results':'Prepare for Flight';
  summary.textContent=card?(card.atmosphericProfile?.question||card.atmosphericTransect?.question||card.survey?.question||card.observationFlight?.question||card.experimentalSensor?.question||card.name):plan?plan.name:'Choose a starter or select a saved mission.';
  connection.textContent=`${flight?.connection?.source==='simulated'?'Synthetic test fixture':fresh?'Simulator-reported observations':'Simulator unavailable'}${telemetry?.isPaused?' · MSFS paused':''}`;
  recording.textContent=active?`Recording · ${active.sampleCount} retained flight samples`:'Not recording';
  const phases={armed:'Armed — science has not started',collecting:'Collecting',paused:'Paused',betweenSessions:'Session ended — ready for another session',stopped:'Stopped'};
  collection.textContent=`Science collection: ${!active?'Not started':unavailable?'Unavailable — check the connection, session or storage hold':phases[state.phase]||'Unavailable'}`;
  const retained=same?intelligence.retainedScience:null;
  count.textContent=!active?'':retained?.missionId===active.id&&Number.isFinite(retained.originalObservations)?`Retained original scientific observations: ${retained.originalObservations}${stale?' · last checked count':''}`:'Retained scientific observations: unavailable';
  let retainedDetail=explanation.querySelector('[data-derived-counts]');if(!retainedDetail){retainedDetail=node('p');retainedDetail.dataset.derivedCounts='';explanation.append(retainedDetail);}
  retainedDetail.textContent=retained&&active&&retained.missionId===active.id?`Modeled/derived observations: ${retained.derivedObservations??'Unavailable'} · saved remote products: ${retained.remoteProducts??'Unavailable'}. ${stale?'Last checked count; connection is stale.':''}`:'';
  const requiredScience=!!(card?.atmosphericProfile||card?.atmosphericTransect||card?.survey||card?.experimentalSensor);
  const starts=(intelligence?.triggers||[]).filter(t=>t.status==='Configured'&&t.trigger.action==='startData');
  const waiting=[...new Set(starts.map(t=>({waypoint:'a waypoint crossing',directionalGate:'a directional gate',areaEntry:'entry into a survey region',areaExit:'exit from a survey region'}[t.trigger.kind]||'a configured Start condition')))];
  const startAdvice=state?.authority==='automatic'&&!state.manualAutomationHold?waiting.length?` Automatic collection is waiting for ${waiting.join(' or ')}. You can start manually below.`:' No automatic Start is configured. Use Start Science Collection below.':' Use Start Science Collection below.';
  if(active&&fault)scientificNotice(next,'important',{problem:'Review the collection problem in Science Controls below.'});
  else if(active&&(!fresh||telemetry?.isPaused))scientificNotice(next,'critical',{problem:fault?'Scientific storage or collection is unavailable.':telemetry?.isPaused?'MSFS is paused.':'Aircraft telemetry is stale or unavailable.',consequence:'New qualifying science cannot be confirmed.',action:fault?'Review Science Controls and resolve the hold.':'Load or unpause the aircraft and wait for fresh data.'});
  else if(active&&stale)scientificNotice(next,'important',{problem:'Collection status is unavailable. Review Science Controls below.'});
  else if(active&&state&&!needed.length)scientificNotice(next,requiredScience?'critical':'important',{problem:'No instruments enabled.',consequence:requiredScience?'This research cannot collect its required measurements.':'Flight recording continues without scientific collection.',action:'Choose Instruments to review your payload.'});
  else if(active&&!state)scientificNotice(next,'important',{problem:'Collection status is unavailable.',consequence:'Current science collection is unconfirmed.',action:'Check the companion connection before issuing a command.'});
  else scientificNotice(next,'important',{problem:!active?plan?!fresh?'Mission prepared offline — connect MSFS and load an aircraft to record.':telemetry?.isPaused?'Mission prepared — unpause MSFS before Start Recording.':'Mission prepared — Start Recording next.':'Choose Research or select a mission below.':state?.phase==='armed'?`Science collection has not started.${startAdvice}`:state?.phase==='betweenSessions'?'Science session ended. Start Science Collection for another session.':state?.phase==='paused'?'Collection paused · use Resume science collection when ready.':retained?.originalObservations===0?'Waiting for the first saved scientific observation.':'End mission when finished, then open Review Results.'});
  let preparation=explanation.querySelector('[data-preparation-details]');if(!preparation){preparation=node('p');preparation.dataset.preparationDetails='';explanation.append(preparation);}preparation.textContent=`${needed.length} instrument(s) enabled. ${card?.atmosphericProfile||card?.atmosphericTransect||card?.experimentalSensor?'Onboard atmospheric observations need no NASA download, organization or cloud account.':'Review the saved instrument and source requirements.'}${card?' '+researchCardTargets(card):''}`;
  results.hidden=!!active;show.hidden=!active;instruments.textContent=needed.length?'Review Instruments':'Choose Instruments';
  const step=active?3:last?4:plan?2:0;[...steps.children].forEach((item,i)=>{if(i===step)item.setAttribute('aria-current','step');else item.removeAttribute('aria-current');});
  const identity=last?.id||'';if(results.dataset.flight!==identity){results.dataset.flight=identity;results.replaceChildren();if(last){results.append(node('p',`Saved flight: ${last.name}`));link(results,'Review Results',`#workspace/map?missions=${last.id}`);link(results,'Open Scientific Analysis',`#workspace/analysis?missions=${last.id}`);link(results,'Original Evidence Timeline',`#operations?flight=${last.id}`);}}
 }
 async function recover(){if(recovered)return;recovered=true;const own=recoveryEpoch;try{const rows=await api('/api/missions?limit=1');if(own===recoveryEpoch&&!flight?.activeMission&&rows[0]?.status!=='recording'){last=rows[0]||null;paint();}}catch{if(own===recoveryEpoch)recovered=false;}}
 // An explicit preparation/selection starts a new task. Late history is reference data,
 // never authority to replace that intent. Original flights remain in the existing archive.
 window.addEventListener('aeroscience-selection',()=>{recoveryEpoch++;recovered=true;last=null;paint();});
 window.addEventListener('aeroscience-intelligence',event=>{intelligence=event.detail;stale=false;paint();});
 window.addEventListener('aeroscience-intelligence-stale',()=>{stale=true;paint();});
 return {selection(p,c){plan=p;card=c;paint();},update(value){if(flight?.activeMission&&!value.activeMission)last=flight.activeMission;flight=value;paint();if(!value.activeMission)recover();}};
}
