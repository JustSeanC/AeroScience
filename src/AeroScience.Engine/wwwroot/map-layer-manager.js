import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {layerSelector,mapRequest} from './basemaps.js';
import {dialogHeader} from './dialog-ui.js';
import {aviationMapLayers} from './aviation-map-layers.js';
import {openAviationReference} from './aviation-reference.js';
import {tfrMapLayer,openTfrReferences} from './tfr-layers.js';
const el=(tag,text)=>{const n=document.createElement(tag);if(text!==undefined)setPresentationText(n,text);return n;};
const preferenceKey='aeroscience.map-overlay-visibility.v1';
function saved(){try{const text=localStorage.getItem(preferenceKey);if(!text||text.length>4096)return {};const value=JSON.parse(text);return value&&typeof value==='object'&&!Array.isArray(value)?value:{};}catch{return {};}}

// One permanent map control: exactly one basemap; independently visible overlay groups.
// Changing visibility neither changes source filters nor mutates the underlying mission/science.
const groupMessages={Mission:'toolbar.mission',Science:'toolbar.science'};
const groupLabel=value=>Object.hasOwn(groupMessages,value)?localizedCopy(groupMessages[value]):value;
const groupActions={Mission:'map.groups.showMission',Science:'map.groups.showScience',FAA:'map.groups.showFaa'};
export function mapLayerManager(L,map,definitions,applyBasemap,{beginReadOnly=()=>()=>{}}={}){
 const overlays=new Map(),inputs=new Map(),groups=new Map();let dialog,aboutDialog,disposed=false,preferences=saved();
 const Control=L.Control.extend({onAdd(){const box=el('div');box.className='leaflet-bar map-layer-manager';box.setAttribute('role','group');setPresentationText(box,localizedCopy("ui.mapLayerManager.messageMapLayerManager"),"aria-label");L.DomEvent.disableClickPropagation(box);L.DomEvent.disableScrollPropagation(box);return box;}});
 const control=new Control({position:'topright'}).addTo(map),box=control.getContainer();
 const stopBasemap=layerSelector(box,applyBasemap),open=el('button',localizedCopy("ui.mapLayerManager.messageOverlaysLegend"));open.type='button';open.setAttribute('aria-haspopup','dialog');box.append(open);
 function persist(id,visible){preferences[id]=visible;const bounded=Object.fromEntries(Object.entries(preferences).filter(([key,value])=>/^[a-z][a-z0-9-]{0,99}$/.test(key)&&typeof value==='boolean').slice(-32));try{localStorage.setItem(preferenceKey,JSON.stringify(bounded));}catch{}}
 function visibility(item,visible){if(visible&&!map.hasLayer(item.layer))map.addLayer(item.layer);if(!visible&&map.hasLayer(item.layer))map.removeLayer(item.layer);persist(item.id,visible);}
 function sync(){map.getContainer().dataset.visibleOverlays=JSON.stringify([...overlays.values()].filter(item=>map.hasLayer(item.layer)).map(item=>item.id));for(const [id,input] of inputs)input.checked=map.hasLayer(overlays.get(id).layer);for(const [name,input] of groups){const list=[...overlays.values()].filter(i=>i.group===name),on=list.filter(i=>map.hasLayer(i.layer)).length;input.checked=on===list.length&&on>0;input.indeterminate=on>0&&on<list.length;}}
 function canRender(){return dialog?.open&&![...document.querySelectorAll('dialog[open]')].some(d=>d!==dialog);}
 function addOverlay(item){if(disposed||overlays.has(item.id))return;overlays.set(item.id,item);if(preferences[item.id]===false)map.removeLayer(item.layer);else if(preferences[item.id]===true)map.addLayer(item.layer);if(canRender())render();sync();}
 function about(item,opener){
  if(aboutDialog?.open)return;const child=el('dialog');aboutDialog=child;child.className='analysis-dialog scientific-layer-context';setPresentationText(child,localizedCopy("ui.mapLayerManager.messageAboutThisLayer"),"aria-label");child.append(el('h2',localizedCopy("ui.mapLayerManager.messageAboutThisLayer")),el('h3',item.name));
  if(item.describe)item.describe(child);else child.append(el('p',item.description||localizedCopy("ui.mapLayerManager.messageNoScientificSourceMetadataWasSuppliedFor")),el('p',localizedCopy("ui.mapLayerManager.messageDisplayGeometryAloneDoesNotEstablishMeasurement")));
  document.body.append(child);dialogHeader(child,{opener});child.addEventListener('close',()=>{child.remove();if(aboutDialog===child)aboutDialog=null;});child.showModal();
 }
 function render(){
  if(!dialog)return;const content=dialog.querySelector('.map-overlay-content');content.replaceChildren();inputs.clear();groups.clear();
  content.append(el('p',localizedCopy("ui.mapLayerManager.messageBasemapSuppliesGeographicContextMissionGeometryRecorded")));
  for(const name of ['Mission','Science','FAA']){const set=el('fieldset'),legend=el('legend',localizedCopy('map.groups.overlays',{group:groupLabel(name)}));set.append(legend);
   const allLabel=el('label'),all=el('input');all.type='checkbox';setPresentationText(all,localizedCopy('map.groups.overlays',{group:groupLabel(name)}),"aria-label");allLabel.className='checkbox-label';allLabel.append(all,el('span',localizedCopy(groupActions[name])));set.append(allLabel);groups.set(name,all);
   all.onchange=()=>{const visible=all.checked;for(const item of overlays.values())if(item.group===name)visibility(item,visible);sync();};
   for(const item of overlays.values()){if(item.group!==name)continue;const label=el('label'),input=el('input');input.type='checkbox';setPresentationText(input,item.name,"aria-label");label.className='checkbox-label';label.append(input,item.name);input.onchange=()=>{visibility(item,input.checked);sync();};inputs.set(item.id,input);set.append(label);
    if(item.description)set.append(el('p',item.description));
    if(item.group==='Science'){const info=el('button',localizedCopy("ui.mapLayerManager.messageAboutThisLayer"));info.type='button';setPresentationText(info,localizedCopy("ui.mapLayerManager.messageAboutThisLayer64098082",{value1:(item.name)}),"aria-label");info.onclick=()=>about(item,info);set.append(info);}
    if(item.inspect){const inspect=el('button',localizedCopy("ui.mapLayerManager.messageViewReferences",{value1:(String(item.name))}));inspect.type='button';inspect.onclick=item.inspect;set.append(inspect);}
    if(item.setOpacity){const opacityLabel=el('label',localizedCopy("ui.mapLayerManager.messageOpacity",{value1:(String(item.name))})),opacity=el('input');opacity.type='range';opacity.min='0';opacity.max='1';opacity.step='.05';opacity.value=item.opacity??1;setPresentationText(opacity,localizedCopy("ui.mapLayerManager.messageOpacity",{value1:(item.name)}),"aria-label");opacity.oninput=()=>{item.opacity=Number(opacity.value);item.setOpacity(item.opacity);};opacityLabel.append(opacity);set.append(opacityLabel);}
   }
   content.append(set);
  }
  const faa=el('section');faa.append(el('p',aviation.status()),el('h3',localizedCopy("ui.mapLayerManager.messageFAAAviationTFROverlays")),el('p',localizedCopy("ui.mapLayerManager.messageImportedFAAPointsRunwaysClassBCDEPolygonsAnd")));
  const references=el('button',localizedCopy("ui.aviationReference.messageFAACatalogNotices"));references.type='button';references.onclick=()=>openAviationReference(mapRequest,map);faa.append(references);content.append(faa);
  const tfrButton=el('button',localizedCopy("ui.mapLayerManager.messageTFRBoundariesScreening"));tfrButton.type='button';tfrButton.onclick=()=>openTfrReferences(mapRequest,map);faa.append(tfrButton);
  content.append(el('p',localizedCopy("ui.mapLayerManager.messageSourceEditionAsOfInspectTheOriginal")));sync();
 }
 open.onclick=()=>{if(dialog?.open)return;dialog=el('dialog');setPresentationText(dialog,localizedCopy("ui.mapLayerManager.messageMapOverlaysLegend"),"aria-label");dialog.className='map-overlays-dialog';dialog.append(el('h2',localizedCopy("ui.mapLayerManager.messageMapOverlaysLegend")));const content=el('div');content.className='map-overlay-content';dialog.append(content);document.body.append(dialog);dialogHeader(dialog,{opener:open});dialog.addEventListener('close',()=>{dialog.remove();dialog=null;inputs.clear();groups.clear();});render();dialog.showModal();aviation.discover().then(changed=>{if(changed&&canRender())render();});};
 const aviation=aviationMapLayers(L,map,addOverlay);map.on('layeradd layerremove',sync);definitions.forEach(addOverlay);aviation.discover();
 const tfr=tfrMapLayer(L,map,addOverlay,{
  persistVisibility:visible=>persist('faa-tfr-boundaries',visible),
  isolateMission:()=>{const restoreInteraction=beginReadOnly();const shown=[...overlays.values()].filter(i=>['Mission','Science'].includes(i.group)&&map.hasLayer(i.layer));shown.forEach(i=>map.removeLayer(i.layer));return()=>{shown.forEach(i=>map.addLayer(i.layer));restoreInteraction();};}
 });
 return {addOverlay,dispose(){if(disposed)return;disposed=true;aboutDialog?.close();tfr.dispose();aviation.dispose();stopBasemap();if(dialog){dialog.close();dialog?.remove();}map.off('layeradd layerremove',sync);control.remove();}};
}
