import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {organizationAssignmentSummary} from './organization-assignment-ui.js';
import {openPilotObservations} from './observation-research.js';
import {researchCardTargets} from './personal-research-start.js';
import {node,button,newIdentity,table} from './workstation-ui.js';
import {missionEvidenceTimeline} from './mission-timeline.js';
import {attemptView} from './personal-research.js';

const attemptOutcome=value=>({pass:textForUI("ui.scienceAnalysis.messagePass"),fail:textForUI("ui.campaignProgressUi.messageFail"),insufficientEvidence:textForUI("ui.campaignProgressUi.messageInsufficientEvidence")}[value]||textForUI("ui.campaignProgressUi.messageInsufficientEvidence"));
const readableCardMessage=value=>value.replace(/ \[manual:[^\]]+\]/g,'').replace(/ \[detection:[^\]]+\]/g,'').replace(/ \[point:[0-9a-f-]{36}; attempt:[0-9a-f-]{36}\]$/i,'');

export function researchCardSelection(parent,api,references,changed){
 const selected=new Map(references.map(ref=>[ref.id,ref]));let offset=0,epoch=0;
 const section=node('section');section.id='plan-research-cards';section.append(node('h3',localizedCopy("ui.personalResearchFlight.messagePersonalResearchTestCards")),node('p',localizedCopy("ui.personalResearchFlight.messageChooseCardsBeforeRecordingEachFlightFreezes")));
 const chosen=node('div'),catalog=node('div'),status=node('p');status.setAttribute('role','status');section.append(chosen,catalog,status);parent.append(section);
 const drawChosen=()=>{chosen.replaceChildren(node('strong',localizedCopy("ui.personalResearchFlight.messageCardsSelectedForThisFlight",{value1:(String(selected.size))})));for(const ref of selected.values()){
  const row=node('div',undefined,'action-row');row.append(node('span',localizedCopy("ui.personalResearchFlight.messageRevision",{value1:(String(ref.name||'Saved test card')),value2:(String(ref.revision))})));button(row,localizedCopy("ui.personalResearchFlight.messageRemoveCard"),()=>{selected.delete(ref.id);changed();drawChosen();});chosen.append(row);
 }};
 async function load(){const own=++epoch;try{const cards=await api(`/api/research/cards?offset=${offset}`);if(own!==epoch||!section.isConnected)return;catalog.replaceChildren();
  for(const card of cards){const row=node('div',undefined,'plan-row');row.append(node('span',localizedCopy("ui.calibrationSettings.messageRevision",{value1:(String(card.name)),value2:(String(card.revision)),value3:(String(researchCardTargets(card)))})));button(row,localizedCopy("ui.personalResearchFlight.messageAddCard"),()=>{if(selected.size>=16&&!selected.has(card.id))throw Error(textForUI("ui.personalResearchFlight.messageChooseAtMost16CardsForThis"));selected.set(card.id,{id:card.id,revision:card.revision,name:card.name});changed();drawChosen();});catalog.append(row);}
  button(catalog,localizedCopy("ui.personalResearchFlight.messageNewerTestCards"),()=>{offset=Math.max(0,offset-20);return load();});button(catalog,localizedCopy("ui.personalResearchFlight.messageOlderTestCards"),()=>{offset+=20;return load();});setPresentationText(status,cards.length?localizedCopy("ui.personalResearchFlight.messageSavedPersonalCardsDifferentTargetRangesMay"):localizedCopy("ui.personalResearchFlight.messageNoSavedCardsOnThisPageCreate"));
 }catch(error){setPresentationText(status,error.message);}}
 for(const ref of references)api(`/api/research/cards/${ref.id}?revision=${ref.revision}`).then(card=>{if(selected.get(ref.id)?.revision===ref.revision){selected.set(ref.id,{...ref,name:card.name});drawChosen();}}).catch(error=>setPresentationText(status,error.message));
 drawChosen();load();const read=()=>[...selected.values()].map(({id,revision})=>({id,revision}));read.replaceReference=card=>{if(selected.has(card.id)){selected.set(card.id,{id:card.id,revision:card.revision,name:card.name});drawChosen();}};return read;
}

export function researchFlightControls(parent,api,{shared=false,onFocus=()=>{},onChange=()=>{}}={}){
 const panel=node('section',undefined,'panel research-panel');panel.id='research-flight-controls';panel.append(node('h2',localizedCopy("ui.personalResearchFlight.messagePersonalResearchTestCards")));
 const status=node('p');status.setAttribute('role','status');const rows=node('div');panel.append(status,rows);parent.append(panel);
 let flight=null,state=null,busy=false,pending=false,lastRead=0,epoch=0,signature='',lastCommand=null,readController;const controls=new Map();
 function apply(next){
  if(next.flightId!==flight||state&&next.revision<state.revision)return;state=next;
  const key=next.cards.map(c=>c.card.id).join('/');if(key!==signature){signature=key;rows.replaceChildren();controls.clear();
   for(const selected of next.cards){const card=selected.card,row=node('article',undefined,'research-panel');row.dataset.cardId=card.id;setPresentationText(row,card.name,"aria-label");row.append(node('h3',localizedCopy("ui.personalResearchFlight.messageRevision",{value1:(String(card.name)),value2:(String(card.revision))})));row.tabIndex=-1;row.append(organizationAssignmentSummary(card));button(row,localizedCopy("ui.missionOperations.messageFocusCard"),()=>{row.focus();onFocus(card.id);});
    if(card.procedure||card.version===7)row.append(node('p',localizedCopy("ui.personalResearchFlight.messageAutomaticDetectionIsOptInForTheChosen")));
    const info=node('p'),actions=node('div',undefined,'action-row');info.setAttribute('role','status');row.append(info,actions);const buttons={};let pointSelect=null;
    if(card.procedure){const label=node('label',localizedCopy("ui.personalResearchFlight.messageTestPoint")),select=node('select');setPresentationText(select,localizedCopy("ui.personalResearchFlight.messageTestPointFor",{value1:(card.name)}),"aria-label");for(const point of card.procedure.points){const option=node('option',point.conditions.testPoint.name);option.value=point.id;select.append(option);}pointSelect=select;label.append(select);row.insertBefore(label,actions);}
    for(const [action,label] of [['activate',textForUI("ui.personalResearchFlight.messageActivateCard")],['deactivate',textForUI("ui.personalResearchFlight.messageDeactivateCard")],['beginAttempt',textForUI("ui.personalResearchFlight.messageBeginAttempt")],['endAttempt',textForUI("ui.personalResearchFlight.messageEndAttempt")],...((card.version===7&&card.flightRegime?.testPoint)||card.procedure?[['enableAutomatic',textForUI("ui.personalResearchFlight.messageEnableAutomaticDetection")],['disableAutomatic',textForUI("ui.personalResearchFlight.messageDisableAutomaticDetection")]]:[])]){
     const b=node('button',label,action==='beginAttempt'?'primary':'secondary');b.type='button';buttons[action]=b;
     b.onclick=async()=>{if(busy||!state)return;const current=state.cards.find(c=>c.card.id===card.id);if(!current)return;
      const input={version:1,flightId:flight,expectedRevision:state.revision,action,cardId:card.id,expectedAttemptId:current.openAttempt?.id||null,...(['beginAttempt','enableAutomatic'].includes(action)&&pointSelect?{testPointId:pointSelect.value}:{})};const key=JSON.stringify(input);
      if(lastCommand?.key!==key)lastCommand={key,body:{...input,requestId:newIdentity()}};
      const own=epoch;busy=true;refreshButtons();
      try{const result=await api('/api/research/control',lastCommand.body);if(own===epoch){apply(result.state);setPresentationText(status,readableCardMessage(result.receipt.message));}}
      catch(error){if(own===epoch){setPresentationText(status,error.message);lastRead=0;}}
      finally{if(own===epoch){busy=false;refreshButtons();}}
     };actions.append(b);
    }if(card.observationFlight)button(actions,localizedCopy("ui.personalResearchFlight.messageRecordPilotObservation"),openPilotObservations,'primary');controls.set(card.id,{info,buttons,pointSelect,lastNext:null});rows.append(row);
   }
  }
  refreshButtons();onChange(next);
 }
 function refreshButtons(){if(!state)return;for(const card of state.cards){const view=controls.get(card.card.id);if(!view)continue;
  if(view.pointSelect){const next=card.openAttempt?.testPointId||card.procedure?.nextPointId;if(next&&next!==view.lastNext){view.pointSelect.value=next;view.lastNext=next;}view.pointSelect.disabled=busy||!!card.openAttempt;}
  setPresentationText(view.info,`${card.openAttempt?'Attempt recording since '+card.openAttempt.startUtc:card.active?'Active — ready for an attempt':'Inactive'} · ${card.detection?`${card.detection.status}${card.detection.lastOutcome?' — '+attemptOutcome(card.detection.lastOutcome):''} · ${card.detection.message} · `:''}${researchCardTargets(card.card)} ${card.activationBlocked||''}${card.procedure?' · Provisional repetitions: '+card.procedure.points.map(p=>p.completedRepetitions+'/'+p.requiredRepetitions).join(' · ')+'. Verify original evidence after flight.':''}`);
  for(const [action,b]of Object.entries(view.buttons))b.disabled=busy||!state.recording||action==='activate'&&(card.active||!!card.activationBlocked)||action==='deactivate'&&!card.active||action==='beginAttempt'&&(!card.active||!!card.openAttempt&&!card.openAttempt.automatic||!state.canBeginAttempt)||action==='endAttempt'&&!card.openAttempt||action==='enableAutomatic'&&(!card.active||!!card.openAttempt||!state.canBeginAttempt)||action==='disableAutomatic'&&!card.detection?.enabled;
 }}
 function update(snapshot){
  const id=snapshot.activeMission?.id||null;if(id!==flight){epoch++;readController?.abort();pending=false;flight=id;state=null;signature='';controls.clear();rows.replaceChildren();busy=false;lastRead=0;setPresentationText(status,id?localizedCopy("ui.personalResearchFlight.messageReadingSelectedFlightCards"):localizedCopy("ui.personalResearchFlight.messageChooseTestCardsInMissionPlannerBefore"));}
  if(shared||!id||pending||!panel.offsetParent||document.hidden||Date.now()-lastRead<1500)return;
  const own=epoch;pending=true;lastRead=Date.now();const controller=new AbortController();readController=controller;const timeout=setTimeout(()=>controller.abort(),6000);api(`/api/missions/${id}/research-operations`,undefined,{signal:controller.signal}).then(next=>{if(own!==epoch)return;apply(next);if(!next.cards.length)setPresentationText(status,localizedCopy("ui.personalResearchFlight.messageNoPersonalCardsWereSelectedForThis"));else if(!next.canBeginAttempt)setPresentationText(status,localizedCopy("ui.personalResearchFlight.messageBeginAttemptWaitsForFreshUnpausedAircraft"));}).catch(error=>{if(own===epoch){setPresentationText(status,error.name==='AbortError'?localizedCopy("ui.personalResearchFlight.messageCardControlsAreWaitingForSynchronizationFlight"):error.message);for(const c of controls.values())for(const b of Object.values(c.buttons))b.disabled=true;}}).finally(()=>{clearTimeout(timeout);if(own===epoch)pending=false;});
 }
 return {update,feed(snapshot,next){update(snapshot);if(next&&next.flightId===flight){apply(next);setPresentationText(status,next.cards.length?localizedCopy("ui.personalResearchFlight.messageSynchronizedFrozenResearchCardsActivationFocusAttempts"):localizedCopy("ui.personalResearchFlight.messageNoCardsSelectedForThisFlight"));}},stale(message){setPresentationText(status,localizedCopy("ui.personalResearchFlight.messageCardControlsUnavailableOrStale",{value1:(String(message))}));for(const c of controls.values())for(const b of Object.values(c.buttons))b.disabled=true;}};
}

export async function researchFlightTimeline(parent,api,flight,{attemptId,at,original}={}){
 const root=node('section',undefined,'panel research-panel');setPresentationText(root,localizedCopy("ui.personalResearchFlight.messageTestCardActivityAndEvidence"),"aria-label");root.append(node('h2',localizedCopy("ui.personalResearchFlight.messageTestCardActivityEvidenceTimeline")),node('p',localizedCopy("ui.personalResearchFlight.messageActualHostUTCEventsAndOriginalEvidence")));parent.append(root);
 const status=node('p');status.setAttribute('role','status');root.append(status);const [state]=await Promise.all([api(`/api/missions/${flight}/research-operations`)]);if(!root.isConnected)return;
 let selected=attemptId?await api(`/api/research/recorded-attempts/${attemptId}`):null;
 if(selected&&selected.flightId!==flight)throw Error(textForUI("ui.personalResearchFlight.messageTheSelectedAttemptBelongsToAnotherFlight"));
 let offset=0;const attempts=node('div');root.append(attempts);const timeline=missionEvidenceTimeline(root,api);timeline.setFlight(flight,{recording:state.recording,cards:state.cards});
 async function loadAttempts(){const rows=selected?[selected]:await api(`/api/missions/${flight}/research-attempts?offset=${offset}`);if(!root.isConnected)return;attempts.replaceChildren(node('h3',localizedCopy("ui.personalResearchFlight.messageRecordedAttempts")));
  if(!rows.length)attempts.append(node('p',localizedCopy("ui.personalResearchFlight.messageNoMarkedAttemptsOnThisPageExisting")));
  for(const attempt of rows){const card=state.cards.find(c=>c.card.id===attempt.cardId)?.card;const label={recording:textForUI("html.index.textRecording"),recorded:textForUI("ui.personalResearchFlight.messageRecorded"),interrupted:textForUI("html.index.textInterrupted"),finalizing:textForUI("ui.personalResearchFlight.messageVerifyingOriginalEvidence")}[attempt.status]||textForUI("localization.unknownValue");const row=node('article');row.append(node('strong',localizedCopy("ui.personalResearchFlight.messageRevision29a8eae3",{value1:(String(attempt.automatic?'Automatic':'Manual')),value2:(String(card?.name||'Personal test card')),value3:(String(attempt.cardRevision)),value4:(String(label))})),node('p',localizedCopy("ui.personalResearchFlight.messageEvents",{value1:(String(attempt.startUtc)),value2:(String(attempt.endUtc||'Attempt still recording')),value3:(String(attempt.endReason||'Explicit attempt boundaries')),value4:(String(attempt.beginEventId)),value5:(String(attempt.endEventId??'pending'))})));
   if(attempt.testPointId)row.append(node('p',localizedCopy("ui.personalResearchFlight.messageTestPointLongestSupportedHold",{value1:(String(card?.procedure?.points.find(p=>p.id===attempt.testPointId)?.conditions.testPoint.name||card?.flightRegime?.testPoint?.name||'Unknown point')),value2:(String(attemptOutcome(attempt.pointEvidence?.outcome))),value3:(String(attempt.pointEvidence?.longestHoldSeconds||0)),value4:(String(card?.procedure?'Review Multi-Point Flight verifies originals and the complete procedure.':'Inspect Custom Test Evidence verifies this original interval.'))})));
   if(attempt.endUtc&&!state.recording){const command=newIdentity();button(row,localizedCopy("ui.personalResearchFlight.messageEvaluateAttempt"),async()=>{const result=await api(`/api/research/recorded-attempts/${attempt.id}/evaluate`,{requestId:command});if(row.isConnected){row.querySelector('.attempt-result')?.remove();const shown=attemptView(result,api);shown.classList.add('attempt-result');row.append(shown);}});}
   button(row,localizedCopy("ui.personalResearchFlight.messageInspectOriginalAttempt"),()=>timeline.inspectOriginal('flightEvent',attempt.beginEventId));attempts.append(row);
  }if(selected)button(attempts,localizedCopy("ui.personalResearchFlight.messageShowAllFlightAttempts"),()=>{selected=null;return loadAttempts();});else{button(attempts,localizedCopy("ui.personalResearchFlight.messageNewerAttemptPage"),()=>{offset=Math.max(0,offset-20);return loadAttempts();});button(attempts,localizedCopy("ui.personalResearchFlight.messageOlderAttemptPage"),()=>{offset+=20;return loadAttempts();});}
 }
 if(!state.recording&&state.cards.some(c=>c.detection)){const requestId=newIdentity();button(root,localizedCopy("ui.personalResearchFlight.messageRecoverAutomaticAttempts"),async()=>{await api(`/api/missions/${flight}/research-detection/recover`,{requestId});setPresentationText(status,localizedCopy("ui.personalResearchFlight.messageOriginallyArmedWindowsQueuedForBoundedReplay"));});}
 try{await loadAttempts();if(original?.id)await timeline.inspectOriginal(original.source,original.id);else if(original?.productId)root.append(node('p',localizedCopy("ui.personalResearchFlight.messageOriginalScientificProductIsNotAScalar",{value1:(String(original.productId))})));else if(selected)await timeline.inspectOriginal('flightEvent',selected.beginEventId);else if(at)setPresentationText(status,localizedCopy("ui.personalResearchFlight.messageUTCContextSuppliedSelectAnOriginalEvent"));setPresentationText(status,localizedCopy("ui.personalResearchFlight.messageRecordedEventsRetainTheirOriginalUTCAnd"));}catch(error){setPresentationText(status,error.message);}
}
