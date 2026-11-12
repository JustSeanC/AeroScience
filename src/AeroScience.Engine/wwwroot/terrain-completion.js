import {node as el,button,select,disclosure,status,errorMessage,link,bytes} from './workstation-ui.js';
import {t} from './i18n.js';
import {libraryDownload} from './library-download.js';

// Minimal post-flight adapter; all authoritative preparation, jobs, source policy,
// transfers and artifact verification remain in their original server controllers.
export async function terrainCompletion(parent,mission,api,disposals){
  let stopped=false,timer,geometry,afterChunk=0,afterSource=0,throughSource=null,latestJob=null,reviewEpoch=0;
  const selected=new Map(),pendingControls=new Set();
  const panel=el('section',undefined,'panel terrain-completion');panel.setAttribute('aria-label',t('terrain.title'));parent.append(panel);
  panel.append(el('h3',t('terrain.title')),el('p',t('terrain.separation')));
  const feedback=status(panel,t('terrain.initial')),actions=el('div',undefined,'action-row'),review=el('div'),jobs=el('div'),preparation=el('div');panel.append(actions,review,jobs,preparation);
  let releasePreparation=()=>{};
  disposals.push(()=>{stopped=true;clearTimeout(timer);releasePreparation();});
  const current=()=>!stopped&&panel.isConnected;
  const artifacts=await api(`/api/missions/${mission}/artifacts`);if(!current())return;
  const corridors=artifacts.filter(a=>a.kind==='continuous-terrain-survey').reverse();
  const choice=select(actions,t('terrain.geometry'),[['',t('terrain.chooseGeometry')],...corridors.map(a=>[a.id,new Date(a.createdUtc).toLocaleString()+' · '+a.id.slice(0,8)])],'');
  const searchLabel=el('label',t('terrain.searchRetained')),sourceSearch=el('input');sourceSearch.type='search';sourceSearch.maxLength=256;searchLabel.append(sourceSearch);actions.append(searchLabel);sourceSearch.onchange=()=>{reviewEpoch++;afterSource=0;throughSource=null;review.replaceChildren();};
  choice.onchange=()=>{reviewEpoch++;geometry=choice.value;afterChunk=afterSource=0;throughSource=null;selected.clear();review.replaceChildren();};
  button(actions,t('terrain.buildGeometry'),async()=>{
    const job=await api(`/api/missions/${mission}/terrain-surveys`,{});if(!current())return;feedback.textContent=t('terrain.geometryQueued');
    async function poll(){
      if(!current())return;const state=await api(`/api/analysis/jobs/${job.id}`);if(!current())return;
      feedback.textContent=state.message||state.state;
      if(['Queued','Running'].includes(state.state))timer=setTimeout(()=>poll().catch(e=>{if(current())errorMessage(panel,e);}),1000);
      else if(state.state==='Completed'&&state.artifacts.length){const id=state.artifacts[0],option=el('option',id.slice(0,8));option.value=id;choice.append(option);choice.value=geometry=id;afterChunk=afterSource=0;throughSource=null;await showReview();}
    }
    await poll();
  });
  button(actions,t('terrain.review'),showReview);
  async function showReview(){
    geometry=choice.value;if(!geometry)throw Error(t('terrain.chooseGeometry'));
    const own=++reviewEpoch,requestedGeometry=geometry;
    const data=await api(`/api/science/artifacts/${geometry}/elevation-review?afterChunk=${afterChunk}&afterSource=${afterSource}&search=${encodeURIComponent(sourceSearch.value)}`+(throughSource!==null?`&throughSource=${throughSource}`:''));if(!current()||own!==reviewEpoch||geometry!==requestedGeometry)return;
    throughSource=data.throughSource;review.replaceChildren();feedback.textContent=t('terrain.reviewed');
    review.append(el('p',t('terrain.gridWarning')));
    const limitations=disclosure(review,t('terrain.limitations'));for(const message of data.limitations)limitations.append(el('p',message));
    const windows=select(review,t('terrain.searchArea'),data.searchWindows.map((w,i)=>[String(i),`${i+1}: ${w.west.toFixed(5)}, ${w.south.toFixed(5)} → ${w.east.toFixed(5)}, ${w.north.toFixed(5)}`]),'0');
    button(review,t('terrain.prepare'),async()=>{
      if(own!==reviewEpoch)throw Error(t('error.notConfirmed'));
      const area=data.searchWindows[Number(windows.value)];if(!area)throw Error(t('terrain.noSearchArea'));
      releasePreparation();preparation.replaceChildren();preparation.append(el('p',t('terrain.downloadConsent')));
      const release=await libraryDownload(preparation,api,()=>!current(),{area,science:'terrain'});
      if(!current())release?.();else releasePreparation=release||(()=>{});
    }).disabled=!data.searchWindows.length;
    for(const source of data.sources){
      const box=disclosure(review,source.name+' · '+source.state);box.dataset.sourceId=source.sourceId;box.append(el('p',source.gridResolution),el('p',source.verticalDatum));
      for(const reason of source.reasons)box.append(el('p',reason));
      const bands=source.bands.filter(b=>['m','ft'].includes(b.unit)&&!b.badBand),band=select(box,t('terrain.band'),bands.map(b=>[b.id,`${b.id} · ${b.unit}`]),bands[0]?.id);
      const surface=select(box,t('terrain.surface'),[['unknown',t('terrain.definitionRequired')],['dem','DEM'],['dsm','DSM']],'unknown');
      const definition=checkbox(box,t('terrain.localDefinition')),datum=checkbox(box,t('terrain.datumConsent'));
      const qa=select(box,t('terrain.qualityBand'),[['',t('terrain.none')],...source.bands.filter(b=>!bands.some(v=>v.id===b.id)).map(b=>[b.id,b.id])],'');
      const qaLabel=el('label',t('terrain.qualityValues')),qaValues=el('input');qaValues.type='text';qaLabel.append(qaValues);box.append(qaLabel);
      const use=checkbox(box,t('terrain.useSource'));use.checked=selected.has(source.sourceId);use.disabled=source.state==='Unavailable'||!bands.length;
      const update=()=>{if(use.checked)selected.set(source.sourceId,{sourceId:source.sourceId,manifestSha256:source.manifestSha256,bandId:band.value,surface:surface.value,localDefinitionAcknowledged:definition.checked,unverifiedDatumAcknowledged:datum.checked,qualityBandId:qa.value||null,acceptedQualityValues:qa.value?qaValues.value.split(',').filter(v=>v.trim()).map(Number):null});else selected.delete(source.sourceId);};
      for(const control of [use,band,surface,definition,datum,qa,qaValues])control.addEventListener('change',update);
      const prior=selected.get(source.sourceId);if(prior){band.value=prior.bandId;surface.value=prior.surface;definition.checked=prior.localDefinitionAcknowledged;datum.checked=prior.unverifiedDatumAcknowledged;qa.value=prior.qualityBandId||'';qaValues.value=(prior.acceptedQualityValues||[]).join(',');}
      disclosure(box,t('terrain.gridMasks')).append(el('pre',JSON.stringify(source.potentialGridSupport,null,2)));
    }
    const paging=el('div',undefined,'action-row');review.append(paging);
    if(data.nextSource!==null)button(paging,t('terrain.moreSources'),async()=>{afterSource=data.nextSource;await showReview();});
    if(data.nextChunk!==null)button(paging,t('terrain.nextRegion'),async()=>{afterChunk=data.nextChunk;afterSource=0;throughSource=null;await showReview();});
    if(afterChunk>0)button(paging,t('terrain.firstRegion'),async()=>{afterChunk=afterSource=0;throughSource=null;await showReview();});
    button(paging,t('terrain.refresh'),async()=>{afterSource=0;throughSource=null;await showReview();});
    const advanced=disclosure(review,t('terrain.budgets'));
    advanced.append(el('p',t('terrain.budgetExplanation')));
    const cells=numberInput(advanced,t('terrain.cellLimit'),2000000),output=numberInput(advanced,t('terrain.outputLimit'),128),tileSize=numberInput(advanced,t('terrain.tileSize'),16);
    const first=numberInput(advanced,t('terrain.firstChunk'),data.geometryChunks?.[0]||1),last=numberInput(advanced,t('terrain.lastChunk'),data.geometryChunks?.at(-1)||1);
    button(review,t('terrain.createJob'),async()=>{
      if(own!==reviewEpoch)throw Error(t('error.notConfirmed'));
      latestJob=await api(`/api/missions/${mission}/terrain-elevation/jobs`,{geometryArtifactId:data.geometryArtifactId,sources:[...selected.values()],options:{tileSize:Number(tileSize.value),maximumCandidateCells:Number(cells.value),maximumOutputBytes:Number(output.value)*1048576,firstGeometryChunk:Number(first.value),lastGeometryChunk:Number(last.value)}});
      if(current()){feedback.textContent=t('terrain.jobReviewed');await renderJobs();}
    });
  }
  async function renderJobs(){
    if(!current())return;const data=await api(`/api/missions/${mission}/terrain-elevation/jobs`);if(!current())return;
    const active=jobs.contains(document.activeElement)?document.activeElement:null,focusedJob=active?.closest('[data-job-id]')?.dataset.jobId,focusedAction=active?.dataset.terrainAction;
    const openJobs=new Set([...jobs.querySelectorAll('[data-job-id][open]')].map(e=>e.dataset.jobId));
    const openTiles=new Set([...jobs.querySelectorAll('[data-job-id] details[open]')].map(e=>e.closest('[data-job-id]').dataset.jobId));
    jobs.replaceChildren();for(const job of data){
      const box=disclosure(jobs,t('terrain.job')+' '+job.id.slice(0,8)+' · '+job.state);box.dataset.jobId=job.id;if(latestJob?.id===job.id||openJobs.has(job.id))box.open=true;
      box.append(el('p',`${job.completedTiles} / ${job.totalTiles??'?'} · ${job.validCells} ${t('terrain.validCells')} · ${job.missingCells} ${t('terrain.missingCells')} · ${bytes(job.outputBytes)}`),el('p',job.message));
      box.append(el('p',t('terrain.reviewBounds',{tiles:job.totalTiles??'?',cells:job.totalTiles!==null?job.totalTiles*job.options.tileSize**2:'?',output:bytes(job.options.maximumOutputBytes),scratch:bytes(job.options.maximumScratchBytes),first:job.options.firstGeometryChunk,last:job.options.lastGeometryChunk??'…'})));
      const originals=disclosure(box,t('terrain.selectedSources'));for(const s of job.sources)originals.append(el('p',`${s.original.manifest.name} · ${s.surface} · ${s.original.manifest.verticalDatum} · ${s.original.manifest.observationUtc||t('common.unknown')}`),el('code',s.original.id+' · '+s.original.manifestSha256));
      const control=async action=>{if(pendingControls.has(job.id))return;pendingControls.add(job.id);try{const current=await api(`/api/terrain-elevation/jobs/${job.id}`);await api(`/api/terrain-elevation/jobs/${job.id}/control`,{requestId:crypto.randomUUID(),expectedRevision:current.revision,action});latestJob=job;}finally{pendingControls.delete(job.id);}await renderJobs();};
      const actionButton=(action,key)=>{const b=button(box,t(key),()=>control(action));b.dataset.terrainAction=action;b.disabled=pendingControls.has(job.id);};
      if(job.state==='Reviewed'&&job.sources.length)actionButton('start','terrain.process');
      if(['Queued','Running'].includes(job.state))actionButton('pause','terrain.pause');
      if(['Paused','Interrupted','Cancelled'].includes(job.state)&&job.sources.length)actionButton('resume','terrain.resume');
      if(job.state==='Failed')actionButton('retry','terrain.retry');
      if(!['Cancelled','Ready','PartiallyComplete','Invalidated'].includes(job.state))actionButton('cancel','terrain.cancel');
      if(job.resultArtifactId)link(box,t('terrain.openIndex'),`/api/science/artifacts/${job.resultArtifactId}`).dataset.terrainAction='result';
      const tiles=disclosure(box,t('terrain.tiles'));let shown=false;
      tiles.addEventListener('toggle',async()=>{if(tiles.open&&!shown){shown=true;try{const items=await api(`/api/terrain-elevation/jobs/${job.id}/tiles`);if(!current())return;for(const item of items)link(tiles,`${item.sourceId.slice(0,8)} · ${item.tileColumn},${item.tileRow}`,`/api/science/artifacts/${item.artifactId}`);}catch(e){shown=false;if(current())errorMessage(tiles,e);}}});
      if(openTiles.has(job.id))tiles.open=true;
    }
    if(focusedJob){const box=[...jobs.querySelectorAll('[data-job-id]')].find(e=>e.dataset.jobId===focusedJob);(focusedAction?box?.querySelector(`[data-terrain-action="${focusedAction}"]`):null)?.focus({preventScroll:true});if(document.activeElement===document.body)box?.querySelector(':scope > summary')?.focus({preventScroll:true});}
    clearTimeout(timer);if(data.some(j=>['Queued','Running','Pausing'].includes(j.state)))timer=setTimeout(()=>renderJobs().catch(e=>{if(current())errorMessage(jobs,e);}),1500);
  }
  await renderJobs();
}
function checkbox(parent,text){const label=el('label',text,'checkbox-label'),input=el('input');input.type='checkbox';label.prepend(input);parent.append(label);return input;}
function numberInput(parent,text,value){const label=el('label',text),input=el('input');input.type='number';input.min='1';input.step='1';input.value=value;label.append(input);parent.append(label);return input;}
