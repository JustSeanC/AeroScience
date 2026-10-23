import {mapRequest} from './basemaps.js';
import {node as el,button} from './workstation-ui.js';
import {dialogHeader} from './dialog-ui.js';

// Host-authenticated local PNGs only. Catalog/viewport reads never acquire FAA originals.
export function aviationChartLayers(L,map,register){
 let disposed=false,pending=null,message='Chart originals have not been inspected on this map.';
 const entries=new Map();
 const StatusControl=L.Control.extend({onAdd(){const box=el('div');box.className='leaflet-control aviation-chart-status';box.setAttribute('role','status');box.setAttribute('aria-live','polite');return box;}});
 const statusControl=new StatusControl({position:'bottomleft'}).addTo(map),statusNode=statusControl.getContainer();
 const announce=text=>{if(statusNode.textContent!==text)statusNode.textContent=text;map.getContainer().dataset.chartStatus=text;};
 const pane='faaChartPane';if(!map.getPane(pane)){map.createPane(pane);map.getPane(pane).style.zIndex='250';map.getPane(pane).style.pointerEvents='none';}
 function add(sheet){
  const id=`faa-chart-${sheet.jobId.replaceAll('-','')}-${sheet.id}`;
  if(entries.has(id)||sheet.state!=='Reviewed interior available'||sheet.mainBody.length<4)return entries.get(id);
  const bounds=L.latLngBounds(sheet.mainBody.map(p=>[p.latitude,p.longitude]));
  const layer=L.tileLayer(`/api/aviation/charts/${sheet.jobId}/${sheet.id}/{z}/{x}/{y}.png`,{pane,minZoom:3,maxNativeZoom:15,maxZoom:20,noWrap:true,bounds,opacity:.85,attribution:'FAA — flight simulation reference only',keepBuffer:0,updateWhenIdle:true});
  const entry={sheet,layer,bounds};entries.set(id,entry);
  layer.on('tileerror',()=>announce('Chart tile unavailable or waiting for processing capacity. Hide/show the chart to retry. Recording continues normally.'));
  layer.on('tileload',()=>announce('Retained chart interior · review source edition and clipping limitations.'));
  layer.on('remove',()=>{if(![...entries.values()].some(e=>map.hasLayer(e.layer)))announce('');});
  register({id,group:'FAA',name:`FAA chart ${sheet.name} · ${sheet.edition}`,layer,opacity:.85,
   description:`${sheet.state}. ${sheet.explanation} Main-body interior only; full edge/control-point acceptance pending. Source SHA-256 ${sheet.originalSha256}.`,
   inspect:()=>open(sheet),setOpacity:value=>layer.setOpacity(value)});
  return entry;
 }
 async function discover(){
  if(disposed)return false;if(pending)return pending;
  pending=(async()=>{const before=entries.size;try{const page=await mapRequest('/api/aviation/charts?offset=0');if(disposed)return false;
   message=`${page.status} ${page.runtimeAvailable?'Verified chart runtime available.':'Chart runtime unavailable; retained originals remain protected.'}`;
   page.sheets.forEach(add);
  }catch(error){if(!disposed)message='Retained charts unavailable: '+error.message;}finally{pending=null;}return entries.size!==before;})();return pending;
 }
 function open(selected){
  const dialog=el('dialog');dialog.className='aviation-reference-dialog';dialog.setAttribute('aria-label','Retained FAA Charts');
  dialog.append(el('h2','Retained FAA Charts'),el('p','Choose the exact sheet and edition. Each chart is an independent overlay below mission/science layers. Clipped interiors exclude surrounding artwork. Unreviewed sheets and editions are retained without guessed map placement.'),el('p','Displaying charts can retain up to 128 MiB of regenerable tiles plus small checksum receipts in aviation storage, separate from original-download allowances. Physical free-space protections still apply.'));
  const status=el('p');status.setAttribute('role','status');const list=el('div'),actions=el('div',undefined,'action-row');let offset=0,epoch=0,closed=false,nextOffset=null;
  const previous=button(actions,'Previous Chart Sheets',()=>{offset=Math.max(0,offset-50);load();}),next=button(actions,'Next Chart Sheets',()=>{if(nextOffset!==null){offset=nextOffset;load();}});
  dialog.append(status,list,actions);document.body.append(dialog);dialogHeader(dialog);dialog.addEventListener('close',()=>{closed=true;epoch++;dialog.remove();});dialog.showModal();
  async function load(){const own=++epoch;status.textContent='Reading retained chart originals; no provider request…';previous.disabled=next.disabled=true;list.replaceChildren();
   try{const page=await mapRequest('/api/aviation/charts?offset='+offset);if(closed||own!==epoch)return;status.textContent=page.status;
    for(const warning of page.limitations)list.append(el('p',warning));
    for(const sheet of page.sheets){const card=el('article',undefined,'panel');card.dataset.chartJob=sheet.jobId;card.dataset.chartSheet=sheet.id;card.append(el('h3',sheet.name),el('p',`${sheet.product} · edition ${sheet.edition} · ${sheet.state}`),el('p',sheet.explanation),el('p',sheet.currency||'Current operational validity Unknown'),el('p',`Publisher UTC effective: ${sheet.effectiveUtc?new Date(sheet.effectiveUtc).toISOString():'Unknown'}; expiry: ${sheet.expiresUtc?new Date(sheet.expiresUtc).toISOString():'Unknown'}. Edition labels alone do not establish exact UTC validity.`),el('p',`Original SHA-256 ${sheet.originalSha256}`));
     if(sheet.mainBody.length){const lat=sheet.mainBody.map(p=>p.latitude),lon=sheet.mainBody.map(p=>p.longitude);card.append(el('p',`Clipped display interior: latitude ${Math.min(...lat).toFixed(5)} to ${Math.max(...lat).toFixed(5)}; longitude ${Math.min(...lon).toFixed(5)} to ${Math.max(...lon).toFixed(5)}. Approximate NAD83/WGS84 map reference, not certified navigation.`));}
     if(sheet.crs){const advanced=el('details');advanced.append(el('summary','Original projection details'),el('p',sheet.crs));card.append(advanced);}
     const entry=add(sheet);if(entry){button(card,'Show '+sheet.name,()=>{map.addLayer(entry.layer);map.fitBounds(entry.bounds,{animate:false});dialog.close();});button(card,'Hide '+sheet.name,()=>{map.removeLayer(entry.layer);status.textContent='Chart hidden. Recording, observations and retained originals are unchanged.';});}
     if(selected?.jobId===sheet.jobId&&selected?.id===sheet.id)card.dataset.selectedChart='true';list.append(card);
    }
    if(!page.sheets.length)list.append(el('p','No retained chart sheets on this page. Use FAA Downloads to approve an exact original; this view does not start downloads.'));
    nextOffset=page.nextOffset??null;previous.disabled=offset===0;next.disabled=nextOffset===null;
   }catch(error){if(!closed&&own===epoch)status.textContent='Chart inventory unavailable: '+error.message;}}
  load();
 }
 return{discover,open:()=>open(null),status:()=>message,dispose(){disposed=true;for(const entry of entries.values()){entry.layer.off();map.removeLayer(entry.layer);}entries.clear();statusControl.remove();}};
}
