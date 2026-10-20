import {ResearchMap} from './map.js';
import {measurements,initialMeasurement,layerSignature} from './science-layers.js';
import {node as el,numeric as number,dateTime as local,button,link,select,disclosure,status,table,color,legend,errorMessage,readableName} from './workstation-ui.js';
const remote=new Set(['terrain-scanner','thermal-imager','multispectral-imager','hyperspectral-imager']);
const categorical=['#bba3f5','#f3bd83','#79d2a5','#80bdf2','#f0d17d','#79d8dd','#d6a9dd'];
const support='Actual sampled native cells only; transparent gaps remain missing. An acquisition footprint is modeled geometry, not valid-pixel coverage.';
const categoryKey=item=>[item.instrumentId,item.measurement||'',item.unit||''].join('|');
const savedViews=new Map();let mapPreferences={enabled:[],quantities:[],opacity:'.8'};
const pause=()=>new Promise(resolve=>setTimeout(resolve,0));
function expanded(bounds){if(!bounds)return null;return {west:Math.max(-180,bounds.west-.000001),east:Math.min(180,bounds.east+.000001),south:Math.max(-90,bounds.south-.000001),north:Math.min(90,bounds.north+.000001)};}
export async function scienceMap(parent,api,params,ids,instrument,disposals,catalog){
  parent.classList.add('science-map-page');const viewKey=JSON.stringify([[...ids].sort(),instrument,[...params].sort(([a],[b])=>a.localeCompare(b))]);const savedView=savedViews.get(viewKey);
  const counts=el('div',undefined,'map-overview-counts'),progress=status(parent,'Loading saved scientific records…');parent.append(counts);
  const layout=el('div',undefined,'science-map-layout'),main=el('section',undefined,'science-map-main'),panel=el('aside',undefined,'science-layer-panel');panel.setAttribute('aria-label','Science layers and inspection');layout.append(main,panel);parent.append(layout);
  const tools=el('div',undefined,'workspace-controls map-primary-actions'),holder=el('div',undefined,'workspace-map');main.append(tools,holder);
  const map=new ResearchMap(holder,null,{coverageOnly:true});window.L.control.scale({metric:true,imperial:false}).addTo(map.map);
  const layers=window.L.layerGroup().addTo(map.map),imagery=window.L.layerGroup().addTo(map.map);
  const inspector=el('section',undefined,'sample-inspector');inspector.setAttribute('aria-label','Science sample inspection');
  inspector.append(el('h3','Inspect saved science'),el('p','Choose an overview cluster to zoom, or inspect any acquisition / recorded value in the accessible browser below.'));
  panel.append(el('h2','Science layers'));const controls=el('div',undefined,'independent-layers');panel.append(controls);
  const opacityLabel=el('label','Science layer opacity'),opacity=el('input');opacity.type='range';opacity.min=0;opacity.max=1;opacity.step='.05';opacity.value=mapPreferences.opacity;opacity.setAttribute('aria-label','Science layer opacity');opacityLabel.append(opacity);panel.append(opacityLabel);
  const legendBox=el('section',undefined,'raster-legend');legendBox.setAttribute('aria-label','Scientific color legend');legendBox.append(el('p',support));const derivedLegends=el('div',undefined,'derived-legends');main.append(legendBox,derivedLegends);panel.append(inspector);
  const advanced=disclosure(parent,'Advanced — flight/source dates and quality filters'),filters=el('div',undefined,'workspace-controls');advanced.append(filters);
  function dateField(title,key){const label=el('label',title),input=el('input');input.type='date';input.setAttribute('aria-label',title);input.value=params.get(key)||'';label.append(input);filters.append(label);return input;}
  const from=dateField('Flight date from (UTC)','from'),to=dateField('Flight date to (UTC)','to'),sourceFrom=dateField('Source observation from (UTC)','sourceFrom'),sourceTo=dateField('Source observation to (UTC)','sourceTo');
  const sources=await api('/api/science/sources');
  const source=select(filters,'Source / scene',[['','All historical sources'],...sources.map(item=>[item.source.id,item.source.manifest.provider+' · '+item.source.manifest.sceneId])],params.get('source')||'');
  const quality=select(filters,'Acquisition quality',[['','All saved support'],['gaps','Known incomplete acquisitions'],['complete','Known full sample grids']],params.get('quality')||'');
  advanced.append(el('p','Historical source and sample-grid quality filters apply to remote acquisitions. Simulator environmental values retain their own recorded quality and timestamps.'));
  const evidence=disclosure(parent,'Sources, dates & sampled support',support),evidenceRows=el('div');evidence.append(evidenceRows);
  const browse=el('section',undefined,'panel scientific-record-browser');browse.setAttribute('aria-label','Browse all saved scientific records');browse.append(el('h2','Browse recorded science'),el('p','The overview includes the whole selected scope. This paged browser can open any saved acquisition or simulator value, including overlapping records at the same location.'));
  const browseControls=el('div',undefined,'workspace-controls'),browseType=select(browseControls,'Recorded science category',[['','All geographic acquisitions']],'');
  const searchLabel=el('label','Find saved metadata / environmental values'),search=el('input');search.type='search';search.maxLength=128;search.setAttribute('aria-label','Search recorded science');search.placeholder='UTC, record ID, quality or environmental value';search.value=params.get('search')||'';searchLabel.append(search);browseControls.append(searchLabel);const searchButton=button(browseControls,'Search recorded science',()=>{const q=new URLSearchParams(params);if(search.value.trim())q.set('search',search.value.trim());else q.delete('search');location.hash='#workspace/map?'+q;});search.onkeydown=event=>{if(event.key==='Enter'){event.preventDefault();searchButton.click();}};
  const pageLabel=el('label','Archive page'),pageNumber=el('input');pageNumber.type='number';pageNumber.min=1;pageNumber.step=1;pageNumber.value=1;pageNumber.setAttribute('aria-label','Archive page');pageLabel.append(pageNumber);browseControls.append(pageLabel);
  const visibleLabel=el('label',undefined,'checkbox-label'),onlyVisible=el('input');onlyVisible.type='checkbox';visibleLabel.append(onlyVisible,'Only the visible geographic region');browseControls.append(visibleLabel);browse.append(browseControls);
  const browseStatus=status(browse,'Loading recorded science…'),browseTable=el('div');browse.append(browseStatus,browseTable);parent.append(browse);
  const values=el('section',undefined,'recorded-value-inspection');values.setAttribute('aria-label','Recorded scientific values');parent.append(values);
  link(panel,'How to read the science map','#help?guide=v0.4.2-science-visualization.md');
  let scope=null,visibleScope=null,stopped=false,initial=true,version=0,controller=null,debounce,browseVersion=0,browseRows=[],displayedCells=0,displayedAtmosphere=0,selectedProduct=null,nativeEntries=[],detailWarnings=[];
  const enabled=new Map(mapPreferences.enabled),quantities=new Map(mapPreferences.quantities),nativeLayers=new Map(),overviewLayers=new Map(),rawCache=new Map(),viewCache=new Map();
  disposals.push(()=>{stopped=true;clearTimeout(debounce);controller?.abort();mapPreferences={enabled:[...enabled],quantities:[...quantities],opacity:opacity.value};savedViews.delete(viewKey);savedViews.set(viewKey,{center:map.map.getCenter(),zoom:map.map.getZoom()});while(savedViews.size>16)savedViews.delete(savedViews.keys().next().value);map.destroy();});
  const baseQuery=()=>({missions:ids.length?ids:null,instrument:instrument||null,from:from.value?from.value+'T00:00:00Z':null,to:to.value?to.value+'T23:59:59.9999999Z':null,source:source.value||null,sourceFrom:sourceFrom.value?sourceFrom.value+'T00:00:00Z':null,sourceTo:sourceTo.value?sourceTo.value+'T23:59:59.9999999Z':null,hasGaps:quality.value?quality.value==='gaps':null,search:search.value.trim()||null});
  function area(){const b=map.map.getBounds(),wrap=value=>((value+180)%360+360)%360-180,world=b.getEast()-b.getWest()>=360;return {west:world?-180:wrap(b.getWest()),south:Math.max(-90,b.getSouth()),east:world?180:wrap(b.getEast()),north:Math.min(90,b.getNorth())};}
  function visibility(){
    const alpha=Number(opacity.value);
    for(const [key,group] of overviewLayers){const shown=enabled.get(key)!==false;if(shown&&!layers.hasLayer(group))layers.addLayer(group);if(!shown&&layers.hasLayer(group))layers.removeLayer(group);group.eachLayer(layer=>layer.setStyle({opacity:alpha,fillOpacity:.25*alpha}));}
    for(const [key,group] of nativeLayers){const shown=enabled.get(key)!==false;if(shown&&!layers.hasLayer(group))layers.addLayer(group);if(!shown&&layers.hasLayer(group))layers.removeLayer(group);group.eachLayer(layer=>layer.setStyle({opacity:alpha,fillOpacity:layer.feature?.properties?.quality&&layer.feature.properties.quality!=='valid'?0:alpha}));}
    imagery.eachLayer(layer=>layer.setOpacity(enabled.get(layer.options.instrumentId)===false?0:Number(opacity.value)));
  }
  opacity.oninput=visibility;
  function describeSample(item,focus=false){
    const p=item.feature.properties;
    inspector.replaceChildren(el('h3',`${number(p.value)} ${p.unit}`),el('p',readableName(item.instrumentId,catalog)),el('dl'));
    const dl=inspector.lastChild;const coordinate=item.feature.geometry.type==='Point'?item.feature.geometry.coordinates:item.feature.geometry.coordinates[0][0];dl.append(el('dt','Location'),el('dd',`${number(coordinate[1],5)}°, ${number(coordinate[0],5)}°`));
    for(const [title,value] of [['Flight observation',local(p.flightUtc)],['Historical observation date',item.atmospheric?'Simulator environment — no satellite observation':local(p.sourceUtc)],['Source',item.provider],['Data quality',item.quality||'Saved valid native support'],['Scientific quantity',item.measurement],['Wavelength',item.wavelength?`${number(item.wavelength)} nm`:'Not applicable']])dl.append(el('dt',title),el('dd',value));
    const actions=el('div',undefined,'action-row');inspector.append(actions);link(actions,'Open mission research',`#workspace/analysis?missions=${p.missionId}`);link(actions,'Compare',`#workspace/compare?missions=${p.missionId}`);if(p.productId)link(actions,'Export acquisition CSV',`/api/science/products/${p.productId}/export/csv`);
    if(focus){inspector.tabIndex=-1;inspector.focus({preventScroll:true});}
  }
  function sampleTable(items,title){
    values.replaceChildren();table(values,title,['Measurement','Value','Units','Flight observation','Historical observation date','Source','Quality'],items.map(item=>[item.measurement,number(item.feature.properties.value),item.feature.properties.unit,local(item.feature.properties.flightUtc),item.atmospheric?'Simulator packet':local(item.feature.properties.sourceUtc),item.provider,item.quality||'Valid saved support']),{onSelect:index=>describeSample(items[index],true)});
  }
  function updateCounts(snapshot){
    displayedCells=0;displayedAtmosphere=0;for(const group of nativeLayers.values())for(const path of group.getLayers()){if(path.feature?.properties?.productId)displayedCells++;else displayedAtmosphere++;}
    counts.replaceChildren();const content=[['Total indexed geographic acquisitions',scope?.acquisitions||0],['Total geolocated simulator values',scope?.atmosphericValues||0],['Acquisitions in visible extent',snapshot.acquisitions],['Simulator values in visible extent',snapshot.atmosphericValues],['Overview features displayed',snapshot.bins.filter(bin=>enabled.get(bin.instrumentId)!==false).length],['Native cell records currently loaded',displayedCells],['Individual simulator values currently loaded',displayedAtmosphere]];
    for(const [title,value] of content){const box=el('div');box.append(el('strong',number(value)),el('span',title));counts.append(box);}
    holder.dataset.renderedFeatures=snapshot.bins.filter(bin=>enabled.get(bin.instrumentId)!==false).length+[...nativeLayers].filter(([id])=>enabled.get(id)!==false).reduce((n,[,group])=>n+group.getLayers().length,0);
    counts.dataset.totalAcquisitions=scope?.acquisitions||0;counts.dataset.totalAtmosphericValues=scope?.atmosphericValues||0;counts.dataset.displayedCells=displayedCells;
  }
  function controlsForScope(){
    controls.replaceChildren();const groups=new Map();for(const category of scope.categories){if(!groups.has(category.instrumentId))groups.set(category.instrumentId,[]);groups.get(category.instrumentId).push(category);}
    browseType.replaceChildren();const all=el('option','All geographic acquisitions');all.value='';browseType.append(all);
    for(const [id,categories] of groups){
      if(!enabled.has(id))enabled.set(id,true);
      const colorIndex=[...groups.keys()].indexOf(id),card=el('section',undefined,'independent-layer-control');card.dataset.instrument=id;
      const label=el('label',undefined,'checkbox-label'),checkbox=el('input');checkbox.type='checkbox';checkbox.checked=enabled.get(id);checkbox.setAttribute('aria-label','Show '+readableName(id,catalog));label.append(checkbox,readableName(id,catalog));card.append(label);
      checkbox.onchange=()=>{enabled.set(id,checkbox.checked);visibility();if(visibleScope)updateCounts(visibleScope);};
      const atmospheric=categories[0].measurement!=null;
      const categoryCount=categories.reduce((n,c)=>n+c.acquisitions+c.atmosphericValues,0);
      const optionsPanel=disclosure(card,'Measurement & details');optionsPanel.append(el('p',`${number(categoryCount)} ${atmospheric?'recorded environmental values':'historical-source acquisitions'}. ${atmospheric?'Location overview; actual packet quality remains visible.':'Modeled acquisition footprints; not continuous valid-pixel coverage.'}`));
      const swatch=el('span',undefined,'category-swatch');swatch.style.background=categorical[colorIndex%categorical.length];swatch.setAttribute('aria-hidden','true');label.prepend(swatch);
      const original=sources.find(item=>item.source.manifest.kind==={'terrain-scanner':'terrain','thermal-imager':'thermal','multispectral-imager':'multispectral','hyperspectral-imager':'hyperspectral'}[id])?.source;
      let choices=atmospheric?categories.map(c=>[c.measurement,readableName(c.measurement)+' ('+c.unit+')']):id==='terrain-scanner'?measurements.filter(([id])=>id==='elevation'):id==='thermal-imager'?measurements.filter(([id])=>id.startsWith('temperature')):id==='multispectral-imager'?measurements.filter(([id])=>['natural-color','false-color','ndvi','B04','B08'].includes(id)):(original?.manifest.bands||[]).filter(b=>!b.badBand).map(b=>[b.id,`${b.id} · ${b.wavelengthNm??'unknown'} nm · ${b.unit}`]);
      const quantity=select(optionsPanel,readableName(id,catalog)+' measurement',choices,quantities.get(id)||(instrument===id&&params.get('measurement'))||initialMeasurement(id,original)||choices[0]?.[0]);quantities.set(id,quantity.value);
      const describeQuantity=()=>optionsPanel.firstChild.textContent=quantity.selectedOptions[0]?.textContent+' · '+number(categoryCount)+' records';describeQuantity();
      quantity.onchange=()=>{imagery.clearLayers();derivedLegends.replaceChildren();quantities.set(id,quantity.value);describeQuantity();refreshVisible(true).catch(error=>errorMessage(main,error));if(browseType.value.startsWith(id))loadBrowser().catch(error=>errorMessage(browse,error));};
      button(optionsPanel,'Inspect '+readableName(id,catalog),()=>{browseType.value=id+(atmospheric?'|'+quantity.value:'');pageNumber.value=1;return loadBrowser();});
      const summary=el('small');summary.textContent=atmospheric?`${categories.reduce((n,c)=>n+c.validAtmosphericValues,0)} valid values; other quality flags are retained.`:`${categories.reduce((n,c)=>n+c.savedValidSamplePositions,0)} saved valid sample positions · ${categories.reduce((n,c)=>n+c.knownMissingSamplePositions,0)} known missing grid positions · ${categories.reduce((n,c)=>n+c.unknownSampleCounts,0)} acquisitions with unknown counts. Repeated samples are not extra area coverage.`;optionsPanel.append(summary);controls.append(card);
      const options=atmospheric?categories.map(c=>[id+'|'+c.measurement,readableName(id,catalog)+' · '+readableName(c.measurement)]):[[id,readableName(id,catalog)+' acquisitions']];for(const [value,title] of options){const option=el('option',title);option.value=value;browseType.append(option);}
    }
  }
  function renderOverview(snapshot){
    layers.clearLayers();nativeLayers.clear();overviewLayers.clear();nativeEntries=[];detailWarnings=[];displayedCells=displayedAtmosphere=0;legendBox.replaceChildren(el('p',support));
    const names=[...new Set(scope.categories.map(c=>c.instrumentId))];
    for(const bin of snapshot.bins){let group=overviewLayers.get(bin.instrumentId);if(!group){group=window.L.layerGroup();overviewLayers.set(bin.instrumentId,group);layers.addLayer(group);}
      const marker=window.L.circleMarker([bin.center.latitude,bin.center.longitude],{radius:Math.min(19,6+Math.log2(bin.records+1)*2),color:categorical[names.indexOf(bin.instrumentId)%categorical.length],weight:2,fillOpacity:.2});
      const tooltip=el('span',`${readableName(bin.instrumentId,catalog)} · ${number(bin.records)} ${bin.measurement?'recorded values':'acquisitions'} · location overview`);marker.bindTooltip(tooltip);
      marker.on('click',()=>{const bounds=expanded(bin.bounds);if(bounds)map.map.fitBounds([[bounds.south,bounds.west],[bounds.north,bounds.east]],{maxZoom:18,padding:[30,30]});browseType.value=bin.instrumentId+(bin.measurement?'|'+bin.measurement:'');onlyVisible.checked=true;pageNumber.value=1;loadBrowser().catch(error=>errorMessage(browse,error));});group.addLayer(marker);
    }
    updateCounts(snapshot);visibility();
  }
  async function getLayer(product,measurement,request){const key=product.id+'|'+product.sha256+'|'+measurement;if(rawCache.has(key)){const value=rawCache.get(key);rawCache.delete(key);rawCache.set(key,value);return value;}const value=await request(`/api/science/products/${product.id}/map-layer?measurement=${encodeURIComponent(measurement)}`);rawCache.set(key,value);while(rawCache.size>16)rawCache.delete(rawCache.keys().next().value);return value;}
  function addNative(product,layer,items){
    let group=nativeLayers.get(product.instrumentId);if(!group){group=window.L.layerGroup();nativeLayers.set(product.instrumentId,group);layers.addLayer(group);}
    const scaleKey=layerSignature(layer)+'|'+quantities.get(product.instrumentId);nativeEntries.push({product,layer,measurement:quantities.get(product.instrumentId),scaleKey});
    for(const feature of layer.features){const item={feature,instrumentId:product.instrumentId,measurement:quantities.get(product.instrumentId),provider:layer.source.provider,quality:product.quality,wavelength:layer.wavelengthNm};items.push(item);
      const geometry=window.L.geoJSON(feature,{style:()=>({stroke:false,fillOpacity:Number(opacity.value),fillColor:feature.properties.rgb?`rgb(${feature.properties.rgb.map(value=>Math.round(255*Math.max(0,Math.min(1,value/.3)))).join(',')})`:color(feature.properties.value,layer.minimum,layer.maximum)}),onEachFeature:(f,path)=>{path.feature=f;path.scientificScale=scaleKey;path.on('click',()=>describeSample(item));}});geometry.eachLayer(path=>group.addLayer(path));}
    displayedCells+=layer.features.length;
  }
  function rescaleNative(){
    for(const node of [...legendBox.children])if(node.dataset.remote)node.remove();
    const ranges=new Map();for(const entry of nativeEntries){const previous=ranges.get(entry.scaleKey);if(Number.isFinite(entry.layer.minimum)&&Number.isFinite(entry.layer.maximum))ranges.set(entry.scaleKey,{low:Math.min(Number.isFinite(previous?.low)?previous.low:Infinity,entry.layer.minimum),high:Math.max(Number.isFinite(previous?.high)?previous.high:-Infinity,entry.layer.maximum)});else if(!previous)ranges.set(entry.scaleKey,{low:NaN,high:NaN});}
    for(const group of nativeLayers.values())group.eachLayer(path=>{const range=ranges.get(path.scientificScale),p=path.feature?.properties;if(range&&p&&!p.rgb)path.setStyle({fillColor:color(p.value,range.low,range.high)});});
    const seen=new Set();for(const {product,layer,measurement,scaleKey} of nativeEntries){const key=product.sourceId+'|'+measurement;if(seen.has(key))continue;seen.add(key);const item=el('section');item.dataset.remote='true';item.dataset.source=product.sourceId;const range=ranges.get(scaleKey);
      if(['natural-color','false-color'].includes(measurement))item.replaceChildren(el('strong',readableName(product.instrumentId,catalog)+' · RGB reflectance'),el('p','0–0.3 reflectance display stretch; these colors do not represent one scalar quantity.'),el('small','Historical observation date '+local(layer.source.observationUtc)));
      else legend(item,readableName(product.instrumentId,catalog),range.low,range.high,layer.unit,`${layer.wavelengthNm?layer.wavelengthNm+' nm · ':''}Historical observation date ${local(layer.source.observationUtc)} · ${layer.source.verticalDatum}. Same numeric scale across compatible loaded cells; no cross-source scientific blend.`);
      legendBox.append(item);
    }
  }
  async function nativeDetail(snapshot,request,current){
    const items=[];let cursor=0,products=[];
    // A bounded metadata page decides representation. The complete scope remains
    // represented by overview bins; any individual record is directly browsable.
    if(snapshot.acquisitions){const page=await request('/api/science/workspace/map',{...baseQuery(),area:area(),limit:100});products=page.products;
      const estimated=products.reduce((n,p)=>n+p.width*p.height,0);
      const complete=!page.hasMore&&estimated<=6000;
      const selected=complete?products:[...new Map(products.map(product=>[product.instrumentId,product])).values()];
      for(const product of selected){if(!current())return;const measurement=quantities.get(product.instrumentId)||initialMeasurement(product.instrumentId,sources.find(item=>item.source.id===product.sourceId)?.source);if(!measurement)continue;
        try{const layer=await getLayer(product,measurement,request);if(!current())return;addNative(product,layer,items);}catch(error){if(error.name==='AbortError')throw error;const warning=`${readableName(product.instrumentId,catalog)}: ${error.message}`;detailWarnings.push(warning);evidenceRows.append(el('p',warning));}await pause();}
      holder.dataset.detailRepresentation=complete?'all-visible-native-cells':'overview-and-selected-acquisitions';
    }
    if(snapshot.atmosphericValues&&snapshot.atmosphericValues<=1000){
      let more=true;const byQuantity=new Map();while(more){const page=await request('/api/science/workspace/atmosphere',{...baseQuery(),area:area(),limit:500,after:cursor});if(!current())return;cursor=page.next;more=page.hasMore;
        for(const observation of page.items){if(!Number.isFinite(observation.value)||observation.measurementId!==quantities.get(observation.instrumentId))continue;const feature={type:'Feature',geometry:{type:'Point',coordinates:[observation.longitude,observation.latitude]},properties:{value:observation.value,unit:observation.unit,flightUtc:observation.timestampUtc,missionId:observation.missionId,quality:observation.quality}};
          const item={feature,instrumentId:observation.instrumentId,measurement:observation.measurementId,provider:observation.source,quality:observation.quality,atmospheric:true};items.push(item);const key=observation.instrumentId+'|'+observation.measurementId+'|'+observation.unit;if(!byQuantity.has(key))byQuantity.set(key,[]);byQuantity.get(key).push(item);displayedAtmosphere++;}
      }
      for(const entries of byQuantity.values()){const first=entries[0];let group=nativeLayers.get(first.instrumentId);if(!group){group=window.L.layerGroup();nativeLayers.set(first.instrumentId,group);layers.addLayer(group);}const valid=entries.filter(item=>item.quality==='valid');const low=Math.min(...valid.map(item=>item.feature.properties.value)),high=Math.max(...valid.map(item=>item.feature.properties.value));
        for(const item of entries){const feature=item.feature,p=feature.properties,marker=window.L.circleMarker([feature.geometry.coordinates[1],feature.geometry.coordinates[0]],{radius:4,color:item.quality==='valid'?color(p.value,low,high):'#a8b3be',fillOpacity:item.quality==='valid'?.8:0});marker.feature=feature;marker.on('click',()=>describeSample(item));group.addLayer(marker);}
        const key=el('section');legend(key,readableName(first.measurement),low,high,first.feature.properties.unit,'Actual recorded simulator packets; invalid/stale values retain quality flags and are not colored as valid measurements.');legendBox.append(key);
      }
    }
    if(current()){rescaleNative();sampleTable(items,'Currently loaded scientific values — use the archive browser for any saved record');updateCounts(snapshot);visibility();}
  }
  async function refreshVisible(force=false){
    controller?.abort();controller=new AbortController();const own=++version,current=()=>!stopped&&own===version;
    const request=(path,body)=>api(path,body,{signal:controller.signal});holder.setAttribute('aria-busy','true');progress.textContent='Rendering saved geographic science…';evidenceRows.replaceChildren();
    try{const query={...baseQuery(),area:area()},key=JSON.stringify(query),cached=viewCache.get(key);const snapshot=!force&&cached&&Date.now()-cached.at<15000?cached.value:await request('/api/science/workspace/overview',query);if(!current())return;
      viewCache.set(key,{value:snapshot,at:Date.now()});while(viewCache.size>8)viewCache.delete(viewCache.keys().next().value);visibleScope=snapshot;renderOverview(snapshot);await nativeDetail(snapshot,request,current);if(!current())return;
      progress.textContent=`${holder.dataset.detailRepresentation==='overview-and-selected-acquisitions'?'Overview mode: native detail shows one saved acquisition per instrument; inspect any other record below. ':''}${scope.acquisitions} geographically indexed acquisitions reviewed in the selected scope · ${scope.atmosphericValues} recorded simulator values. Overview accounts for all matching indexed records. ${snapshot.acquisitions} acquisitions and ${snapshot.atmosphericValues} environmental values in view; ${snapshot.bins.length} aggregated location features. ${displayedCells} detailed native cell records and ${displayedAtmosphere} individual simulator values currently loaded. Zoom or browse any record for exact values; no historical source imagery or missing pixels were invented.`;
      if(detailWarnings.length)progress.textContent+=' Some saved layers are unavailable: '+detailWarnings.slice(0,3).join('; ')+(detailWarnings.length>3?' · '+(detailWarnings.length-3)+' further warnings in Sources.':'');
      if(!scope.acquisitions&&!scope.atmosphericValues)progress.textContent='No geolocated saved science in this scope. Choose another mission or instrument. Recorded flight telemetry remains available in Replay.';
      if(onlyVisible.checked)await loadBrowser();
    }catch(error){if(error.name!=='AbortError'&&current())errorMessage(main,error,()=>refreshVisible(true));}
    finally{if(current())holder.removeAttribute('aria-busy');}
  }
  async function loadScope(fit=true){
    rawCache.clear();viewCache.clear();imagery.clearLayers();derivedLegends.replaceChildren();progress.textContent='Loading saved scientific records and counting the complete selected scope…';scope=await api('/api/science/workspace/overview',baseQuery());if(stopped)return;controlsForScope();
    if(fit&&savedView){map.map.setView(savedView.center,savedView.zoom,{animate:false});}else if(fit&&scope.extent){const b=expanded(scope.extent);map.map.fitBounds([[b.south,b.west],[b.north,b.east]],{padding:[24,24],maxZoom:17});}
    await refreshVisible(true);await loadBrowser();initial=false;
  }
  async function inspectProduct(product){
    selectedProduct=product;const measurement=quantities.get(product.instrumentId)||initialMeasurement(product.instrumentId,sources.find(item=>item.source.id===product.sourceId)?.source);if(!measurement)throw Error('Choose an available wavelength for this instrument.');
    const layer=await getLayer(product,measurement,api);if(stopped)return;
    const old=nativeLayers.get(product.instrumentId);if(old)layers.removeLayer(old);nativeLayers.delete(product.instrumentId);nativeEntries=nativeEntries.filter(entry=>entry.product.instrumentId!==product.instrumentId);const items=[];displayedCells=0;addNative(product,layer,items);rescaleNative();sampleTable(items,`${readableName(product.instrumentId,catalog)} — acquisition ${local(product.flightPacketUtc)}`);if(items.length)describeSample(items[0],true);
    const coordinates=layer.features.flatMap(feature=>feature.geometry.coordinates[0].map(p=>[p[1],p[0]]));if(coordinates.length){initial=true;map.map.fitBounds(window.L.latLngBounds(coordinates),{padding:[20,20],maxZoom:18});setTimeout(()=>initial=false,0);}visibility();if(visibleScope)updateCounts(visibleScope);
    const detail=disclosure(evidenceRows,'Inspected source and exact scientific provenance');detail.append(el('pre',JSON.stringify({source:layer.source,provenance:layer.provenance,method:layer.method,flight:product.flightPacketUtc},null,2)));link(detail,'Export acquisition CSV',`/api/science/products/${product.id}/export/csv`);
  }
  async function loadBrowser(){
    const own=++browseVersion,[category,measurement]=browseType.value.split('|'),atmospheric=!!measurement;const page=Math.max(1,Math.floor(Number(pageNumber.value)||1));pageNumber.value=page;
    browseStatus.textContent='Loading saved archive records…';const query={...baseQuery(),instrument:category||instrument||null,measurement:measurement||null,offset:(page-1)*30,limit:30,area:onlyVisible.checked?area():null};
    const result=await api('/api/science/workspace/'+(atmospheric?'atmosphere':'map'),query);if(stopped||own!==browseVersion)return;browseRows=atmospheric?result.items:result.products;
    const categoryRows=(onlyVisible.checked?visibleScope:scope)?.categories.filter(c=>(!category||c.instrumentId===category)&&(!measurement||c.measurement===measurement))||[];const total=categoryRows.reduce((n,c)=>n+(atmospheric?c.atmosphericValues:c.acquisitions),0);
    pageNumber.max=Math.max(1,Math.ceil(total/30));browseStatus.textContent=`Page ${page} of ${Math.max(1,Math.ceil(total/30))} · ${total} ${atmospheric?'environmental values':'acquisitions'} available in ${onlyVisible.checked?'the visible':'the selected'} scope. No viewing ceiling. Metadata pages contain at most 30 records.`;
    browseTable.replaceChildren();
    table(browseTable,atmospheric?'Saved simulator observations':'Saved geographic acquisitions',atmospheric?['Measurement','Value','Units','Observation time','Quality']:['Acquisition','Instrument','Flight observation','Historical observation date','Quality'],browseRows.map((row,index)=>atmospheric?[readableName(row.measurementId),number(row.value),row.unit,local(row.timestampUtc),row.quality]:[(page-1)*30+index+1,readableName(row.instrumentId,catalog),local(row.flightPacketUtc),local(row.sourceObservationUtc),row.quality]),{pageSize:30,onSelect:async index=>{const row=browseRows[index];if(!row)return;if(atmospheric){const feature={type:'Feature',geometry:{type:'Point',coordinates:[row.longitude,row.latitude]},properties:{value:row.value,unit:row.unit,flightUtc:row.timestampUtc,missionId:row.missionId}};describeSample({feature,atmospheric:true,instrumentId:row.instrumentId,measurement:row.measurementId,provider:row.source,quality:row.quality},true);}else await inspectProduct(row);}});
    previous.disabled=page<=1;next.disabled=!result.hasMore;
  }
  button(browseControls,'Go to archive page',()=>loadBrowser());const previous=button(browseControls,'Previous archive page',()=>{pageNumber.value=Math.max(1,Number(pageNumber.value)-1);return loadBrowser();});const next=button(browseControls,'Next archive page',()=>{pageNumber.value=Number(pageNumber.value)+1;return loadBrowser();});browseType.onchange=()=>{pageNumber.value=1;loadBrowser().catch(error=>errorMessage(browse,error));};onlyVisible.onchange=()=>{pageNumber.value=1;loadBrowser().catch(error=>errorMessage(browse,error));};
  button(tools,'Fit Data',()=>{if(scope?.extent){const b=expanded(scope.extent);map.map.fitBounds([[b.south,b.west],[b.north,b.east]],{padding:[24,24],maxZoom:17});}});button(tools,'Refresh Saved Science',()=>loadScope(false));
  if(ids.length===1){button(tools,'Show Flight Track',async()=>{const [research,track]=await Promise.all([api(`/api/missions/${ids[0]}/research`),api(`/api/missions/${ids[0]}/track?limit=1500`)]);map.set({plan:research.planSnapshot,track:track.points});map.fit();});link(tools,'Compare',`#workspace/compare?missions=${ids[0]}`);link(tools,'Export',`#workspace/results?missions=${ids[0]}`);}
  button(advanced,'Apply Filters / Fit Data',()=>{const q=new URLSearchParams(params);for(const [key,value] of [['from',from.value],['to',to.value],['sourceFrom',sourceFrom.value],['sourceTo',sourceTo.value],['source',source.value],['quality',quality.value],['search',search.value.trim()]]){if(value)q.set(key,value);else q.delete(key);}const target='#workspace/map?'+q;if(location.hash===target)return loadScope();location.hash=target;});
  const derived=disclosure(evidence,'Advanced — generate an exact native-grid composite','This derives a visualization from saved acquisitions. It does not retrieve external data or alter original scientific values.');
  button(derived,'Build Native Composite',async()=>{const product=selectedProduct||browseRows.find(p=>p.sourceId);if(!product)throw Error('Inspect a geographic acquisition in the archive browser first.');const measurement=quantities.get(product.instrumentId);let job=await api(`/api/missions/${product.missionId}/composites`,{sourceId:product.sourceId,bandId:measurement,mode:'mission-samples'});
    while(!stopped&&['Queued','Running'].includes(job.state)){progress.textContent=`Generating a derived visualization — ${job.completed}/${job.total??'unknown'} complete; no external science download requested.`;await new Promise(resolve=>setTimeout(resolve,600));job=await api('/api/analysis/jobs/'+job.id);}if(stopped)return;if(job.state!=='Completed')throw Error(job.message||'Derived visualization did not complete. Original measurements are unchanged.');
    imagery.clearLayers();derivedLegends.replaceChildren();for(const id of job.artifacts){const preview=await api(`/api/science/artifacts/${id}/preview`);if(stopped)return;imagery.addLayer(window.L.imageOverlay(preview.image,[[preview.bounds.south,preview.bounds.west],[preview.bounds.north,preview.bounds.east]],{opacity:Number(opacity.value),instrumentId:product.instrumentId}));const key=el('section');if(preview.bands.length>=3)key.append(el('strong','Derived RGB reflectance image'),el('p','0–0.3 reflectance display stretch; no scalar color legend.'),el('small',preview.method));else legend(key,'Derived image — independent source scale',preview.minimum,preview.maximum,preview.units.join(', '),preview.method);derivedLegends.append(key);link(derived,'Download exact numeric GeoTIFF',`/api/science/artifacts/${id}`);}progress.textContent='Saved native-grid composite displayed with its source scale. Transparent nodata and exact numeric exports are preserved.';visibility();});
  map.map.on('moveend',()=>{if(initial||stopped)return;clearTimeout(debounce);debounce=setTimeout(()=>refreshVisible().catch(error=>{if(error.name!=='AbortError')errorMessage(main,error);}),200);});
  await loadScope();
}
