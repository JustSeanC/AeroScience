import {t} from './i18n.js';
// Advisory only: this module never saves, records or prepares scientific sources.
export function preflightReadiness(parent, readDraft, api,{id='preflight-readiness',active=()=>true,buttonClass='',saved=false}={}) {
  const panel=document.createElement('section'); panel.id=id;
  const title=document.createElement('h3');title.textContent='Preflight Science Readiness';
  const status=document.createElement('p');status.setAttribute('role','status');status.textContent=saved?t('missions.readinessUnassessed'):'Not yet assessed · evaluate this draft without saving.';
  const results=document.createElement('div');const run=document.createElement('button');run.type='button';run.textContent='Evaluate science readiness';
  run.className=buttonClass;
  panel.append(title,status,run,results);parent.append(panel);let generation=0,current=null,timer,disposed=false;
  const fingerprint=()=>JSON.stringify(readDraft());
  const invalidate=()=>{if(disposed)return;generation++;current=null;status.textContent='Stale · plan or context changed. Reassess before relying on these findings.';results.dataset.stale='true';};
  parent.addEventListener('input',invalidate);parent.addEventListener('change',invalidate);
  run.onclick=async()=>{
    if(disposed||!active())return;
    const ticket=++generation;let draft,hash;
    try {draft=readDraft();hash=JSON.stringify(draft);status.textContent='Assessing draft…';
      const response=await api('/api/plans/readiness',draft);
      if(disposed||!active()||!panel.isConnected||ticket!==generation||hash!==fingerprint())return;
      if(response?.version!==1||!Array.isArray(response.checks)||!response.checks.every(c=>['ready','caution','unavailable','notYetAssessed'].includes(c.status)&&Array.isArray(c.reasons)&&Array.isArray(c.actions))||!Array.isArray(response.limitations)||!Number.isFinite(Date.parse(response.assessedUtc))||Date.parse(response.assessedUtc)>Date.now()+2000||response.expiresUtc&&(!Number.isFinite(Date.parse(response.expiresUtc))||Date.parse(response.expiresUtc)<=Date.now()))throw Error(t('missions.invalidReadiness'));
      current=response;results.replaceChildren();delete results.dataset.stale;
      const counts=['ready','caution','unavailable','notYetAssessed'].map(value=>response.checks.filter(c=>c.status===value).length);
      status.textContent=`Assessed ${new Date(response.assessedUtc).toLocaleTimeString()} · ${counts[0]} ready · ${counts[1]} caution · ${counts[2]} unavailable · ${counts[3]} not assessed`;
      for(const category of [...new Set(response.checks.map(c=>c.category))]){
        const group=document.createElement('details');group.open=response.checks.some(c=>c.category===category&&c.status==='unavailable');const heading=document.createElement('summary');heading.textContent=category;group.append(heading);
        for(const check of response.checks.filter(c=>c.category===category)){
          const item=document.createElement('article');const label={ready:'Ready',caution:'Caution',unavailable:'Unavailable',notYetAssessed:'Not Yet Assessed'}[check.status]||check.status;
          const h=document.createElement('h4');h.textContent=`${label} · ${check.id}`;item.append(h);
          for(const line of [...check.reasons,...check.actions]){const p=document.createElement('p');p.textContent=line;item.append(p);}
          if(check.inputs?.length){const diagnostics=document.createElement('details'),title=document.createElement('summary');title.textContent='Advanced Diagnostics';diagnostics.append(title);for(const line of check.inputs){const p=document.createElement('p');p.textContent=line;diagnostics.append(p);}item.append(diagnostics);}group.append(item);
        }results.append(group);
      }
      const method=document.createElement('details'),methodTitle=document.createElement('summary');methodTitle.textContent='Assessment method & limitations';method.append(methodTitle);for(const line of response.limitations){const p=document.createElement('p');p.textContent=line;method.append(p);}results.append(method);
    }catch(error){if(!disposed&&ticket===generation){invalidate();status.textContent=error.message;}}
  };
  async function observe(){
    if(disposed||!panel.isConnected)return;
    if(current&&active()){try{const context=await api('/api/plans/readiness/context');if(current&&(context.contextSha256!==current.contextSha256||Date.now()>=Date.parse(current.expiresUtc)))invalidate();}catch{if(current)invalidate();}}
    if(!disposed&&panel.isConnected)timer=setTimeout(observe,10000);
  }
  timer=setTimeout(observe,10000);
  return {invalidate,dispose(){disposed=true;generation++;clearTimeout(timer);parent.removeEventListener('input',invalidate);parent.removeEventListener('change',invalidate);run.onclick=null;}};
}
