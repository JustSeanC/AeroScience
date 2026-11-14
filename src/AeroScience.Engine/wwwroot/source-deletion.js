import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node as el,bytes,button} from './workstation-ui.js';
import {dialogHeader,requestDialogClose} from './dialog-ui.js';
export async function reviewSourceDeletion(api,selection,onChanged){
 const opener=document.activeElement,preview=await api('/api/science/sources/deletion/preview',selection),dialog=el('dialog');setPresentationText(dialog,localizedCopy("ui.sourceDeletion.messageDeleteDownloadedExternalData"),"aria-label");
 const total=preview.totalSources??preview.sources.length,blockedCount=preview.blockedSources??preview.sources.filter(s=>s.blockers.length).length;
 dialog.append(el('h2',localizedCopy("ui.sourceDeletion.messageDeleteDownloadedExternalData")),el('p',preview.all?localizedCopy("ui.sourceDeletion.messageEveryEligibleExternalSourceInThisExact"):localizedCopy("ui.sourceDeletion.messageTheReviewedExternalDatasetsWillBePermanently")),el('p',localizedCopy("ui.sourceDeletion.messageSelectedSourceRegionsEligible",{value1:(String(total)),value2:(String(preview.eligibleSources)),value3:(String(bytes(preview.eligibleManagedBytes)))})),el('p',preview.preserved));
 const list=el('div');dialog.append(list);
 function show(items,next){list.replaceChildren();for(const source of items){const row=el('article',undefined,'finding-card');row.append(el('h3',source.name),el('p',localizedCopy("ui.sourceDeletion.messageIDVerified",{value1:(String(source.provider)),value2:(String(source.kind)),value3:(String(source.id)),value4:(String(bytes(source.managedBytes))),value5:(String(source.managedFileCount??source.files.length))})),el('p',source.blockers.length?localizedCopy("ui.sourceDeletion.messageBlocked",{value1:(String(source.blockers.join(' · ')))}):localizedCopy("ui.sourceDeletion.messageEligibleOwnershipOriginalHashesAndResearchDependencies")));list.append(row);}if(next!=null)button(list,localizedCopy("ui.sourceDeletion.messageReviewMoreSelectedRegions"),async()=>{const page=await api(`/api/science/sources/deletion/${preview.id}/items?after=${next}`);if(dialog.open)show(page.items,page.next);},'secondary');}
 show(preview.sources,preview.next);
 if(blockedCount)dialog.append(el('p',localizedCopy("ui.sourceDeletion.messageSelectedRegionsAreProtectedOrUnavailable",{value1:(String(blockedCount))})));
 const feedback=el('p');feedback.setAttribute('role','status');feedback.setAttribute('aria-live','polite');const actions=el('footer',undefined,'dialog-actions');dialog.append(feedback,actions);
 const cancel=button(actions,localizedCopy("lidar.cancel"),()=>requestDialogClose(dialog),'secondary');
 const remove=button(actions,preview.all?localizedCopy("ui.sourceDeletion.messageDeleteAllDownloadedData"):localizedCopy("ui.collectedScienceDeletion.messageDeleteData"),async()=>{
  remove.disabled=true;cancel.disabled=true;dialog.setAttribute('aria-busy','true');setPresentationText(feedback,localizedCopy("ui.sourceDeletion.messageRecheckingTheExactSelectionAndProtectedResearch"));
  try{const result=await api('/api/science/sources/deletion/execute',{previewId:preview.id,selectionHash:preview.selectionHash});setPresentationText(feedback,localizedCopy("ui.sourceDeletion.messageSourceRegionsRemovedManaged",{value1:(String(result.state)),value2:(String(result.removedSources)),value3:(String(bytes(result.removedManagedBytes))),value4:(String(bytes(result.pendingManagedBytes))),value5:(String(result.metadataCleanupPending?"; database/backup cleanup is also incomplete":"")),value6:(String(result.physicalFreeSpaceChangeBytes==null?'Unknown':bytes(result.physicalFreeSpaceChangeBytes))),value7:(String(result.warning||''))}));remove.disabled=result.state==='Completed';await onChanged?.();}
  catch(error){setPresentationText(feedback,localizedCopy("ui.sourceDeletion.messageNoReducedSelectionWasSilentlyDeleted",{value1:(String(error.message))}));}
  finally{cancel.disabled=false;dialog.removeAttribute('aria-busy');}
 },'danger');remove.disabled=blockedCount>0;
 dialog.addEventListener('close',()=>dialog.remove());document.body.append(dialog);dialogHeader(dialog,{opener});dialog.showModal();cancel.focus();
}
