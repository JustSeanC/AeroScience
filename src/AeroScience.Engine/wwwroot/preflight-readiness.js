// Advisory only: this module never saves, records or prepares scientific sources.
export function preflightReadiness(parent, readDraft, api,{id='preflight-readiness',active=()=>true}={}) {
  const panel=document.createElement('section'); panel.id=id;
  const title=document.createElement('h3');title.textContent='Preflight Science Readiness';
  const status=document.createElement('p');status.setAttribute('role','status');status.textContent='Not yet assessed · evaluate this draft without saving.';
  const results=document.createElement('div');const run=document.createElement('button');run.type='button';run.textContent='Evaluate science readiness';
  panel.append(title,status,run,results);parent.append(panel);let generation=0,current=null,timer;
  const fingerprint=()=>JSON.stringify(readDraft());
  const invalidate=()=>{generation++;current=null;status.textContent='Stale · plan or context changed. Reassess before relying on these findings.';results.dataset.stale='true';};
  parent.addEventListener('input',invalidate);parent.addEventListener('change',invalidate);
  run.onclick=async()=>{
    const ticket=++generation;let draft,hash;
    try {draft=readDraft();hash=JSON.stringify(draft);status.textContent='Assessing draft…';
      const response=await api('/api/plans/readiness',draft);
      if(!panel.isConnected||ticket!==generation||hash!==fingerprint())return;
      current=response;results.replaceChildren();delete results.dataset.stale;
      status.textContent=`Assessed ${new Date(response.assessedUtc).toLocaleTimeString()} · advisory, with unknowns shown explicitly. No data was downloaded.`;
      for(const category of [...new Set(response.checks.map(c=>c.category))]){
        const group=document.createElement('details');group.open=true;const heading=document.createElement('summary');heading.textContent=category;group.append(heading);
        for(const check of response.checks.filter(c=>c.category===category)){
          const item=document.createElement('article');const label={ready:'Ready',caution:'Caution',unavailable:'Unavailable',notYetAssessed:'Not Yet Assessed'}[check.status]||check.status;
          const h=document.createElement('h4');h.textContent=`${label} · ${check.id}`;item.append(h);
          for(const line of [...check.reasons,...check.actions,...(check.inputs||[])]){const p=document.createElement('p');p.textContent=line;item.append(p);}group.append(item);
        }results.append(group);
      }
      for(const line of response.limitations){const p=document.createElement('p');p.className='footnote';p.textContent=line;results.append(p);}
    }catch(error){if(ticket===generation)status.textContent=error.message;}
  };
  async function observe(){
    if(!panel.isConnected)return;
    if(current&&active()){try{const context=await api('/api/plans/readiness/context');if(current&&(context.contextSha256!==current.contextSha256||Date.now()>=Date.parse(current.expiresUtc)))invalidate();}catch{if(current)invalidate();}}
    if(panel.isConnected)timer=setTimeout(observe,10000);
  }
  timer=setTimeout(observe,10000);
  return {invalidate};
}
