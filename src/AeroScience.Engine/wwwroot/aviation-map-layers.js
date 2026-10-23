import {mapRequest} from './basemaps.js';
import {dialogHeader} from './dialog-ui.js';
import {node as el,button} from './workstation-ui.js';
const names={airport:'Airports',heliport:'Heliports',runway:'Runways','runway-end':'Runway ends',navaid:'NAVAIDs',fix:'Navigation fixes','class-b':'Class B airspace','class-c':'Class C airspace','class-d':'Class D airspace','class-e':'Class E airspace',sua:'Special-use airspace'};
const families={APT:['airport','heliport','runway','runway-end'],NAV:['navaid'],FIX:['fix'],CLASS:['class-b','class-c','class-d','class-e'],SUA:['sua']};
const polygonDataset=d=>d.family==='CLASS'||d.family==='SUA';
const colors={'class-b':'#2955c8','class-c':'#a13996','class-d':'#287d97','class-e':'#8456aa',sua:'#9f661e'};

// Retained host reference data only: no remote provider calls, science writes or toolbar messages.
export function aviationMapLayers(L,map,register){
 let disposed=false,message='No imported FAA references. Download an exact NASR edition and explicitly import it in FAA Downloads.';
 const entries=new Map();let discovery;
 function updateCounts(){map.getContainer().dataset.aviationFeatureCounts=JSON.stringify(Object.fromEntries([...entries].map(([id,e])=>[id,e.count])));}
 function currentBounds(){const b=map.getBounds();if(map.getZoom()<7||b.getWest() < -180||b.getEast()>180||b.getEast()-b.getWest()>30||b.getNorth()-b.getSouth()>30)return null;return {west:b.getWest(),south:Math.max(-90,b.getSouth()),east:b.getEast(),north:Math.min(90,b.getNorth())};}
 const url=(entry,b,offset=0)=>`/api/aviation/${polygonDataset(entry.dataset)?'airspace':'vectors'}/${entry.dataset.id}?`+new URLSearchParams({...b,kind:entry.kind,offset});
 const boundsFor=entry=>{const b=currentBounds();return b&&(!polygonDataset(entry.dataset)||b.east-b.west<=12&&b.north-b.south<=12)?b:null;};
 function altitude(limit){if(limit.status==='Published unlimited')return 'Unlimited (explicit published UNL)';if(limit.status==='Known')return `${limit.value} ${limit.unit} · ${limit.reference}`;return `Unknown${limit.rawValue?' · source '+limit.rawValue+' '+limit.rawUnit+' '+limit.rawReference:''}`;}
 function details(feature,dataset,sourceStatus){
  const dialog=el('dialog');dialog.className='aviation-reference-dialog';dialog.setAttribute('aria-label','FAA Reference Details');dialog.append(el('h2','FAA Reference Details'),el('h3',feature.name),el('p',`${names[feature.kind]} · FAA ${dataset.family} · edition ${dataset.edition}`),el('p',dataset.sourceCrs));
  dialog.append(el('p',sourceStatus||'Current operational status Unknown; review the retained source edition.'));
  if(feature.polygons){dialog.append(el('p',`Floor: ${altitude(feature.lower)}. Ceiling: ${altitude(feature.upper)}.`),el('p',feature.schedule.interpretation),el('p',`Published hours: ${feature.schedule.code||'Unknown'}. ${feature.schedule.remarks||''}`),el('p',`DST: ${feature.schedule.dst||'Unknown'}; source UTC offset: ${feature.schedule.utcOffset||'Unknown'}.`),el('p',`Original horizontal vertices: ${feature.originalVertices}. Display tolerance: ${feature.displayToleranceDegrees} degrees. Original geometry is retained; display is not an operational clearance.`));}
  else for(const point of feature.coordinates)dialog.append(el('p',`Original latitude ${point.latitude}; longitude ${point.longitude}. NAD83 geographic reference, approximate basemap display.`));
  const advanced=el('details'),list=el('dl');advanced.append(el('summary','Original reference attributes'));for(const [key,value] of Object.entries(feature.attributes)){list.append(el('dt',key),el('dd',value||'Unknown'));}advanced.append(list);dialog.append(advanced);
  for(const limitation of dataset.limitations)dialog.append(el('p',limitation));document.body.append(dialog);dialogHeader(dialog);dialog.addEventListener('close',()=>dialog.remove());dialog.showModal();
 }
 async function inspect(entry){
  const dialog=el('dialog');dialog.className='aviation-reference-dialog';dialog.setAttribute('aria-label','FAA Reference List');dialog.append(el('h2','FAA Reference List'),el('p',`${names[entry.kind]} · FAA ${entry.dataset.family} · edition ${entry.dataset.edition} · ${entry.dataset.sourceCrs}`));
  const status=el('p');status.setAttribute('role','status');const records=el('ul');let offset=0,epoch=0,closed=false,nextOffset=null;const history=[];
  const actions=el('div',undefined,'action-row'),previous=button(actions,'Previous FAA Features',()=>{offset=history.pop()??0;load();}),next=button(actions,'Next FAA Features',()=>{if(nextOffset===null)return;history.push(offset);offset=nextOffset;load();});
  dialog.append(status,records,actions);document.body.append(dialog);dialogHeader(dialog);dialog.addEventListener('close',()=>{closed=true;epoch++;dialog.remove();});dialog.showModal();
  async function load(){const own=++epoch,b=boundsFor(entry);records.replaceChildren();if(!b){status.textContent='Zoom to a supported region (zoom 7 or closer; airspace viewports at most 12 degrees, other references 30 degrees per side).';previous.disabled=next.disabled=true;return;}
   status.textContent='Reading retained references for this map viewport…';try{const page=await mapRequest(url(entry,b,offset));if(closed||own!==epoch)return;const poly=polygonDataset(entry.dataset);status.textContent=`${page.status}. ${poly?page.boundingCandidates+' bounding candidates (not active restrictions)':page.total+' retained references in viewport'}. Showing ${page.features.length} from offset ${page.offset}. Edition ${page.dataset.edition}; this is not live operational status.`;
    for(const feature of page.features){const row=el('li');button(row,feature.name,()=>details(feature,page.dataset,page.status));row.append(el('span',poly?` · Floor ${altitude(feature.lower)}; ceiling ${altitude(feature.upper)}; activation unverified.`:` · ${feature.coordinates.map(p=>`${p.latitude}, ${p.longitude}`).join(' → ')} (NAD83)`));records.append(row);}
    nextOffset=poly?(page.nextOffset??null):offset+250<page.total&&offset<10000?offset+250:null;previous.disabled=history.length===0;next.disabled=nextOffset===null;
   }catch(error){if(!closed&&own===epoch)status.textContent='FAA references unavailable: '+error.message;}}
  await load();
 }
 async function refresh(entry){
  const own=++entry.epoch;clearTimeout(entry.timer);if(disposed||!map.hasLayer(entry.layer)){entry.count=0;updateCounts();return;}
  const b=boundsFor(entry);entry.layer.clearLayers();entry.count=0;updateCounts();if(!b)return;
  try{const page=await mapRequest(url(entry,b));if(disposed||own!==entry.epoch||!map.hasLayer(entry.layer))return;
   for(const feature of page.features){const geometry=feature.polygons?feature.polygons.map(p=>p.map(r=>r.map(c=>[c.latitude,c.longitude]))):feature.coordinates.map(p=>[p.latitude,p.longitude]);const marker=feature.polygons?L.polygon(geometry,{color:colors[entry.kind],weight:2,opacity:entry.opacity,fillOpacity:.08*entry.opacity}):geometry.length===2?L.polyline(geometry,{color:'#7955a6',weight:3,opacity:entry.opacity}):L.circleMarker(geometry[0],{radius:entry.kind==='fix'?3:5,color:'#7955a6',weight:2,opacity:entry.opacity,fillOpacity:.7*entry.opacity});marker.on('click',()=>details(feature,page.dataset,page.status));entry.layer.addLayer(marker);}
   entry.count=page.features.length;updateCounts();
  }catch(error){if(!disposed&&own===entry.epoch){message='Retained FAA reference data unavailable; reconnect the original aviation drive. '+error.message;entry.layer.clearLayers();}}
 }
 function schedule(){for(const entry of entries.values()){clearTimeout(entry.timer);entry.timer=setTimeout(()=>refresh(entry),150);}}
 function layerChange(event){for(const entry of entries.values())if(event.layer===entry.layer)refresh(entry);}
 map.on('moveend',schedule);map.on('layeradd layerremove',layerChange);
 async function discover(){
  if(disposed)return;if(discovery)return discovery;
  discovery=(async()=>{const before=entries.size,oldMessage=message;try{const datasets=await mapRequest('/api/aviation/vectors');if(disposed)return;message=datasets.length?'Imported FAA references are local and available offline. Choose each edition explicitly. Point layers draw at most 250 features; airspace pages examine at most 100 bounding candidates and return at most 25,000 display vertices. View references provides paging. Zoom 7 or closer. NAD83 display is approximate; activation and operational clearance are unverified.':'No imported FAA references. Download an exact NASR edition and explicitly import it in FAA Downloads.';
   for(const dataset of datasets)for(const kind of families[dataset.family]||[]){const id=`faa-${kind}-${dataset.id.slice(0,16)}`;if(entries.has(id))continue;const entry={id,dataset,kind,layer:L.layerGroup(),count:0,epoch:0,opacity:1};entries.set(id,entry);
    register({id,group:'FAA',name:`FAA ${names[kind]} · ${dataset.edition}`,layer:entry.layer,description:`${dataset.family} original edition ${dataset.edition}. ${dataset.sourceCrs}. Imported ${new Date(dataset.importedUtc).toISOString()}. ${dataset.omittedRecords} explicitly unsupported source records. Review edition and limitations before use.`,inspect:()=>inspect(entry),opacity:1,setOpacity:value=>{entry.opacity=value;entry.layer.eachLayer(layer=>layer.setStyle({opacity:value,fillOpacity:(polygonDataset(dataset) ? .08 : .7)*value}));}});}
  }catch(error){if(!disposed)message='FAA reference inventory unavailable: '+error.message;}finally{discovery=null;}return entries.size!==before||message!==oldMessage;})();return discovery;
 }
 return {discover,status:()=>message,dispose(){disposed=true;for(const entry of entries.values()){entry.epoch++;clearTimeout(entry.timer);entry.layer.clearLayers();map.removeLayer(entry.layer);}map.off('moveend',schedule);map.off('layeradd layerremove',layerChange);}};
}
