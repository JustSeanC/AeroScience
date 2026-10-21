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
 const status=node('p','Review automation targets after editing geographic geometry, then save the mission plan.');status.setAttribute('role','status');root.append(status);
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
  triggers.push({id:newIdentity(),occurrenceId:newIdentity(),action:action.value,kind:kind.value,targetId:target.value,pass:Number(pass.value),toleranceMetres:Number(tolerance.value),directionTrueDegrees:kind.value==='directionalGate'?Number(direction.value):null,plannedSessionKey:key.value.trim()||null,initialInside:inside.checked?'startWhenInitiallyInside':'requireWitnessedEntry'});
  reviewed=null;render();dirty();status.textContent='Action added to draft. Review targets, then Save mission plan.';
 };root.append(add);
 function draft(){const plan=getPlan();return {version:1,missionPlanId:plan.id,initialPolicy:policy.value,triggers,continuity:{maximumGapSeconds:Number(gap.value),maximumPositionAgeSeconds:Number(age.value)},surveyAreas:identities.filter(a=>plan.surveyAreas[a.planAreaIndex]),boundary:boundary.checked?{surveyAreaIds:[...selectedAreas],pauseOnExit:true,resumeOnEntry:true,hysteresisMetres:Number(hysteresis.value),dwellSeconds:Number(dwell.value)}:null};}
 function signature(){return JSON.stringify({plan:getPlan(),collection:draft()});}
 const review=node('button','Refresh and review automation targets');review.type='button';review.onclick=async()=>{review.disabled=true;try{refreshTargets();const bound=await api('/api/plans/collection/bind',{...getPlan(),collection:draft()});identities=bound.surveyAreas;reviewed=signature();dirty();status.textContent=`Reviewed ${bound.triggers.length} actions. Explicit End only; ${bound.boundary?'boundary pause/resume enabled':'boundary automation off'}. No downloads or collection started. Save mission plan to retain this configuration.`;}catch(e){reviewed=null;status.textContent=e.message;}finally{review.disabled=false;}};root.append(review);
 kind.onchange=refreshTargets;root.addEventListener('input',()=>{reviewed=null;dirty();});root.addEventListener('toggle',()=>{if(root.open){refreshTargets();render();}});render();refreshTargets();
 return {read(){if(!enabled.checked)return null;if(reviewed!==signature())throw new Error('Refresh and review automation targets before saving. Geographic/configuration edits require a new review.');return draft();}};
}
