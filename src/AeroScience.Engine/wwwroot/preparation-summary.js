import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {readableName} from './workstation-ui.js';
const el=(t,v)=>{const n=document.createElement(t);if(v!==undefined)setPresentationText(n,v);return n;};
export function transferEstimate(samples,total,now=Date.now()){
 if(!Number.isFinite(total)||total<=0)return {label:textForUI("ui.preparationSummary.messageTimeRemainingUnknown"),seconds:null};
 const recent=samples.filter(s=>Number.isFinite(s.bytes)&&Number.isFinite(s.time)&&now-s.time<=15000).sort((a,b)=>a.time-b.time);
 if(recent.length<3||recent.at(-1).time-recent[0].time<2000||now-recent.at(-1).time>5000)return {label:textForUI("ui.preparationSummary.messageCalculatingEstimateWaitingForTransfer"),seconds:null};
 const elapsed=(recent.at(-1).time-recent[0].time)/1000,delta=recent.at(-1).bytes-recent[0].bytes;
 if(delta<=0||recent.at(-1).bytes>total)return {label:textForUI("ui.preparationSummary.messageTimeRemainingUnknown"),seconds:null};
 const remaining=(total-recent.at(-1).bytes)/(delta/elapsed);
 return {label:textForUI("ui.preparationSummary.messageEstimatedDownloadTimeSecondsAdditionalProcessing",{value1:(String(Math.ceil(remaining*.75))),value2:(String(Math.ceil(remaining*1.5)))}),seconds:remaining};
}
export function preparationPhase(row){
 if(row.status==='Downloading')return ['Validating','Saving'].includes(row.stage)?'Validating / Processing':row.stage==='Finding source'?'Finding Source':row.downloadedBytes>0&&Date.now()-Date.parse(row.updatedUtc)<6000?'Downloading':'Finding Source';
 return ({Offline:'Waiting for Network','Waiting for download allowance':'Waiting for Allowance','Dataset Ready':'Ready','Partially usable':'Partially Ready','Limited Coverage':'Limited Coverage',Superseded:'Queued'})[row.status]||row.status;
}
export function instrumentSummaries(rows){
 const groups=new Map();for(const row of rows){if(!groups.has(row.kind))groups.set(row.kind,[]);groups.get(row.kind).push(row);}
 return [...groups].map(([kind,items])=>{const ready=items.filter(r=>['Ready','Dataset Ready'].includes(r.status)).length,failed=items.filter(r=>['Unavailable','Failed','Limited Coverage','Dataset Required','Dataset Invalid','Dataset Outside Coverage'].includes(r.status)).length;
 const phases=[...new Set(items.filter(r=>!['Ready','Dataset Ready'].includes(r.status)).map(preparationPhase))];
 return {instrumentId:kind,name:readableName(kind),profile:null,status:ready===items.length?"Ready":ready>0?"Partially Ready":phases.includes('Downloading')?"Downloading":phases[0]||"Not Prepared",activelyDownloading:phases.includes('Downloading'),total:items.length,ready,awaiting:items.length-ready-failed,unavailable:failed,reusedBytes:items.reduce((n,r)=>n+(r.reusedBytes||0),0),downloadedBytes:items.reduce((n,r)=>n+(r.downloadedBytes||0),0),eta:textForUI("ui.preparationSummary.messageTimeRemainingUnknown"),reasons:[...new Set(items.map(r=>r.message))].slice(0,2)};});
}
// Original phase/status strings remain comparison identities; translate only the rendered label.
const phaseMessages={"Not Prepared":"preparation.status.notPrepared","Downloading":"preparation.status.downloading","Partially Ready":"preparation.status.partiallyReady","Ready": "ui.missionOperations.messageReady", "Validating / Processing": "ui.preparationSummary.messageValidatingProcessing", "Finding Source": "ui.preparationSummary.messageFindingSource", "Waiting for Network": "ui.preparationSummary.messageWaitingForNetwork", "Waiting for Allowance": "ui.preparationSummary.messageWaitingForAllowance", "Limited Coverage": "ui.preparationSummary.messageLimitedCoverage", "Unavailable": "localization.unavailableValue", "Queued": "ui.downloadActivity.messageQueued"};
const phaseLabel=code=>Object.hasOwn(phaseMessages,code)?localizedCopy(phaseMessages[code]):code;
export function renderInstrumentSummaries(parent,items){
 parent.className='instrument-preparation';const present=new Set(items.map(i=>i.instrumentId));for(const old of [...parent.children])if(!present.has(old.dataset.instrument))old.remove();
 for(const item of items){let row=[...parent.children].find(n=>n.dataset.instrument===item.instrumentId);if(!row){row=el('article');row.className='preparation-row';row.dataset.instrument=item.instrumentId;row.append(el('h4'),el('strong'),el('p'),el('p'));parent.append(row);}setPresentationText(row.children[0],readableName(item.name||item.instrumentId)+(item.profile?' · '+item.profile:''));setPresentationText(row.children[1],item.status==='Partially Ready'&&item.awaiting>0?(item.activelyDownloading?localizedCopy("ui.preparationSummary.messagePartiallyReadyDownloadingRemainingData"):localizedCopy("ui.preparationSummary.messagePartiallyReadyAwaitingRemainingData")):phaseLabel(item.status));let progress=row.querySelector('progress');if(!progress){progress=el('progress');progress.className='preparation-progress';setPresentationText(progress,localizedCopy("ui.preparationSummary.messageRegionsReady"),"aria-label");row.append(progress);}progress.hidden=item.total<=0||item.status.startsWith("Plan revision changed");progress.max=Math.max(1,item.total);progress.value=item.ready;setPresentationText(row.children[2],localizedCopy("ui.preparationSummary.messageInstrumentRegionsReadyAwaiting",{value1:(String(item.ready)),value2:(String(item.total)),value3:(String(item.awaiting)),value4:(String(item.unavailable)),value5:(String(item.reusedBytes?' · Reused Locally':''))}));setPresentationText(row.children[3],item.eta||localizedCopy("ui.preparationSummary.messageTimeRemainingUnknown"));}
}
