import {node as el,bytes,button} from './workstation-ui.js';
import {dialogHeader,requestDialogClose} from './dialog-ui.js';

export async function reviewCollectedScienceDeletion(api,selection,onChanged){
 const opener=document.activeElement,review=await api('/api/science/collected/deletion/preview',selection),dialog=el('dialog');
 dialog.setAttribute('aria-label','Delete Collected Mission Science');
 dialog.append(el('h2','Delete Collected Mission Science'),el('p',`${review.missions} selected missions · ${review.datasets} scientific datasets · ${review.observations} measurements · ${review.products} generated products. Estimated managed file space freed: ${bytes(review.managedBytes)}; physical recovery may differ.`),el('p','Deletion is permanent. Saved scientific measurements, acquired products, generated analysis, dependent grades, and their managed cached or backup representations will be removed. No new recovery backup is created.'),el('p','Flight recordings, telemetry, mission plans and external Scientific Library source datasets remain separate and untouched. Deleted scientific evidence cannot be reconstructed or represented by an old grade.'));
 const status=el('p',review.blockers.length?review.blockers.join(' · '):'The exact selection and integrity will be rechecked before deletion.');status.setAttribute('role','status');status.setAttribute('aria-live','polite');dialog.append(status);
 const actions=el('footer',undefined,'dialog-actions');dialog.append(actions);
 const cancel=button(actions,'Cancel',()=>requestDialogClose(dialog),'secondary');
 const remove=button(actions,review.all?'Delete All Collected Science':'Delete Data',async()=>{
  remove.disabled=true;cancel.disabled=true;dialog.setAttribute('aria-busy','true');status.textContent='Deleting reviewed scientific data. Closing this window does not cancel an approved deletion.';
  try{const result=await api('/api/science/collected/deletion/execute',{previewId:review.id,selectionHash:review.selectionHash});status.textContent=`${result.state}: ${result.removedFiles} managed files removed; ${result.pendingFiles??"Unknown"} pending files${result.databaseCleanupPending?"; database/backup cleanup is incomplete":""}. ${result.warning||''}`;remove.disabled=result.state==='Completed';await onChanged?.();}
  catch(error){status.textContent=error.message+' Nothing is represented as completely deleted until managed cleanup succeeds.';}
  finally{cancel.disabled=false;dialog.removeAttribute('aria-busy');}
 },'danger');remove.disabled=review.blockers.length>0;
 dialog.addEventListener('close',()=>dialog.remove());document.body.append(dialog);dialogHeader(dialog,{opener});dialog.showModal();cancel.focus();
}
