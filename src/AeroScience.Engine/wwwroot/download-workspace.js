import {node as el,numeric as number,bytes,readableName,button,link,select,status,disclosure,errorMessage,metric} from './workstation-ui.js';
import {renderInstrumentSummaries,instrumentSummaries} from './preparation-summary.js';
import {openDownloadQuickStatus,reviewPreflight,allowanceText} from './download-activity.js';
let dispose=()=>{};
export function disposeDownloadWorkspace(){dispose();}
export async function downloadWorkspace(parent,api){
  dispose();let stopped=false,timer,busy=false,lastState='';dispose=()=>{stopped=true;clearTimeout(timer);};
  parent.replaceChildren(el('div',undefined,'page-heading'));
  const heading=parent.firstChild;heading.append(el('div'));heading.firstChild.append(el('p','PREPARE / SCIENTIFIC DATA','eyebrow'),el('h1','Download Activity'),el('p','Mission readiness and background retrieval. Ready source files do not guarantee cloud-free or complete scientific pixels.'));
  button(heading,'Quick status',()=>openDownloadQuickStatus(api));
  const controls=el('div',undefined,'workspace-controls'),mission=select(controls,'Preparation mission',[['','All preparation and background activity']],'');parent.append(controls);
  const announcement=status(parent,'Loading scientific preparation status…'),overview=el('div',undefined,'review-kpis'),sessions=el('div',undefined,'download-sessions'),instruments=el('div');
  parent.append(overview,sessions,instruments);const advanced=disclosure(parent,'Advanced — individual regions & source diagnostics');
  const filters=el('div',undefined,'workspace-controls');advanced.append(filters);
  const activity=select(filters,'Activity',[['active','Active'],['queued','Queued'],['completed','Completed'],['failed','Failed'],['all','All']],'all');
  const instrument=select(filters,'Download instrument',[['','All instruments'],...['terrain','thermal','multispectral','hyperspectral'].map(kind=>[kind,readableName(kind)])],'');
  const provider=select(filters,'Download provider',[['','All providers']],'');
  const allowance=el('p'),list=el('div',undefined,'download-jobs'),pageInfo=el('p');advanced.append(allowance,pageInfo,list);
  let limit=30,data=null;const rows=new Map(),sessionRows=new Map(),jobOrder=new Map();let nextJobOrder=0;
  const more=button(advanced,'View More Activity',()=>{limit=Math.min(120,limit+30);render();});
  const global=disclosure(parent,'Global background-download controls and network diagnostics');const globalControl=el('div',undefined,'action-row'),diagnostics=el('pre');global.append(el('p','These controls apply to all new scientific retrieval, separately from a selected mission’s preparation. Flight recording continues.'),globalControl,diagnostics);
  button(global,'Export Scientific Diagnostics',async()=>{if(!confirm('This report includes geographic preparation areas, provider/instrument identities, timing and errors. It contains no scientific originals or credentials. Review it before sharing.'))return;const report=await api('/api/science/diagnostics'),url=URL.createObjectURL(new Blob([JSON.stringify(report,null,2)],{type:'application/json'}));const a=el('a');a.href=url;a.download='AeroScience-scientific-diagnostics.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
  link(parent,'Scientific data settings','#settings/science');link(parent,'Why preparation may be incomplete','#help?guide=v0.4.2-pilot-guide.md');
  let chosen=new URLSearchParams(location.hash.split('?')[1]||'').get('plan')||'';
  function group(job){return ['Queued','Waiting for download allowance'].includes(job.status)?'queued':job.status==='Downloading'?'active':['Ready','Dataset Ready','Partially usable','Limited Coverage'].includes(job.status)?'completed':'failed';}
  function render(){
    if(!data||stopped)return;
    const previous=mission.value;mission.replaceChildren();for(const [id,title] of [['','All preparation and background activity'],...data.plans.map(p=>[p.planId,p.planName])]){const option=el('option',title);option.value=id;mission.append(option);}mission.value=chosen||previous||'';
    const selected=data.plans.filter(p=>!mission.value||p.planId===mission.value);for(const [id,card] of sessionRows)if(!selected.some(plan=>plan.planId===id)){card.remove();sessionRows.delete(id);}overview.replaceChildren();
    metric(overview,'Current downloads',number(data.queue.downloading),data.queue.paused?'Global retrieval paused':'Existing completed data stays available');metric(overview,'Queued retrieval',number(data.queue.queued),'Subject to consent, allowance and storage limits');metric(overview,'Transferred this session',bytes(data.queue.combinedNetworkBytes||0),'Actual network bytes, not retained-file size');
    for(const plan of selected){let card=sessionRows.get(plan.planId);if(!card){card=el('article',undefined,'panel preparation-session');card.append(el('h2',plan.planName),el('p'),el('div',undefined,'action-row'));sessionRows.set(plan.planId,card);sessions.append(card);}const text=card.children[1],actions=card.children[2];
      text.textContent=`${plan.planName} · ${plan.status} · ${plan.ready} ready / ${plan.awaiting} awaiting / ${plan.unavailable} limited or unavailable regions.`;
      const available=plan.status==='Paused'?['resume','cancel']:plan.status==='Waiting for active flight'?['pause','cancel']:plan.awaiting?['pause','cancel']:[];const actionKey=available.join(',')+'|'+!!plan.unavailable;if(actions.dataset.key!==actionKey){actions.dataset.key=actionKey;actions.replaceChildren();for(const action of available){
        const b=button(actions,action[0].toUpperCase()+action.slice(1),async()=>{if(action==='cancel'&&!confirm('Cancel this mission’s remaining preparation? Retained sources and recorded missions will be preserved.'))return;await api(`/api/plans/${plan.planId}/preparation/control`,{action});await refresh(true);},action==='cancel'?'danger':'secondary');b.setAttribute('aria-label',`${action} Data Preparation for ${plan.planName}`);
      }
      if(plan.unavailable)button(actions,'Review and retry preparation',()=>reviewPreflight(api,plan.planId));}
    }
    const liveKeys=new Set(data.jobs.map(job=>job.key));for(const key of jobOrder.keys())if(!liveKeys.has(key))jobOrder.delete(key);for(const job of data.jobs)if(!jobOrder.has(job.key))jobOrder.set(job.key,nextJobOrder++);const jobs=data.jobs.filter(job=>!mission.value||job.scope==='plan:'+mission.value).sort((a,b)=>jobOrder.get(a.key)-jobOrder.get(b.key));
    if(!mission.value)renderInstrumentSummaries(instruments,instrumentSummaries(jobs));
    const currentState=selected.map(p=>p.planName+': '+p.status).join('. ')||`${data.queue.downloading} downloads active; ${data.queue.queued} queued.`;
    if(currentState!==lastState){announcement.textContent=(data.plansWarning?data.plansWarning+' ':'')+currentState;lastState=currentState;}
    allowance.textContent=allowanceText(data.queue);
    for(const name of new Set(data.jobs.map(job=>job.provider).filter(Boolean)))if(![...provider.options].some(option=>option.value===name)){const option=el('option',name);option.value=name;provider.append(option);}
    const filtered=jobs.filter(job=>(activity.value==='all'||group(job)===activity.value)&&(!instrument.value||job.kind===instrument.value)&&(!provider.value||job.provider===provider.value));
    const displayed=filtered.slice(0,limit),present=new Set(displayed.map(job=>job.key));for(const [key,row] of rows)if(!present.has(key)){row.remove();rows.delete(key);}
    for(const job of displayed){let row=rows.get(job.key);if(!row){row=el('details');row.append(el('summary'),el('p'),el('p'));rows.set(job.key,row);list.append(row);}row.children[0].textContent=`${readableName(job.kind)} · ${job.status} · ${job.provider||'Finding source'}`;row.children[1].textContent=job.message;row.children[2].textContent=`${job.dataset||'Source not selected'} · ${job.stage||job.status} · ${bytes(job.downloadedBytes)} transferred · ${job.estimatedDownloadBytes==null?'Download size unknown':bytes(job.estimatedDownloadBytes)} · Source observed ${job.observationUtc||'Unknown / composite'} · Area ${job.area.west.toFixed(4)}, ${job.area.south.toFixed(4)} to ${job.area.east.toFixed(4)}, ${job.area.north.toFixed(4)}.`;}
    pageInfo.textContent=`Showing ${displayed.length} of ${filtered.length} regional activities. Display is bounded to 120 rows. Size and completion time remain unknown until defensible.`;more.hidden=displayed.length===filtered.length||limit>=120;
    let globalButton=globalControl.querySelector('button');if(!globalButton)globalButton=button(globalControl,'',async()=>{await api('/api/science/preparation/control',{action:data.queue.paused?'resume':'pause'});await refresh(true);});globalButton.textContent=data.queue.paused?'Resume all new scientific downloads':'Pause all new scientific downloads';diagnostics.textContent=JSON.stringify(data.network,null,2);
  }
  async function refresh(force=false){if(stopped||busy||(!force&&document.hidden))return;busy=true;try{data=await api('/api/science/activity');if(stopped)return;render();if(mission.value){const items=await api(`/api/plans/${mission.value}/preparation/instruments`);if(!stopped)renderInstrumentSummaries(instruments,items);}}catch(error){if(!stopped)errorMessage(parent,error,()=>refresh(true));}finally{busy=false;if(!stopped){clearTimeout(timer);timer=setTimeout(refresh,3000);}}}
  for(const field of [activity,instrument,provider])field.onchange=()=>{limit=30;render();};mission.onchange=()=>{chosen=mission.value;const params=new URLSearchParams();if(chosen)params.set('plan',chosen);history.replaceState(null,'','#downloads'+(params.size?'?'+params:''));refresh(true);};
  await refresh(true);
}
