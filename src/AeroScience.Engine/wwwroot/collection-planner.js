import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import { newIdentity } from "./workstation-ui.js";
// Extends the existing saved plan. Targets are stable IDs, not transient map indexes.
export function collectionPlanner(parent,initial,getPlan,api,dirty){
 const root=document.createElement('details');root.className='panel research-panel';root.id='collection-planner';parent.append(root);
 const node=(tag,value)=>{const n=document.createElement(tag);setPresentationText(n,value);return n;};root.append(node('summary',localizedCopy("ui.collectionPlanner.messageScientificCollectionAutomation")));
 root.append(node('p',localizedCopy("ui.collectionPlanner.messageConfigureOptionalStartPauseResumeAndEndCurrentSession")));
 function input(label,value='',type='text'){const wrap=node('label',label),field=document.createElement('input');setPresentationText(field,label,"aria-label");field.type=type;if(type==='number')field.step='any';if(type==='checkbox')field.checked=!!value;else field.value=value;wrap.append(field);root.append(wrap);return field;}
 function select(label,options,value){const wrap=node('label',label),field=document.createElement('select');setPresentationText(field,label,"aria-label");for(const [v,t]of options){const o=node('option',t);o.value=v;field.append(o);}if(value)field.value=value;wrap.append(field);root.append(wrap);return field;}
 const enabled=input(localizedCopy("ui.collectionPlanner.messageConfigureScientificCollectionAutomation"),!!initial,'checkbox');
 const policy=select(localizedCopy("ui.collectionPlanner.messageInitialSciencePolicy"),[['armed',localizedCopy("ui.collectionPlanner.messageArmedWaitForConfiguredStart")],['manual',localizedCopy("ui.collectionPlanner.messageManualPilotControlsScience")]],initial?.initialPolicy||'armed');
 const gap=input(localizedCopy("ui.collectionPlanner.messageMaximumContinuousPositionGapSeconds"),initial?.continuity.maximumGapSeconds??6,'number');gap.min=.1;gap.max=60;
 const age=input(localizedCopy("ui.collectionPlanner.messageMaximumPositionAgeSeconds"),initial?.continuity.maximumPositionAgeSeconds??6,'number');age.min=.1;age.max=60;
 const action=select(localizedCopy("ui.collectionPlanner.messageCollectionAction"),[['startData',localizedCopy("ui.collectionPlanner.messageStartDataNewScienceSession")],['pauseData',localizedCopy("ui.collectionPlanner.messagePauseData")],['resumeData',localizedCopy("ui.collectionPlanner.messageResumeData")],['endData',localizedCopy("flight.action.endData")]]);
 const kind=select(localizedCopy("ui.collectionPlanner.messageTriggerGeometry"),[['waypoint',localizedCopy("ui.collectionPlanner.messageObservedWaypointProximity")],['directionalGate',localizedCopy("ui.collectionPlanner.messageDirectionalCrossingGate")],['areaEntry',localizedCopy("ui.collectionPlanner.messageSurveyAreaEntry")],['areaExit',localizedCopy("ui.collectionPlanner.messageSurveyAreaExit")]]);
 const target=select(localizedCopy("ui.collectionPlanner.messageSavedGeographicTarget"),[]);
 const pass=input(localizedCopy("ui.collectionPlanner.messageTargetVisitPassNumber"),1,'number');pass.min=1;pass.max=1000;
 const tolerance=input(localizedCopy("ui.collectionPlanner.messageWaypointToleranceGateHalfWidthM"),100,'number');tolerance.min=.01;tolerance.max=10000;
 const direction=input(localizedCopy("ui.collectionPlanner.messageGateCrossingDirectionDegreesTrue"),90,'number');direction.min=0;direction.max=359.999999;
 const key=input(localizedCopy("ui.collectionPlanner.messagePlannedSessionKeyOptionalEGAOr"),'');key.maxLength=100;
 const inside=input(localizedCopy("ui.collectionPlanner.messageStartWhenInitiallyInsideExplicitPermission"),false,'checkbox');
 let triggers=structuredClone(initial?.triggers||[]),identities=structuredClone(initial?.surveyAreas||[]),reviewed=null;
 const list=node('ul','');root.append(list);
 const boundary=input(localizedCopy("ui.collectionPlanner.messagePauseOutsideSelectedSurveyAreasAndResume"),!!initial?.boundary,'checkbox');
 const boundaryAreas=node('fieldset','');boundaryAreas.append(node('legend',localizedCopy("ui.collectionPlanner.messageBoundaryRegionsUnionInsideAnySelected")));root.append(boundaryAreas);
 const selectedAreas=new Set(initial?.boundary?.surveyAreaIds||[]);
 const hysteresis=input(localizedCopy("ui.collectionPlanner.messageBoundaryHysteresisM"),initial?.boundary?.hysteresisMetres??10,'number');hysteresis.min=0;hysteresis.max=1000;
 const dwell=input(localizedCopy("ui.collectionPlanner.messageBoundaryDwellTimeSeconds"),initial?.boundary?.dwellSeconds??2,'number');dwell.min=0;dwell.max=60;
 const status=node('p',localizedCopy("ui.collectionPlanner.messageReviewAutomationTargetsAfterEditingGeographicGeometry"));status.setAttribute('role','status');setPresentationText(status,localizedCopy("ui.collectionPlanner.messageAutomationReview"),"aria-label");root.append(status);
 function refreshTargets(){const plan=getPlan();const prior=target.value;target.replaceChildren();
  for(const [i,area]of (plan.surveyAreas||[]).entries())if(!identities.some(a=>a.planAreaIndex===i))identities.push({id:newIdentity(),planAreaIndex:i,geometrySha256:''});
  const values=kind.value.startsWith('area')?identities.filter(a=>plan.surveyAreas[a.planAreaIndex]).map(a=>[a.id,plan.surveyAreas[a.planAreaIndex].name]):(plan.waypoints||[]).map(w=>[w.id,w.name]);
  for(const [id,name]of values){const option=node('option',name);option.value=id;target.append(option);}if(values.some(v=>v[0]===prior))target.value=prior;
  boundaryAreas.querySelectorAll('label').forEach(n=>n.remove());
  for(const area of identities.filter(a=>plan.surveyAreas[a.planAreaIndex])){const label=node('label',plan.surveyAreas[area.planAreaIndex].name),check=document.createElement('input');check.type='checkbox';check.checked=selectedAreas.has(area.id);check.onchange=()=>{if(check.checked)selectedAreas.add(area.id);else selectedAreas.delete(area.id);reviewed=null;dirty();};label.append(check);boundaryAreas.append(label);}
 }
 function render(){list.replaceChildren();for(const t of triggers){const plan=getPlan(),binding=identities.find(a=>a.id===t.targetId),name=plan.waypoints.find(w=>w.id===t.targetId)?.name??plan.surveyAreas[binding?.planAreaIndex]?.name??textForUI("ui.collectionPlanner.messageMissingTargetReviewRequired");const labels={startData:textForUI("ui.collectionPlanner.messageStartData"),pauseData:textForUI("ui.collectionPlanner.messagePauseData"),resumeData:textForUI("ui.collectionPlanner.messageResumeData"),endData:textForUI("flight.action.endData")};const row=node('li',localizedCopy("ui.collectionPlanner.messagePass",{value1:(String(labels[t.action]||t.action)),value2:(String(name)),value3:(String(t.kind)),value4:(String(t.pass)),value5:(String(t.plannedSessionKey?' · session '+t.plannedSessionKey:''))})),remove=node('button',localizedCopy("ui.collectionPlanner.messageRemoveAction"));remove.type='button';remove.onclick=()=>{triggers=triggers.filter(v=>v.id!==t.id);reviewed=null;render();dirty();};row.append(remove);list.append(row);}}
 const add=node('button',localizedCopy("ui.collectionPlanner.messageAddCollectionAction"));add.type='button';add.onclick=()=>{if(!target.value){setPresentationText(status,localizedCopy("ui.collectionPlanner.messageSaveDraftWaypointsOrSurveyAreasThen"));return;}
  if(action.value==='endData'&&kind.value.startsWith('area')){setPresentationText(status,localizedCopy("ui.collectionPlanner.messageAutomaticEndRequiresAnExplicitWaypointOr"));return;}
  if(triggers.length>=128){setPresentationText(status,localizedCopy("ui.collectionPlanner.messageAPlanSupportsAtMost128Explicit"));return;}
  triggers.push({id:newIdentity(),occurrenceId:newIdentity(),action:action.value,kind:kind.value,targetId:target.value,pass:Number(pass.value),toleranceMetres:kind.value.startsWith('area')?100:Number(tolerance.value),directionTrueDegrees:kind.value==='directionalGate'?Number(direction.value):null,plannedSessionKey:key.value.trim()||null,initialInside:kind.value==='areaEntry'&&action.value==='startData'&&inside.checked?'startWhenInitiallyInside':'requireWitnessedEntry'});
  reviewed=null;render();dirty();setPresentationText(status,localizedCopy("ui.collectionPlanner.messageActionAddedToDraftReviewTargetsThen"));
 };root.append(add);
 function draft(){const plan=getPlan();return {version:1,missionPlanId:plan.id,initialPolicy:policy.value,triggers,continuity:{maximumGapSeconds:Number(gap.value),maximumPositionAgeSeconds:Number(age.value)},surveyAreas:identities.filter(a=>plan.surveyAreas[a.planAreaIndex]),boundary:boundary.checked?{surveyAreaIds:[...selectedAreas],pauseOnExit:true,resumeOnEntry:true,hysteresisMetres:Number(hysteresis.value),dwellSeconds:Number(dwell.value)}:null};}
 function signature(){return JSON.stringify({plan:getPlan(),collection:draft()});}
 const review=node('button',localizedCopy("ui.collectionPlanner.messageReviewAutomationRules"));review.type='button';review.onclick=async()=>{review.disabled=true;try{refreshTargets();const bound=await api('/api/plans/collection/bind',{...getPlan(),collection:draft()});identities=bound.surveyAreas;reviewed=signature();dirty();updateSummary();setPresentationText(status,localizedCopy("ui.collectionPlanner.messageReviewedActionsExplicitEndOnlyNo",{value1:(String(bound.triggers.length)),value2:(String(bound.boundary?'boundary pause/resume enabled':'boundary automation off'))}));}catch(e){reviewed=null;setPresentationText(status,e.message);}finally{review.disabled=false;}};root.append(review);

 const overview=node('p','');setPresentationText(overview,localizedCopy("ui.collectionPlanner.messageAutomationSummary"),"aria-label");overview.setAttribute('role','status');root.querySelector('summary').after(overview);
 const group=(title,fields,fold=false)=>{const box=node(fold?'details':'fieldset','');box.append(node(fold?'summary':'legend',title));box.className='automation-grid';for(const field of fields)box.append(field.closest('label')||field);root.append(box);return box;};
 group(localizedCopy("ui.collectionPlanner.messageCollectionPolicy"),[enabled,policy]);
 const builder=group(localizedCopy("ui.collectionPlanner.messageBuildACollectionRule"),[action,kind,target,pass,tolerance,direction,key,inside]);
 const rulePreview=node('p','');setPresentationText(rulePreview,localizedCopy("ui.collectionPlanner.messageRulePreview"),"aria-label");rulePreview.setAttribute('aria-live','polite');builder.append(rulePreview,add);
 const boundaries=group(localizedCopy("ui.collectionPlanner.messageOptionalSurveyBoundaryPauseResume"),[boundary,boundaryAreas,hysteresis,dwell]);
 group(localizedCopy("ui.collectionPlanner.messageAdvancedPositionReliability"),[gap,age],true);
 const help=(field,text)=>{const note=node('small',text),id='automation-help-'+newIdentity();note.id=id;field.setAttribute('aria-describedby',id);field.closest('label').append(note);};
 help(enabled,localizedCopy("ui.collectionPlanner.messageConfiguresDraftAutomationOnlyItBecomesEligible"));
 help(policy,localizedCopy("ui.collectionPlanner.messageArmedWaitsForAnExplicitlyConfiguredStart"));
 help(action,localizedCopy("ui.collectionPlanner.messageStartCreatesANewScienceSessionPauseResume"));
 help(kind,localizedCopy("ui.collectionPlanner.messageWaypointProximityUsesObservedPositionsWithinTolerance"));
 help(target,localizedCopy("ui.collectionPlanner.messageChooseASavedWaypointOrNamedSurvey"));
 help(pass,localizedCopy("ui.collectionPlanner.message1MeansTheFirstQualifyingObservedVisitCrossing"));
 help(tolerance,localizedCopy("ui.collectionPlanner.messageMetersWaypointProximityRadiusOrDirectionalGate"));
 help(direction,localizedCopy("ui.collectionPlanner.messageDegreesTrue0LessThan360NotMagnetic"));
 help(key,localizedCopy("ui.collectionPlanner.messageOptionalPlannedSessionIdentityUpTo100"));
 help(inside,localizedCopy("ui.collectionPlanner.messageExplicitlyPermitsAStartOnAnInitial"));
 help(boundary,localizedCopy("ui.collectionPlanner.messageOptionalPauseOnLeavingTheUnionOf"));
 help(hysteresis,localizedCopy("ui.collectionPlanner.messageMeters01000AnEdgeMarginHelpsPrevent"));
 help(dwell,localizedCopy("ui.collectionPlanner.messageSeconds060RequiresTheRelevantBoundaryCondition"));
 help(gap,localizedCopy("ui.collectionPlanner.messageSeconds0160MaximumAcceptableIntervalBetweenTelemetry"));
 help(age,localizedCopy("ui.collectionPlanner.messageSeconds0160MaximumAgeOfTheCurrent"));
 function updateSummary(){const plan=getPlan(),labels={startData:textForUI("ui.collectionPlanner.messageStartANewScienceSession"),pauseData:textForUI("ui.collectionPlanner.messagePauseScience"),resumeData:textForUI("ui.collectionPlanner.messageResumeTheCurrentScienceSession"),endData:textForUI("ui.collectionPlanner.messageEndTheCurrentScienceSession")};
  const name=target.selectedOptions[0]?.textContent||'a saved target';
  const where=kind.value==='waypoint'?`comes within ${tolerance.value} meters of ${name}`:kind.value==='directionalGate'?`crosses ${name} in the selected ${direction.value}° true direction (gate half-width ${tolerance.value} m)`:kind.value==='areaEntry'?`enters ${name}${inside.checked&&action.value==='startData'?' (initial-inside Start explicitly allowed)':''}`:`exits ${name}`;
  setPresentationText(rulePreview,localizedCopy("ui.collectionPlanner.messageWhenTheAircraftOnQualifying",{value1:(String(labels[action.value])),value2:(String(where)),value3:(String(pass.value))}));
  const area=kind.value.startsWith('area'),gate=kind.value==='directionalGate';tolerance.closest('label').hidden=area;direction.closest('label').hidden=!gate;inside.closest('label').hidden=kind.value!=='areaEntry'||action.value!=='startData';
  for(const field of[boundaryAreas,hysteresis.closest('label'),dwell.closest('label')])field.hidden=!boundary.checked;
  add.disabled=area&&action.value==='endData';if(add.disabled)setPresentationText(rulePreview,localizedCopy("ui.collectionPlanner.messageAutomaticEndRequiresAnExplicitWaypointOr64f6d9c3"));
  const missing=triggers.some(t=>!((plan.waypoints||[]).some(w=>w.id===t.targetId)||identities.some(a=>a.id===t.targetId&&plan.surveyAreas?.[a.planAreaIndex])));
  setPresentationText(overview,localizedCopy("ui.collectionPlanner.messageExplicitCollectionActionSManualOverride",{value1:(String(enabled.checked?'Automatic collection is configured in this draft':'Automatic collection is off')),value2:(String(triggers.length)),value3:(String(boundary.checked?'Pauses outside the selected boundary union; re-entry may resume the same session.':'Boundary pause/resume is off.')),value4:(String(!triggers.some(t=>t.action==='startData')?'No Start rule: use manual Start or configure one. ':'')),value5:(String(missing?'A referenced geographic target is missing. ':'')),value6:(String(boundary.checked&&!selectedAreas.size?'No boundary regions selected. ':'')),value7:(String(reviewed?'Rules reviewed for this draft.':'Rules are not yet reviewed for the current draft.'))}));
 }
 kind.onchange=()=>{refreshTargets();updateSummary();};for(const field of[action,target,pass,tolerance,direction,key,inside,boundary,enabled,policy])field.addEventListener('change',updateSummary);
 root.addEventListener('input',()=>{reviewed=null;dirty();updateSummary();});root.addEventListener('click',()=>queueMicrotask(updateSummary));root.addEventListener('toggle',()=>{if(root.open){refreshTargets();render();updateSummary();}});root.append(list,review,status);render();refreshTargets();updateSummary();

 return {read(){if(!enabled.checked)return null;if(reviewed!==signature())throw new Error(textForUI("ui.collectionPlanner.messageRefreshAndReviewAutomationTargetsBeforeSaving"));return draft();}};
}
