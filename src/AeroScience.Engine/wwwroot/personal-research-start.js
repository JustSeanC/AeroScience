import {organizationAssignmentSummary} from './organization-assignment-ui.js';
import {procedureFields,procedureTargets} from './flight-test-procedure.js';
import {customFlightFields,customFlightTargets} from './custom-flight-test.js';
import {sensorFields,sensorTargets} from './experimental-sensor.js';
import {node,button,link,newIdentity,numeric} from './workstation-ui.js';
import {observationFields,observationTargets,openPilotObservations} from './observation-research.js';
import {surveyFields,surveyTargets,surveySourceReview} from './survey-research.js';
import {preflightReadiness} from './preflight-readiness.js';
import {renderResearchContext} from './scientific-context.js';
import {t} from './i18n.js';

const pendingKey='AeroScience.personalResearch.preparation';let memory={},busy=false,storage=true;const controls=new Set(),availability=new WeakMap();
function pending(){if(!storage)return memory;try{const value=JSON.parse(sessionStorage.getItem(pendingKey)||'{}');return value&&typeof value==='object'&&!Array.isArray(value)?value:{};}catch{storage=false;return memory;}}
function savePending(value){memory=value;try{sessionStorage.setItem(pendingKey,JSON.stringify(value));}catch{storage=false;/* Current-view retries remain available without browser storage. */}}
function forget(id){const saved=pending();for(const key of Object.keys(saved))if(saved[key]===id)delete saved[key];savePending(saved);}
window.addEventListener('aeroscience-research-plan-opened',event=>forget(event.detail.planId));
function setBusy(value){busy=value;for(const control of controls){if(!control.isConnected)controls.delete(control);else control.disabled=value||availability.get(control)?.()===false;}}
export function refreshResearchEntryActions(actions){for(const control of controls)if(actions.contains(control))control.disabled=busy||availability.get(control)?.()===false;}
export const researchTargets=rule=>`${numeric(rule.airspeedKnots)} ± ${numeric(rule.airspeedToleranceKnots)} knots IAS · ${numeric(rule.altitudeMslFeet)} ± ${numeric(rule.altitudeToleranceFeet)} feet MSL · ${numeric(rule.holdSeconds)} second hold`;
export const isAtmosphericResearch=card=>!!(card.atmosphericProfile||card.atmosphericTransect);
export const isScienceResearch=card=>isAtmosphericResearch(card)||!!card.experimentalSensor||!!card.survey||!!card.observationFlight;
export const researchDefinition=card=>card.procedure||card.experimentalSensor||card.observationFlight||card.survey||card.atmosphericTransect||card.atmosphericProfile||card.flightRegime;
export const researchFields=(parent,card,options)=>card.procedure?procedureFields(parent,card.procedure,{...options,cardVersion:card.version??8,cardFields:Object.fromEntries(Object.entries(card).filter(([key])=>key!=='procedure'))}):card.templateId==='custom-flight-test'||card.flightRegime?.testPoint?customFlightFields(parent,card.flightRegime,{...options,cardVersion:card.version??7,cardFields:Object.fromEntries(Object.entries(card).filter(([key])=>key!=='flightRegime'))}):card.experimentalSensor?sensorFields(parent,card.experimentalSensor,options):card.observationFlight?observationFields(parent,card.observationFlight,options):card.survey?surveyFields(parent,card.survey,options):card.atmosphericTransect?transectFields(parent,card.atmosphericTransect,options):card.atmosphericProfile?atmosphericFields(parent,card.atmosphericProfile,options):flightRegimeFields(parent,card.flightRegime,options);
export const researchCardTargets=card=>card.procedure?procedureTargets(card.procedure):card.templateId==='custom-flight-test'||card.flightRegime?.testPoint?customFlightTargets(card.flightRegime):card.experimentalSensor?sensorTargets(card.experimentalSensor):card.observationFlight?observationTargets(card.observationFlight):card.survey?surveyTargets(card.survey):card.atmosphericTransect?transectTargets(card.atmosphericTransect):card.atmosphericProfile?atmosphericTargets(card.atmosphericProfile):researchTargets(card.flightRegime);
export const atmosphericTargets=rule=>`${rule.requiredBands} neighboring ${numeric(rule.bandWidthFeet)}-foot MSL bands · ${rule.minimumReadingsPerBand} consecutive valid temperature readings per band · maximum gap ${numeric(rule.maximumGapSeconds)} seconds. Start at achievable altitudes anywhere; no fixed flight level.`;
export const transectTargets=rule=>`${rule.requiredStations} distinct sampling locations at least ${numeric(rule.stationSpacingMetres)} m apart · ${rule.minimumReadingsPerStation} consecutive temperature readings per location · ${numeric(rule.minimumExtentMetres)} m extent in one supported leg · maximum gap ${numeric(rule.maximumGapSeconds)} seconds.`;
export function transectFields(parent,rule,{prefix=''}={}){
 const label=node('label',prefix+'Scientific question'),question=node('input');question.value=rule.question;question.required=true;question.maxLength=500;label.append(question);parent.append(label);const fields={};
 for(const [key,title,min,max]of [['stationSpacingMetres','Sampling location spacing (m)',100,50000],['requiredStations','Distinct sampling locations required',2,100],['minimumReadingsPerStation','Consecutive readings per location',2,600],['minimumExtentMetres','Minimum geographic extent (m)',100,1000000],['maximumGapSeconds','Maximum reading gap (seconds)',1,60],['maximumStepMetres','Largest supported step between readings (m)',100,100000]]){const label=node('label',prefix+title),input=node('input');input.type='number';input.step=['requiredStations','minimumReadingsPerStation'].includes(key)?'1':'any';input.min=min;input.max=max;input.required=true;input.value=rule[key];label.append(input);parent.append(label);fields[key]=input;}
 parent.append(node('p','Goals apply to actual sampled locations, not the surrounding area. Gaps and larger recorded steps break the sampled path; this is an analysis setting, not an aircraft limit. Free flight works from anywhere.'));
 return {read:()=>({version:1,question:question.value,...Object.fromEntries(Object.entries(fields).map(([key,input])=>[key,Number(input.value)]))})};
}
export function atmosphericFields(parent,rule,{prefix=''}={}){
 const label=node('label',prefix+'Scientific question'),question=node('input');question.value=rule.question;question.required=true;question.maxLength=500;label.append(question);parent.append(label);const fields={};
 for(const [key,title,min,max]of [['bandWidthFeet','Altitude band width (feet)',100,5000],['requiredBands','Neighboring bands required',2,20],['minimumReadingsPerBand','Consecutive readings per band',2,600],['maximumGapSeconds','Maximum reading gap (seconds)',1,60]]){
  const label=node('label',prefix+title),input=node('input');input.type='number';input.step=['requiredBands','minimumReadingsPerBand'].includes(key)?'1':'any';input.min=min;input.max=max;input.required=true;input.value=rule[key];label.append(input);parent.append(label);fields[key]=input;
 }return {read:()=>({version:1,question:question.value,...Object.fromEntries(Object.entries(fields).map(([key,input])=>[key,Number(input.value)]))})};
}

// The same supported fields are used by My Research and the existing Mission Planner.
export function flightRegimeFields(parent,rule,{prefix=''}={}){
 const fields={};
 for(const [key,label,min,max]of [
  ['airspeedKnots','Indicated airspeed target (knots)',1,2000],['airspeedToleranceKnots','Airspeed tolerance (± knots)',.01,null],
  ['altitudeMslFeet','Altitude target (feet MSL)',-2000,100000],['altitudeToleranceFeet','Altitude tolerance (± feet)',.01,10000],['holdSeconds','Required continuous hold (seconds)',1,3600]
 ]){const wrap=node('label',prefix+label),input=node('input');input.type='number';input.required=true;input.step='any';input.value=rule[key];input.min=min;if(max!==null)input.max=max;wrap.append(input);parent.append(wrap);fields[key]=input;}
 return {read:()=>Object.fromEntries(Object.entries(fields).map(([key,input])=>[key,Number(input.value)]))};
}

export function researchEntryActions(parent,api,{starter,card,survey},isCurrent,{prepareHref,canPrepare=()=>true,navigate=href=>{location.hash=href;}}={}){
 if(card?.procedure||card?.templateId==='custom-flight-test')return null; // Custom cards use the existing Open in Mission Planner action, not a built-in starter.
 for(const control of controls)if(!control.isConnected)controls.delete(control);
 const actions=node('div',undefined,'action-row'),status=node('p');status.setAttribute('role','status');parent.append(actions,status);
 for(const [intent,title]of [['flyNow',t('missions.flyNow')],['customize',t('missions.customize')]]){
  const control=button(actions,title,async()=>{
   if(busy||!isCurrent()||!canPrepare())return;setBusy(true);status.textContent=t('missions.preparing');
   const locked=[...parent.querySelectorAll('fieldset')].map(field=>[field,field.disabled]);for(const [field]of locked)field.disabled=true;
   const selectedSurvey=survey?.read();const currentDraft=()=>isCurrent()&&JSON.stringify(survey?.read()||null)===JSON.stringify(selectedSurvey||null);const keepCurrent=()=>{const current=currentDraft();if(!current&&isCurrent())status.textContent=t('missions.preparationChanged');return current;};const key=JSON.stringify([selectedSurvey||null,starter?.id||card.templateId,starter?.version||card.version,card?.id||null,card?.revision||null,intent]);
   const saved=pending();saved[key]||=newIdentity();savePending(saved);
   const request={version:1,requestId:saved[key],starterVersion:starter?.version||1,starterId:starter?.id||card.templateId,intent,card:card?{id:card.id,revision:card.revision}:null,...(selectedSurvey?{survey:selectedSurvey}:{})};
   try{
    const state=await api('/api/state');if(!keepCurrent())return;
    if(intent==='flyNow'&&state.activeMission){status.textContent='A flight is already recording. Keep using its cards, or choose Customize to prepare a later flight. Nothing was stopped.';return;}
    const prepared=await api('/api/research/starters/prepare',request);if(!keepCurrent())return;
    if(prepared?.plan?.id!==request.requestId||prepared.plan.researchStarter?.requestId!==request.requestId)throw Error(t('missions.receiptMismatch'));
    if(intent==='customize'){status.textContent='An independent editable copy is prepared. Your other plans and historic results are unchanged.';navigate(`#planner?researchPlan=${prepared.plan.id}`);}
    else{
     const selection=await api('/api/mission-selection',{planId:prepared.plan.id});if(!keepCurrent())return;
     if(selection?.planId!==prepared.plan.id)throw Error(t('missions.selectionMismatch'));
     forget(request.requestId);status.textContent='Mission prepared — Start Recording next. Recording and science have not started.';window.dispatchEvent(new Event('aeroscience-selection'));navigate(prepareHref?prepareHref(prepared.plan):`#operations?researchPlan=${prepared.plan.id}`);
    }
   }catch(error){if(isCurrent()){
    status.textContent=error.message;
    if(!actions.querySelector('[data-new-preparation]')){const reset=button(actions,'Start a New Preparation',()=>{forget(request.requestId);reset.remove();status.textContent='Choose Fly Now or Customize again. Existing saved plans are unchanged.';});reset.dataset.newPreparation='true';controls.add(reset);}
   }}finally{for(const [field,disabled]of locked)field.disabled=disabled;setBusy(false);}
  },intent==='flyNow'?'primary':'secondary');availability.set(control,canPrepare);control.disabled=busy||!canPrepare();controls.add(control);
 }
 return actions;
}

export function researchStarterChoices(parent,api,starters,isCurrent){
 const section=node('section',undefined,'panel research-panel');section.id='research-starters';section.append(node('h2','Choose Research'),node('p','Fly Now prepares a mission. Start Recording is a separate action.'));parent.append(section);
 for(const starter of starters){
  const card=node('article',undefined,'research-panel');card.setAttribute('aria-label',starter.name+' starter');card.append(node('h3',starter.name));
  renderResearchContext(card,{name:starter.name,atmosphericProfile:starter.atmosphericExample,atmosphericTransect:starter.transectExample,survey:starter.surveyExample,observationFlight:starter.observationExample,experimentalSensor:starter.sensorExample,flightRegime:starter.example});
  const details=node('details');details.dataset.scienceLevel='information';details.append(node('summary','Requirements & instruments'),node('p',starter.description),node('p','Available measurements: '+starter.measurements.join(', ')+'.'),node('p',starter.sensorExample?sensorTargets(starter.sensorExample):starter.observationExample?observationTargets(starter.observationExample):starter.surveyExample?`Each selected objective requires ${starter.surveyExample.minimumValidSamples} qualified samples and ${starter.surveyExample.minimumLocations} sampled locations.`:starter.transectExample?transectTargets(starter.transectExample):starter.atmosphericExample?atmosphericTargets(starter.atmosphericExample):researchTargets(starter.example)));
  details.append(node('p',starter.sensorExample?'Uses original ambient-temperature readings and a fictional modeled response. No physical sensor is validated.':starter.observationExample?'Pilot documentation is separate from optional scientific collection.':starter.surveyExample?'Air Temperature works without external sources. Terrain and historical surface-temperature objectives need compatible retained external data.':starter.atmosphericExample||starter.transectExample?'Collect actual retained atmospheric readings. No external dataset, account or internet connection is required.':'Uses original flight telemetry. Flight recording, scientific collection and test-card activation remain independent.'));card.append(details);
  const survey=starter.surveyExample?surveyFields(card,starter.surveyExample,{measurementsOnly:true}):null;researchEntryActions(card,api,{starter,survey},isCurrent);section.append(card);
 }
 return section;
}

export function researchLaunchGuide(parent,api,plan,card){
 const root=node('section',undefined,'panel research-panel');root.id='research-launch-guide';if(card.organizationAssignment)root.append(organizationAssignmentSummary(card));root.append(node('h3','Your Research Flight'),node('p',`${card.name} · revision ${card.revision} · ${researchCardTargets(card)}`));
 renderResearchContext(root,card,{plan});
 const status=node('p');status.setAttribute('role','status');root.append(status,node('p',card.experimentalSensor?'1. Use Start Recording, then Start Science Collection. 2. Activate Card → Begin Attempt → End Attempt while collecting changing air temperatures. 3. End mission, then My Research → Review Sensor Checkout. Card controls never start or stop recording or science.':card.observationFlight?'1. Use Start Recording. 2. In Pilot Observations, choose a Category and enter Notes, then Add observation marker or Save in-flight note. 3. End mission, then My Research → Review Scientific Observations. Optional instruments require Start Science Collection; no science starts automatically.':card.survey?'1. Review the selected measurements and existing science/data readiness. 2. Use Start Recording, then Start Science Collection. 3. End mission and choose Review Terrain & Temperature Survey in My Research. Air temperature needs no external scene; missing required terrain/thermal data stays incomplete.':card.atmosphericTransect?'1. Review your sampling goals. 2. Use Start Recording, then explicitly Start Science Collection when armed. 3. Fly across different locations, then End mission and choose Review Atmospheric Transect in My Research. Optional Activate Card → Begin Attempt → End Attempt marks a shorter investigation; it does not start science.':card.atmosphericProfile?'1. Review the sampling bands for your aircraft. 2. Use Start Recording when ready and ensure science is Collecting; if armed, use Start Science Collection. 3. Climb or descend through achievable bands, then End mission and choose Review Atmospheric Profile in My Research. Optional Activate Card → Begin Attempt → End Attempt marks a shorter investigation; it does not start science.':'1. Review the targets for your aircraft. 2. Use Start Recording when ready; normal startup and readiness checks still apply. 3. In Personal Research Test Cards, choose Activate Card → Begin Attempt → End Attempt. End mission remains separate.'),node('p',card.experimentalSensor?'Original simulator air temperature is the required input. The fictional first-order response is calculated only from saved valid readings. No physical or MSFS-mounted experimental sensor exists; constant temperatures may not exercise your chosen variation criterion.':card.observationFlight?'These are Pilot Observations, not sensor measurements or proof of a hypothesis. Host UTC is recorded at entry; unavailable or stale aircraft positions are left missing. Optional supporting values must exist in the science archive.':card.survey?'DEM/DSM elevations are not LiDAR returns. Landsat surface temperatures are historical satellite observations in Kelvin; simulator air temperature is a separate Celsius measurement. Each objective retains its own source, date, units and QA.':isAtmosphericResearch(card)?'Uses actual simulator-reported air temperature, pressure, wind and density at the aircraft. Missing or paused collection stays missing. These are not historical satellite surface temperatures, modeled weather away from the aircraft or real-world certified measurements.':'A route and extra instruments are optional for Flight-Regime Validation. Any added scientific instruments retain their own source requirements. Flight recording, scientific collection and test-card activation are independent.'));
 if(card.observationFlight)button(root,'Record Pilot Observation',openPilotObservations,'primary');if(card.survey)surveySourceReview(root,api,plan);const details=node('details');details.append(node('summary','Review Science and Storage Readiness'));preflightReadiness(details,()=>plan,api,{id:'research-launch-readiness',active:()=>root.isConnected&&!root.closest('[hidden]')&&details.open});root.append(details);link(root,'Back to My Research','#my-research');parent.append(root);
 return {update(snapshot){
  const message=snapshot.activeMission?'A flight is recording. Use its Personal Research Test Cards; nothing here starts or stops flight or science.'
   :snapshot.connection?.state!=='connected'?'Connect MSFS 2024 and load an aircraft to record. Your research plan stays prepared; you can review it while disconnected.'
   :!snapshot.telemetry||snapshot.telemetry.isValid===false||snapshot.telemetry.isPaused||Date.now()-Date.parse(snapshot.telemetry.timestampUtc)>6000?'Recording needs fresh, valid, unpaused aircraft telemetry. Your prepared plan and targets are preserved.'
   :snapshot.connection.source==='simulated'?'Demo/test telemetry is available. This is not an installed MSFS connection; the normal Start Recording checks still apply.'
   :'Aircraft telemetry is available. Review your targets, then use Start Recording button. Storage and other startup requirements are still checked.';
  if(status.textContent!==message)status.textContent=message;
 }};
}
