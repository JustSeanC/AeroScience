import {layerSelector,mapRequest} from './basemaps.js';
import {dialogHeader} from './dialog-ui.js';
import {aviationMapLayers} from './aviation-map-layers.js';
import {openAviationReference} from './aviation-reference.js';
const el=(tag,text)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
const preferenceKey='aeroscience.map-overlay-visibility.v1';
function saved(){try{const text=localStorage.getItem(preferenceKey);if(!text||text.length>4096)return {};const value=JSON.parse(text);return value&&typeof value==='object'&&!Array.isArray(value)?value:{};}catch{return {};}}

// One permanent map control: exactly one basemap; independently visible overlay groups.
// Changing visibility neither changes source filters nor mutates the underlying mission/science.
export function mapLayerManager(L,map,definitions,applyBasemap){
 const overlays=new Map(),inputs=new Map(),groups=new Map();let dialog,disposed=false,preferences=saved();
 const Control=L.Control.extend({onAdd(){const box=el('div');box.className='leaflet-bar map-layer-manager';box.setAttribute('role','group');box.setAttribute('aria-label','Map layer manager');L.DomEvent.disableClickPropagation(box);L.DomEvent.disableScrollPropagation(box);return box;}});
 const control=new Control({position:'topright'}).addTo(map),box=control.getContainer();
 const stopBasemap=layerSelector(box,applyBasemap),open=el('button','Overlays & Legend');open.type='button';open.setAttribute('aria-haspopup','dialog');box.append(open);
 function persist(id,visible){preferences[id]=visible;const bounded=Object.fromEntries(Object.entries(preferences).filter(([key,value])=>/^[a-z][a-z0-9-]{0,99}$/.test(key)&&typeof value==='boolean').slice(-32));try{localStorage.setItem(preferenceKey,JSON.stringify(bounded));}catch{}}
 function visibility(item,visible){if(visible&&!map.hasLayer(item.layer))map.addLayer(item.layer);if(!visible&&map.hasLayer(item.layer))map.removeLayer(item.layer);persist(item.id,visible);}
 function sync(){map.getContainer().dataset.visibleOverlays=JSON.stringify([...overlays.values()].filter(item=>map.hasLayer(item.layer)).map(item=>item.id));for(const [id,input] of inputs)input.checked=map.hasLayer(overlays.get(id).layer);for(const [name,input] of groups){const list=[...overlays.values()].filter(i=>i.group===name),on=list.filter(i=>map.hasLayer(i.layer)).length;input.checked=on===list.length&&on>0;input.indeterminate=on>0&&on<list.length;}}
 function addOverlay(item){if(disposed||overlays.has(item.id))return;overlays.set(item.id,item);if(preferences[item.id]===false)map.removeLayer(item.layer);else if(preferences[item.id]===true)map.addLayer(item.layer);if(dialog?.open)render();sync();}
 function render(){
  if(!dialog)return;const content=dialog.querySelector('.map-overlay-content');content.replaceChildren();inputs.clear();groups.clear();
  content.append(el('p','Basemap supplies geographic context. Mission geometry, recorded science and FAA references are independent overlays. An outline or locally retained dataset does not prove valid scientific pixels.'));
  for(const name of ['Mission','Science','FAA']){const set=el('fieldset'),legend=el('legend',name+' overlays');set.append(legend);
   const allLabel=el('label'),all=el('input');all.type='checkbox';all.setAttribute('aria-label',name+' overlays');allLabel.className='checkbox-label';allLabel.append(all,'Show '+name.toLowerCase()+' overlays');set.append(allLabel);groups.set(name,all);
   all.onchange=()=>{const visible=all.checked;for(const item of overlays.values())if(item.group===name)visibility(item,visible);sync();};
   for(const item of overlays.values()){if(item.group!==name)continue;const label=el('label'),input=el('input');input.type='checkbox';input.setAttribute('aria-label',item.name);label.className='checkbox-label';label.append(input,item.name);input.onchange=()=>{visibility(item,input.checked);sync();};inputs.set(item.id,input);set.append(label);
    if(item.description)set.append(el('p',item.description));
    if(item.inspect){const inspect=el('button','View '+item.name+' references');inspect.type='button';inspect.onclick=item.inspect;set.append(inspect);}
    if(item.setOpacity){const opacityLabel=el('label',item.name+' opacity'),opacity=el('input');opacity.type='range';opacity.min='0';opacity.max='1';opacity.step='.05';opacity.value=item.opacity??1;opacity.setAttribute('aria-label',item.name+' opacity');opacity.oninput=()=>{item.opacity=Number(opacity.value);item.setOpacity(item.opacity);};opacityLabel.append(opacity);set.append(opacityLabel);}
   }
   content.append(set);
  }
  const faa=el('section');faa.append(el('p',aviation.status()),el('h3','FAA aviation & TFR overlays'),el('p','FAA imported NASR point/runway references are independent overlays. Airspace/SUA polygons, chart rendering remain unavailable. TFR polygons: unverified / unavailable. No layer can clear a route of restrictions.'));
  const references=el('button','FAA Catalog & Notices');references.type='button';references.onclick=()=>openAviationReference(mapRequest);faa.append(references);content.append(faa);
  content.append(el('p','Source edition / as-of: inspect the original source or recorded observation details. No chart edition or restriction freshness is inferred from the basemap.'));sync();
 }
 open.onclick=()=>{if(dialog?.open)return;dialog=el('dialog');dialog.setAttribute('aria-label','Map Overlays & Legend');dialog.className='map-overlays-dialog';dialog.append(el('h2','Map Overlays & Legend'));const content=el('div');content.className='map-overlay-content';dialog.append(content);document.body.append(dialog);dialogHeader(dialog,{opener:open});dialog.addEventListener('close',()=>{dialog.remove();dialog=null;inputs.clear();groups.clear();});render();dialog.showModal();aviation.discover().then(changed=>{if(changed&&dialog?.open)render();});};
 const aviation=aviationMapLayers(L,map,addOverlay);map.on('layeradd layerremove',sync);definitions.forEach(addOverlay);aviation.discover();
 return {addOverlay,dispose(){if(disposed)return;disposed=true;aviation.dispose();stopBasemap();if(dialog){dialog.close();dialog?.remove();}map.off('layeradd layerremove',sync);control.remove();}};
}
