import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node as el,bytes,readableName} from './workstation-ui.js';
// Both combined and regional rates use bounded actual-byte windows. Provider totals,
// local cache hits and verification never enter a network-speed estimate.
const rateWindows=new Map();
function measuredRate(key,bytes,stamp,active,now,clock=now/1000){
 let previous=rateWindows.get(key),samples=previous?.samples||[],lastAdvance=previous?.lastAdvance??now,advancing=previous?.advancing??false;
 const changed=!previous||stamp!==previous.stamp;
 if(!active||!Number.isFinite(bytes)||!previous?.active||bytes<previous.bytes||changed&&clock<=previous.clock){samples=[];advancing=false;lastAdvance=now;}
 if(active&&Number.isFinite(bytes)&&changed){
  advancing=!!previous?.active&&bytes>previous.bytes;
  if(advancing)lastAdvance=now;
  samples.push({time:clock,bytes});samples=samples.filter(s=>clock-s.time<=15).slice(-32);
 }
 if(rateWindows.size>=256&&!rateWindows.has(key))rateWindows.delete(rateWindows.keys().next().value);
 rateWindows.set(key,{samples,lastAdvance,advancing,active,bytes,stamp,clock:changed?clock:previous.clock});
 const elapsed=samples.length? samples.at(-1).time-samples[0].time:0,delta=samples.length?samples.at(-1).bytes-samples[0].bytes:0;
 return active&&advancing&&now-lastAdvance<6500&&samples.length>=3&&elapsed>=2&&delta>=65536?delta/elapsed:null;
}
export function networkTransfer(snapshot,now=performance.now()){
 if(!snapshot)return {bytesPerSecond:null,active:0,bytes:0};
 const key='network:'+snapshot.sessionId,previous=rateWindows.get(key),seen=!previous||previous.stamp!==snapshot.elapsedSeconds?now:previous.seen??now;
 const rate=measuredRate(key,snapshot.bytes,snapshot.elapsedSeconds,snapshot.activeTransfers>0,now,snapshot.elapsedSeconds);rateWindows.get(key).seen=seen;
 return {bytesPerSecond:now-seen<8000?rate:null,active:snapshot.activeTransfers,bytes:snapshot.bytes,stale:now-seen>=8000};
}
export function preparationTransfer(row,now=performance.now()){
 const active=row.status==='Downloading'&&row.stage==='Downloading';
 const rate=measuredRate('preparation:'+row.key+'|'+(row.attemptId||''),row.downloadedBytes,row.updatedUtc,active,now);
 return {stage:row.status==='Downloading'?row.stage==='Saving'?'Importing':row.stage==='Validating'?'Verifying':row.stage:row.status==='Offline'?'Waiting for Network':row.status,measuredNetworkBytes:row.downloadedBytes,totalOriginalBytes:null,preparedOriginalBytes:null,currentBytesPerSecond:rate,bytesPerSecond:rate,remainingSecondsLow:null,remainingSecondsHigh:null,basis:textForUI("ui.transferProgress.messageNumericSubsetsOriginalTransferTotalRemainsUnknown")};
}
export function timeRange(low,high){
 if(!Number.isFinite(low)||!Number.isFinite(high))return textForUI("ui.transferProgress.messageTimeEstimateAvailableAfterDownloadStarts");
 const unit=high>=3600?'hours':high>=60?'minutes':'seconds',divisor=high>=3600?3600:high>=60?60:1;
 return textForUI('transfer.estimate.'+({hours:'hour',minutes:'minute',seconds:'second'}[unit]),{low:Math.max(1,Math.floor(low/divisor)),count:Math.max(1,Math.ceil(high/divisor))});
}
const transferMessages={"Queued":"ui.downloadActivity.messageQueued","Downloading":"preparation.status.downloading","Finding source":"ui.downloadActivity.messageFindingSource","Waiting for Network":"ui.preparationSummary.messageWaitingForNetwork","Waiting for Allowance":"ui.preparationSummary.messageWaitingForAllowance","Awaiting Approval":"transfer.stage.awaitingApproval","Waiting for Authorization":"transfer.stage.waitingForAuthorization","Verifying":"ui.transferProgress.messageVerifying","Importing":"ui.transferProgress.messageImporting","Completed":"ui.downloadActivity.messageCompleted","Cancelled":"ui.submissionBuilder.messageCancelled","Failed":"ui.downloadActivity.messageFailed","Paused":"home.science.paused","Ready":"ui.missionOperations.messageReady","Dataset Ready":"ui.hyperspectralImport.messageDatasetReady","Offline":"transfer.stage.offline","Unavailable":"common.unavailable"};
const transferLabel=code=>Object.hasOwn(transferMessages,code)?localizedCopy(transferMessages[code]):code;
export function renderTransferProgress(parent,progress,state='Queued'){
 const measuredAt=Date.parse(progress?.updatedUtc),fresh=Number.isFinite(measuredAt)&&Date.now()-measuredAt<8000;
 if(progress&&!fresh)progress={...progress,bytesPerSecond:null,currentBytesPerSecond:null,remainingSecondsLow:null,remainingSecondsHigh:null};
 const box=el('div',undefined,'transfer-progress');setPresentationText(box,localizedCopy("ui.transferProgress.messageMeasuredTransferStatus"),"aria-label");parent.append(box);
 box.append(el('strong',transferLabel(progress?.stage||state)));
 if(!progress){box.append(el('p',state==='Completed'?localizedCopy("ui.transferProgress.messageDatasetReadyVerifiedLibraryPublicationComplete"):localizedCopy("ui.transferProgress.messageTransferSizeTimeRemainingUnknownQueueAuthorizationAllowance")));return box;}
 const known=Number.isFinite(progress.totalOriginalBytes),prepared=Number.isFinite(progress.preparedOriginalBytes)?progress.preparedOriginalBytes:null;
 box.append(el('p',localizedCopy("ui.transferProgress.messageOriginalFileBytesPresentActual",{value1:(String(prepared==null?textForUI("localization.unknownValue"):bytes(prepared))),value2:(String(known?bytes(progress.totalOriginalBytes):textForUI("localization.unknownValue"))),value3:(String(bytes(progress.measuredNetworkBytes)))})));
 if(known&&progress.totalOriginalBytes>0&&prepared!=null){const indicator=el('progress');indicator.max=progress.totalOriginalBytes;indicator.value=Math.min(prepared,progress.totalOriginalBytes);setPresentationText(indicator,localizedCopy("ui.transferProgress.messageOriginalFileTransferCompletionNotScientificVerification"),"aria-label");box.append(indicator,el('span',localizedCopy("ui.transferProgress.messageOriginalBytesPresent",{value1:(String((100*prepared/progress.totalOriginalBytes).toFixed(1)))})));}
 box.append(el('p',localizedCopy("ui.transferProgress.messageCurrentMeasuredSpeedSmoothedRecentSpeed",{value1:(String(Number.isFinite(progress.currentBytesPerSecond)?bytes(progress.currentBytesPerSecond)+'/s':textForUI("ui.transferProgress.messageCalculatingNotTransferring"))),value2:(String(Number.isFinite(progress.bytesPerSecond)?bytes(progress.bytesPerSecond)+'/s':textForUI("ui.downloadWorkspace.messageNotEstablished")))})),el('p',progress.stage==='Verifying'||progress.stage==='Importing'?localizedCopy("ui.transferProgress.messageScientificFilesTimeRemainingUnknown",{value1:(String(progress.stage))}):timeRange(progress.remainingSecondsLow,progress.remainingSecondsHigh)),el('p',progress.totalFiles?localizedCopy("ui.transferProgress.messageOriginalFilesTransferredFile",{value1:(String(progress.completedFiles)),value2:(String(progress.totalFiles)),value3:(String(progress.currentFile?textForUI("ui.transferProgress.messageCurrentFile",{value1:(progress.currentFile)}):''))}):localizedCopy("ui.transferProgress.messageOriginalFileCountUnknownBoundedNumericSubsets")),el('p',progress.basis));return box;
}
export function renderQueueTransfers(parent,jobs=[]){
 const active=jobs.filter(j=>!['Awaiting Approval','Completed','Cancelled'].includes(j.state));if(!active.length)return;
 const section=el('section',undefined,'panel');setPresentationText(section,localizedCopy("ui.transferProgress.messageScientificLibraryTransfers"),"aria-label");section.append(el('h3',localizedCopy("ui.transferProgress.messageScientificLibraryTransfersSeparateFromMission")));
 let remaining=0,unknown=0,rate=0;for(const job of active){const p=job.progress;if(Number.isFinite(p?.totalOriginalBytes)&&Number.isFinite(p?.preparedOriginalBytes))remaining+=Math.max(0,p.totalOriginalBytes-p.preparedOriginalBytes);else unknown++;if(job.state==='Downloading'&&p?.stage==='Downloading'&&Date.now()-Date.parse(p.updatedUtc)<8000&&Number.isFinite(p?.bytesPerSecond))rate+=p.bytesPerSecond;}
 section.append(el('p',localizedCopy("ui.transferProgress.messageKnownOriginalBytesRemainingJobs",{value1:(String(bytes(remaining))),value2:(String(unknown)),value3:(String(rate?bytes(rate)+'/s current combined measured throughput':textForUI("ui.transferProgress.messageCombinedSpeedNotEstablished")))})));
 for(const job of active.slice(0,10)){const row=el('article');row.append(el('h4',job.preview.products[0]?.product||localizedCopy("ui.transferProgress.messageLibraryJob")));renderTransferProgress(row,job.progress,job.state);section.append(row);}parent.append(section);
}

// Read-only display of shared HTTP body counters. No network polling is performed here.
export function renderNetworkActivity(parent,data){
 parent.replaceChildren();const section=el('section',undefined,'panel scientific-network-activity');setPresentationText(section,localizedCopy('network.title'),'aria-label');section.append(el('h3',localizedCopy('network.title')));
 const measured=networkTransfer(data?.networkActivity);if(!data?.networkActivity||measured.stale){section.append(el('p',localizedCopy('network.unavailable')));parent.append(section);return;}
 section.append(el('p',localizedCopy('network.summary',{speed:measured.bytesPerSecond==null?localizedCopy('network.rateUnknown'):bytes(measured.bytesPerSecond)+'/s',activity:localizedCopy('network.activeTransfers',{count:measured.active}),bytes:bytes(measured.bytes)})),el('p',localizedCopy('network.basis')));
 const sources=[...(data?.jobs||[]).filter(j=>j.status==='Downloading').map(j=>[readableName(j.kind),j.provider||j.dataset].filter(Boolean).join(' · ')),...(data?.libraryJobs||[]).filter(j=>j.state==='Downloading').flatMap(j=>(j.preview?.products||[]).map(p=>p.product))];
 if(sources.length)section.append(el('p',localizedCopy('network.sources',{sources:[...new Set(sources)].slice(0,8).join('; ')})));
 parent.append(section);
}
export function preparationActionText(action,name){
 const keys={pause:'preparation.pauseNamed',resume:'preparation.resumeNamed',continue:'preparation.continueNamed',cancel:'preparation.cancelNamed',retry:'preparation.retryNamed'};
 return localizedCopy(keys[action],{name});
}
