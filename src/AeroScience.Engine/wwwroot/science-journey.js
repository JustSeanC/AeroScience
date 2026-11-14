import {message as localizedCopy,setPresentationText} from './i18n.js';
import {node,link,button,informationDetails,scientificNotice} from './workstation-ui.js';
import {researchCardTargets} from './personal-research-start.js';
import {currentFlightPresentation} from './current-flight-state.js';
import {t as textForUI,t} from './i18n.js';

// A view of existing recording/collection controllers, never a recorder or evaluator.
export function scienceJourney(parent,api){
 const root=node('section',undefined,'science-journey');root.id='science-journey';setPresentationText(root,localizedCopy("ui.scienceJourney.messageYourResearchFlight"),"aria-label");
 const title=node('h2',localizedCopy("ui.scienceJourney.messageReadyToStart")),steps=node('ol'),summary=node('p'),connection=node('p'),recording=node('p'),collection=node('p'),count=node('p'),next=node('p'),actions=node('div',undefined,'action-row'),results=node('div',undefined,'action-row');
 const name=node('h3',undefined,'current-flight-name'),aircraft=node('p',undefined,'current-flight-aircraft'),session=node('p',undefined,'current-flight-session'),payload=node('p',undefined,'current-flight-payload');
 const statuses=node('div',undefined,'current-flight-statuses');statuses.append(recording,collection);
 recording.dataset.flightRecording='';collection.dataset.flightScience='';connection.dataset.flightConnection='';session.dataset.flightSession='';
 // The existing collection controller moves its own command buttons here.
 // This is one command authority, not another recorder or optimistic shortcut.
  const collectionControls=node('div');collectionControls.id='science-journey-controls';collectionControls.setAttribute('role','group');setPresentationText(collectionControls,localizedCopy("ui.scienceJourney.messageScienceCollectionActions"),"aria-label");
 for(const label of [textForUI("ui.personalResearchStart.messageChooseResearch"),textForUI("ui.scienceJourney.messagePrepareForFlight"),textForUI("flight.action.record"),textForUI("ui.scienceJourney.messageCollectScience"),textForUI("ui.missionOperations.messageReviewResults")])steps.append(node('li',label));
 recording.setAttribute('role','status');collection.setAttribute('role','status');count.dataset.retainedScience='';
 const explanation=informationDetails(root,localizedCopy("ui.scienceJourney.messageRecordingSourceCountDetails"),node('p',localizedCopy("ui.scienceJourney.messageFlightSamplesAreRecordedAircraftTelemetryOriginal")));
 root.prepend(title,name,aircraft,steps,summary,connection,statuses,session,payload,count,next,collectionControls,actions,results);parent.prepend(root);
 link(actions,localizedCopy("ui.personalResearchStart.messageChooseResearch"),'#my-research');const instruments=link(actions,localizedCopy("ui.scienceJourney.messageReviewInstruments"),'#science');
  const show=button(actions,localizedCopy("ui.scienceJourney.messageScienceDetails"),()=>{const panel=document.getElementById('mission-intelligence');panel?.scrollIntoView({block:'center'});const details=panel?.querySelector('details');if(details){details.open=true;details.querySelector('summary')?.focus({preventScroll:true});}});
 let flight=null,intelligence=null,plan=null,card=null,last=null,stale=false,recovered=false,recoveryEpoch=0;
 function paint(){
  const cockpit=currentFlightPresentation(intelligence,flight,{stale});
  const active=flight?.activeMission,telemetry=flight?.telemetry,age=telemetry?Date.now()-Date.parse(telemetry.timestampUtc):Infinity;
  const fresh=flight?.connection?.state==='connected'&&telemetry?.isValid!==false&&age>=-2000&&age<=6000;
  const same=intelligence?.flight?.activeMission?.id===active?.id&&active;
  const state=same&&!stale?intelligence.collection?.state:null,enabled=(same?intelligence.flight.research?.instruments:(plan?.instruments??flight?.research?.instruments))||[];
  const needed=enabled.filter(i=>i.enabled),fault=same&&intelligence.collection?.fault;
  setPresentationText(payload,localizedCopy('flight.enabledInstruments',{names:needed.length?needed.map(i=>intelligence?.sensorOperating?.instruments?.find(s=>s.instrumentId===i.instrumentId)?.name||i.instrumentId).join(', '):t('flight.noneEnabled')}));
  const unavailable=active&&(!fresh||telemetry?.isPaused||stale||!state||fault||!cockpit.fresh||['unknown','unavailable'].includes(cockpit.science));
  setPresentationText(title,active?localizedCopy("ui.personalResearchStart.messageYourResearchFlight"):last?localizedCopy("ui.scienceJourney.messageFlightSavedReviewResults"):localizedCopy("ui.scienceJourney.messagePrepareForFlight"));
  setPresentationText(name,active?.name||plan?.name||'');
  setPresentationText(aircraft,localizedCopy('flight.aircraftElapsed',{aircraft:telemetry?.aircraft||active?.aircraft||t('common.unavailable'),elapsed:cockpit.elapsedSeconds===null?t('common.unavailable'):t('flight.elapsed',{seconds:cockpit.elapsedSeconds})}));
  setPresentationText(session,cockpit.sessionId?localizedCopy('flight.session',{sequence:cockpit.sessionSequence,id:cockpit.sessionId}):localizedCopy('flight.noSession'));
  root.dataset.connection=cockpit.connection;root.dataset.recording=cockpit.recording;root.dataset.science=cockpit.science;root.dataset.sessionId=cockpit.sessionId||'';
  setPresentationText(summary,card?(card.atmosphericProfile?.question||card.atmosphericTransect?.question||card.survey?.question||card.observationFlight?.question||card.experimentalSensor?.question||card.name):plan?plan.name:localizedCopy("ui.scienceJourney.messageChooseAStarterOrSelectASaved"));
  setPresentationText(connection,localizedCopy('flight.connection.'+cockpit.connection)+` · ${flight?.connection?.source==='simulated'?'Synthetic test fixture':fresh?'Simulator-reported observations':'Simulator unavailable'}`);
  setPresentationText(recording,active?localizedCopy("ui.scienceJourney.messageRecordingRetainedFlightSamples",{value1:(String(active.sampleCount))}):localizedCopy("ui.scienceJourney.messageNotRecording"));
  const phases={armed:textForUI("ui.scienceJourney.messageArmedScienceHasNotStarted"),collecting:textForUI("home.science.collecting"),paused:textForUI("home.science.paused"),betweenSessions:textForUI("ui.scienceJourney.messageSessionEndedReadyForAnotherSession"),stopped:textForUI("home.science.stopped")};
  setPresentationText(collection,localizedCopy("ui.scienceJourney.messageScienceCollection",{value1:(String(!active?'Not started':unavailable?'Unavailable — check the connection, session or storage hold':phases[state.phase]||'Unavailable'))}));
  const retained=same?intelligence.retainedScience:null;
  setPresentationText(count,!active?'':retained?.missionId===active.id&&Number.isFinite(retained.originalObservations)?localizedCopy("ui.scienceJourney.messageRetainedOriginalScientificObservations",{value1:(String(retained.originalObservations)),value2:(String(stale?' · last checked count':'')),value3:(String(t('flight.retainedProducts',{products:retained.remoteProducts??t('common.unavailable'),derived:retained.derivedObservations??t('common.unavailable')})))}):localizedCopy("ui.scienceJourney.messageRetainedScientificObservationsUnavailable"));
  let retainedDetail=explanation.querySelector('[data-derived-counts]');if(!retainedDetail){retainedDetail=node('p');retainedDetail.dataset.derivedCounts='';explanation.append(retainedDetail);}
  setPresentationText(retainedDetail,retained&&active&&retained.missionId===active.id?localizedCopy("ui.scienceJourney.messageModeledDerivedObservationsSavedRemoteProducts",{value1:(String(retained.derivedObservations??'Unavailable')),value2:(String(retained.remoteProducts??'Unavailable')),value3:(String(stale?'Last checked count; connection is stale.':''))}):'');
  const requiredScience=!!(card?.atmosphericProfile||card?.atmosphericTransect||card?.survey||card?.experimentalSensor);
  const starts=(intelligence?.triggers||[]).filter(t=>t.status==='Configured'&&t.trigger.action==='startData');
  const waiting=[...new Set(starts.map(t=>({waypoint:'a waypoint crossing',directionalGate:'a directional gate',areaEntry:'entry into a survey region',areaExit:'exit from a survey region'}[t.trigger.kind]||'a configured Start condition')))];
  const startAdvice=state?.authority==='automatic'&&!state.manualAutomationHold?waiting.length?` Automatic collection is waiting for ${waiting.join(' or ')}. You can start manually below.`:' No automatic Start is configured. Use Start Science Collection below.':' Use Start Science Collection below.';
  if(active&&fault)scientificNotice(next,'important',{problem:localizedCopy("ui.scienceJourney.messageReviewTheCollectionProblemInScienceControls")});
  else if(active&&(!fresh||telemetry?.isPaused))scientificNotice(next,'critical',{problem:fault?localizedCopy("ui.scienceJourney.messageScientificStorageOrCollectionIsUnavailable"):telemetry?.isPaused?localizedCopy("ui.scienceJourney.messageMSFSIsPaused"):localizedCopy("ui.scienceJourney.messageAircraftTelemetryIsStaleOrUnavailable"),consequence:localizedCopy("ui.scienceJourney.messageNewQualifyingScienceCannotBeConfirmed"),action:fault?localizedCopy("ui.scienceJourney.messageReviewScienceControlsAndResolveTheHold"):localizedCopy("ui.scienceJourney.messageLoadOrUnpauseTheAircraftAndWait")});
  else if(active&&stale)scientificNotice(next,'important',{problem:localizedCopy("ui.scienceJourney.messageCollectionStatusIsUnavailableReviewScienceControls")});
  else if(active&&state&&!needed.length)scientificNotice(next,requiredScience?'critical':'important',{problem:localizedCopy("ui.scienceJourney.messageNoInstrumentsEnabled"),consequence:requiredScience?localizedCopy("ui.scienceJourney.messageThisResearchCannotCollectItsRequiredMeasurements"):localizedCopy("ui.scienceJourney.messageFlightRecordingContinuesWithoutScientificCollection"),action:localizedCopy("ui.scienceJourney.messageChooseInstrumentsToReviewYourPayload")});
  else if(active&&!state)scientificNotice(next,'important',{problem:localizedCopy("ui.scienceJourney.messageCollectionStatusIsUnavailable"),consequence:localizedCopy("ui.scienceJourney.messageCurrentScienceCollectionIsUnconfirmed"),action:localizedCopy("ui.scienceJourney.messageCheckTheCompanionConnectionBeforeIssuingA")});
  else scientificNotice(next,'important',{problem:!active?plan?!fresh?localizedCopy("ui.scienceJourney.messageMissionPreparedOfflineConnectMSFSAnd"):telemetry?.isPaused?localizedCopy("ui.scienceJourney.messageMissionPreparedUnpauseMSFSBeforeStart"):localizedCopy("ui.scienceJourney.messageMissionPreparedStartRecordingNext"):localizedCopy("ui.scienceJourney.messageChooseResearchOrSelectAMissionBelow"):state?.phase==='armed'?localizedCopy("ui.scienceJourney.messageScienceCollectionHasNotStarted",{value1:(String(startAdvice))}):state?.phase==='betweenSessions'?localizedCopy("ui.scienceJourney.messageScienceSessionEndedStartScienceCollectionFor"):state?.phase==='paused'?localizedCopy("ui.scienceJourney.messageCollectionPausedUseResumeScienceCollection"):retained?.originalObservations===0?localizedCopy("ui.scienceJourney.messageWaitingForTheFirstSavedScientificObservation"):localizedCopy("ui.scienceJourney.messageEndMissionWhenFinishedThenOpenReview")});
  let preparation=explanation.querySelector('[data-preparation-details]');if(!preparation){preparation=node('p');preparation.dataset.preparationDetails='';explanation.append(preparation);}setPresentationText(preparation,localizedCopy("ui.scienceJourney.messageInstrumentSEnabled",{value1:(String(needed.length)),value2:(String(card?.atmosphericProfile||card?.atmosphericTransect||card?.experimentalSensor?'Onboard atmospheric observations need no NASA download, organization or cloud account.':'Review the saved instrument and source requirements.')),value3:(String(card?' '+researchCardTargets(card):''))}));
  results.hidden=!!active;show.hidden=!active;setPresentationText(instruments,needed.length?localizedCopy("ui.scienceJourney.messageReviewInstruments"):localizedCopy("ui.scienceJourney.messageChooseInstruments"));
  const step=active?3:last?4:plan?2:0;[...steps.children].forEach((item,i)=>{if(i===step)item.setAttribute('aria-current','step');else item.removeAttribute('aria-current');});
  const identity=last?.id||'';if(results.dataset.flight!==identity){results.dataset.flight=identity;results.replaceChildren();if(last){results.append(node('p',localizedCopy("ui.scienceJourney.messageSavedFlight",{value1:(String(last.name))})));link(results,localizedCopy("ui.missionOperations.messageReviewResults"),`#workspace/map?missions=${last.id}`);link(results,localizedCopy("ui.missionTimeline.messageOpenScientificAnalysis"),`#workspace/analysis?missions=${last.id}`);link(results,localizedCopy("ui.scienceJourney.messageOriginalEvidenceTimeline"),`#operations?flight=${last.id}`);}}
 }
 async function recover(){if(recovered)return;recovered=true;const own=recoveryEpoch;try{const rows=await api('/api/missions?limit=1');if(own===recoveryEpoch&&!flight?.activeMission&&rows[0]?.status!=='recording'){last=rows[0]||null;paint();}}catch{if(own===recoveryEpoch)recovered=false;}}
 // An explicit preparation/selection starts a new task. Late history is reference data,
 // never authority to replace that intent. Original flights remain in the existing archive.
 window.addEventListener('aeroscience-selection',()=>{recoveryEpoch++;recovered=true;last=null;paint();});
 window.addEventListener('aeroscience-intelligence',event=>{intelligence=event.detail;stale=false;paint();});
 window.addEventListener('aeroscience-intelligence-stale',()=>{stale=true;paint();});
 return {selection(p,c){plan=p;card=c;paint();},update(value){if(flight?.activeMission&&!value.activeMission)last=flight.activeMission;flight=value;paint();if(!value.activeMission)recover();}};
}
