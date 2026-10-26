import {node,button,link,newIdentity,numeric} from './workstation-ui.js';
import {preflightReadiness} from './preflight-readiness.js';

const pendingKey='AeroScience.personalResearch.preparation';let memory={},busy=false,storage=true;const controls=new Set();
function pending(){if(!storage)return memory;try{const value=JSON.parse(sessionStorage.getItem(pendingKey)||'{}');return value&&typeof value==='object'&&!Array.isArray(value)?value:{};}catch{storage=false;return memory;}}
function savePending(value){memory=value;try{sessionStorage.setItem(pendingKey,JSON.stringify(value));}catch{storage=false;/* Current-view retries remain available without browser storage. */}}
function forget(id){const saved=pending();for(const key of Object.keys(saved))if(saved[key]===id)delete saved[key];savePending(saved);}
window.addEventListener('aeroscience-research-plan-opened',event=>forget(event.detail.planId));
function setBusy(value){busy=value;for(const control of controls){if(!control.isConnected)controls.delete(control);else control.disabled=value;}}
export const researchTargets=rule=>`${numeric(rule.airspeedKnots)} ± ${numeric(rule.airspeedToleranceKnots)} knots IAS · ${numeric(rule.altitudeMslFeet)} ± ${numeric(rule.altitudeToleranceFeet)} feet MSL · ${numeric(rule.holdSeconds)} second hold`;

// The same supported fields are used by My Research and the existing Mission Planner.
export function flightRegimeFields(parent,rule,{prefix=''}={}){
 const fields={};
 for(const [key,label,min,max]of [
  ['airspeedKnots','Indicated airspeed target (knots)',1,2000],['airspeedToleranceKnots','Airspeed tolerance (± knots)',.01,null],
  ['altitudeMslFeet','Altitude target (feet MSL)',-2000,100000],['altitudeToleranceFeet','Altitude tolerance (± feet)',.01,10000],['holdSeconds','Required continuous hold (seconds)',1,3600]
 ]){const wrap=node('label',prefix+label),input=node('input');input.type='number';input.required=true;input.step='any';input.value=rule[key];input.min=min;if(max!==null)input.max=max;wrap.append(input);parent.append(wrap);fields[key]=input;}
 return {read:()=>Object.fromEntries(Object.entries(fields).map(([key,input])=>[key,Number(input.value)]))};
}

export function researchEntryActions(parent,api,{starter,card},isCurrent){
 for(const control of controls)if(!control.isConnected)controls.delete(control);
 const actions=node('div',undefined,'action-row'),status=node('p');status.setAttribute('role','status');parent.append(actions,status);
 for(const [intent,title]of [['flyNow','Fly Now'],['customize','Customize']]){
  const control=button(actions,title,async()=>{
   if(busy||!isCurrent())return;setBusy(true);status.textContent='Preparing your local experiment…';
   const key=JSON.stringify([starter?.id||card.templateId,starter?.version||card.version,card?.id||null,card?.revision||null,intent]);
   const saved=pending();saved[key]||=newIdentity();savePending(saved);
   const request={version:1,requestId:saved[key],starterVersion:starter?.version||1,starterId:starter?.id||card.templateId,intent,card:card?{id:card.id,revision:card.revision}:null};
   try{
    const state=await api('/api/state');if(!isCurrent())return;
    if(intent==='flyNow'&&state.activeMission){status.textContent='A flight is already recording. Keep using its cards, or choose Customize to prepare a later flight. Nothing was stopped.';return;}
    const prepared=await api('/api/research/starters/prepare',request);if(!isCurrent())return;
    if(intent==='customize'){status.textContent='An independent editable copy is prepared. Your other plans and historic results are unchanged.';location.hash=`#planner?researchPlan=${prepared.plan.id}`;}
    else{
     await api('/api/mission-selection',{planId:prepared.plan.id});if(!isCurrent())return;
     forget(request.requestId);window.dispatchEvent(new Event('aeroscience-selection'));location.hash=`#control?researchPlan=${prepared.plan.id}`;
    }
   }catch(error){if(isCurrent()){
    status.textContent=error.message;
    if(!actions.querySelector('[data-new-preparation]')){const reset=button(actions,'Start a New Preparation',()=>{forget(request.requestId);reset.remove();status.textContent='Choose Fly Now or Customize again. Existing saved plans are unchanged.';});reset.dataset.newPreparation='true';controls.add(reset);}
   }}finally{setBusy(false);}
  },intent==='flyNow'?'primary':'secondary');control.disabled=busy;controls.add(control);
 }
 return actions;
}

export function researchStarterChoices(parent,api,starters,isCurrent){
 const section=node('section',undefined,'panel research-panel');section.id='research-starters';section.append(node('h2','Choose an Experiment'),node('p','Fly Now prepares a local plan and takes you to the normal flight controls. Customize opens your own editable copy in Mission Planner. Neither starts recording.'));
 parent.append(section);
 for(const starter of starters){const card=node('article',undefined,'research-panel');card.setAttribute('aria-label',starter.name+' starter');card.append(node('h3',starter.name),node('p',starter.description),node('p','Uses '+starter.measurements.join(', ')+'.'),node('p','Example targets: '+researchTargets(starter.example)+'. Review these for your simulated aircraft; they do not suit every aircraft. No aircraft settings are changed.'),node('p','This activity uses flight telemetry. No extra scientific instrument, external dataset, account or internet connection is required. Science collection keeps its separate controls.'));researchEntryActions(card,api,{starter},isCurrent);section.append(card);}
}

export function researchLaunchGuide(parent,api,plan,card){
 const root=node('section',undefined,'panel research-panel');root.id='research-launch-guide';root.append(node('h3','Your Research Flight'),node('p',`${card.name} · revision ${card.revision} · ${researchTargets(card.flightRegime)}`));
 const status=node('p');status.setAttribute('role','status');root.append(status,node('p','1. Review the targets for your aircraft. 2. Use Start mission when ready; normal startup and readiness checks still apply. 3. In Personal Research Test Cards, choose Activate Card → Begin Attempt → End Attempt. End mission remains separate.'),node('p','A route and extra instruments are optional for Flight-Regime Validation. Any added scientific instruments retain their own source requirements. Flight recording, scientific collection and test-card activation are independent.'));
 const details=node('details');details.append(node('summary','Review Science and Storage Readiness'));preflightReadiness(details,()=>plan,api,{id:'research-launch-readiness',active:()=>root.isConnected&&!root.closest('[hidden]')&&details.open});root.append(details);link(root,'Back to My Research','#my-research');parent.append(root);
 return {update(snapshot){
  const message=snapshot.activeMission?'A flight is recording. Use its Personal Research Test Cards; nothing here starts or stops flight or science.'
   :snapshot.connection?.state!=='connected'?'Connect MSFS 2024 and load an aircraft to record. Your research plan stays prepared; you can review it while disconnected.'
   :!snapshot.telemetry||snapshot.telemetry.isValid===false||snapshot.telemetry.isPaused||Date.now()-Date.parse(snapshot.telemetry.timestampUtc)>6000?'Recording needs fresh, valid, unpaused aircraft telemetry. Your prepared plan and targets are preserved.'
   :snapshot.connection.source==='simulated'?'Demo/test telemetry is available. This is not an installed MSFS connection; the normal Start mission checks still apply.'
   :'Aircraft telemetry is available. Review your targets, then use the normal Start mission button. Storage and other startup requirements are still checked.';
  if(status.textContent!==message)status.textContent=message;
 }};
}
