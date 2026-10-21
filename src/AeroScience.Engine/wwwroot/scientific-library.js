import {libraryDownload} from './library-download.js';
import {ResearchMap} from './map.js';
import {openHyperspectralImport} from './hyperspectral-import.js';
import {node as el,bytes,dateTime as local,readableName,button,link,select,disclosure,status} from './workstation-ui.js';
let dispose=()=>{};
export function disposeScientificLibrary(){dispose();}
export async function scientificLibrary(parent,api){
  dispose();let stopped=false,timer;const maps=[];dispose=()=>{stopped=true;clearTimeout(timer);maps.splice(0).forEach(map=>map.destroy());};
  parent.replaceChildren();if(location.hash.startsWith('#library/download')){const close=await libraryDownload(parent,api,()=>stopped);if(stopped)close();else dispose=()=>{stopped=true;close();};return;}
  const heading=el('div',undefined,'page-heading');heading.append(el('div'));heading.firstChild.append(el('p','REUSABLE HISTORICAL SOURCES','eyebrow'),el('h1','Scientific Data Library'),el('p','Retained Earth observations for future research flights. Your mission-acquired results live in Science Workspace.'));parent.append(heading);link(heading,'Browse & Download Data','#library/download');
  const tools=el('div',undefined,'workspace-controls'),searchLabel=el('label','Search collections'),search=el('input');search.type='search';search.placeholder='Provider, product, scene or datum';search.setAttribute('aria-label','Search Scientific Data Library');searchLabel.append(search);tools.append(searchLabel);
  const category=select(tools,'Scientific category',[['','All scientific categories'],...['terrain','thermal','multispectral','hyperspectral'].map(kind=>[kind,readableName(kind)])],'');
  const provider=select(tools,'Library provider',[['','All providers']],'');
  const sort=select(tools,'Sort collections',[['name','Name (A–Z)'],['date','Newest source observation'],['size','Largest storage usage']],'name');parent.append(tools);
  const progress=status(parent,'Reading the retained source catalog…'),list=el('div',undefined,'library-grid');parent.append(list);
  const sources=await api('/api/science/sources');if(stopped)return;
  for(const name of [...new Set(sources.map(item=>item.source.manifest.provider))].sort()){const option=el('option',name);option.value=name;provider.append(option);}
  const groups=new Map();for(const item of sources){const m=item.source.manifest,product=({'usgs-3dep-numeric':'3DEP numeric elevation','landsat-c2-l2':'Landsat surface temperature','sentinel-2-l2a':'Sentinel-2 Level-2A','cop-dem-glo-30':'Copernicus DEM'})[m.acquisition?.workflow]||m.name,key=m.provider+'|'+product;
    if(!groups.has(key))groups.set(key,{provider:m.provider,product,kind:m.kind,items:[],bytes:0,date:0});const group=groups.get(key);group.items.push({...item,search:[m.name,m.provider,m.sceneId,m.verticalDatum,m.kind,m.notes||''].join(' ').toLowerCase()});group.bytes+=item.source.files.reduce((n,file)=>n+file.bytes,0);group.date=Math.max(group.date,Date.parse(m.observationUtc)||0);
  }
  function render(){
    maps.splice(0).forEach(map=>map.destroy());list.replaceChildren();const query=search.value.trim().toLowerCase();const chosen=[...groups.values()].filter(group=>(!category.value||group.kind===category.value)&&(!provider.value||group.provider===provider.value)&&(!query||group.items.some(item=>item.search.includes(query))));
    chosen.sort((a,b)=>sort.value==='size'?b.bytes-a.bytes:sort.value==='date'?b.date-a.date:a.product.localeCompare(b.product));
    progress.textContent=`${chosen.length} collections · ${chosen.reduce((n,group)=>n+group.items.length,0)} retained regions · ${bytes(chosen.reduce((n,group)=>n+group.bytes,0))}. Original values, source dates and files are preserved; grouping does not merge scientific sources.`;
    if(!chosen.length){list.append(el('p','No matching retained collections. Clear filters, configure automatic access, or use advanced import.','empty-state'));return;}
    for(const group of chosen){
      const box=el('article',undefined,'panel library-collection');box.append(el('h2',group.product));const meta=el('div',undefined,'collection-meta');meta.append(el('span',group.provider),el('span',readableName(group.kind),'library-category'),el('span',`${group.items.length} retained regions`),el('span',bytes(group.bytes)));box.append(meta,el('p',`${group.items.filter(item=>item.filesAvailable).length} available regions · ${new Set(group.items.map(item=>item.source.manifest.sceneId)).size} distinct historical scenes. ${group.date?'Newest source observation '+local(group.date):'Source observation dates unknown / composite'}.`));list.append(box);
      const actions=el('div',undefined,'action-row');box.append(actions);
      let coverageMap;
      button(actions,'View Combined Geographic Coverage',async()=>{if(coverageMap){coverageMap.fit();return;}const holder=el('div'),feedback=status(box,'Reading original geographic extents…');box.append(holder);coverageMap=new ResearchMap(holder,null,{coverageOnly:true});maps.push(coverageMap);const areas=[];
        for(let index=0;index<group.items.length;index+=8){const batch=await Promise.all(group.items.slice(index,index+8).map(async item=>({name:item.source.manifest.name+' · '+item.source.manifest.sceneId,vertices:await api(`/api/science/sources/${item.source.id}/coverage`)})));if(stopped||!holder.isConnected)return;areas.push(...batch);coverageMap.set({plan:{surveyAreas:areas}});feedback.textContent=`${areas.length}/${group.items.length} retained geographic extents loaded. Source envelopes do not establish cloud-free pixels or aircraft-acquired coverage.`;}coverageMap.fit();});
      const regions=disclosure(box,'Individual Regions & Provenance'),rows=el('div');regions.append(rows);let offset=0;
      function page(){rows.replaceChildren(el('p',`${offset+1}–${Math.min(group.items.length,offset+20)} of ${group.items.length} regions`));for(const item of group.items.slice(offset,offset+20)){const source=item.source,m=source.manifest,row=el('article',undefined,'library-region');const detail=disclosure(row,m.name+' · '+local(m.observationUtc));detail.append(el('p',`${item.filesAvailable?'Available':'Unavailable — check the selected storage drive'} · ${m.verticalDatum} · ${bytes(source.files.reduce((n,file)=>n+file.bytes,0))}`),el('pre',JSON.stringify(source,null,2)));const result=el('p');
        button(detail,'Exclude from active source selection',async()=>{await api('/api/science/source-selection',{sourceId:source.id,enabled:false});result.textContent='Excluded from active selection. The retained original files remain in the library.';});button(detail,'Use for active source selection',async()=>{await api('/api/science/source-selection',{sourceId:source.id,enabled:true});result.textContent='Eligible for source selection. Geographic coverage and actual quality masks still apply.';});detail.append(result);rows.append(row);}if(offset)button(rows,'Previous regions',()=>{offset-=20;page();});if(offset+20<group.items.length)button(rows,'More regions',()=>{offset+=20;page();});}
      regions.addEventListener('toggle',()=>{if(regions.open&&!rows.children.length)page();});
    }
  }
  search.oninput=()=>{clearTimeout(timer);timer=setTimeout(render,120);};category.onchange=provider.onchange=sort.onchange=render;render();
  const advanced=disclosure(parent,'Advanced — import and source configuration');button(advanced,'Import Hyperspectral Dataset',()=>openHyperspectralImport(api,'hyperspectral'));link(advanced,'Configure providers, consent and advanced import','#settings/science');
}
