import { newIdentity } from "./workstation-ui.js";
// Extends the existing saved plan. Targets are stable IDs, not transient map indexes.
export function collectionPlanner(parent,initial,getPlan,api,dirty){
 const root=document.createElement('details');root.className='panel research-panel';root.id='collection-planner';parent.append(root);
 const node=(tag,value)=>{const n=document.createElement(tag);n.textContent=value;return n;};root.append(node('summary','Scientific collection automation'));
 root.append(node('p','Configure optional Start, Pause, Resume and End-current-session actions. End is never inferred from the last waypoint or an area exit. Flight recording continues across science sessions.'));
 function input(label,value='',type='text'){const wrap=node('label',label),field=document.createElement('input');field.setAttribute('aria-label',label);field.type=type;if(type==='number')field.step='any';if(type==='checkbox')field.checked=!!value;else field.value=value;wrap.append(field);root.append(wrap);return field;}
 function select(label,options,value){const wrap=node('label',label),field=document.createElement('select');field.setAttribute('aria-label',label);for(const [v,t]of options){const o=node('option',t);o.value=v;field.append(o);}if(value)field.value=value;wrap.append(field);root.append(wrap);return field;}
 const enabled=input('Configure scientific collection automation',!!initial,'checkbox');
 const policy=select('Initial science policy',[['armed','Armed — wait for configured Start'],['manual','Manual — pilot controls science']],initial?.initialPolicy||'armed');
 const gap=input('Maximum continuous position gap (seconds)',initial?.continuity.maximumGapSeconds??6,'number');gap.min=.1;gap.max=60;
 const age=input('Maximum position age (seconds)',initial?.continuity.maximumPositionAgeSeconds??6,'number');age.min=.1;age.max=60;
 const action=select('Collection action',[['startData','Start Data — new science session'],['pauseData','Pause Data'],['resumeData','Resume Data'],['endData','End current science session']]);
 const kind=select('Trigger geometry',[['waypoint','Observed waypoint proximity'],['directionalGate','Directional crossing gate'],['areaEntry','Survey-area entry'],['areaExit','Survey-area exit']]);
 const target=select('Saved geographic target',[]);
 const pass=input('Target visit / pass number',1,'number');pass.min=1;pass.max=1000;
 const tolerance=input('Waypoint tolerance / gate half-width (m)',100,'number');tolerance.min=.01;tolerance.max=10000;
 const direction=input('Gate crossing direction (degrees true)',90,'number');direction.min=0;direction.max=359.999999;
 const key=input('Planned session key (optional, e.g. A or B)','');key.maxLength=100;
 const inside=input('Start when initially inside (explicit permission)',false,'checkbox');
 let triggers=structuredClone(initial?.triggers||[]),identities=structuredClone(initial?.surveyAreas||[]),reviewed=null;
 const list=node('ul','');root.append(list);
 const boundary=input('Pause outside selected survey areas and resume on re-entry',!!initial?.boundary,'checkbox');
 const boundaryAreas=node('fieldset','');boundaryAreas.append(node('legend','Boundary regions (union — inside any selected region)'));root.append(boundaryAreas);
 const selectedAreas=new Set(initial?.boundary?.surveyAreaIds||[]);
 const hysteresis=input('Boundary hysteresis (m)',initial?.boundary?.hysteresisMetres??10,'number');hysteresis.min=0;hysteresis.max=1000;
 const dwell=input('Boundary dwell time (seconds)',initial?.boundary?.dwellSeconds??2,'number');dwell.min=0;dwell.max=60;
 const status=node('p','Review automation targets after editing geographic geometry, then save the mission plan.');status.setAttribute('role','status');status.setAttribute('aria-label','Automation review');root.append(status);
 function refreshTargets(){const plan=getPlan();const prior=target.value;target.replaceChildren();
  for(const [i,area]of (plan.surveyAreas||[]).entries())if(!identities.some(a=>a.planAreaIndex===i))identities.push({id:newIdentity(),planAreaIndex:i,geometrySha256:''});
  const values=kind.value.startsWith('area')?identities.filter(a=>plan.surveyAreas[a.planAreaIndex]).map(a=>[a.id,plan.surveyAreas[a.planAreaIndex].name]):(plan.waypoints||[]).map(w=>[w.id,w.name]);
  for(const [id,name]of values){const option=node('option',name);option.value=id;target.append(option);}if(values.some(v=>v[0]===prior))target.value=prior;
  boundaryAreas.querySelectorAll('label').forEach(n=>n.remove());
  for(const area of identities.filter(a=>plan.surveyAreas[a.planAreaIndex])){const label=node('label',plan.surveyAreas[area.planAreaIndex].name),check=document.createElement('input');check.type='checkbox';check.checked=selectedAreas.has(area.id);check.onchange=()=>{if(check.checked)selectedAreas.add(area.id);else selectedAreas.delete(area.id);reviewed=null;dirty();};label.append(check);boundaryAreas.append(label);}
 }
 function render(){list.replaceChildren();for(const t of triggers){const plan=getPlan(),binding=identities.find(a=>a.id===t.targetId),name=plan.waypoints.find(w=>w.id===t.targetId)?.name??plan.surveyAreas[binding?.planAreaIndex]?.name??'Missing target — review required';const labels={startData:'Start Data',pauseData:'Pause Data',resumeData:'Resume Data',endData:'End current science session'};const row=node('li',`${labels[t.action]||t.action} · ${name} · ${t.kind} · pass ${t.pass}${t.plannedSessionKey?' · session '+t.plannedSessionKey:''}`),remove=node('button','Remove action');remove.type='button';remove.onclick=()=>{triggers=triggers.filter(v=>v.id!==t.id);reviewed=null;render();dirty();};row.append(remove);list.append(row);}}
 const add=node('button','Add collection action');add.type='button';add.onclick=()=>{if(!target.value){status.textContent='Save draft waypoints or survey areas, then refresh targets first.';return;}
  if(action.value==='endData'&&kind.value.startsWith('area')){status.textContent='Automatic End requires an explicit waypoint or directional gate. An area exit may Pause, never End.';return;}
  if(triggers.length>=128){status.textContent='A plan supports at most 128 explicit actions.';return;}
  triggers.push({id:newIdentity(),occurrenceId:newIdentity(),action:action.value,kind:kind.value,targetId:target.value,pass:Number(pass.value),toleranceMetres:kind.value.startsWith('area')?100:Number(tolerance.value),directionTrueDegrees:kind.value==='directionalGate'?Number(direction.value):null,plannedSessionKey:key.value.trim()||null,initialInside:kind.value==='areaEntry'&&action.value==='startData'&&inside.checked?'startWhenInitiallyInside':'requireWitnessedEntry'});
  reviewed=null;render();dirty();status.textContent='Action added to draft. Review targets, then Save mission plan.';
 };root.append(add);
 function draft(){const plan=getPlan();return {version:1,missionPlanId:plan.id,initialPolicy:policy.value,triggers,continuity:{maximumGapSeconds:Number(gap.value),maximumPositionAgeSeconds:Number(age.value)},surveyAreas:identities.filter(a=>plan.surveyAreas[a.planAreaIndex]),boundary:boundary.checked?{surveyAreaIds:[...selectedAreas],pauseOnExit:true,resumeOnEntry:true,hysteresisMetres:Number(hysteresis.value),dwellSeconds:Number(dwell.value)}:null};}
 function signature(){return JSON.stringify({plan:getPlan(),collection:draft()});}
 const review=node('button','Review Automation Rules');review.type='button';review.onclick=async()=>{review.disabled=true;try{refreshTargets();const bound=await api('/api/plans/collection/bind',{...getPlan(),collection:draft()});identities=bound.surveyAreas;reviewed=signature();dirty();updateSummary();status.textContent=`Reviewed ${bound.triggers.length} actions. Explicit End only; ${bound.boundary?'boundary pause/resume enabled':'boundary automation off'}. No downloads or collection started. Save mission plan to retain this configuration.`;}catch(e){reviewed=null;status.textContent=e.message;}finally{review.disabled=false;}};root.append(review);

 const overview=node('p','');overview.setAttribute('aria-label','Automation summary');overview.setAttribute('role','status');root.querySelector('summary').after(overview);
 const group=(title,fields,fold=false)=>{const box=node(fold?'details':'fieldset','');box.append(node(fold?'summary':'legend',title));box.className='automation-grid';for(const field of fields)box.append(field.closest('label')||field);root.append(box);return box;};
 group('Collection policy',[enabled,policy]);
 const builder=group('Build a collection rule',[action,kind,target,pass,tolerance,direction,key,inside]);
 const rulePreview=node('p','');rulePreview.setAttribute('aria-label','Rule preview');rulePreview.setAttribute('aria-live','polite');builder.append(rulePreview,add);
 const boundaries=group('Optional survey-boundary pause/resume',[boundary,boundaryAreas,hysteresis,dwell]);
 group('Advanced — Position Reliability',[gap,age],true);
 const help=(field,text)=>{const note=node('small',text),id='automation-help-'+newIdentity();note.id=id;field.setAttribute('aria-describedby',id);field.closest('label').append(note);};
 help(enabled,'Configures draft automation only. It becomes eligible after plan review/save and a flight using that plan; editing never starts flight recording or collection.');
 help(policy,'Armed waits for an explicitly configured Start. Manual leaves the pilot in control. Manual End suspends automatic starts until explicit rearm; manual Start New Session remains possible.');
 help(action,'Start creates a new science session; Pause/Resume retain the current session; End closes only that session. End Flight is separate. Automatic End is allowed only at an explicit waypoint or directional gate.');
 help(kind,'Waypoint proximity uses observed positions within tolerance. A gate requires a directional crossing. Area entry/exit uses supported boundary geometry and continuity, not the final waypoint.');
 help(target,'Choose a saved waypoint or named survey area. Stable IDs are preserved when targets are refreshed; removed targets must be corrected before review.');
 help(pass,'1 means the first qualifying observed visit/crossing; later numbered passes require actual witnessed re-entry/crossing. Duplicate or stale telemetry does not create a new visit. Accepted range: 1–1000.');
 help(tolerance,'Meters: waypoint proximity radius or directional gate half-width, according to the selected trigger. Accepted range: greater than 0 to 10,000 m. Area rules use actual polygon geometry instead.');
 help(direction,'Degrees true (0–less than 360), not magnetic. Specifies the permitted crossing direction for a directional gate; ignored and omitted for other geometries.');
 help(key,'Optional planned session identity (up to 100 characters). Start assigns it; subsequent actions may target that planned session and cannot be redirected to another one. This is not a flight or database ID.');
 help(inside,'Explicitly permits a Start on an initial qualifying observation already inside this area. Other entry actions require a witnessed entry; it does not bypass telemetry safeguards.');
 help(boundary,'Optional pause on leaving the union of the checked areas and resume on re-entry to any selected area. Does not End a session. Manual holds and End still require pilot rearm where applicable.');
 help(hysteresis,'Meters (0–1000). An edge margin helps prevent repeated transitions near a boundary. The current engine also applies its boundary policy to supported area transitions; this does not enlarge scientific coverage.');
 help(dwell,'Seconds (0–60). Requires the relevant boundary condition to persist where the engine applies it; it is not a delay for every waypoint/gate action.');
 help(gap,'Seconds (0.1–60). Maximum acceptable interval between telemetry observations for continuity evaluation. Gaps/jumps cannot imply unseen crossings.');
 help(age,'Seconds (0.1–60). Maximum age of the current position at evaluation; older positions are stale, even if two samples are close together.');
 function updateSummary(){const plan=getPlan(),labels={startData:'Start a new science session',pauseData:'Pause science',resumeData:'Resume the current science session',endData:'End the current science session'};
  const name=target.selectedOptions[0]?.textContent||'a saved target';
  const where=kind.value==='waypoint'?`comes within ${tolerance.value} meters of ${name}`:kind.value==='directionalGate'?`crosses ${name} in the selected ${direction.value}° true direction (gate half-width ${tolerance.value} m)`:kind.value==='areaEntry'?`enters ${name}${inside.checked&&action.value==='startData'?' (initial-inside Start explicitly allowed)':''}`:`exits ${name}`;
  rulePreview.textContent=`${labels[action.value]} when the aircraft ${where}, on qualifying visit ${pass.value}. Subject to continuity, authority and current-session safeguards. Review this rule, then Add collection action; Save mission plan remains separate.`;
  const area=kind.value.startsWith('area'),gate=kind.value==='directionalGate';tolerance.closest('label').hidden=area;direction.closest('label').hidden=!gate;inside.closest('label').hidden=kind.value!=='areaEntry'||action.value!=='startData';
  for(const field of[boundaryAreas,hysteresis.closest('label'),dwell.closest('label')])field.hidden=!boundary.checked;
  add.disabled=area&&action.value==='endData';if(add.disabled)rulePreview.textContent='Automatic End requires an explicit waypoint or directional gate. Leaving a survey area may Pause, never End a session.';
  const missing=triggers.some(t=>!((plan.waypoints||[]).some(w=>w.id===t.targetId)||identities.some(a=>a.id===t.targetId&&plan.surveyAreas?.[a.planAreaIndex])));
  overview.textContent=`${enabled.checked?'Automatic collection is configured in this draft':'Automatic collection is off'}. ${triggers.length} explicit collection action(s). ${boundary.checked?'Pauses outside the selected boundary union; re-entry may resume the same session.':'Boundary pause/resume is off.'} ${!triggers.some(t=>t.action==='startData')?'No Start rule: use manual Start or configure one. ':''}${missing?'A referenced geographic target is missing. ':''}${boundary.checked&&!selectedAreas.size?'No boundary regions selected. ':''}${reviewed?'Rules reviewed for this draft.':'Rules are not yet reviewed for the current draft.'} Manual override can require explicit rearm during flight. Configured rules are not proof of active automation.`;
 }
 kind.onchange=()=>{refreshTargets();updateSummary();};for(const field of[action,target,pass,tolerance,direction,key,inside,boundary,enabled,policy])field.addEventListener('change',updateSummary);
 root.addEventListener('input',()=>{reviewed=null;dirty();updateSummary();});root.addEventListener('click',()=>queueMicrotask(updateSummary));root.addEventListener('toggle',()=>{if(root.open){refreshTargets();render();updateSummary();}});root.append(list,review,status);render();refreshTargets();updateSummary();

 return {read(){if(!enabled.checked)return null;if(reviewed!==signature())throw new Error('Refresh and review automation targets before saving. Geographic/configuration edits require a new review.');return draft();}};
}
