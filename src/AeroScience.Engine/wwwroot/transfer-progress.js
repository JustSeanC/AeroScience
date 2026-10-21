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
 return {stage:row.status==='Downloading'?now-time>=8000?'Waiting for Network':row.stage==='Saving'?'Importing':row.stage==='Validating'?'Verifying':row.stage:row.status==='Offline'?'Waiting for Network':row.status,measuredNetworkBytes:row.downloadedBytes,totalOriginalBytes:null,preparedOriginalBytes:null,bytesPerSecond:fresh?smooth:null,remainingSecondsLow:null,remainingSecondsHigh:null,basis:'Numeric subsets: original transfer total remains Unknown until the provider determines required native blocks. Transfer caps and footprint candidates are not size estimates.'};
}
export function timeRange(low,high){
 if(!Number.isFinite(low)||!Number.isFinite(high))return 'Time estimate available after download starts';
 const unit=high>=3600?'hours':high>=60?'minutes':'seconds',divisor=high>=3600?3600:high>=60?60:1;
 return `About ${Math.max(1,Math.floor(low/divisor))}–${Math.max(1,Math.ceil(high/divisor))} ${unit}`;
}
export function renderTransferProgress(parent,progress,state='Queued'){
 const box=el('div',undefined,'transfer-progress');box.setAttribute('aria-label','Measured transfer status');parent.append(box);
 box.append(el('strong',progress?.stage||state));
 if(!progress){box.append(el('p',state==='Completed'?'Dataset ready — verified Library publication complete.':'Transfer size/time remaining unknown. Queue, authorization, allowance and flight priority can delay work.'));return box;}
 const known=Number.isFinite(progress.totalOriginalBytes),prepared=Number.isFinite(progress.preparedOriginalBytes)?progress.preparedOriginalBytes:null;
 box.append(el('p',`Original file bytes present: ${prepared==null?'Unknown':bytes(prepared)} / ${known?bytes(progress.totalOriginalBytes):'Unknown'}. Actual network bytes this attempt: ${bytes(progress.measuredNetworkBytes)}; retries may transfer the same bytes again.`));
 if(known&&progress.totalOriginalBytes>0&&prepared!=null){const indicator=el('progress');indicator.max=progress.totalOriginalBytes;indicator.value=Math.min(prepared,progress.totalOriginalBytes);indicator.setAttribute('aria-label','Original file transfer completion, not scientific verification');box.append(indicator,el('span',` ${(100*prepared/progress.totalOriginalBytes).toFixed(1)}% original bytes present`));}
 box.append(el('p',`Current measured speed: ${Number.isFinite(progress.currentBytesPerSecond)?bytes(progress.currentBytesPerSecond)+'/s':'Calculating / not transferring'} · Smoothed recent speed: ${Number.isFinite(progress.bytesPerSecond)?bytes(progress.bytesPerSecond)+'/s':'Not established'}`),el('p',progress.stage==='Verifying'||progress.stage==='Importing'?`${progress.stage} scientific files — time remaining unknown`:timeRange(progress.remainingSecondsLow,progress.remainingSecondsHigh)),el('p',progress.totalFiles?`${progress.completedFiles}/${progress.totalFiles} original files transferred${progress.currentFile?' · current file '+progress.currentFile:''}. File transfer complete does not imply Dataset ready.`:'Original file count unknown; bounded numeric subsets are selected by the provider worker.'),el('p',progress.basis));return box;
}
export function renderQueueTransfers(parent,jobs=[]){
 const active=jobs.filter(j=>!['Awaiting Approval','Completed','Cancelled'].includes(j.state));if(!active.length)return;
 const section=el('section',undefined,'panel');section.setAttribute('aria-label','Scientific Library transfers');section.append(el('h3','Scientific Library transfers — separate from mission readiness'));
 let remaining=0,unknown=0,rate=0;for(const job of active){const p=job.progress;if(Number.isFinite(p?.totalOriginalBytes)&&Number.isFinite(p?.preparedOriginalBytes))remaining+=Math.max(0,p.totalOriginalBytes-p.preparedOriginalBytes);else unknown++;if(job.state==='Downloading'&&Number.isFinite(p?.bytesPerSecond))rate+=p.bytesPerSecond;}
 section.append(el('p',`${bytes(remaining)} known original bytes remaining · ${unknown} jobs with unknown remaining size · ${rate?bytes(rate)+'/s current combined measured throughput':'Combined speed not established'}. Queue completion time unknown: future work depends on consent, authorization, allowance and flight priority.`));
 for(const job of active.slice(0,10)){const row=el('article');row.append(el('h4',job.preview.products[0]?.product||'Library job'));renderTransferProgress(row,job.progress,job.state);section.append(row);}parent.append(section);
}
