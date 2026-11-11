import {node as el,bytes} from './workstation-ui.js';
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
 return {bytesPerSecond:measuredRate('network:'+snapshot.sessionId,snapshot.bytes,snapshot.elapsedSeconds,snapshot.activeTransfers>0,now,snapshot.elapsedSeconds),active:snapshot.activeTransfers,bytes:snapshot.bytes};
}
export function preparationTransfer(row,now=performance.now()){
 const active=row.status==='Downloading'&&row.stage==='Downloading';
 const rate=measuredRate('preparation:'+row.key+'|'+(row.attemptId||''),row.downloadedBytes,row.updatedUtc,active,now);
 return {stage:row.status==='Downloading'?row.stage==='Saving'?'Importing':row.stage==='Validating'?'Verifying':row.stage:row.status==='Offline'?'Waiting for Network':row.status,measuredNetworkBytes:row.downloadedBytes,totalOriginalBytes:null,preparedOriginalBytes:null,currentBytesPerSecond:rate,bytesPerSecond:rate,remainingSecondsLow:null,remainingSecondsHigh:null,basis:'Numeric subsets: original transfer total remains Unknown until the provider determines required native blocks. Transfer caps and footprint candidates are not size estimates.'};
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
 else if(progress.stage==='Downloading'){const indicator=el('progress');indicator.setAttribute('aria-label','Downloading — transfer size unknown');box.append(indicator);}
 box.append(el('p',`Current measured speed: ${Number.isFinite(progress.currentBytesPerSecond)?bytes(progress.currentBytesPerSecond)+'/s':'Calculating / not transferring'} · Smoothed recent speed: ${Number.isFinite(progress.bytesPerSecond)?bytes(progress.bytesPerSecond)+'/s':'Not established'}`),el('p',progress.stage==='Verifying'||progress.stage==='Importing'?`${progress.stage} scientific files — time remaining unknown`:timeRange(progress.remainingSecondsLow,progress.remainingSecondsHigh)),el('p',progress.totalFiles?`${progress.completedFiles}/${progress.totalFiles} original files transferred${progress.currentFile?' · current file '+progress.currentFile:''}. File transfer complete does not imply Dataset ready.`:'Original file count unknown; bounded numeric subsets are selected by the provider worker.'),el('p',progress.basis));return box;
}
export function renderQueueTransfers(parent,jobs=[]){
 const active=jobs.filter(j=>!['Awaiting Approval','Completed','Cancelled'].includes(j.state));if(!active.length)return;
 const section=el('section',undefined,'panel');section.setAttribute('aria-label','Scientific Library transfers');section.append(el('h3','Scientific Library transfers — separate from mission readiness'));
 let remaining=0,unknown=0,rate=0;for(const job of active){const p=job.progress;if(Number.isFinite(p?.totalOriginalBytes)&&Number.isFinite(p?.preparedOriginalBytes))remaining+=Math.max(0,p.totalOriginalBytes-p.preparedOriginalBytes);else unknown++;if(job.state==='Downloading'&&Number.isFinite(p?.bytesPerSecond))rate+=p.bytesPerSecond;}
 section.append(el('p',`${bytes(remaining)} known original bytes remaining · ${unknown} jobs with unknown remaining size · ${rate?bytes(rate)+'/s current combined measured throughput':'Combined speed not established'}. Queue completion time unknown: future work depends on consent, authorization, allowance and flight priority.`));
 for(const job of active.slice(0,10)){const row=el('article');row.append(el('h4',job.preview.products[0]?.product||'Library job'));renderTransferProgress(row,job.progress,job.state);section.append(row);}parent.append(section);
}
