import {dialogHeader} from './dialog-ui.js';
import {node as el,bytes,button} from './workstation-ui.js';

// Closing the view never cancels durable host work. No request begins a transfer
// until the exact retained preview is explicitly approved.
export async function openAviationDownloads(api,selection=null){
 const dialog=el('dialog');dialog.className='aviation-reference-dialog';dialog.setAttribute('aria-label','FAA Downloads');
 dialog.append(el('h2','FAA Downloads'),el('p','Retain official aviation originals for offline reference. Chart rendering and geographic placement are separate capabilities. These files are not scientific observations.'));
 const status=el('p');status.setAttribute('role','status');status.setAttribute('aria-live','polite');
 const storage=el('p'),settings=el('details'),settingsTitle=el('summary','Aviation network & storage settings');settings.append(settingsTitle);
 const work=el('section'),jobs=el('section');jobs.setAttribute('aria-label','Saved FAA download jobs');
 dialog.append(status,storage,settings,work,jobs);document.body.append(dialog);dialogHeader(dialog);dialog.showModal();
 let closed=false,epoch=0,busy=false,timer;
 dialog.addEventListener('close',()=>{closed=true;epoch++;clearTimeout(timer);dialog.remove();});
 const field=(parent,title,value)=>{const label=el('label',title),input=el('input');input.type='number';input.min='1';input.value=value;input.setAttribute('aria-label',title);label.append(input);parent.append(label);return input;};
 try{
  const [{configuration,canConfigure},preferences]=await Promise.all([api('/api/aviation/configuration'),api('/api/aviation/downloads/configuration')]);if(closed)return;
  settings.append(el('p',preferences.policy));
  if(canConfigure){
   const label=el('label',undefined,'checkbox-label'),unmetered=el('input');unmetered.type='checkbox';unmetered.checked=preferences.unmeteredOnly;unmetered.setAttribute('aria-label','FAA downloads only on a verified unmetered network');label.append(unmetered,'FAA downloads only on a verified unmetered network');settings.append(label);
   button(settings,'Save Aviation Download Preferences',async()=>{try{await api('/api/aviation/downloads/configuration',{unmeteredOnly:unmetered.checked});status.textContent='Aviation download preference saved. Scientific preferences are unchanged.';}catch(error){status.textContent=error.message;}});
  }else settings.append(el('p','Aviation network and storage settings are managed on the companion PC.'));
  if(selection){
   work.append(el('h3',selection.item.originalFileName),el('p',`Exact catalog edition ${selection.item.edition}. Original transfer size: ${bytes(selection.item.originalBytes)}. Entire original ZIP will be requested, even for a small map area.`));
   const transfer=field(work,'Aviation transfer allowance (MiB)',128),expanded=field(work,'Expanded storage allowance (MiB)',512);transfer.max='2048';expanded.max='8192';
   const previewArea=el('div');
   button(work,'Review FAA Download',async()=>{
    if(busy)return;busy=true;try{
     const job=await api('/api/aviation/downloads/preview',{kind:selection.kind,itemId:selection.item.id,maximumTransferBytes:Number(transfer.value)*1048576,maximumExpandedBytes:Number(expanded.value)*1048576});if(closed)return;
     previewArea.replaceChildren();
     if(job.state==='Completed'){previewArea.append(el('p','Exact verified original already retained. Hashes checked; no further transfer is needed.'));return;}
     previewArea.append(el('h4','Review & Approve'),el('p',`Original size: ${bytes(job.preview.expectedBytes)}. Transfer allowance: ${bytes(job.preview.maximumTransferBytes)}. Expanded storage allowance: ${bytes(job.preview.maximumExpandedBytes)}. These limits are not estimates.`));
     const limits=el('ul');for(const text of job.preview.limitations)limits.append(el('li',text));previewArea.append(limits);
     button(previewArea,'Approve FAA Download',async()=>{if(busy)return;busy=true;try{await api(`/api/aviation/downloads/${job.id}/control`,{revision:job.revision,action:'approve',selectionHash:job.preview.selectionHash});previewArea.replaceChildren(el('p','Approved. The host manages the stored job; this window may be closed.'));await refresh();}catch(error){status.textContent=error.message;}finally{busy=false;}});
    }catch(error){if(!closed)status.textContent=error.message;}finally{busy=false;}
   });work.append(previewArea);
   if(!configuration.onlineEnabled)work.append(el('p','FAA access is disabled. Enable access on the companion PC before approving acquisition.'));
  }
  await refresh();
 }catch(error){if(!closed)status.textContent=error.message;}
 async function refresh(){
  clearTimeout(timer);const own=++epoch;
  try{
   const [list,s]=await Promise.all([api('/api/aviation/downloads'),api('/api/aviation/downloads/storage')]);if(closed||own!==epoch)return;
   storage.textContent=s.available?`Aviation used: ${bytes(s.usedBytes)} · Reserved allowances: ${bytes(s.reservedBytes)} · Physical drive free: ${bytes(s.physicalFreeBytes)}. Physical capacity is separate from per-job limits.`:(s.error||'Aviation storage Unknown.');
   jobs.replaceChildren(el('h3','Saved FAA downloads'));
   for(const job of list){
    const card=el('article',undefined,'panel');card.dataset.jobId=job.id;card.append(el('h4',`${job.preview.item.originalFileName} · ${job.preview.item.edition}`),el('p',job.state),el('p',`Received across attempts: ${bytes(job.downloadedBytes)}. Approved transfer limit: ${bytes(job.preview.maximumTransferBytes)}. Original size: ${bytes(job.preview.expectedBytes)}.`));
    if(job.state==='Completed')card.append(el('p',`Verified original SHA-256: ${job.originalSha256}. ${(job.files||[]).length} checked companions retained. Geographic rendering not yet assessed.`));
    if(job.error)card.append(el('p',job.error));
    if(job.retryAfterUtc)card.append(el('p',`Next eligible provider retry: ${new Date(job.retryAfterUtc).toLocaleString()}`));
    const controls=el('div',undefined,'action-row');card.append(controls);
    const actions=job.state==='Completed'?['verify']:job.state==='Awaiting Approval'||job.state==='Cancelled'?[]:job.state==='Paused by User'||job.state==='Interrupted'||job.state==='Failed'||job.state.startsWith('Waiting')?['resume','cancel']:['pause','cancel'];
    for(const action of actions)button(controls,{verify:'Verify Retained Original',pause:'Pause FAA Download',resume:'Resume / Retry FAA Download',cancel:'Cancel FAA Download'}[action],async()=>{try{await api(`/api/aviation/downloads/${job.id}/`+(action==='verify'?'verify':'control'),action==='verify'?{}:{revision:job.revision,action});status.textContent=action==='verify'?'Retained original and all extracted files match their recorded hashes.':'Stored job updated.';await refresh();}catch(error){status.textContent=error.message;}});
    jobs.append(card);
   }
   if(!list.length)jobs.append(el('p','No retained download intents. Select an exact chart edition in FAA Catalog & Notices.'));
  }catch(error){if(!closed&&own===epoch)status.textContent=error.message;}
  finally{if(!closed)timer=setTimeout(refresh,5000);}
 }
}
