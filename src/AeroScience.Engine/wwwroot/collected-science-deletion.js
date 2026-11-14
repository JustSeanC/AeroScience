import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node as el,bytes,button} from './workstation-ui.js';
import {dialogHeader,requestDialogClose} from './dialog-ui.js';

export async function reviewCollectedScienceDeletion(api,selection,onChanged){
 const opener=document.activeElement,review=await api('/api/science/collected/deletion/preview',selection),dialog=el('dialog');
 setPresentationText(dialog,localizedCopy("ui.collectedScienceDeletion.messageDeleteCollectedMissionScience"),"aria-label");
 dialog.append(el('h2',localizedCopy("ui.collectedScienceDeletion.messageDeleteCollectedMissionScience")),el('p',localizedCopy("ui.collectedScienceDeletion.messageSelectedMissionsScientificDatasets",{value1:(String(review.missions)),value2:(String(review.datasets)),value3:(String(review.observations)),value4:(String(review.products)),value5:(String(review.pilotObservations||0)),value6:(String(bytes(review.managedBytes)))})),el('p',localizedCopy("ui.collectedScienceDeletion.messageDeletionIsPermanentSavedPilotObservationsAssociated")),el('p',localizedCopy("ui.collectedScienceDeletion.messageFlightRecordingsTelemetryMissionPlansAndExternal")));
 const status=el('p',review.blockers.length?review.blockers.join(' · '):localizedCopy("ui.collectedScienceDeletion.messageTheExactSelectionAndIntegrityWillBe"));status.setAttribute('role','status');status.setAttribute('aria-live','polite');dialog.append(status);
 const actions=el('footer',undefined,'dialog-actions');dialog.append(actions);
 const cancel=button(actions,localizedCopy("lidar.cancel"),()=>requestDialogClose(dialog),'secondary');
 const remove=button(actions,review.all?localizedCopy("ui.collectedScienceDeletion.messageDeleteAllCollectedScience"):localizedCopy("ui.collectedScienceDeletion.messageDeleteData"),async()=>{
  remove.disabled=true;cancel.disabled=true;dialog.setAttribute('aria-busy','true');setPresentationText(status,localizedCopy("ui.collectedScienceDeletion.messageDeletingReviewedScientificDataClosingThisWindow"));
  try{const result=await api('/api/science/collected/deletion/execute',{previewId:review.id,selectionHash:review.selectionHash});setPresentationText(status,localizedCopy("ui.collectedScienceDeletion.messageManagedFilesRemovedPendingFiles",{value1:(String(result.state)),value2:(String(result.removedFiles)),value3:(String(result.pendingFiles??"Unknown")),value4:(String(result.databaseCleanupPending?"; database/backup cleanup is incomplete":"")),value5:(String(result.warning||''))}));remove.disabled=result.state==='Completed';await onChanged?.();}
  catch(error){setPresentationText(status,localizedCopy("ui.collectedScienceDeletion.messageNothingIsRepresentedAsCompletelyDeleted",{value1:(String(error.message))}));}
  finally{cancel.disabled=false;dialog.removeAttribute('aria-busy');}
 },'danger');remove.disabled=review.blockers.length>0;
 dialog.addEventListener('close',()=>dialog.remove());document.body.append(dialog);dialogHeader(dialog,{opener});dialog.showModal();cancel.focus();
}
