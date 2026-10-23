import {mapRequest} from './basemaps.js';
import {dialogHeader} from './dialog-ui.js';
import {node as el,button} from './workstation-ui.js';
const names={airport:'Airports',heliport:'Heliports',runway:'Runways','runway-end':'Runway ends',navaid:'NAVAIDs',fix:'Navigation fixes'};
const families={APT:['airport','heliport','runway','runway-end'],NAV:['navaid'],FIX:['fix']};

// Retained host reference data only: no remote provider calls, science writes or toolbar messages.
export function aviationMapLayers(L,map,register){
 let disposed=false,message='No imported FAA references. Download an exact NASR edition and explicitly import it in FAA Downloads.';
 const entries=new Map();let discovery;
 function updateCounts(){map.getContainer().dataset.aviationFeatureCounts=JSON.stringify(Object.fromEntries([...entries].map(([id,e])=>[id,e.count])));}
 function currentBounds(){const b=map.getBounds();if(map.getZoom()<7||b.getWest() < -180||b.getEast()>180||b.getEast()-b.getWest()>30||b.getNorth()-b.getSouth()>30)return null;return {west:b.getWest(),south:Math.max(-90,b.getSouth()),east:b.getEast(),north:Math.min(90,b.getNorth())};}
 const url=(entry,b,offset=0)=>`/api/aviation/vectors/${entry.dataset.id}?`+new URLSearchParams({...b,kind:entry.kind,offset});
 function details(feature,dataset,sourceStatus){
  const dialog=el('dialog');dialog.className='aviation-reference-dialog';dialog.setAttribute('aria-label','FAA Reference Details');dialog.append(el('h2','FAA Reference Details'),el('h3',feature.name),el('p',`${names[feature.kind]} · FAA ${dataset.family} · edition ${dataset.edition}`),el('p',dataset.sourceCrs));
  dialog.append(el('p',sourceStatus||'Current operational status Unknown; review the retained source edition.'));
  for(const point of feature.coordinates)dialog.append(el('p',`Original latitude ${point.latitude}; longitude ${point.longitude}. NAD83 geographic reference, approximate basemap display.`));
  const list=el('dl');for(const [key,value] of Object.entries(feature.attributes)){list.append(el('dt',key),el('dd',value||'Unknown'));}dialog.append(list);
  for(const limitation of dataset.limitations)dialog.append(el('p',limitation));document.body.append(dialog);dialogHeader(dialog);dialog.addEventListener('close',()=>dialog.remove());dialog.showModal();
 }
 async function inspect(entry){
  const dialog=el('dialog');dialog.className='aviation-reference-dialog';dialog.setAttribute('aria-label','FAA Reference List');dialog.append(el('h2','FAA Reference List'),el('p',`${names[entry.kind]} · FAA ${entry.dataset.family} · edition ${entry.dataset.edition} · ${entry.dataset.sourceCrs}`));
  const status=el('p');status.setAttribute('role','status');const records=el('ul');let offset=0,epoch=0,closed=false;
  const actions=el('div',undefined,'action-row'),previous=button(actions,'Previous FAA Features',()=>{offset=Math.max(0,offset-250);load();}),next=button(actions,'Next FAA Features',()=>{offset+=250;load();});
  dialog.append(status,records,actions);document.body.append(dialog);dialogHeader(dialog);dialog.addEventListener('close',()=>{closed=true;epoch++;dialog.remove();});dialog.showModal();
  async function load(){const own=++epoch,b=currentBounds();records.replaceChildren();if(!b){status.textContent='Zoom to a supported region (zoom 7 or closer, at most 30 degrees per side) to inspect local FAA features.';previous.disabled=next.disabled=true;return;}
   status.textContent='Reading retained references for this map viewport…';try{const page=await mapRequest(url(entry,b,offset));if(closed||own!==epoch)return;status.textContent=`${page.status}. ${page.total} retained references in viewport. Showing ${page.features.length} from offset ${page.offset}. Edition ${page.dataset.edition}; this is not live operational status.`;
    for(const feature of page.features){const row=el('li');button(row,feature.name,()=>details(feature,page.dataset,page.status));row.append(el('span',` · ${feature.coordinates.map(p=>`${p.latitude}, ${p.longitude}`).join(' → ')} (NAD83)`));records.append(row);}
    previous.disabled=offset===0;next.disabled=offset+250>=page.total||offset>=10000;
   }catch(error){if(!closed&&own===epoch)status.textContent='FAA references unavailable: '+error.message;}}
  await load();
 }
 async function refresh(entry){
  const own=++entry.epoch;clearTimeout(entry.timer);if(disposed||!map.hasLayer(entry.layer)){entry.count=0;updateCounts();return;}
  const b=currentBounds();entry.layer.clearLayers();entry.count=0;updateCounts();if(!b)return;
  try{const page=await mapRequest(url(entry,b));if(disposed||own!==entry.epoch||!map.hasLayer(entry.layer))return;
   for(const feature of page.features){const geometry=feature.coordinates.map(p=>[p.latitude,p.longitude]);const marker=geometry.length===2?L.polyline(geometry,{color:'#7955a6',weight:3,opacity:entry.opacity}):L.circleMarker(geometry[0],{radius:entry.kind==='fix'?3:5,color:'#7955a6',weight:2,opacity:entry.opacity,fillOpacity:.7*entry.opacity});marker.on('click',()=>details(feature,page.dataset,page.status));entry.layer.addLayer(marker);}
   entry.count=page.features.length;updateCounts();
  }catch(error){if(!disposed&&own===entry.epoch){message='Retained FAA reference data unavailable; reconnect the original aviation drive. '+error.message;entry.layer.clearLayers();}}
 }
 function schedule(){for(const entry of entries.values()){clearTimeout(entry.timer);entry.timer=setTimeout(()=>refresh(entry),150);}}
 function layerChange(event){for(const entry of entries.values())if(event.layer===entry.layer)refresh(entry);}
 map.on('moveend',schedule);map.on('layeradd layerremove',layerChange);
 async function discover(){
  if(disposed)return;if(discovery)return discovery;
  discovery=(async()=>{const before=entries.size,oldMessage=message;try{const datasets=await mapRequest('/api/aviation/vectors');if(disposed)return;message=datasets.length?'Verified imported FAA references are local and available offline. Choose each edition explicitly. At most 250 features per enabled layer are drawn; View references provides paging. Zoom 7 or closer. NAD83 display is approximate; no WGS84 positional accuracy or operational status is certified.':'No imported FAA references. Download an exact NASR edition and explicitly import it in FAA Downloads.';
   for(const dataset of datasets)for(const kind of families[dataset.family]||[]){const id=`faa-${kind}-${dataset.id.slice(0,16)}`;if(entries.has(id))continue;const entry={id,dataset,kind,layer:L.layerGroup(),count:0,epoch:0,opacity:1};entries.set(id,entry);
    register({id,group:'FAA',name:`FAA ${names[kind]} · ${dataset.edition}`,layer:entry.layer,description:`${dataset.family} original edition ${dataset.edition}. Retained NAD83 reference; approximate geographic display. Imported ${new Date(dataset.importedUtc).toISOString()}. Review edition before use.`,inspect:()=>inspect(entry),opacity:1,setOpacity:value=>{entry.opacity=value;entry.layer.eachLayer(layer=>layer.setStyle({opacity:value,fillOpacity:.7*value}));}});}
  }catch(error){if(!disposed)message='FAA reference inventory unavailable: '+error.message;}finally{discovery=null;}return entries.size!==before||message!==oldMessage;})();return discovery;
 }
 return {discover,status:()=>message,dispose(){disposed=true;for(const entry of entries.values()){entry.epoch++;clearTimeout(entry.timer);entry.layer.clearLayers();map.removeLayer(entry.layer);}map.off('moveend',schedule);map.off('layeradd layerremove',layerChange);}};
}
