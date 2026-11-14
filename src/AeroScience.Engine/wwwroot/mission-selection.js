import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {readableName} from './workstation-ui.js';
import {reviewPreflight,openDownloadActivity} from './download-activity.js';
import {researchLaunchGuide} from './personal-research-start.js';
const el=(t,s)=>{const n=document.createElement(t);if(s!==undefined)setPresentationText(n,s);return n;};
export function missionSelection(parent,api,{onChange=()=>{},onSelection=()=>{}}={}){
 const box=el('section');box.className='mission-selection';const label=el('label',localizedCopy("ui.missionSelection.messageChooseMission")),select=el('select'),status=el('p'),details=el('p'),buttons=el('div'),research=el('div');select.id='selected-plan';setPresentationText(select,localizedCopy("ui.missionSelection.messageChooseMission"),"aria-label");status.setAttribute('role','status');label.append(select);box.append(label,status,details,buttons,research);parent.prepend(box);
 const name=document.getElementById('mission-name'),mode=document.getElementById('mission-mode');let selected=null,ready=false,loading=false,queued=false,generation=0,guide,guideIdentity,lastState,lastKey;
 async function refresh(){
  const own=++generation;ready=false;if(loading){queued=true;onChange();return;}loading=true;onChange();setPresentationText(status,localizedCopy("ui.missionSelection.messageRefreshingMissions"));
  try{
   const state=await api('/api/state'),plans=[];
   for(let offset=0;offset<1000;offset+=100){const page=await api('/api/plans?limit=100&offset='+offset);plans.push(...page);if(page.length<100)break;}
   const plan=plans.find(p=>p.id===state.selectedPlanId),reference=plan?.researchCards?.[0]??plan?.researchCard;
   let card=null,cardError=null;
   if(reference)try{const selectedCard=await api(`/api/research/cards/${reference.id}?revision=${reference.revision}`);if(plan?.researchStarter||selectedCard.organizationAssignment)card=selectedCard;}catch(error){cardError=error.message;}
   if(own!==generation)return;selected=state.selectedPlanId;select.replaceChildren();
   const unplanned=el('option',localizedCopy("ui.missionSelection.messageUnplannedFlight"));unplanned.value='';select.append(unplanned);
   for(const p of plans){const option=el('option',p.name);option.value=p.id;select.append(option);}
   if(selected&&!plan){const missing=el('option',localizedCopy("ui.missionSelection.messageSelectedPlanUnavailableRefreshOrChoose"));missing.value=selected;select.append(missing);setPresentationText(status,localizedCopy("ui.missionSelection.messageSelectedPlanUnavailableStartingWillNotFall"));}
   select.value=selected||'';name.disabled=!!selected;mode.disabled=!!selected;select.disabled=!!state.activeMission;
   if(plan){name.value=plan.name;mode.value=plan.mode;setPresentationText(status,localizedCopy("ui.missionSelection.messagePlanSelectedRecordingHasNotStarted"));setPresentationText(details,localizedCopy("ui.missionSelection.messageWaypointsSurveyAreas",{value1:(String(readableName(plan.template))),value2:(String(plan.waypoints?.length||0)),value3:(String(plan.surveyAreas?.length||0)),value4:(String((plan.instruments||[]).filter(i=>i.enabled).length))}));}
   else if(!selected){setPresentationText(status,localizedCopy("ui.missionSelection.messageUnplannedFlightEnterANameAnd"));setPresentationText(details,localizedCopy("ui.missionSelection.messageSelectionDoesNotRecordOrDownloadUse"));}
   ready=!selected||!!plan;
   const identity=card?JSON.stringify([plan,card]):JSON.stringify([state.selectedPlanId,cardError]);
   // Identical refreshes must not discard an open/pending readiness assessment.
   // Any changed saved plan/card still replaces it, invalidating the old context.
   if(identity!==guideIdentity||cardError){guideIdentity=identity;research.replaceChildren();guide=null;if(card){const advanced=el('details');advanced.append(el('summary',localizedCopy("ui.missionSelection.messageResearchDefinitionSourceDetails")));research.append(advanced);guide=researchLaunchGuide(advanced,api,plan,card);}else if(cardError)research.append(el('p',localizedCopy("ui.missionSelection.messagePreparedExperimentCouldNotLoadRefresh",{value1:(String(cardError))})));}
   guide?.update(lastState||state);onSelection(plan||null,card);
   buttons.replaceChildren();
   if(selected)for(const [title,run]of [[textForUI("ui.missionSelection.messagePrepareScienceData"),()=>reviewPreflight(api,selected)],[textForUI("ui.scientificSources.messageViewDownloadActivity"),()=>openDownloadActivity(api)],[textForUI("ui.missionSelection.messageEditInFlightPlanner"),()=>{location.hash='#planner?researchPlan='+selected;}],[textForUI("ui.missionSelection.messageViewOnMap"),()=>document.getElementById('mission-map').scrollIntoView()]]){const b=el('button',title);b.type='button';b.className='secondary';b.onclick=()=>Promise.resolve(run()).catch(e=>setPresentationText(status,e.message));buttons.append(b);}
   const b=el('button',localizedCopy("ui.missionSelection.messageRefreshMissions"));b.type='button';b.className='secondary';b.onclick=refresh;buttons.append(b);
  }catch(error){if(own===generation)setPresentationText(status,error.message);}
  finally{loading=false;if(queued){queued=false;refresh();}else onChange();}
 }
 select.onchange=async()=>{try{await api('/api/mission-selection',{planId:select.value||null});window.dispatchEvent(new Event('aeroscience-selection'));await refresh();}catch(error){setPresentationText(status,error.message);}};
 window.addEventListener('aeroscience-selection',refresh);window.addEventListener('aeroscience-session',refresh);refresh();
 // A fresh telemetry tick must not enable Start before a saved selection's required form values bind.
 // The recorder still checks the authoritative selected plan and its frozen revision on submission.
 return {refresh,canStart(snapshot){return ready&&!loading&&!queued&&selected===(snapshot.selectedPlanId||null)&&(select.value||null)===selected;},update(snapshot){lastState=snapshot;guide?.update(snapshot);const key=(snapshot.selectedPlanId||'')+'/'+(snapshot.activeMission?.id||'');if(key!==lastKey){lastKey=key;refresh();}}};
}
