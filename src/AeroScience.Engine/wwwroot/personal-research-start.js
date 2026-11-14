import {message as localizedCopy,setPresentationText} from './i18n.js';
import {organizationAssignmentSummary} from './organization-assignment-ui.js';
import {procedureFields,procedureTargets} from './flight-test-procedure.js';
import {customFlightFields,customFlightTargets} from './custom-flight-test.js';
import {sensorFields,sensorTargets} from './experimental-sensor.js';
import {node,button,link,newIdentity,numeric} from './workstation-ui.js';
import {observationFields,observationTargets,openPilotObservations} from './observation-research.js';
import {surveyFields,surveyTargets,surveySourceReview} from './survey-research.js';
import {preflightReadiness} from './preflight-readiness.js';
import {renderResearchContext} from './scientific-context.js';
import {t as textForUI,t} from './i18n.js';

const pendingKey='AeroScience.personalResearch.preparation';let memory={},busy=false,storage=true;const controls=new Set(),availability=new WeakMap();
function pending(){if(!storage)return memory;try{const value=JSON.parse(sessionStorage.getItem(pendingKey)||'{}');return value&&typeof value==='object'&&!Array.isArray(value)?value:{};}catch{storage=false;return memory;}}
function savePending(value){memory=value;try{sessionStorage.setItem(pendingKey,JSON.stringify(value));}catch{storage=false;/* Current-view retries remain available without browser storage. */}}
function forget(id){const saved=pending();for(const key of Object.keys(saved))if(saved[key]===id)delete saved[key];savePending(saved);}
window.addEventListener('aeroscience-research-plan-opened',event=>forget(event.detail.planId));
function setBusy(value){busy=value;for(const control of controls){if(!control.isConnected)controls.delete(control);else control.disabled=value||availability.get(control)?.()===false;}}
export function refreshResearchEntryActions(actions){for(const control of controls)if(actions.contains(control))control.disabled=busy||availability.get(control)?.()===false;}
export const researchTargets=rule=>textForUI("ui.personalResearchStart.messageKnotsIASFeet",{value1:(String(numeric(rule.airspeedKnots))),value2:(String(numeric(rule.airspeedToleranceKnots))),value3:(String(numeric(rule.altitudeMslFeet))),value4:(String(numeric(rule.altitudeToleranceFeet))),value5:(String(numeric(rule.holdSeconds)))});
export const isAtmosphericResearch=card=>!!(card.atmosphericProfile||card.atmosphericTransect);
export const isScienceResearch=card=>isAtmosphericResearch(card)||!!card.experimentalSensor||!!card.survey||!!card.observationFlight;
export const researchDefinition=card=>card.procedure||card.experimentalSensor||card.observationFlight||card.survey||card.atmosphericTransect||card.atmosphericProfile||card.flightRegime;
export const researchFields=(parent,card,options)=>card.procedure?procedureFields(parent,card.procedure,{...options,cardVersion:card.version??8,cardFields:Object.fromEntries(Object.entries(card).filter(([key])=>key!=='procedure'))}):card.templateId==='custom-flight-test'||card.flightRegime?.testPoint?customFlightFields(parent,card.flightRegime,{...options,cardVersion:card.version??7,cardFields:Object.fromEntries(Object.entries(card).filter(([key])=>key!=='flightRegime'))}):card.experimentalSensor?sensorFields(parent,card.experimentalSensor,options):card.observationFlight?observationFields(parent,card.observationFlight,options):card.survey?surveyFields(parent,card.survey,options):card.atmosphericTransect?transectFields(parent,card.atmosphericTransect,options):card.atmosphericProfile?atmosphericFields(parent,card.atmosphericProfile,options):flightRegimeFields(parent,card.flightRegime,options);
export const researchCardTargets=card=>card.procedure?procedureTargets(card.procedure):card.templateId==='custom-flight-test'||card.flightRegime?.testPoint?customFlightTargets(card.flightRegime):card.experimentalSensor?sensorTargets(card.experimentalSensor):card.observationFlight?observationTargets(card.observationFlight):card.survey?surveyTargets(card.survey):card.atmosphericTransect?transectTargets(card.atmosphericTransect):card.atmosphericProfile?atmosphericTargets(card.atmosphericProfile):researchTargets(card.flightRegime);
export const atmosphericTargets=rule=>textForUI("ui.personalResearchStart.messageNeighboringFootMSLBandsConsecutive",{value1:(String(rule.requiredBands)),value2:(String(numeric(rule.bandWidthFeet))),value3:(String(rule.minimumReadingsPerBand)),value4:(String(numeric(rule.maximumGapSeconds)))});
export const transectTargets=rule=>textForUI("ui.personalResearchStart.messageDistinctSamplingLocationsAtLeastM",{value1:(String(rule.requiredStations)),value2:(String(numeric(rule.stationSpacingMetres))),value3:(String(rule.minimumReadingsPerStation)),value4:(String(numeric(rule.minimumExtentMetres))),value5:(String(numeric(rule.maximumGapSeconds)))});
export function transectFields(parent,rule,{prefix=''}={}){
 const label=node('label',localizedCopy("ui.experimentalSensor.messageScientificQuestion",{value1:(String(prefix))})),question=node('input');question.value=rule.question;question.required=true;question.maxLength=500;label.append(question);parent.append(label);const fields={};
 for(const [key,title,min,max]of [['stationSpacingMetres',textForUI("ui.personalResearchStart.messageSamplingLocationSpacingM"),100,50000],['requiredStations',textForUI("ui.personalResearchStart.messageDistinctSamplingLocationsRequired"),2,100],['minimumReadingsPerStation',textForUI("ui.personalResearchStart.messageConsecutiveReadingsPerLocation"),2,600],['minimumExtentMetres',textForUI("ui.personalResearchStart.messageMinimumGeographicExtentM"),100,1000000],['maximumGapSeconds',textForUI("ui.personalResearchStart.messageMaximumReadingGapSeconds"),1,60],['maximumStepMetres',textForUI("ui.personalResearchStart.messageLargestSupportedStepBetweenReadingsM"),100,100000]]){const label=node('label',prefix+title),input=node('input');input.type='number';input.step=['requiredStations','minimumReadingsPerStation'].includes(key)?'1':'any';input.min=min;input.max=max;input.required=true;input.value=rule[key];label.append(input);parent.append(label);fields[key]=input;}
 parent.append(node('p',localizedCopy("ui.personalResearchStart.messageGoalsApplyToActualSampledLocationsNot")));
 return {read:()=>({version:1,question:question.value,...Object.fromEntries(Object.entries(fields).map(([key,input])=>[key,Number(input.value)]))})};
}
export function atmosphericFields(parent,rule,{prefix=''}={}){
 const label=node('label',localizedCopy("ui.experimentalSensor.messageScientificQuestion",{value1:(String(prefix))})),question=node('input');question.value=rule.question;question.required=true;question.maxLength=500;label.append(question);parent.append(label);const fields={};
 for(const [key,title,min,max]of [['bandWidthFeet',textForUI("ui.personalResearchStart.messageAltitudeBandWidthFeet"),100,5000],['requiredBands',textForUI("ui.personalResearchStart.messageNeighboringBandsRequired"),2,20],['minimumReadingsPerBand',textForUI("ui.personalResearchStart.messageConsecutiveReadingsPerBand"),2,600],['maximumGapSeconds',textForUI("ui.personalResearchStart.messageMaximumReadingGapSeconds"),1,60]]){
  const label=node('label',prefix+title),input=node('input');input.type='number';input.step=['requiredBands','minimumReadingsPerBand'].includes(key)?'1':'any';input.min=min;input.max=max;input.required=true;input.value=rule[key];label.append(input);parent.append(label);fields[key]=input;
 }return {read:()=>({version:1,question:question.value,...Object.fromEntries(Object.entries(fields).map(([key,input])=>[key,Number(input.value)]))})};
}

// The same supported fields are used by My Research and the existing Mission Planner.
export function flightRegimeFields(parent,rule,{prefix=''}={}){
 const fields={};
 for(const [key,label,min,max]of [
  ['airspeedKnots',textForUI("ui.personalResearchStart.messageIndicatedAirspeedTargetKnots"),1,2000],['airspeedToleranceKnots',textForUI("ui.personalResearchStart.messageAirspeedToleranceKnots"),.01,null],
  ['altitudeMslFeet',textForUI("ui.personalResearchStart.messageAltitudeTargetFeetMSL"),-2000,100000],['altitudeToleranceFeet',textForUI("ui.personalResearchStart.messageAltitudeToleranceFeet"),.01,10000],['holdSeconds',textForUI("ui.personalResearchStart.messageRequiredContinuousHoldSeconds"),1,3600]
 ]){const wrap=node('label',prefix+label),input=node('input');input.type='number';input.required=true;input.step='any';input.value=rule[key];input.min=min;if(max!==null)input.max=max;wrap.append(input);parent.append(wrap);fields[key]=input;}
 return {read:()=>Object.fromEntries(Object.entries(fields).map(([key,input])=>[key,Number(input.value)]))};
}

export function researchEntryActions(parent,api,{starter,card,survey},isCurrent,{prepareHref,canPrepare=()=>true,navigate=href=>{location.hash=href;}}={}){
 if(card?.procedure||card?.templateId==='custom-flight-test')return null; // Custom cards use the existing Open in Mission Planner action, not a built-in starter.
 for(const control of controls)if(!control.isConnected)controls.delete(control);
 const actions=node('div',undefined,'action-row'),status=node('p');status.setAttribute('role','status');parent.append(actions,status);
 for(const [intent,title]of [['flyNow',t('missions.flyNow')],['customize',t('missions.customize')]]){
  const control=button(actions,title,async()=>{
   if(busy||!isCurrent()||!canPrepare())return;setBusy(true);setPresentationText(status,localizedCopy('missions.preparing'));
   const locked=[...parent.querySelectorAll('fieldset')].map(field=>[field,field.disabled]);for(const [field]of locked)field.disabled=true;
   const selectedSurvey=survey?.read();const currentDraft=()=>isCurrent()&&JSON.stringify(survey?.read()||null)===JSON.stringify(selectedSurvey||null);const keepCurrent=()=>{const current=currentDraft();if(!current&&isCurrent())setPresentationText(status,localizedCopy('missions.preparationChanged'));return current;};const key=JSON.stringify([selectedSurvey||null,starter?.id||card.templateId,starter?.version||card.version,card?.id||null,card?.revision||null,intent]);
   const saved=pending();saved[key]||=newIdentity();savePending(saved);
   const request={version:1,requestId:saved[key],starterVersion:starter?.version||1,starterId:starter?.id||card.templateId,intent,card:card?{id:card.id,revision:card.revision}:null,...(selectedSurvey?{survey:selectedSurvey}:{})};
   try{
    const state=await api('/api/state');if(!keepCurrent())return;
    if(intent==='flyNow'&&state.activeMission){setPresentationText(status,localizedCopy("ui.personalResearchStart.messageAFlightIsAlreadyRecordingKeepUsing"));return;}
    const prepared=await api('/api/research/starters/prepare',request);if(!keepCurrent())return;
    if(prepared?.plan?.id!==request.requestId||prepared.plan.researchStarter?.requestId!==request.requestId)throw Error(t('missions.receiptMismatch'));
    if(intent==='customize'){setPresentationText(status,localizedCopy("ui.personalResearchStart.messageAnIndependentEditableCopyIsPreparedYour"));navigate(`#planner?researchPlan=${prepared.plan.id}`);}
    else{
     const selection=await api('/api/mission-selection',{planId:prepared.plan.id});if(!keepCurrent())return;
     if(selection?.planId!==prepared.plan.id)throw Error(t('missions.selectionMismatch'));
     forget(request.requestId);setPresentationText(status,localizedCopy("ui.personalResearchStart.messageMissionPreparedStartRecordingNextRecording"));window.dispatchEvent(new Event('aeroscience-selection'));navigate(prepareHref?prepareHref(prepared.plan):`#operations?researchPlan=${prepared.plan.id}`);
    }
   }catch(error){if(isCurrent()){
    setPresentationText(status,error.message);
    if(!actions.querySelector('[data-new-preparation]')){const reset=button(actions,localizedCopy("ui.personalResearchStart.messageStartANewPreparation"),()=>{forget(request.requestId);reset.remove();setPresentationText(status,localizedCopy("ui.personalResearchStart.messageChooseFlyNowOrCustomizeAgainExisting"));});reset.dataset.newPreparation='true';controls.add(reset);}
   }}finally{for(const [field,disabled]of locked)field.disabled=disabled;setBusy(false);}
  },intent==='flyNow'?'primary':'secondary');availability.set(control,canPrepare);control.disabled=busy||!canPrepare();controls.add(control);
 }
 return actions;
}

export function researchStarterChoices(parent,api,starters,isCurrent){
 const section=node('section',undefined,'panel research-panel');section.id='research-starters';section.append(node('h2',localizedCopy("ui.personalResearchStart.messageChooseResearch")),node('p',localizedCopy("ui.personalResearchStart.messageFlyNowPreparesAMissionStartRecording")));parent.append(section);
 for(const starter of starters){
  const card=node('article',undefined,'research-panel');setPresentationText(card,localizedCopy("ui.missions.messageStarter",{value1:(starter.name)}),"aria-label");card.append(node('h3',starter.name));
  renderResearchContext(card,{name:starter.name,atmosphericProfile:starter.atmosphericExample,atmosphericTransect:starter.transectExample,survey:starter.surveyExample,observationFlight:starter.observationExample,experimentalSensor:starter.sensorExample,flightRegime:starter.example});
  const details=node('details');details.dataset.scienceLevel='information';details.append(node('summary',localizedCopy("ui.personalResearchStart.messageRequirementsInstruments")),node('p',starter.description),node('p',localizedCopy("ui.personalResearchStart.messageAvailableMeasurements",{value1:(String(starter.measurements.join(', ')))})),node('p',starter.sensorExample?sensorTargets(starter.sensorExample):starter.observationExample?observationTargets(starter.observationExample):starter.surveyExample?localizedCopy("ui.personalResearchStart.messageEachSelectedObjectiveRequiresQualifiedSamplesAnd",{value1:(String(starter.surveyExample.minimumValidSamples)),value2:(String(starter.surveyExample.minimumLocations))}):starter.transectExample?transectTargets(starter.transectExample):starter.atmosphericExample?atmosphericTargets(starter.atmosphericExample):researchTargets(starter.example)));
  details.append(node('p',starter.sensorExample?localizedCopy("ui.personalResearchStart.messageUsesOriginalAmbientTemperatureReadingsAndAFictional"):starter.observationExample?localizedCopy("ui.personalResearchStart.messagePilotDocumentationIsSeparateFromOptionalScientific"):starter.surveyExample?localizedCopy("ui.personalResearchStart.messageAirTemperatureWorksWithoutExternalSourcesTerrain"):starter.atmosphericExample||starter.transectExample?localizedCopy("ui.personalResearchStart.messageCollectActualRetainedAtmosphericReadingsNoExternal"):localizedCopy("ui.personalResearchStart.messageUsesOriginalFlightTelemetryFlightRecordingScientific")));card.append(details);
  const survey=starter.surveyExample?surveyFields(card,starter.surveyExample,{measurementsOnly:true}):null;researchEntryActions(card,api,{starter,survey},isCurrent);section.append(card);
 }
 return section;
}

export function researchLaunchGuide(parent,api,plan,card){
 const root=node('section',undefined,'panel research-panel');root.id='research-launch-guide';if(card.organizationAssignment)root.append(organizationAssignmentSummary(card));root.append(node('h3',localizedCopy("ui.personalResearchStart.messageYourResearchFlight")),node('p',localizedCopy("ui.calibrationSettings.messageRevision",{value1:(String(card.name)),value2:(String(card.revision)),value3:(String(researchCardTargets(card)))})));
 renderResearchContext(root,card,{plan});
 const status=node('p');status.setAttribute('role','status');root.append(status,node('p',card.experimentalSensor?localizedCopy("ui.personalResearchStart.message1UseStartRecordingThenStartScience"):card.observationFlight?localizedCopy("ui.personalResearchStart.message1UseStartRecording2InPilot"):card.survey?localizedCopy("ui.personalResearchStart.message1ReviewTheSelectedMeasurementsAndExisting"):card.atmosphericTransect?localizedCopy("ui.personalResearchStart.message1ReviewYourSamplingGoals2Use"):card.atmosphericProfile?localizedCopy("ui.personalResearchStart.message1ReviewTheSamplingBandsForYour"):localizedCopy("ui.personalResearchStart.message1ReviewTheTargetsForYourAircraft")),node('p',card.experimentalSensor?localizedCopy("ui.personalResearchStart.messageOriginalSimulatorAirTemperatureIsTheRequired"):card.observationFlight?localizedCopy("ui.personalResearchStart.messageTheseArePilotObservationsNotSensorMeasurements"):card.survey?localizedCopy("ui.personalResearchStart.messageDEMDSMElevationsAreNotLiDARReturnsLandsat"):isAtmosphericResearch(card)?localizedCopy("ui.personalResearchStart.messageUsesActualSimulatorReportedAirTemperaturePressureWind"):localizedCopy("ui.personalResearchStart.messageARouteAndExtraInstrumentsAreOptional")));
 if(card.observationFlight)button(root,localizedCopy("ui.personalResearchFlight.messageRecordPilotObservation"),openPilotObservations,'primary');if(card.survey)surveySourceReview(root,api,plan);const details=node('details');details.append(node('summary',localizedCopy("ui.personalResearchStart.messageReviewScienceAndStorageReadiness")));preflightReadiness(details,()=>plan,api,{id:'research-launch-readiness',active:()=>root.isConnected&&!root.closest('[hidden]')&&details.open});root.append(details);link(root,localizedCopy("ui.personalResearchStart.messageBackToMyResearch"),'#my-research');parent.append(root);
 return {update(snapshot){
  const message=snapshot.activeMission?'A flight is recording. Use its Personal Research Test Cards; nothing here starts or stops flight or science.'
   :snapshot.connection?.state!=='connected'?textForUI("ui.personalResearchStart.messageConnectMSFS2024AndLoadAnAircraft")
   :!snapshot.telemetry||snapshot.telemetry.isValid===false||snapshot.telemetry.isPaused||Date.now()-Date.parse(snapshot.telemetry.timestampUtc)>6000?textForUI("ui.personalResearchStart.messageRecordingNeedsFreshValidUnpausedAircraftTelemetry")
   :snapshot.connection.source==='simulated'?textForUI("ui.personalResearchStart.messageDemoTestTelemetryIsAvailableThisIsNot")
   :textForUI("ui.personalResearchStart.messageAircraftTelemetryIsAvailableReviewYourTargets");
  if(status.textContent!==message)setPresentationText(status,message);
 }};
}
