import {node,button,newIdentity,table} from './workstation-ui.js';
import {attemptView} from './personal-research.js';

export function researchCardSelection(parent,api,references,changed){
 const selected=new Map(references.map(ref=>[ref.id,ref]));let offset=0,epoch=0;
 const section=node('section');section.id='plan-research-cards';section.append(node('h3','Personal Research Test Cards'),node('p','Choose cards before recording. Each flight freezes these revisions. You can activate compatible cards together or switch between different targets without stopping flight or science.'));
 const chosen=node('div'),catalog=node('div'),status=node('p');status.setAttribute('role','status');section.append(chosen,catalog,status);parent.append(section);
 const drawChosen=()=>{chosen.replaceChildren(node('strong',`${selected.size} cards selected for this flight`));for(const ref of selected.values()){
  const row=node('div',undefined,'action-row');row.append(node('span',`${ref.name||'Saved test card'} · revision ${ref.revision}`));button(row,'Remove Card',()=>{selected.delete(ref.id);changed();drawChosen();});chosen.append(row);
 }};
 async function load(){const own=++epoch;try{const cards=await api(`/api/research/cards?offset=${offset}`);if(own!==epoch||!section.isConnected)return;catalog.replaceChildren();
  for(const card of cards){const row=node('div',undefined,'plan-row');row.append(node('span',`${card.name} · revision ${card.revision} · ${card.flightRegime.airspeedKnots} knots IAS / ${card.flightRegime.altitudeMslFeet} feet MSL`));button(row,'Add Card',()=>{if(selected.size>=16&&!selected.has(card.id))throw Error('Choose at most 16 cards for this flight. Your saved card library is not limited.');selected.set(card.id,{id:card.id,revision:card.revision,name:card.name});changed();drawChosen();});catalog.append(row);}
  button(catalog,'Newer Test Cards',()=>{offset=Math.max(0,offset-20);return load();});button(catalog,'Older Test Cards',()=>{offset+=20;return load();});status.textContent=cards.length?'Saved personal cards. Different target ranges may require separate activation.':'No saved cards on this page. Create one in My Research.';
 }catch(error){status.textContent=error.message;}}
 for(const ref of references)api(`/api/research/cards/${ref.id}?revision=${ref.revision}`).then(card=>{if(selected.get(ref.id)?.revision===ref.revision){selected.set(ref.id,{...ref,name:card.name});drawChosen();}}).catch(error=>status.textContent=error.message);
 drawChosen();load();const read=()=>[...selected.values()].map(({id,revision})=>({id,revision}));read.replaceReference=card=>{if(selected.has(card.id)){selected.set(card.id,{id:card.id,revision:card.revision,name:card.name});drawChosen();}};return read;
}

export function researchFlightControls(parent,api){
 const panel=node('section',undefined,'panel research-panel');panel.id='research-flight-controls';panel.append(node('h2','Personal Research Test Cards'));
 const status=node('p');status.setAttribute('role','status');const rows=node('div');panel.append(status,rows);parent.append(panel);
 let flight=null,state=null,busy=false,pending=false,lastRead=0,epoch=0,signature='',lastCommand=null,readController;const controls=new Map();
 function apply(next){
  if(next.flightId!==flight||state&&next.revision<state.revision)return;state=next;
  const key=next.cards.map(c=>c.card.id).join('/');if(key!==signature){signature=key;rows.replaceChildren();controls.clear();
   for(const selected of next.cards){const card=selected.card,row=node('article',undefined,'research-panel');row.setAttribute('aria-label',card.name);row.append(node('h3',`${card.name} · revision ${card.revision}`));
    const info=node('p'),actions=node('div',undefined,'action-row');row.append(info,actions);const buttons={};
    for(const [action,label] of [['activate','Activate Card'],['deactivate','Deactivate Card'],['beginAttempt','Begin Attempt'],['endAttempt','End Attempt']]){
     const b=node('button',label,action==='beginAttempt'?'primary':'secondary');b.type='button';buttons[action]=b;
     b.onclick=async()=>{if(busy||!state)return;const current=state.cards.find(c=>c.card.id===card.id);if(!current)return;
      const input={version:1,flightId:flight,expectedRevision:state.revision,action,cardId:card.id,expectedAttemptId:current.openAttempt?.id||null};const key=JSON.stringify(input);
      if(lastCommand?.key!==key)lastCommand={key,body:{...input,requestId:newIdentity()}};
      const own=epoch;busy=true;refreshButtons();
      try{const result=await api('/api/research/control',lastCommand.body);if(own===epoch){apply(result.state);status.textContent=result.receipt.message;}}
      catch(error){if(own===epoch){status.textContent=error.message;lastRead=0;}}
      finally{if(own===epoch){busy=false;refreshButtons();}}
     };actions.append(b);
    }controls.set(card.id,{info,buttons});rows.append(row);
   }
  }
  refreshButtons();
 }
 function refreshButtons(){if(!state)return;for(const card of state.cards){const view=controls.get(card.card.id);if(!view)continue;
  view.info.textContent=`${card.openAttempt?'Attempt recording since '+card.openAttempt.startUtc:card.active?'Active — ready for an attempt':'Inactive'} · ${card.card.flightRegime.airspeedKnots} ± ${card.card.flightRegime.airspeedToleranceKnots} knots IAS · ${card.card.flightRegime.altitudeMslFeet} ± ${card.card.flightRegime.altitudeToleranceFeet} feet MSL. ${card.activationBlocked||''}`;
  for(const [action,b]of Object.entries(view.buttons))b.disabled=busy||!state.recording||action==='activate'&&(card.active||!!card.activationBlocked)||action==='deactivate'&&!card.active||action==='beginAttempt'&&(!card.active||!!card.openAttempt||!state.canBeginAttempt)||action==='endAttempt'&&!card.openAttempt;
 }}
 return {update(snapshot){
  const id=snapshot.activeMission?.id||null;if(id!==flight){epoch++;readController?.abort();pending=false;flight=id;state=null;signature='';controls.clear();rows.replaceChildren();busy=false;lastRead=0;status.textContent=id?'Reading selected flight cards…':'Choose test cards in Mission Planner before your next flight. Card activation is separate from flight and science recording.';}
  if(!id||pending||parent.hidden||document.hidden||Date.now()-lastRead<1500)return;
  const own=epoch;pending=true;lastRead=Date.now();const controller=new AbortController();readController=controller;const timeout=setTimeout(()=>controller.abort(),6000);api(`/api/missions/${id}/research-operations`,undefined,{signal:controller.signal}).then(next=>{if(own!==epoch)return;apply(next);if(!next.cards.length)status.textContent='No personal cards were selected for this flight. The flight and science controls continue normally.';else if(!next.canBeginAttempt)status.textContent='Begin Attempt waits for fresh, unpaused aircraft telemetry. End Attempt and Deactivate Card remain separate controls.';}).catch(error=>{if(own===epoch){status.textContent=error.name==='AbortError'?'Card controls are waiting for synchronization. Flight recording is unchanged.':error.message;for(const c of controls.values())for(const b of Object.values(c.buttons))b.disabled=true;}}).finally(()=>{clearTimeout(timeout);if(own===epoch)pending=false;});
 }};
}

export async function researchFlightTimeline(parent,api,flight,{attemptId,at}={}){
 const root=node('section',undefined,'panel research-panel');root.setAttribute('aria-label','Test-card activity and evidence');root.append(node('h2','Test-card Activity & Evidence Timeline'),node('p','Actual host UTC events and original evidence references. Card activation is independent of science sessions. Selecting an event reads nearby retained evidence; gaps are never interpolated.'));parent.append(root);
 const status=node('p');status.setAttribute('role','status');root.append(status);const [state]=await Promise.all([api(`/api/missions/${flight}/research-operations`)]);if(!root.isConnected)return;
 let selected=attemptId?await api(`/api/research/recorded-attempts/${attemptId}`):null;
 if(selected&&selected.flightId!==flight)throw Error('The selected attempt belongs to another flight. Refresh the experiment.');
 let offset=0,after=selected?Math.max(0,selected.beginEventId-1):0,inspection=0;const attempts=node('div'),events=node('div'),evidence=node('div');evidence.setAttribute('role','status');root.append(attempts,events,evidence);
 async function loadAttempts(){const rows=selected?[selected]:await api(`/api/missions/${flight}/research-attempts?offset=${offset}`);if(!root.isConnected)return;attempts.replaceChildren(node('h3','Recorded Attempts'));
  if(!rows.length)attempts.append(node('p','No marked attempts on this page. Existing postflight-only P01 results remain available in My Research.'));
  for(const attempt of rows){const card=state.cards.find(c=>c.card.id===attempt.cardId)?.card;const label={recording:'Recording',recorded:'Recorded',interrupted:'Interrupted'}[attempt.status]||'Unknown';const row=node('article');row.append(node('strong',`${card?.name||'Personal test card'} · revision ${attempt.cardRevision} · ${label}`),node('p',`${attempt.startUtc} → ${attempt.endUtc||'Attempt still recording'} · ${attempt.endReason||'Explicit attempt boundaries'} · Events ${attempt.beginEventId} / ${attempt.endEventId??'pending'}`));
   if(attempt.endUtc&&!state.recording){const command=newIdentity();button(row,'Evaluate Attempt',async()=>{const result=await api(`/api/research/recorded-attempts/${attempt.id}/evaluate`,{requestId:command});if(row.isConnected){row.querySelector('.attempt-result')?.remove();const shown=attemptView(result);shown.classList.add('attempt-result');row.append(shown);}});}
   attempts.append(row);
  }if(selected)button(attempts,'Show All Flight Attempts',()=>{selected=null;return loadAttempts();});else{button(attempts,'Newer Attempt Page',()=>{offset=Math.max(0,offset-20);return loadAttempts();});button(attempts,'Older Attempt Page',()=>{offset+=20;return loadAttempts();});}
 }
 async function inspect(at){
  const own=++inspection,q=encodeURIComponent(at);const found=await Promise.allSettled([api(`/api/missions/${flight}/replay?at=${q}&limit=3`),api(`/api/missions/${flight}/replay/context?at=${q}`),api(`/api/missions/${flight}/replay/acquisitions?at=${q}`)]);if(!root.isConnected||own!==inspection)return;evidence.replaceChildren(node('h3','Original Evidence Near '+at));
  const [track,science,products]=found;
  if(track.status==='fulfilled'){const samples=track.value.samples.filter(s=>Math.abs(Date.parse(s.frame.timestampUtc)-Date.parse(at))<=3000);table(evidence,'Recorded flight samples',['Sequence','Original UTC','IAS (knots)','Altitude (feet MSL)'],samples.map(s=>[s.sequence,s.frame.timestampUtc,s.frame.indicatedAirspeedKnots,s.frame.altitudeMslFeet]));if(!samples.length)evidence.append(node('p','No recorded flight sample within three seconds at or after this event. No value was filled in.'));}else evidence.append(node('p','Recorded flight evidence unavailable: '+track.reason.message));
  if(science.status==='fulfilled'){evidence.append(node('p','Science collection: '+science.value.instruments.scienceState));const rows=science.value.observations||[];table(evidence,'Retained scientific observations',['Original UTC','Measurement','Original value','Quality'],rows.map(o=>[o.timestampUtc,o.measurementId,Number.isFinite(o.value)?`${o.value} ${o.unit}`:'Unknown',o.quality]));if(!rows.length)evidence.append(node('p',science.value.error||'No retained scientific observations near this event.'));}else evidence.append(node('p','Scientific evidence unavailable: '+science.reason.message));
  if(products.status==='fulfilled')evidence.append(node('p',`${products.value.length} retained acquisition products near this event. Original source provenance remains in Science Results & Analysis.`));else evidence.append(node('p','Acquisition evidence unavailable: '+products.reason.message));
 }
 async function loadEvents(){const rows=await api(`/api/missions/${flight}/replay/events?after=${after}`);if(!root.isConnected)return;events.replaceChildren(node('h3','Flight, Card and Science Events'));
  for(const event of rows){const row=node('div',undefined,'research-panel');const technical=['payload-snapshot','instruments-configured','mission-performance-report','operating-assessment'].includes(event.kind);const summary=technical?'Configuration or assessment retained in the mission archive':event.message;
   row.append(node('p',`${event.timestampUtc} · ${event.kind} · ${summary}`));button(row,'Show Recorded Evidence',()=>inspect(event.timestampUtc));events.append(row);
   if(technical){const details=node('details');details.append(node('summary','Technical Event Evidence'),node('pre',event.message));row.append(details);}
  }button(events,'First Event Page',()=>{after=0;return loadEvents();});if(rows.length===100)button(events,'Next Event Page',()=>{after=rows.at(-1).id;return loadEvents();});
 }
 try{await Promise.all([loadAttempts(),loadEvents()]);if(at)await inspect(at);status.textContent='Recorded events retain their original UTC and identities. An interrupted attempt cannot receive an automatic pass.';}catch(error){status.textContent=error.message;}
}
