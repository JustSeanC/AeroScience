import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node as el,bytes} from './workstation-ui.js';
const preparationRates=new Map();
export function preparationTransfer(row,now=Date.now()){
 const time=Date.parse(row.updatedUtc),previous=preparationRates.get(row.key);let samples=previous?.samples||[],smooth=previous?.smooth??null;
 if(row.status!=='Downloading'||previous&&row.downloadedBytes<previous.bytes){samples=[];smooth=null;}
 if(row.status==='Downloading'&&Number.isFinite(time)&&(!previous||time!==previous.time)){
  if(previous&&time>previous.time&&row.downloadedBytes>previous.bytes){const rate=(row.downloadedBytes-previous.bytes)/((time-previous.time)/1000);smooth=smooth==null?rate:.25*rate+.75*smooth;}
  samples.push({time,bytes:row.downloadedBytes});samples=samples.filter(s=>time-s.time<=15000).slice(-64);
 }
 if(preparationRates.size>=256&&!preparationRates.has(row.key))preparationRates.delete(preparationRates.keys().next().value);preparationRates.set(row.key,{samples,smooth,time,bytes:row.downloadedBytes});
 const fresh=row.status==='Downloading'&&now-time<8000&&samples.length>=3&&time-samples[0].time>=2000&&row.downloadedBytes-samples[0].bytes>=65536;
 return {stage:row.status==='Downloading'?now-time>=8000?"Waiting for Network":row.stage==='Saving'?"Importing":row.stage==='Validating'?"Verifying":row.stage:row.status==='Offline'?"Waiting for Network":row.status,measuredNetworkBytes:row.downloadedBytes,totalOriginalBytes:null,preparedOriginalBytes:null,bytesPerSecond:fresh?smooth:null,remainingSecondsLow:null,remainingSecondsHigh:null,basis:textForUI("ui.transferProgress.messageNumericSubsetsOriginalTransferTotalRemainsUnknown")};
}
export function timeRange(low,high){
 if(!Number.isFinite(low)||!Number.isFinite(high))return textForUI("ui.transferProgress.messageTimeEstimateAvailableAfterDownloadStarts");
 const unit=high>=3600?'hours':high>=60?'minutes':'seconds',divisor=high>=3600?3600:high>=60?60:1;
 return textForUI('transfer.estimate.'+({hours:'hour',minutes:'minute',seconds:'second'}[unit]),{low:Math.max(1,Math.floor(low/divisor)),count:Math.max(1,Math.ceil(high/divisor))});
}
const transferMessages={"Queued":"ui.downloadActivity.messageQueued","Downloading":"preparation.status.downloading","Finding source":"ui.downloadActivity.messageFindingSource","Waiting for Network":"ui.preparationSummary.messageWaitingForNetwork","Waiting for Allowance":"ui.preparationSummary.messageWaitingForAllowance","Awaiting Approval":"transfer.stage.awaitingApproval","Waiting for Authorization":"transfer.stage.waitingForAuthorization","Verifying":"ui.transferProgress.messageVerifying","Importing":"ui.transferProgress.messageImporting","Completed":"ui.downloadActivity.messageCompleted","Cancelled":"ui.submissionBuilder.messageCancelled","Failed":"ui.downloadActivity.messageFailed","Paused":"home.science.paused","Ready":"ui.missionOperations.messageReady","Dataset Ready":"ui.hyperspectralImport.messageDatasetReady","Offline":"transfer.stage.offline","Unavailable":"common.unavailable"};
const transferLabel=code=>Object.hasOwn(transferMessages,code)?localizedCopy(transferMessages[code]):code;
export function renderTransferProgress(parent,progress,state='Queued'){
 const box=el('div',undefined,'transfer-progress');setPresentationText(box,localizedCopy("ui.transferProgress.messageMeasuredTransferStatus"),"aria-label");parent.append(box);
 box.append(el('strong',transferLabel(progress?.stage||state)));
 if(!progress){box.append(el('p',state==='Completed'?localizedCopy("ui.transferProgress.messageDatasetReadyVerifiedLibraryPublicationComplete"):localizedCopy("ui.transferProgress.messageTransferSizeTimeRemainingUnknownQueueAuthorizationAllowance")));return box;}
 const known=Number.isFinite(progress.totalOriginalBytes),prepared=Number.isFinite(progress.preparedOriginalBytes)?progress.preparedOriginalBytes:null;
 box.append(el('p',localizedCopy("ui.transferProgress.messageOriginalFileBytesPresentActual",{value1:(String(prepared==null?'Unknown':bytes(prepared))),value2:(String(known?bytes(progress.totalOriginalBytes):'Unknown')),value3:(String(bytes(progress.measuredNetworkBytes)))})));
 if(known&&progress.totalOriginalBytes>0&&prepared!=null){const indicator=el('progress');indicator.max=progress.totalOriginalBytes;indicator.value=Math.min(prepared,progress.totalOriginalBytes);setPresentationText(indicator,localizedCopy("ui.transferProgress.messageOriginalFileTransferCompletionNotScientificVerification"),"aria-label");box.append(indicator,el('span',localizedCopy("ui.transferProgress.messageOriginalBytesPresent",{value1:(String((100*prepared/progress.totalOriginalBytes).toFixed(1)))})));}
 box.append(el('p',localizedCopy("ui.transferProgress.messageCurrentMeasuredSpeedSmoothedRecentSpeed",{value1:(String(Number.isFinite(progress.currentBytesPerSecond)?bytes(progress.currentBytesPerSecond)+'/s':'Calculating / not transferring')),value2:(String(Number.isFinite(progress.bytesPerSecond)?bytes(progress.bytesPerSecond)+'/s':'Not established'))})),el('p',progress.stage==='Verifying'||progress.stage==='Importing'?localizedCopy("ui.transferProgress.messageScientificFilesTimeRemainingUnknown",{value1:(String(progress.stage))}):timeRange(progress.remainingSecondsLow,progress.remainingSecondsHigh)),el('p',progress.totalFiles?localizedCopy("ui.transferProgress.messageOriginalFilesTransferredFile",{value1:(String(progress.completedFiles)),value2:(String(progress.totalFiles)),value3:(String(progress.currentFile?' · current file '+progress.currentFile:''))}):localizedCopy("ui.transferProgress.messageOriginalFileCountUnknownBoundedNumericSubsets")),el('p',progress.basis));return box;
}
export function renderQueueTransfers(parent,jobs=[]){
 const active=jobs.filter(j=>!['Awaiting Approval','Completed','Cancelled'].includes(j.state));if(!active.length)return;
 const section=el('section',undefined,'panel');setPresentationText(section,localizedCopy("ui.transferProgress.messageScientificLibraryTransfers"),"aria-label");section.append(el('h3',localizedCopy("ui.transferProgress.messageScientificLibraryTransfersSeparateFromMission")));
 let remaining=0,unknown=0,rate=0;for(const job of active){const p=job.progress;if(Number.isFinite(p?.totalOriginalBytes)&&Number.isFinite(p?.preparedOriginalBytes))remaining+=Math.max(0,p.totalOriginalBytes-p.preparedOriginalBytes);else unknown++;if(job.state==='Downloading'&&Number.isFinite(p?.bytesPerSecond))rate+=p.bytesPerSecond;}
 section.append(el('p',localizedCopy("ui.transferProgress.messageKnownOriginalBytesRemainingJobs",{value1:(String(bytes(remaining))),value2:(String(unknown)),value3:(String(rate?bytes(rate)+'/s current combined measured throughput':'Combined speed not established'))})));
 for(const job of active.slice(0,10)){const row=el('article');row.append(el('h4',job.preview.products[0]?.product||localizedCopy("ui.transferProgress.messageLibraryJob")));renderTransferProgress(row,job.progress,job.state);section.append(row);}parent.append(section);
}
