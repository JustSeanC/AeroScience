import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {mapRequest} from './basemaps.js';
import {node as el,button} from './workstation-ui.js';
import {dialogHeader} from './dialog-ui.js';

// Host-authenticated local PNGs only. Catalog/viewport reads never acquire FAA originals.
export function aviationChartLayers(L,map,register){
 let disposed=false,pending=null,message=textForUI("ui.aviationCharts.messageChartOriginalsHaveNotBeenInspectedOn");
 const entries=new Map();
 const StatusControl=L.Control.extend({onAdd(){const box=el('div');box.className='leaflet-control aviation-chart-status';box.setAttribute('role','status');box.setAttribute('aria-live','polite');return box;}});
 const statusControl=new StatusControl({position:'bottomleft'}).addTo(map),statusNode=statusControl.getContainer();
 const announce=text=>{if(statusNode.textContent!==text)setPresentationText(statusNode,text);map.getContainer().dataset.chartStatus=text;};
 const pane='faaChartPane';if(!map.getPane(pane)){map.createPane(pane);map.getPane(pane).style.zIndex='250';map.getPane(pane).style.pointerEvents='none';}
 function add(sheet){
  const id=`faa-chart-${sheet.jobId.replaceAll('-','')}-${sheet.id}`;
  if(entries.has(id)||sheet.state!=='Reviewed interior available'||sheet.mainBody.length<4)return entries.get(id);
  const bounds=L.latLngBounds(sheet.mainBody.map(p=>[p.latitude,p.longitude]));
  const layer=L.tileLayer(`/api/aviation/charts/${sheet.jobId}/${sheet.id}/{z}/{x}/{y}.png`,{pane,minZoom:3,maxNativeZoom:15,maxZoom:20,noWrap:true,bounds,opacity:.85,attribution:textForUI("ui.aviationCharts.messageFAAFlightSimulationReferenceOnly"),keepBuffer:0,updateWhenIdle:true});
  const entry={sheet,layer,bounds};entries.set(id,entry);
  layer.on('tileerror',()=>announce(localizedCopy("ui.aviationCharts.messageChartTileUnavailableOrWaitingForProcessing")));
  layer.on('tileload',()=>announce(localizedCopy("ui.aviationCharts.messageRetainedChartInteriorReviewSourceEdition")));
  layer.on('remove',()=>{if(![...entries.values()].some(e=>map.hasLayer(e.layer)))announce('');});
  register({id,group:"FAA",name:`FAA chart ${sheet.name} · ${sheet.edition}`,layer,opacity:.85,
   description:textForUI("ui.aviationCharts.messageMainBodyInteriorOnlyFullEdgeControlPoint",{value1:(String(sheet.state)),value2:(String(sheet.explanation)),value3:(String(sheet.originalSha256))}),
   inspect:()=>open(sheet),setOpacity:value=>layer.setOpacity(value)});
  return entry;
 }
 async function discover(){
  if(disposed)return false;if(pending)return pending;
  pending=(async()=>{const before=entries.size;try{const page=await mapRequest('/api/aviation/charts?offset=0');if(disposed)return false;
   message=`${page.status} ${page.runtimeAvailable?'Verified chart runtime available.':'Chart runtime unavailable; retained originals remain protected.'}`;
   page.sheets.forEach(add);
  }catch(error){if(!disposed)message=textForUI("ui.aviationCharts.messageRetainedChartsUnavailable",{value1:(String(error.message))});}finally{pending=null;}return entries.size!==before;})();return pending;
 }
 function open(selected){
  const dialog=el('dialog');dialog.className='aviation-reference-dialog';setPresentationText(dialog,localizedCopy("ui.aviationCharts.messageRetainedFAACharts"),"aria-label");
  dialog.append(el('h2',localizedCopy("ui.aviationCharts.messageRetainedFAACharts")),el('p',localizedCopy("ui.aviationCharts.messageChooseTheExactSheetAndEditionEach")),el('p',localizedCopy("ui.aviationCharts.messageDisplayingChartsCanRetainUpTo128")));
  const status=el('p');status.setAttribute('role','status');const list=el('div'),actions=el('div',undefined,'action-row');let offset=0,epoch=0,closed=false,nextOffset=null;
  const previous=button(actions,localizedCopy("ui.aviationCharts.messagePreviousChartSheets"),()=>{offset=Math.max(0,offset-50);load();}),next=button(actions,localizedCopy("ui.aviationCharts.messageNextChartSheets"),()=>{if(nextOffset!==null){offset=nextOffset;load();}});
  dialog.append(status,list,actions);document.body.append(dialog);dialogHeader(dialog);dialog.addEventListener('close',()=>{closed=true;epoch++;dialog.remove();});dialog.showModal();
  async function load(){const own=++epoch;setPresentationText(status,localizedCopy("ui.aviationCharts.messageReadingRetainedChartOriginalsNoProviderRequest"));previous.disabled=next.disabled=true;list.replaceChildren();
   try{const page=await mapRequest('/api/aviation/charts?offset='+offset);if(closed||own!==epoch)return;setPresentationText(status,page.status);
    for(const warning of page.limitations)list.append(el('p',warning));
    for(const sheet of page.sheets){const card=el('article',undefined,'panel');card.dataset.chartJob=sheet.jobId;card.dataset.chartSheet=sheet.id;card.append(el('h3',sheet.name),el('p',localizedCopy("ui.aviationCharts.messageEdition",{value1:(String(sheet.product)),value2:(String(sheet.edition)),value3:(String(sheet.state))})),el('p',sheet.explanation),el('p',sheet.currency||localizedCopy("ui.aviationCharts.messageCurrentOperationalValidityUnknown")),el('p',localizedCopy("ui.aviationCharts.messagePublisherUTCEffectiveExpiryEdition",{value1:(String(sheet.effectiveUtc?new Date(sheet.effectiveUtc).toISOString():'Unknown')),value2:(String(sheet.expiresUtc?new Date(sheet.expiresUtc).toISOString():'Unknown'))})),el('p',localizedCopy("ui.aviationCharts.messageOriginalSHA256",{value1:(String(sheet.originalSha256))})));
     if(sheet.mainBody.length){const lat=sheet.mainBody.map(p=>p.latitude),lon=sheet.mainBody.map(p=>p.longitude);card.append(el('p',localizedCopy("ui.aviationCharts.messageClippedDisplayInteriorLatitudeToLongitude",{value1:(String(Math.min(...lat).toFixed(5))),value2:(String(Math.max(...lat).toFixed(5))),value3:(String(Math.min(...lon).toFixed(5))),value4:(String(Math.max(...lon).toFixed(5)))})));}
     if(sheet.crs){const advanced=el('details');advanced.append(el('summary',localizedCopy("ui.aviationCharts.messageOriginalProjectionDetails")),el('p',sheet.crs));card.append(advanced);}
     const entry=add(sheet);if(entry){button(card,localizedCopy("ui.aviationCharts.messageShow",{value1:(String(sheet.name))}),()=>{map.addLayer(entry.layer);map.fitBounds(entry.bounds,{animate:false});dialog.close();});button(card,localizedCopy("ui.aviationCharts.messageHide",{value1:(String(sheet.name))}),()=>{map.removeLayer(entry.layer);setPresentationText(status,localizedCopy("ui.aviationCharts.messageChartHiddenRecordingObservationsAndRetainedOriginals"));});}
     if(selected?.jobId===sheet.jobId&&selected?.id===sheet.id)card.dataset.selectedChart='true';list.append(card);
    }
    if(!page.sheets.length)list.append(el('p',localizedCopy("ui.aviationCharts.messageNoRetainedChartSheetsOnThisPage")));
    nextOffset=page.nextOffset??null;previous.disabled=offset===0;next.disabled=nextOffset===null;
   }catch(error){if(!closed&&own===epoch)setPresentationText(status,localizedCopy("ui.aviationCharts.messageChartInventoryUnavailable",{value1:(String(error.message))}));}}
  load();
 }
 return{discover,open:()=>open(null),status:()=>message,dispose(){disposed=true;for(const entry of entries.values()){entry.layer.off();map.removeLayer(entry.layer);}entries.clear();statusControl.remove();}};
}
