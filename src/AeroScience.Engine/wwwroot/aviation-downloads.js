import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {dialogHeader} from './dialog-ui.js';
import {node as el,bytes,button} from './workstation-ui.js';

// Closing the view never cancels durable host work. No request begins a transfer
// until the exact retained preview is explicitly approved.
export async function openAviationDownloads(api,selection=null){
 const dialog=el('dialog');dialog.className='aviation-reference-dialog';setPresentationText(dialog,localizedCopy("ui.aviationDownloads.messageFAADownloads"),"aria-label");
 dialog.append(el('h2',localizedCopy("ui.aviationDownloads.messageFAADownloads")),el('p',localizedCopy("ui.aviationDownloads.messageRetainOfficialAviationOriginalsForOfflineReference")));
 const status=el('p');status.setAttribute('role','status');status.setAttribute('aria-live','polite');
 const storage=el('p'),settings=el('details'),settingsTitle=el('summary',localizedCopy("ui.aviationDownloads.messageAviationNetworkStorageSettings"));settings.append(settingsTitle);
 const work=el('section'),jobs=el('section');setPresentationText(jobs,localizedCopy("ui.aviationDownloads.messageSavedFAADownloadJobs"),"aria-label");
 dialog.append(status,storage,settings,work,jobs);document.body.append(dialog);dialogHeader(dialog);dialog.showModal();
 let closed=false,epoch=0,busy=false,timer,offset=0,lastJobs='';
 const paging=el('div',undefined,'action-row');dialog.append(paging);
 button(paging,localizedCopy("ui.aviationDownloads.messageRefreshFAAJobs"),()=>refresh());
 const previous=button(paging,localizedCopy("ui.aviationDownloads.messagePreviousSavedFAAJobs"),()=>{offset=Math.max(0,offset-50);lastJobs='';refresh();}),next=button(paging,localizedCopy("ui.aviationDownloads.messageNextSavedFAAJobs"),()=>{offset+=50;lastJobs='';refresh();});
 dialog.addEventListener('close',()=>{closed=true;epoch++;clearTimeout(timer);dialog.remove();});
 const field=(parent,title,value)=>{const label=el('label',title),input=el('input');input.type='number';input.min='1';input.value=value;setPresentationText(input,title,"aria-label");label.append(input);parent.append(label);return input;};
 try{
  const [{configuration,canConfigure},preferences]=await Promise.all([api('/api/aviation/configuration'),api('/api/aviation/downloads/configuration')]);if(closed)return;
  settings.append(el('p',preferences.policy));
  if(canConfigure){
   const label=el('label',undefined,'checkbox-label'),unmetered=el('input');unmetered.type='checkbox';unmetered.checked=preferences.unmeteredOnly;setPresentationText(unmetered,localizedCopy("ui.aviationDownloads.messageFAADownloadsOnlyOnAVerifiedUnmetered"),"aria-label");label.append(unmetered,presentationTextNode(localizedCopy("ui.aviationDownloads.messageFAADownloadsOnlyOnAVerifiedUnmetered")));settings.append(label);
   button(settings,localizedCopy("ui.aviationDownloads.messageSaveAviationDownloadPreferences"),async()=>{try{await api('/api/aviation/downloads/configuration',{unmeteredOnly:unmetered.checked});setPresentationText(status,localizedCopy("ui.aviationDownloads.messageAviationDownloadPreferenceSavedScientificPreferencesAre"));}catch(error){setPresentationText(status,error.message);}});
  }else settings.append(el('p',localizedCopy("ui.aviationDownloads.messageAviationNetworkAndStorageSettingsAreManaged")));
  if(selection){
   work.append(el('h3',selection.item.originalFileName),el('p',localizedCopy("ui.aviationDownloads.messageExactCatalogEditionOriginalTransferSize",{value1:(String(selection.item.edition)),value2:(String(bytes(selection.item.originalBytes)))})));
   const transfer=field(work,localizedCopy("ui.aviationDownloads.messageAviationTransferAllowanceMiB"),selection.item.originalFileName==='class_airspace_shape_files.zip'?256:128),expanded=field(work,localizedCopy("ui.aviationDownloads.messageExpandedStorageAllowanceMiB"),512);transfer.max='2048';expanded.max='8192';
   const previewArea=el('div');
   button(work,localizedCopy("ui.aviationDownloads.messageReviewFAADownload"),async()=>{
    if(busy)return;busy=true;try{
     const job=await api('/api/aviation/downloads/preview',{kind:selection.kind,itemId:selection.item.id,maximumTransferBytes:Number(transfer.value)*1048576,maximumExpandedBytes:Number(expanded.value)*1048576});if(closed)return;
     previewArea.replaceChildren();
     if(job.state==='Completed'){previewArea.append(el('p',localizedCopy("ui.aviationDownloads.messageExactVerifiedOriginalAlreadyRetainedHashesChecked")));return;}
     previewArea.append(el('h4',localizedCopy("ui.aviationDownloads.messageReviewApprove")),el('p',localizedCopy("ui.aviationDownloads.messageOriginalSizeTransferAllowanceExpanded",{value1:(String(bytes(job.preview.expectedBytes))),value2:(String(bytes(job.preview.maximumTransferBytes))),value3:(String(bytes(job.preview.maximumExpandedBytes)))})));
     const limits=el('ul');for(const text of job.preview.limitations)limits.append(el('li',text));previewArea.append(limits);
     button(previewArea,localizedCopy("ui.aviationDownloads.messageApproveFAADownload"),async()=>{if(busy)return;busy=true;try{await api(`/api/aviation/downloads/${job.id}/control`,{revision:job.revision,action:'approve',selectionHash:job.preview.selectionHash});previewArea.replaceChildren(el('p',localizedCopy("ui.aviationDownloads.messageApprovedTheHostManagesTheStoredJob")));await refresh();}catch(error){setPresentationText(status,error.message);}finally{busy=false;}});
    }catch(error){if(!closed)setPresentationText(status,error.message);}finally{busy=false;}
   });work.append(previewArea);
   if(!configuration.onlineEnabled)work.append(el('p',localizedCopy("ui.aviationDownloads.messageFAAAccessIsDisabledEnableAccessOn")));
  }
  await refresh();
 }catch(error){if(!closed)setPresentationText(status,error.message);}
 async function refresh(){
  clearTimeout(timer);const own=++epoch;
  try{
   const [list,s]=await Promise.all([api('/api/aviation/downloads?offset='+offset),api('/api/aviation/downloads/storage')]);if(closed||own!==epoch)return;
   setPresentationText(storage,s.available?localizedCopy("ui.aviationDownloads.messageAviationUsedReservedAllowancesPhysical",{value1:(String(bytes(s.usedBytes))),value2:(String(bytes(s.reservedBytes))),value3:(String(bytes(s.physicalFreeBytes)))}):(s.error||localizedCopy("ui.aviationDownloads.messageAviationStorageUnknown")));
   previous.disabled=offset===0;next.disabled=list.length<50||offset>=10000;
   const signature=JSON.stringify(list);if(signature===lastJobs)return;lastJobs=signature;
   const focused=document.activeElement?.closest('[data-job-id]'),focusId=focused?.dataset.jobId,focusAction=focused?.contains(document.activeElement)?document.activeElement?.dataset.action:null;
   jobs.replaceChildren(el('h3',localizedCopy("ui.aviationDownloads.messageSavedFAADownloadsPage",{value1:(String(offset/50+1))})));
   for(const job of list){
    const card=el('article',undefined,'panel');card.dataset.jobId=job.id;card.append(el('h4',`${job.preview.item.originalFileName} · ${job.preview.item.edition}`),el('p',job.state),el('p',localizedCopy("ui.aviationDownloads.messageReceivedAcrossAttemptsApprovedTransferLimit",{value1:(String(bytes(job.downloadedBytes))),value2:(String(bytes(job.preview.maximumTransferBytes))),value3:(String(bytes(job.preview.expectedBytes)))})));
    if(job.state==='Completed')card.append(el('p',localizedCopy("ui.aviationDownloads.messageVerifiedOriginalSHA256CheckedCompanionsRetained",{value1:(String(job.originalSha256)),value2:(String((job.files||[]).length))})));
    if(job.state==='Completed'&&job.preview.item.product==='nasr')card.append(el('p',localizedCopy("ui.aviationDownloads.messageOptionalImportBuildsALocalFAAReference")));
    if(job.error)card.append(el('p',job.error));
    if(job.retryAfterUtc)card.append(el('p',localizedCopy("ui.aviationDownloads.messageNextEligibleProviderRetry",{value1:(String(new Date(job.retryAfterUtc).toLocaleString()))})));
    const controls=el('div',undefined,'action-row');card.append(controls);
    const actions=job.state==='Completed'?(job.preview.item.product==='nasr'?['verify','import-vectors']:['verify']):job.state==='Awaiting Approval'||job.state==='Cancelled'?[]:job.state==='Paused by User'||job.state==='Interrupted'||job.state==='Failed'||job.state.startsWith('Waiting')?['resume','cancel']:['pause','cancel'];
    for(const action of actions){const control=button(controls,{'import-vectors':localizedCopy("ui.aviationDownloads.messageImportFAAReferenceFeatures"),verify:localizedCopy("ui.aviationDownloads.messageVerifyRetainedOriginal"),pause:localizedCopy("ui.aviationDownloads.messagePauseFAADownload"),resume:localizedCopy("ui.aviationDownloads.messageResumeRetryFAADownload"),cancel:localizedCopy("ui.aviationDownloads.messageCancelFAADownload")}[action],async()=>{try{await api(`/api/aviation/downloads/${job.id}/`+(action==='verify'?'verify':action==='import-vectors'?'import-vectors':'control'),action==='verify'||action==='import-vectors'?{}:{revision:job.revision,action});setPresentationText(status,action==='import-vectors'?localizedCopy("ui.aviationDownloads.messageFAAReferenceFeaturesImportedOpenOverlays"):action==='verify'?localizedCopy("ui.aviationDownloads.messageRetainedOriginalAndAllExtractedFilesMatch"):localizedCopy("ui.aviationDownloads.messageStoredJobUpdated"));await refresh();}catch(error){setPresentationText(status,error.message);}});control.dataset.action=action;}
    jobs.append(card);
   }
   if(!list.length)jobs.append(el('p',localizedCopy("ui.aviationDownloads.messageNoRetainedDownloadIntentsOnThisPage")));
   if(focusId&&focusAction)jobs.querySelector(`[data-job-id="${focusId}"] [data-action="${focusAction}"]`)?.focus({preventScroll:true});
  }catch(error){if(!closed&&own===epoch)setPresentationText(status,error.message);}
  finally{if(!closed)timer=setTimeout(refresh,5000);}
 }
}
