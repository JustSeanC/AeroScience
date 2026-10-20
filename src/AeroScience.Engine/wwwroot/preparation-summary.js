import {readableName} from './workstation-ui.js';
const el=(t,v)=>{const n=document.createElement(t);if(v!==undefined)n.textContent=v;return n;};
export function transferEstimate(samples,total,now=Date.now()){
 if(!Number.isFinite(total)||total<=0)return {label:'Time remaining unknown',seconds:null};
 const recent=samples.filter(s=>Number.isFinite(s.bytes)&&Number.isFinite(s.time)&&now-s.time<=15000).sort((a,b)=>a.time-b.time);
 if(recent.length<3||recent.at(-1).time-recent[0].time<2000||now-recent.at(-1).time>5000)return {label:'Calculating estimate / waiting for transfer',seconds:null};
 const elapsed=(recent.at(-1).time-recent[0].time)/1000,delta=recent.at(-1).bytes-recent[0].bytes;
 if(delta<=0||recent.at(-1).bytes>total)return {label:'Time remaining unknown',seconds:null};
 const remaining=(total-recent.at(-1).bytes)/(delta/elapsed);
 return {label:`Estimated download time ${Math.ceil(remaining*.75)}–${Math.ceil(remaining*1.5)} seconds; additional processing not estimated`,seconds:remaining};
}
export function preparationPhase(row){
 if(row.status==='Downloading')return ['Validating','Saving'].includes(row.stage)?'Validating / Processing':row.stage==='Finding source'?'Finding Source':row.downloadedBytes>0&&Date.now()-Date.parse(row.updatedUtc)<6000?'Downloading':'Finding Source';
 return ({Offline:'Waiting for Network','Waiting for download allowance':'Waiting for Allowance','Dataset Ready':'Ready','Partially usable':'Partially Ready','Limited Coverage':'Limited Coverage',Superseded:'Queued'})[row.status]||row.status;
}
export function instrumentSummaries(rows){
 const groups=new Map();for(const row of rows){if(!groups.has(row.kind))groups.set(row.kind,[]);groups.get(row.kind).push(row);}
 return [...groups].map(([kind,items])=>{const ready=items.filter(r=>['Ready','Dataset Ready'].includes(r.status)).length,failed=items.filter(r=>['Unavailable','Failed','Limited Coverage','Dataset Required','Dataset Invalid','Dataset Outside Coverage'].includes(r.status)).length;
 const phases=[...new Set(items.filter(r=>!['Ready','Dataset Ready'].includes(r.status)).map(preparationPhase))];
 return {instrumentId:kind,name:readableName(kind),profile:null,status:ready===items.length?'Ready':ready>0?'Partially Ready':phases.includes('Downloading')?'Downloading':phases[0]||'Not Prepared',activelyDownloading:phases.includes('Downloading'),total:items.length,ready,awaiting:items.length-ready-failed,unavailable:failed,reusedBytes:items.reduce((n,r)=>n+(r.reusedBytes||0),0),downloadedBytes:items.reduce((n,r)=>n+(r.downloadedBytes||0),0),eta:'Time remaining unknown',reasons:[...new Set(items.map(r=>r.message))].slice(0,2)};});
}
export function renderInstrumentSummaries(parent,items){
 parent.className='instrument-preparation';const present=new Set(items.map(i=>i.instrumentId));for(const old of [...parent.children])if(!present.has(old.dataset.instrument))old.remove();
 for(const item of items){let row=[...parent.children].find(n=>n.dataset.instrument===item.instrumentId);if(!row){row=el('article');row.className='preparation-row';row.dataset.instrument=item.instrumentId;row.append(el('h4'),el('strong'),el('p'),el('p'));parent.append(row);}row.children[0].textContent=readableName(item.name||item.instrumentId)+(item.profile?' · '+item.profile:'');row.children[1].textContent=item.status==='Partially Ready'&&item.awaiting>0?(item.activelyDownloading?'Partially Ready — Downloading Remaining Data':'Partially Ready — Awaiting Remaining Data'):item.status;let progress=row.querySelector('progress');if(!progress){progress=el('progress');progress.className='preparation-progress';progress.setAttribute('aria-label','Regions ready');row.append(progress);}progress.max=Math.max(1,item.total);progress.value=item.ready;row.children[2].textContent=`${item.ready}/${item.total} instrument regions ready · ${item.awaiting} awaiting · ${item.unavailable} limited/unavailable${item.reusedBytes?' · Reused Locally':''}`;row.children[3].textContent=item.eta||'Time remaining unknown';}
}
