import {inspectSource} from './source-inspection.js';
import {openEmitSource,openEmitImport} from './emit-library.js';
import {libraryLocation} from './library-location.js';
import {reviewSourceDeletion} from './source-deletion.js';
import {libraryDownload} from './library-download.js';
import {ResearchMap} from './map.js';
import {openHyperspectralImport} from './hyperspectral-import.js';
import {node as el,bytes,dateTime as local,readableName,button,link,select,disclosure,status} from './workstation-ui.js';
let dispose=()=>{};
export function disposeScientificLibrary(){dispose();}
export async function scientificLibrary(parent,api){
 dispose();let stopped=false,timer,epoch=0;const maps=[],popups=[],selected=new Set();
 dispose=()=>{stopped=true;clearTimeout(timer);popups.splice(0).forEach(close=>close());maps.splice(0).forEach(map=>map.destroy());};
 parent.replaceChildren();if(location.hash.startsWith('#library/download')){const close=await libraryDownload(parent,api,()=>stopped);if(stopped)close();else dispose=()=>{stopped=true;close();};return;}
 const heading=el('div',undefined,'page-heading');heading.append(el('p','REUSABLE HISTORICAL SOURCES','eyebrow'),el('h1','External Data Manager'),el('p','Historical scientific datasets can be reused during future missions. Flight recordings remain separate.'));parent.append(heading);link(heading,'Find & Download Data','#library/download');
 await libraryLocation(parent,api,{stopped:()=>stopped});if(stopped)return;
 parent.append(el('h2','Manage Downloaded Data'));const deleteTools=el('div',undefined,'action-row');parent.append(deleteTools);
 async function changed(){window.dispatchEvent(new CustomEvent('aeroscience-sources-changed'));if(!stopped&&location.hash.startsWith('#library')&&parent.isConnected)await scientificLibrary(parent,api);}
 let canDelete=false;const selectionStatus=status(deleteTools,'0 source regions selected across pages.');const deleteSelected=button(deleteTools,'Delete Selected External Data',()=>reviewSourceDeletion(api,{sourceIds:[...selected]},changed),'danger');deleteSelected.disabled=true;function selectionChanged(){selectionStatus.textContent=`${selected.size} source regions selected across pages. The deletion preview reviews these exact identities.`;deleteSelected.disabled=!canDelete||selected.size===0;}button(deleteTools,'Clear source selection',()=>{selected.clear();for(const input of parent.querySelectorAll('input[data-source]'))input.checked=false;selectionChanged();},'secondary');button(deleteTools,'Delete All Downloaded External Data',()=>reviewSourceDeletion(api,{all:true},changed),'danger');
 const controls=el('div',undefined,'workspace-controls'),label=el('label','Search collections'),search=el('input');search.type='search';search.placeholder='Provider, product, scene or datum';search.setAttribute('aria-label','Search External Data Manager');label.append(search);controls.append(label);
 const kind=select(controls,'Scientific category',[['','All scientific categories'],...['terrain','thermal','multispectral','hyperspectral'].map(value=>[value,readableName(value)])],'');
 const provider=select(controls,'Library provider',[['','All providers']],'');const sort=select(controls,'Sort collections',[['name','Name (A–Z)'],['date','Newest source observation'],['size','Largest storage usage']],'name');parent.append(controls);
 const progress=status(parent,'Reading the full retained source inventory…'),list=el('div',undefined,'library-grid'),navigation=el('div',undefined,'action-row');parent.append(list,navigation);
 const access=await api('/api/science/access');if(stopped)return;const excluded=new Set(access.configuration.preferences.excludedSourceIds||[]);canDelete=access.canConfigure;selectionChanged();
 if(!access.canConfigure){deleteTools.append(el('p','Delete downloaded external data on the companion PC’s local dashboard.'));for(const b of deleteTools.querySelectorAll('button'))b.disabled=true;}
 async function render(cursor=null){
  const current=++epoch;maps.splice(0).forEach(map=>map.destroy());list.replaceChildren();navigation.replaceChildren();progress.textContent='Searching the complete retained inventory…';
  const q=new URLSearchParams({sort:sort.value});if(kind.value)q.set('kind',kind.value);if(provider.value)q.set('provider',provider.value);if(search.value.trim())q.set('search',search.value.trim());if(cursor)q.set('after',cursor);
  const page=await api('/api/science/source-collections?'+q);if(stopped||current!==epoch)return;
  maps.splice(0).forEach(map=>map.destroy());list.replaceChildren();navigation.replaceChildren();
  progress.textContent=`${page.total} matching collections in the complete Library. Showing ${page.items.length} collections on this page. Region totals below come from the full inventory; geographic extents are source footprints, not QA-valid pixels or aircraft observations.`;
  for(const group of page.items){
   if(![...provider.options].some(option=>option.value===group.provider)){const option=el('option',group.provider);option.value=group.provider;provider.append(option);}
   const box=el('article',undefined,'panel library-collection');box.append(el('h2',group.product),el('p',`${group.provider} · ${readableName(group.kind)} · ${group.regions} retained regions · ${bytes(group.bytes)} · ${group.latestObservation?'Newest source observation '+local(group.latestObservation):'Original observation dates unknown / composite'}`));if(group.matchingRegions<group.regions)box.append(el('p',`${group.matchingRegions} regions match this search; deleting the collection reviews all ${group.regions} regions.`));list.append(box);
   const actions=el('div',undefined,'action-row');box.append(actions);
   button(actions,'Delete This Collection',()=>reviewSourceDeletion(api,{collectionId:group.id},changed),'danger').disabled=!access.canConfigure;
   const regions=disclosure(box,'Individual Regions & Provenance'),rows=el('div');regions.append(rows);let loaded=false,regionEpoch=0;
   async function regionPage(after=0,through=null){
    const serial=++regionEpoch;rows.replaceChildren(el('p','Reading a bounded source page…'));
    const params=new URLSearchParams({collection:group.id,limit:20,after});if(through!=null)params.set('through',through);
    const result=await api('/api/science/sources/page?'+params);if(stopped||current!==epoch||serial!==regionEpoch||!box.isConnected)return;
    rows.replaceChildren(el('p',`${result.items.length} regions on this page · ${result.total} in this collection. Availability checks apply to these displayed regions.`));
    const pageSources=result.items;
    const choose=el('input'),chooseLabel=el('label','Select regions on this page ');choose.type='checkbox';choose.setAttribute('aria-label','Select regions on this page in '+group.product);chooseLabel.prepend(choose);rows.append(chooseLabel);choose.onchange=()=>{for(const item of pageSources)if(choose.checked)selected.add(item.source.id);else selected.delete(item.source.id);for(const checkbox of rows.querySelectorAll('input[data-source]'))checkbox.checked=selected.has(checkbox.dataset.source);selectionChanged();};
    for(const item of result.items){
     const source=item.source,m=source.manifest,row=el('article',undefined,'library-region'),detail=disclosure(row,m.name+' · '+local(m.observationUtc));
     detail.append(el('p',`${!item.filesAvailable?'Unavailable — reconnect storage or restore the original files':m.acquisition?.usablePixels===0?'Originals retained; checked preview has no usable pixels — inspect quality':'Originals retained; actual pixel quality is assessed separately'} · ${m.verticalDatum} · ${bytes(source.files.reduce((n,file)=>n+file.bytes,0))}`),el('p',`Provider: ${m.provider} · Scene: ${m.sceneId} · Units: ${[...new Set(m.bands.map(b=>b.unit).filter(Boolean))].join(', ')||'Unknown'} · Retained source ${source.id}. Historical source dates remain separate from flight observations.`));
     let folderLoaded=false;detail.addEventListener('toggle',()=>{if(detail.open&&!folderLoaded){folderLoaded=true;libraryLocation(detail,api,{sourceId:source.id,stopped:()=>stopped});}});
     const raw=disclosure(detail,'Advanced Diagnostics — exact managed source metadata');raw.append(el('p','Includes source identifiers and managed relative paths. Review before sharing.'),el('pre',JSON.stringify(source,null,2)));
     const chosen=el('input'),label=el('label','Select this source');chosen.type='checkbox';chosen.dataset.source=source.id;chosen.checked=selected.has(source.id);chosen.setAttribute('aria-label','Select source '+m.name);chosen.onchange=()=>{if(chosen.checked)selected.add(source.id);else selected.delete(source.id);selectionChanged();};label.prepend(chosen);detail.append(label);
     button(detail,'Delete This Source',()=>reviewSourceDeletion(api,{sourceIds:[source.id]},changed),'danger').disabled=!access.canConfigure;
     const state=el('p','Active source selection: '+(excluded.has(source.id)?'Excluded (originals retained)':'Eligible, subject to coverage and QA')),feedback=status(detail,'');detail.append(state);
     button(detail,'Exclude from Active Science',async()=>{await api('/api/science/source-selection',{sourceId:source.id,enabled:false});excluded.add(source.id);state.textContent='Active source selection: Excluded (originals retained)';feedback.textContent='Retained originals were not deleted.';}).disabled=!access.canConfigure;
     button(detail,m.swath?'Aircraft Sampling Not Supported':'Use for Active Science',async()=>{await api('/api/science/source-selection',{sourceId:source.id,enabled:true});excluded.delete(source.id);state.textContent='Active source selection: Eligible, subject to coverage and QA';feedback.textContent='Coverage and actual quality masks still apply.';}).disabled=!access.canConfigure||!!m.swath;
     if(m.swath?.format==='emit-netcdf'){button(detail,'Explore Spectrum / Map / Export',()=>openEmitSource(api,source.id));detail.append(el('p','EMIT development validation. Historical source exploration only; automatic aircraft sampling is not yet supported. GLT extent is not valid-pixel coverage.'));}
     if(!m.swath)button(detail,'Inspect Numeric Values / Map',async()=>{const vertices=await api(`/api/science/sources/${source.id}/coverage`);if(stopped||!detail.isConnected)return;popups.push(inspectSource(api,source,vertices,search));});
     let map;button(detail,'View Coverage',async()=>{if(map){map.fit();return;}const vertices=await api(`/api/science/sources/${source.id}/coverage`);if(stopped||!detail.isConnected)return;const holder=el('div');detail.append(holder);map=new ResearchMap(holder,null,{coverageOnly:true});maps.push(map);map.set({plan:{surveyAreas:[{name:m.name,vertices}]}});map.fit();});rows.append(row);
    }
    if(after)button(rows,'First regions',()=>regionPage(),'secondary');if(result.next!=null)button(rows,'More regions',()=>regionPage(result.next,result.through),'secondary');
   }
   regions.addEventListener('toggle',()=>{if(regions.open&&!loaded){loaded=true;regionPage();}});
  }
  if(!page.items.length)list.append(el('p','No matching retained collections. Clear filters or Find & Download Data.','empty-state'));
  if(cursor)button(navigation,'First collections',()=>render(),'secondary');if(page.next!=null)button(navigation,'More collections',()=>render(page.next),'secondary');
 }
 function refresh(){render().catch(error=>{if(!stopped)progress.textContent=error.message;});}
 search.oninput=()=>{clearTimeout(timer);timer=setTimeout(refresh,200);};kind.onchange=provider.onchange=sort.onchange=refresh;
 await render();
 button(heading,'Import NASA EMIT Originals',()=>openEmitImport(api));
 const advanced=disclosure(parent,'Advanced — import and source configuration');link(heading,'Import Hyperspectral Dataset','#library/download?science=hyperspectral');button(advanced,'Import Files Already on My Computer',()=>openHyperspectralImport(api,'hyperspectral'));link(advanced,'Configure providers, consent and advanced import','#settings/science');
}
