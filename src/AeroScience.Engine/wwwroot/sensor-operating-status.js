import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node, dateTime,informationDetails,scientificNotice} from './workstation-ui.js';

const operatingLabels={get withinCheckedLimits(){return textForUI("ui.missionOperations.messageWithinCheckedLimits");},get caution(){return textForUI("ui.missionOperations.messageCaution");},get cannotAssess(){return textForUI("ui.missionOperations.messageCannotAssess");},get notCollecting(){return textForUI("ui.missionOperations.messageNotCollecting");},get disabled(){return textForUI("ui.research.messageDisabled");}};
const dataLabels={get ready(){return textForUI("ui.missionOperations.messageReady");},get partiallyAvailable(){return textForUI("ui.missionOperations.messagePartiallyAvailable");},get unavailable(){return textForUI("localization.unavailableValue");},get pending(){return textForUI("evidence.pending");},get unknown(){return textForUI("localization.unknownValue");}};
const views=new WeakMap();
const text=(element,value)=>{if(element.textContent!==value)setPresentationText(element,value);};

// Both pages consume the same typed backend projection. No rule/message interpretation here.
export function renderSensorOperating(parent,overview,{compact=false,stale=null}={}){
 let view=views.get(parent);
 if(!view){
  parent.classList.add('sensor-operating-panel');setPresentationText(parent,localizedCopy("ui.sensorOperatingStatus.messageSensorOperatingStatus"),"aria-label");
  const heading=node('h2',localizedCopy("ui.sensorOperatingStatus.messageSensorOperatingStatus")),notice=node('p'),cards=node('div',undefined,'sensor-operating-grid'),disabled=node('details'),disabledTitle=node('summary',localizedCopy("ui.sensorOperatingStatus.messageDisabledInstruments")),disabledCards=node('div',undefined,'sensor-operating-grid');
  disabled.append(disabledTitle,disabledCards);parent.replaceChildren(heading,notice,cards,disabled);informationDetails(parent,localizedCopy("ui.sensorOperatingStatus.messageAboutOperatingAssessments"),node('p',localizedCopy("ui.sensorOperatingStatus.messageSupportedOperatingChecksAreSeparateFromScientific")));
  view={notice,cards,disabled,disabledCards,disabledTitle,items:new Map(),timer:null};views.set(parent,view);
 }
 clearTimeout(view.timer);view.compact=compact;
 parent.classList.toggle('sensor-operating-compact',compact);
 if(!overview||overview.version!==1){const message=stale||textForUI("ui.sensorOperatingStatus.messageSensorStatusIsUnavailableOrUnsupportedNo");if(view.overview?.version===1){renderSensorOperating(parent,view.overview,{compact,stale:message});return;}text(view.notice,message);return;}
 view.overview=overview;
 const age=Date.now()-Date.parse(overview.generatedUtc);
 if(!stale&&(age>6000||age< -2000))stale=textForUI("ui.sensorOperatingStatus.messageStatusResponseIsStaleReconnectAndWait");
 text(view.notice,stale||'');view.notice.hidden=!stale;
 const remaining=new Set(view.items.keys());let enabled=0,disabledCount=0;
 for(const card of overview.instruments){
  let item=view.items.get(card.instrumentId);
  if(!item){
   const root=node('article',undefined,'sensor-operating-card'),name=node('h3'),profile=node('p'),session=node('p'),operating=node('strong'),issue=node('p'),guidance=node('p'),uncertainty=node('p',undefined,'sensor-operating-uncertainty'),data=node('strong'),source=node('p'),acquisition=node('p'),time=node('p',undefined,'footnote'),state=node('strong'),counts=node('p'),alert=node('p');
   root.dataset.sensor=card.instrumentId;setPresentationText(root,card.name,"aria-label");
   const opBlock=node('section'),dataBlock=node('section');setPresentationText(opBlock,localizedCopy("ui.sensorOperatingStatus.messageOperatingConditions8c606062",{value1:(card.name)}),"aria-label");setPresentationText(dataBlock,localizedCopy("ui.sensorOperatingStatus.messageScientificData896b52d8",{value1:(card.name)}),"aria-label");opBlock.append(operating,issue,guidance);dataBlock.append(data,source,acquisition);
   const advice=node('ul');
   const scientific=node('details');scientific.append(node('summary',localizedCopy("ui.operatingAssessments.messageSensorDetails")),profile,session,opBlock,dataBlock,time,uncertainty,advice);
   const details=node('details'),summary=node('summary',localizedCopy("ui.operatingAssessments.messageAdvancedDiagnostics")),technical=node('div');details.append(summary,technical);
   root.append(name,state,counts,alert,scientific,details);item={root,name,profile,session,operating,issue,guidance,uncertainty,advice,data,source,acquisition,time,details,technical,state,counts,alert};view.items.set(card.instrumentId,item);
  }
  remaining.delete(card.instrumentId);const holder=card.enabled?view.cards:view.disabledCards;if(item.root.parentNode!==holder)holder.append(item.root);if(card.enabled)enabled++;else disabledCount++;
  const status=stale&&card.operatingStatus!=='disabled'&&card.operatingStatus!=='notCollecting'?'cannotAssess':card.operatingStatus;
  const remote=['thermal-imager','terrain-scanner','multispectral-imager','hyperspectral-imager'].includes(card.instrumentId),d=card.acquisition;
  text(item.state,stale?localizedCopy("home.science.unknown"):!card.enabled?localizedCopy("ui.research.messageDisabled"):card.collectionState.split(' — ')[0]);
  text(item.counts,localizedCopy("ui.sensorOperatingStatus.messageRejectedMissedIntervals",{value1:(String(remote?'Saved products':'Measurements')),value2:(String(d?.observationCount??'Unavailable')),value3:(String(d?.rejectedObservations??'Unavailable')),value4:(String(d?.missingIntervals??'Unavailable'))}));
  item.root.dataset.operating=status;text(item.name,card.name);text(item.profile,localizedCopy("ui.sensorOperatingStatus.messageReferenceProfile",{value1:(String(card.profileName)),value2:(String(card.profileVersion==null?'':` · version ${card.profileVersion}`))}));
  text(item.session,localizedCopy("ui.sensorOperatingStatus.messageCollection",{value1:(String(stale?'Last known — ':'')),value2:(String(card.collectionState)),value3:(String(card.sessionId?' · Current science session':''))}));
  text(item.operating,localizedCopy("ui.sensorOperatingStatus.messageOperatingConditions",{value1:(String(operatingLabels[status]||'Cannot Assess'))}));
  text(item.issue,stale?localizedCopy("ui.sensorOperatingStatus.messageLiveEvidenceIsNoLongerCurrentEarlier"):card.primaryIssue);
  text(item.guidance,stale?localizedCopy("ui.sensorOperatingStatus.messageCheckTheCompanionConnectionAndWaitFor"):card.guidance||'');item.guidance.hidden=!item.guidance.textContent;
  item.issue.hidden=!stale&&status==='withinCheckedLimits';if(!stale&&status==='withinCheckedLimits')item.guidance.hidden=true;
  item.root.dataset.scienceLevel=stale||status==='cannotAssess'||card.scientificData.status!=='ready'?'important':'information';
  const invalid=(card.report?.assessment?.rules||[]).some(r=>r.derivationValidity==='invalid'),collecting=card.enabled&&card.collectionState==='Collecting';
  if(collecting&&(invalid||card.scientificData.status==='unavailable')){scientificNotice(item.alert,'critical',{problem:invalid?localizedCopy("ui.sensorOperatingStatus.messageInvalidSensorGeometryOrCalibration"):localizedCopy("ui.sensorOperatingStatus.messageScientificSourceUnavailable"),consequence:localizedCopy("ui.missionOperations.messageAffectedMeasurementsCannotQualify"),action:invalid?localizedCopy("ui.sensorOperatingStatus.messageReviewSensorDetailsBeforeContinuingThisCollection"):localizedCopy("ui.sensorOperatingStatus.messagePrepareTheRequiredSourceOrReviewSensor")});item.root.dataset.scienceLevel='critical';}
  else scientificNotice(item.alert,'important',{problem:stale?localizedCopy("ui.sensorOperatingStatus.messageCurrentSensorStatusIsUnavailableReconnectThe"):''});
  text(item.uncertainty,[`${card.checkedRules} checked operating rules; ${card.unassessedRules} applicable rules unassessed.`,...(compact?['Other capabilities, including undocumented hardware limits and detector sampling, remain unassessed.']:(card.uncertainty||[]))].join(' '));
  const advice=JSON.stringify(stale?[]:card.additionalAdvice||[]);if(item.advice.dataset.key!==advice){item.advice.dataset.key=advice;item.advice.replaceChildren(...JSON.parse(advice).map(value=>node('li',value)));}item.advice.hidden=!item.advice.children.length;
  text(item.data,localizedCopy("ui.sensorOperatingStatus.messageScientificData",{value1:(String(stale?'Unknown':dataLabels[card.scientificData.status]||'Unknown'))}));
  text(item.source,stale?localizedCopy("ui.sensorOperatingStatus.messageThePreviousSourceAssessmentIsNotCurrent"):card.scientificData.explanation);
  text(item.acquisition,card.scientificData.acquisitionMessage?localizedCopy("ui.sensorOperatingStatus.messageAcquisitionContext",{value1:(String(card.scientificData.acquisitionMessage))}):'');item.acquisition.hidden=!item.acquisition.textContent;
  text(item.time,localizedCopy("ui.sensorOperatingStatus.messageLastAssessmentEvidence",{value1:(String(dateTime(card.assessedUtc))),value2:(String(stale?'stale':card.evidenceFresh?'fresh':'missing, stale or from before a collection change'))}));
  // Preserve the details/summary nodes and keyboard focus between telemetry refreshes.
  const key=JSON.stringify([card.report,card.configuration,card.evidenceFresh,stale]);
  if(item.technical.dataset.key!==key){item.technical.dataset.key=key;item.technical.replaceChildren();
   const c=card.configuration,r=card.report;
   item.technical.append(node('p',localizedCopy("ui.sensorOperatingStatus.messageEffectiveConfigurationRevisionEffective",{value1:(String(c?.id||'Unknown')),value2:(String(c?.revision??'Unknown')),value3:(String(dateTime(c?.effectiveUtc))),value4:(String(card.sessionId||'none'))})),node('p',localizedCopy("ui.sensorOperatingStatus.messageAssessmentOriginalTelemetry",{value1:(String(dateTime(card.assessedUtc))),value2:(String(dateTime(card.evidenceUtc))),value3:(String(stale?'stale':card.evidenceFresh?'fresh':'not current'))})));
   if(c){item.technical.append(node('p',localizedCopy("ui.sensorOperatingStatus.messageSelectedProfileVersionSaved",{value1:(String(card.profileName)),value2:(String(c.settings.payload?.profile.id||'no hardware profile')),value3:(String(card.profileVersion??'Unknown')),value4:(String(c.settings.intervalMs))})),node('p',localizedCopy("ui.sensorOperatingStatus.messageCalibrationRevisionAuthor",{value1:(String(c.calibration?.status||'unknown')),value2:(String(c.calibration?.id||'no definition')),value3:(String(c.calibration?.revision??'Unknown')),value4:(String(c.calibration?.author||'not supplied')),value5:(String(dateTime(c.calibration?.definedUtc)))})));for(const p of c.calibration?.parameters||[])item.technical.append(node('p',`${p.id}: ${p.value} ${p.unit}. ${p.basis}`));for(const value of [...(c.calibration?.references||[]),...(c.calibration?.limitations||[]),...(c.settings.payload?.profile.references||[])])item.technical.append(node('p',value));}
   if(!r)item.technical.append(node('p',localizedCopy("ui.sensorOperatingStatus.messageNoReportForThisSessionAndEffective")));
   else {item.technical.append(node('p',localizedCopy("ui.sensorOperatingStatus.messageConfigurationRecordedSession",{value1:(String(r.mode)),value2:(String(r.policy)),value3:(String(r.assessment.configurationId)),value4:(String(r.assessment.sessionId||'none')),value5:(String(r.limitation))})));
    const f=r.input.frame;if(f)item.technical.append(node('p',localizedCopy("ui.sensorOperatingStatus.messageOriginalAircraftInputFtAGLFt",{value1:(String(f.altitudeAglFeet??'Unknown')),value2:(String(f.altitudeMslFeet)),value3:(String(f.groundspeedKnots)),value4:(String(f.pitchDegrees)),value5:(String(f.bankDegrees)),value6:(String(f.latitude)),value7:(String(f.longitude)),value8:(String(dateTime(f.timestampUtc)))})));
    for(const rule of r.assessment.rules)item.technical.append(node('p',localizedCopy("ui.sensorOperatingStatus.messageOperatingDerivedValidity",{value1:(String(rule.parameter)),value2:(String(rule.observedValue??'Unknown')),value3:(String(rule.unit)),value4:(String(rule.compliance)),value5:(String(rule.derivationValidity)),value6:(String(rule.sourceQuality)),value7:(String(rule.rule.id)),value8:(String(rule.rule.revision)),value9:(String(rule.reasons.join(' ')))})));
    for(const evidence of r.assessment.evidence)item.technical.append(node('p',localizedCopy("ui.sensorOperatingStatus.messageEvidenceSHA256",{value1:(String(evidence.id)),value2:(String(evidence.origin)),value3:(String(dateTime(evidence.observedUtc))),value4:(String(evidence.sha256))})));
   }
  }
 }
 for(const id of remaining){view.items.get(id).root.remove();view.items.delete(id);}
 view.disabled.hidden=!disabledCount;text(view.disabledTitle,localizedCopy("ui.sensorOperatingStatus.messageDisabledInstrumentsc2fb26ce",{value1:(String(disabledCount))}));
 if(!enabled&&!stale)text(view.notice,localizedCopy("ui.sensorOperatingStatus.messageNoInstrumentsEnabledEnableAnInstrumentTo"));
 if(!stale){const expires=Math.min(Date.parse(overview.generatedUtc)+6000,...overview.instruments.filter(c=>c.evidenceFresh&&c.operatingStatus!=='notCollecting'&&c.enabled).flatMap(c=>[Date.parse(c.assessedUtc)+6000,Date.parse(c.evidenceUtc)+6000]));
  view.timer=setTimeout(()=>{if(parent.isConnected)markSensorOperatingStale(parent,textForUI("ui.sensorOperatingStatus.messageLiveSensorEvidenceHasExpired"));},Math.max(20,expires-Date.now()+20));}
}
export function markSensorOperatingStale(parent,message=textForUI("ui.sensorOperatingStatus.messageSensorStatusUnavailableOrStale")){
 const view=views.get(parent);renderSensorOperating(parent,view?.overview,{compact:view?.compact,stale:message});
}
