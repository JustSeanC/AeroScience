import {node as el,bytes,button} from './workstation-ui.js';
import {dialogHeader,requestDialogClose} from './dialog-ui.js';
export async function reviewSourceDeletion(api,selection,onChanged){
 const opener=document.activeElement,preview=await api('/api/science/sources/deletion/preview',selection),dialog=el('dialog');dialog.setAttribute('aria-label','Delete Downloaded External Data');
 const total=preview.totalSources??preview.sources.length,blockedCount=preview.blockedSources??preview.sources.filter(s=>s.blockers.length).length;
 dialog.append(el('h2','Delete Downloaded External Data'),el('p',preview.all?'Every eligible external source in this exact approved selection will be permanently removed. This cannot be undone.':'The reviewed external datasets will be permanently removed. This cannot be undone.'),el('p',`${total} selected source regions · ${preview.eligibleSources} eligible · estimated space freed: ${bytes(preview.eligibleManagedBytes)}. This is verified managed logical size; actual physical disk recovery can differ due to filesystem allocation and SQLite/WAL.`),el('p',preview.preserved));
 const list=el('div');dialog.append(list);
 function show(items,next){list.replaceChildren();for(const source of items){const row=el('article',undefined,'finding-card');row.append(el('h3',source.name),el('p',`${source.provider} · ${source.kind} · ID ${source.id} · ${bytes(source.managedBytes)} · ${source.managedFileCount??source.files.length} verified managed files`),el('p',source.blockers.length?'Blocked: '+source.blockers.join(' · '):'Eligible. Ownership, original hashes and research dependencies will be rechecked before deletion.'));list.append(row);}if(next!=null)button(list,'Review more selected regions',async()=>{const page=await api(`/api/science/sources/deletion/${preview.id}/items?after=${next}`);if(dialog.open)show(page.items,page.next);},'secondary');}
 show(preview.sources,preview.next);
 if(blockedCount)dialog.append(el('p',`${blockedCount} selected regions are protected or unavailable. Nothing in this batch will be deleted. Cancel and choose a new explicit selection after reviewing the blockers.`));
 const feedback=el('p');feedback.setAttribute('role','status');feedback.setAttribute('aria-live','polite');const actions=el('footer',undefined,'dialog-actions');dialog.append(feedback,actions);
 const cancel=button(actions,'Cancel',()=>requestDialogClose(dialog),'secondary');
 const remove=button(actions,preview.all?'Delete All Downloaded Data':'Delete Data',async()=>{
  remove.disabled=true;cancel.disabled=true;dialog.setAttribute('aria-busy','true');feedback.textContent='Rechecking the exact selection and protected research evidence, then deleting approved data. Closing this window does not cancel an already approved operation.';
  try{const result=await api('/api/science/sources/deletion/execute',{previewId:preview.id,selectionHash:preview.selectionHash});feedback.textContent=`${result.state}: ${result.removedSources} source regions removed · ${bytes(result.removedManagedBytes)} managed bytes removed · ${bytes(result.pendingManagedBytes)} known file bytes pending cleanup${result.metadataCleanupPending?"; database/backup cleanup is also incomplete":""}. Physical free-space change: ${result.physicalFreeSpaceChangeBytes==null?'Unknown':bytes(result.physicalFreeSpaceChangeBytes)}. ${result.warning||''}`;remove.disabled=result.state==='Completed';await onChanged?.();}
  catch(error){feedback.textContent=error.message+' No reduced selection was silently deleted. Cancel and review again before retrying.';}
  finally{cancel.disabled=false;dialog.removeAttribute('aria-busy');}
 },'danger');remove.disabled=blockedCount>0;
 dialog.addEventListener('close',()=>dialog.remove());document.body.append(dialog);dialogHeader(dialog,{opener});dialog.showModal();cancel.focus();
}
