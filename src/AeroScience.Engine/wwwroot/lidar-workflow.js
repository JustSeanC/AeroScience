import {presentationTextNode} from './i18n.js';
import {message as localizedCopy,setPresentationText} from './i18n.js';
import {node as el,button,select,disclosure,status,errorMessage,link,bytes,numeric} from './workstation-ui.js';
import {t} from './i18n.js';

// Post-flight presentation adapter. Original Library/analysis/permission controllers
// own every operation; no recording, collection, acquisition or organization action.
export async function lidarWorkflow(parent,mission,api,disposals){
  let stopped=false,timer,epoch=0,reviewData,latestJob,afterChunk=0,afterSource=0,throughSource=null,download,importKey;
  const selected=new Map(),pending=new Set(),inspections=new Map();
  const root=el('section',undefined,'panel lidar-completion');setPresentationText(root,localizedCopy('lidar.title'),"aria-label");parent.append(root);
  root.append(el('h3',localizedCopy('lidar.title')),el('p',localizedCopy('lidar.separation')));
  const feedback=status(root,localizedCopy('lidar.initial')),actions=el('div',undefined,'action-row'),review=el('div'),preparation=el('div'),jobs=el('div');root.append(actions,review,preparation,jobs);
  const local=disclosure(root,localizedCopy('lidar.localImport')),localStatus=status(local,localizedCopy('lidar.localHelp')),localForm=el('div');local.append(localForm);
  disposals.push(()=>{stopped=true;epoch++;clearTimeout(timer);});const current=()=>!stopped&&root.isConnected;
  const artifacts=await api(`/api/missions/${mission}/artifacts`);if(!current())return;
  const geometry=select(actions,localizedCopy('lidar.geometry'),[['',localizedCopy("lidar.chooseGeometry")],...artifacts.filter(a=>a.kind==='continuous-terrain-survey').reverse().map(a=>[a.id,a.id.slice(0,8)+' · '+new Date(a.createdUtc).toLocaleString()])],'');
  geometry.addEventListener('change',()=>{epoch++;afterChunk=afterSource=0;throughSource=null;reviewData=null;selected.clear();review.replaceChildren();preparation.replaceChildren();});
  const search=input(actions,localizedCopy('lidar.sourceSearch'),'');
  button(actions,localizedCopy('lidar.review'),()=>reviewSources());button(actions,localizedCopy('lidar.refresh'),()=>refreshJobs());
  button(local,localizedCopy('lidar.chooseLocal'),async()=>{await api('/api/desktop/lidar-original',{});if(current())setPresentationText(localStatus,localizedCopy('lidar.dialogOpened'));});
  button(local,localizedCopy('lidar.refreshLocal'),()=>refreshLocal());
  function input(parent,label,value='',type='text'){const field=el('label',label),control=el('input');control.type=type;control.value=value;field.append(control);parent.append(field);return control;}
  function checkbox(parent,label){const row=el('label',undefined,'check-row'),control=el('input');control.type='checkbox';row.append(control,presentationTextNode(label));parent.append(row);return control;}
  async function reviewSources(){
    if(!geometry.value)throw Error(t('lidar.chooseGeometry'));const own=++epoch,id=geometry.value;
    const data=await api(`/api/science/artifacts/${id}/lidar-review?afterChunk=${afterChunk}&afterSource=${afterSource}&search=${encodeURIComponent(search.value)}`+(throughSource!==null?`&throughSource=${throughSource}`:''));
    if(!current()||own!==epoch||id!==geometry.value)return;reviewData=data;throughSource=data.throughSource;review.replaceChildren();preparation.replaceChildren();setPresentationText(feedback,localizedCopy('lidar.reviewed'));
    review.append(el('p',localizedCopy('lidar.boundsWarning')));const limits=disclosure(review,localizedCopy('lidar.details'));for(const m of data.limitations)limits.append(el('p',m));
    const area=select(review,localizedCopy('lidar.studyRegion'),data.searchWindows.map((w,i)=>[i,`${i+1}: ${w.west.toFixed(5)}, ${w.south.toFixed(5)} → ${w.east.toFixed(5)}, ${w.north.toFixed(5)}`]),0);
    const prefix=input(review,localizedCopy('lidar.prefix')),project=input(review,localizedCopy('lidar.exactProject'));
    button(review,localizedCopy('lidar.find'),async()=>{if(own!==epoch)throw Error(t('error.notConfirmed'));const bounds=data.searchWindows[Number(area.value)];if(!bounds)throw Error(t('lidar.noRegion'));const found=await api('/api/science/lidar/discovery',{area:bounds,prefix:prefix.value||null,project:project.value||null,cachedOnly:false});if(!current()||own!==epoch)return;showDiscovery(found,bounds,own);});
    for(const item of data.sources){
      const s=item.source,box=disclosure(review,s.name+' · '+item.state);box.dataset.sourceId=s.id;
      box.append(el('p',`${s.horizontalCrs} · ${s.verticalReference} · ${s.verticalUnit} · ${s.originalAcquisitionUtc||localizedCopy('common.unknown')}`),el('p',`${numeric(s.originalPointCount,0)} ${localizedCopy('lidar.originalRecords')}`),el('p',item.limitation),el('code',s.id+' · '+s.originalSha256));
      const ack=checkbox(box,localizedCopy('lidar.verticalAck')),use=checkbox(box,localizedCopy('lidar.useSource'));use.disabled=item.state==='Unavailable';const prior=selected.get(s.id);use.checked=!!prior;ack.checked=!!prior?.unverifiedVerticalAcknowledged;
      const change=()=>{if(use.checked)selected.set(s.id,{sourceId:s.id,manifestSha256:s.manifestSha256,unverifiedVerticalAcknowledged:ack.checked});else selected.delete(s.id);};use.addEventListener('change',change);ack.addEventListener('change',change);
      disclosure(box,localizedCopy('lidar.metadata')).append(el('pre',JSON.stringify(s,null,2)));
    }
    if(!data.sources.length)review.append(el('p',localizedCopy('lidar.noSources')));
    const paging=el('div',undefined,'action-row');review.append(paging);
    if(data.nextSource!==null)button(paging,localizedCopy('lidar.moreSources'),async()=>{afterSource=data.nextSource;await reviewSources();});
    if(data.nextChunk!==null)button(paging,localizedCopy('lidar.nextRegion'),async()=>{afterChunk=data.nextChunk;afterSource=0;throughSource=null;await reviewSources();});
    const expert=disclosure(review,localizedCopy('lidar.budgets')),first=input(expert,localizedCopy('lidar.firstChunk'),data.geometryChunks[0]||1,'number'),last=input(expert,localizedCopy('lidar.lastChunk'),data.geometryChunks.at(-1)||1,'number'),points=input(expert,localizedCopy('lidar.pointCap'),1000000,'number'),output=input(expert,localizedCopy('lidar.outputCap'),128,'number'),tile=input(expert,localizedCopy('lidar.tileRecords'),2048,'number');
    button(review,localizedCopy('lidar.create'),async()=>{if(own!==epoch)throw Error(t('error.notConfirmed'));const result=await api(`/api/missions/${mission}/lidar/jobs`,{requestId:crypto.randomUUID(),geometryArtifactId:data.geometryArtifactId,sources:[...selected.values()],options:{firstGeometryChunk:Number(first.value),lastGeometryChunk:Number(last.value),maximumCandidatePoints:Number(points.value),maximumOutputBytes:Number(output.value)*1048576,recordsPerTile:Number(tile.value)}});if(!current()||own!==epoch)return;latestJob=result.id;setPresentationText(feedback,localizedCopy('lidar.jobReviewed'));await refreshJobs();});
  }
  function showDiscovery(found,area,own){
    preparation.replaceChildren(el('h4',localizedCopy('lidar.discovered')),el('p',localizedCopy('lidar.discoveryOnly')));for(const message of [...found.limitations,...(found.unavailableProjects||[])])preparation.append(el('p',message));
    const official=disclosure(preparation,localizedCopy('lidar.officialMetadata'));official.append(el('pre',JSON.stringify(found.officialWorkUnits,null,2)));
    for(const source of found.projects){const box=disclosure(preparation,source.project+' · '+source.state);box.append(el('p',source.horizontalCrs+' · '+source.verticalReference),el('p',`${numeric(source.pointCount,0)} ${localizedCopy('lidar.projectPoints')}`));for(const m of source.limitations)box.append(el('p',m));const cap=input(box,localizedCopy('lidar.transferCap'),16,'number');
      button(box,localizedCopy('lidar.previewDownload'),async()=>{if(own!==epoch)throw Error(t('error.notConfirmed'));const value=await api('/api/science/library/lidar/preview',{project:source.project,metadataSha256:source.metadataSha256,area,maximumTransferBytes:Number(cap.value)*1048576,maximumNodes:32,maximumCandidatePoints:1000000});if(!current()||own!==epoch)return;download=value;showDownload(value,box,own);});}
    if(found.nextCursor)button(preparation,localizedCopy('lidar.moreProjects'),async()=>{const next=await api('/api/science/lidar/discovery',{area,prefix:found.prefix,cursor:found.nextCursor});if(current()&&own===epoch)showDiscovery(next,area,own);});
  }
  function showDownload(value,parent,own){
    const box=disclosure(parent,localizedCopy('lidar.downloadReview'));box.open=true;box.append(el('p',`${value.preview.lidar.source.project} · ${value.preview.lidar.nodes.length} ${localizedCopy('lidar.nodes')} · ${numeric(value.preview.lidar.candidatePoints,0)} ${localizedCopy('lidar.candidates')}`),el('p',`${localizedCopy('lidar.transferCap')}: ${bytes(value.preview.maximumTransferBytes)} · ${localizedCopy('lidar.transferUnknown')}`));for(const m of value.preview.warnings)box.append(el('p',m));
    const progress=status(box,value.state);download={...value,view:progress};
    button(box,localizedCopy('lidar.approve'),async()=>{if(own!==epoch)throw Error(t('error.notConfirmed'));await api(`/api/science/library/jobs/${value.id}/approve`,{selectionHash:value.preview.selectionHash});if(current())await refreshDownload();});
    for(const action of ['pause','resume','retry','cancel'])button(box,localizedCopy('lidar.'+action),async()=>{await api(`/api/science/library/jobs/${value.id}/control`,{action});if(current())await refreshDownload();});
  }
  async function refreshDownload(){if(!download)return;const id=download.id,value=await api(`/api/science/library/jobs/${id}`);if(!current()||download?.id!==id)return;if(download.view?.isConnected)setPresentationText(download.view,`${value.state} · ${bytes(value.downloadedBytes)} ${localizedCopy('lidar.transferred')} · ${value.completedItems} / ${value.items.length} · ${value.error||''}`);}
  async function refreshJobs(){
    if(!current()||pending.size)return;const data=await api(`/api/missions/${mission}/lidar/jobs`);if(!current()||pending.size)return;
    const focused=document.activeElement?.closest('[data-lidar-job]')?.dataset.lidarJob,action=document.activeElement?.dataset.lidarAction,opened=new Set([...jobs.querySelectorAll('[data-lidar-job][open]')].map(n=>n.dataset.lidarJob));
    jobs.replaceChildren();for(const job of data){
      if(job.state==='Invalidated'){inspections.get(job.id)?.replaceChildren();inspections.delete(job.id);}
      const box=disclosure(jobs,`${localizedCopy('lidar.job')} ${job.id.slice(0,8)} · ${job.state}`);box.dataset.lidarJob=job.id;box.open=opened.has(job.id)||latestJob===job.id;
      box.append(el('p',`${numeric(job.retainedPoints,0)} ${localizedCopy('lidar.retained')} · ${numeric(job.scannedPoints,0)} ${localizedCopy('lidar.scanned')} · ${job.completedTiles} / ${job.totalTiles??'?'} ${localizedCopy('lidar.tiles')} · ${bytes(job.outputBytes)}`),el('p',job.message));
      const controls=el('div',undefined,'action-row');box.append(controls);const available=job.state==='Reviewed'?['start','cancel']:['Queued','Running'].includes(job.state)?['pause','cancel']:['Paused','Interrupted','Failed','Cancelled'].includes(job.state)?['resume','retry','cancel']:[];
      for(const kind of available){const control=button(controls,localizedCopy('lidar.'+kind),async()=>{if(pending.has(job.id))return;pending.add(job.id);try{await api(`/api/lidar/jobs/${job.id}/control`,{requestId:crypto.randomUUID(),expectedRevision:job.revision,action:kind});}finally{pending.delete(job.id);}if(current())await refreshJobs();});control.dataset.lidarAction=kind;}
      if(job.resultArtifactId&&job.state!=='Invalidated')link(box,localizedCopy('lidar.openIndex'),`/api/science/artifacts/${job.resultArtifactId}`);
      const inspectButton=button(box,localizedCopy('lidar.inspect'),async()=>{const rows=await api(`/api/lidar/jobs/${job.id}/tiles`);if(!current())return;const host=inspections.get(job.id)||el('div');inspections.set(job.id,host);host.replaceChildren();box.append(host);if(!rows.length){host.append(el('p',localizedCopy('lidar.noPoints')));return;}const tileChoice=select(host,localizedCopy('lidar.pointTile'),rows.map(row=>[row.artifactId,`${row.sourceId.slice(0,8)} · ${row.firstRecord} · ${row.retainedPoints} ${t('lidar.retained')}`]),rows[0].artifactId),sample=el('div');host.append(sample);let pointEpoch=0;const load=async(offset=0)=>{const own=++pointEpoch,id=tileChoice.value;const values=await api(`/api/science/artifacts/${id}/lidar-points?offset=${offset}&limit=30`);if(!current()||own!==pointEpoch||id!==tileChoice.value)return;sample.replaceChildren(el('p',values.evidenceClass),el('code',values.originalFile+' · '+values.originalSha256));const scroll=el('div',undefined,'lidar-table-scroll');scroll.tabIndex=0;scroll.setAttribute('role','region');setPresentationText(scroll,localizedCopy('lidar.pointTable'),"aria-label");const table=el('table'),head=el('thead'),tr=el('tr');for(const label of ['record','location','xyz','attributes']){const th=el('th',localizedCopy('lidar.'+label));th.scope='col';tr.append(th);}head.append(tr);table.append(head);const body=el('tbody');for(const p of values.points){const row=el('tr'),o=p.original;row.append(el('td',String(o.record)),el('td',`${p.geographic.latitude}, ${p.geographic.longitude}`),el('td',`${o.x}, ${o.y}, ${o.z}`));const details=el('td');disclosure(details,localizedCopy('lidar.attributes')).append(el('pre',JSON.stringify({...o,rawGpsTimeBits:undefined,support:p.support},null,2)));row.append(details);body.append(row);}table.append(body);scroll.append(table);sample.append(scroll);if(!values.points.length)sample.append(el('p',localizedCopy('lidar.noPoints')));if(values.nextOffset!==null)button(sample,localizedCopy('lidar.nextPoints'),()=>load(values.nextOffset));};tileChoice.addEventListener('change',()=>load().catch(e=>errorMessage(sample,e)));await load();});inspectButton.disabled=job.state==='Invalidated';
      if(inspections.has(job.id))box.append(inspections.get(job.id));
    }
    if(focused&&action)jobs.querySelector(`[data-lidar-job="${focused}"] [data-lidar-action="${action}"]`)?.focus({preventScroll:true});
  }
  async function refreshLocal(){
    try{const value=await api('/api/science/lidar/import');if(!current())return;setPresentationText(localStatus,value.message);const key=value.selectionId+'|'+value.state;if(key===importKey)return;importKey=key;localForm.replaceChildren();if(value.file)localForm.append(el('p',value.file),el('code',value.sha256));if(value.header)disclosure(localForm,localizedCopy('lidar.metadata')).append(el('pre',JSON.stringify(value.header,null,2)));
      if(value.state==='Review'){const name=input(localForm,localizedCopy('lidar.sourceName'),value.file?.split(/[\\/]/).at(-1)||''),project=input(localForm,localizedCopy('lidar.projectId')),license=input(localForm,localizedCopy('lidar.license')),citation=input(localForm,localizedCopy('lidar.citation')),from=input(localForm,localizedCopy('lidar.acquiredFrom')),to=input(localForm,localizedCopy('lidar.acquiredTo'));
        button(localForm,localizedCopy('lidar.import'),async()=>{await api('/api/science/lidar/import',{selectionId:value.selectionId,metadata:{name:name.value,project:project.value,license:license.value,citation:citation.value,acquiredFromUtc:from.value||null,acquiredToUtc:to.value||null}});if(current())await refreshLocal();});}
      if(value.busy)button(localForm,localizedCopy('lidar.cancel'),async()=>{await api('/api/science/lidar/import/cancel',{});if(current())await refreshLocal();});
    }catch(e){if(current())setPresentationText(localStatus,e.message);}
  }
  async function poll(){if(!current())return;try{await refreshJobs();await refreshDownload();await refreshLocal();}catch(e){if(current())setPresentationText(feedback,e.message);}if(current())timer=setTimeout(poll,1500);}
  await refreshJobs();if(current())timer=setTimeout(poll,1500);
}
