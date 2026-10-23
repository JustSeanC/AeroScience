import {ResearchMap} from './map.js';
import {atmosphere} from './mission-workspace.js';
import {measurements,initialMeasurement,layerSignature,measurementsFor} from './science-map.js';
import {node as el,numeric as number,dateTime as local,select,button,link,status,legend,color,table,disclosure,metric,pointInCell,readableName,chart} from './workstation-ui.js';

export async function comparison(parent,api,missions,ids,disposals,catalog){
  parent.append(el('h2','Compare Scientific Results'),el('p','Choose two recorded datasets. Source dates stay separate from flight dates; a simulator revisit is not independently observed Earth change.'));
  const tools=el('div',undefined,'workspace-controls'),message=status(parent,'Loading saved comparison datasets…');parent.append(tools);
  const commonLabel=el('label',undefined,'checkbox-label'),common=el('input');common.type='checkbox';common.setAttribute('aria-label','Use shared numeric color scale');commonLabel.append(common,'Use shared numeric color scale');tools.append(commonLabel);
  const scaleStatus=el('p',undefined,'scale-state');tools.append(scaleStatus);
  const grid=el('div',undefined,'comparison-grid');parent.append(grid);
  const columns=[];let syncing=false,stopped=false,sources;disposals.push(()=>stopped=true);
  const findPoint=point=>{
    for(const column of columns){
      const feature=column.layer?.features.find(feature=>pointInCell(point,feature));
      const p=feature?.properties;column.inspector.replaceChildren(el('h4',`Same location · ${number(point.lat,5)}°, ${number(point.lng,5)}°`),el('p',p?`${number(p.value)} ${p.unit} · Flight ${local(p.flightUtc)} · Historical observation date ${local(p.sourceUtc)}`:'No valid recorded value at this location. No nearest-neighbor substitute or interpolation was used.'));
    }
  };
  function scales(){
    const active=columns.filter(column=>column.layer?.features.length);
    const compatible=active.length>=2&&new Set(active.map(column=>layerSignature(column.layer)+'|'+column.quantity.value)).size===1;
    if(common.checked&&!compatible)common.checked=false;
    common.disabled=!compatible;
    const ranges=active.flatMap(column=>[column.layer.minimum,column.layer.maximum]).filter(Number.isFinite);
    const low=Math.min(...ranges),high=Math.max(...ranges);
    scaleStatus.textContent=common.checked?'Shared numeric scale — the same color represents the same value.':compatible?'Independent scales. Select shared scale for equivalent colors.':'Independent scales — quantities, units, wavelengths or vertical references differ, or valid data are unavailable.';
    for(const column of active){
      const min=common.checked?low:column.layer.minimum,max=common.checked?high:column.layer.maximum;
      column.overlay.eachLayer(path=>{const p=path.feature.properties;path.setStyle({fillColor:p.rgb?`rgb(${p.rgb.map(value=>Math.round(255*Math.max(0,Math.min(1,value/.3)))).join(',')})`:color(p.value,min,max)});});
      if(['natural-color','false-color'].includes(column.quantity.value))column.legend.replaceChildren(el('strong','RGB reflectance'),el('p','0–0.3 reflectance display stretch. No scalar subtraction of color images.'));
      else legend(column.legend,(common.checked?'Shared':'Independent')+' scale',min,max,column.layer.unit,column.layer.wavelengthNm?`${column.layer.wavelengthNm} nm`:'Transparent missing data');
    }
  }
  common.onchange=scales;
  async function addColumn(){
    const index=columns.length;if(index>=8)return;
    const card=el('section',undefined,'comparison-card panel');card.setAttribute('aria-label',`Dataset ${String.fromCharCode(65+index)}`);grid.append(card);card.append(el('h3',`Dataset ${String.fromCharCode(65+index)}`));
    const mission=select(card,'Comparison mission '+(index+1),missions.map(m=>[m.id,m.name]),ids[index]||missions[index]?.id||missions[0]?.id);
    const acquisition=select(card,'Acquisition '+(index+1),[['','Choose recorded science']]);
    const quantity=select(card,'Visual measurement '+(index+1),measurements,'elevation');
    const subtitle=el('p',undefined,'comparison-source'),holder=el('div'),scale=el('section'),inspector=el('section');inspector.className='sample-inspector';inspector.setAttribute('aria-label',`Dataset ${String.fromCharCode(65+index)} selected value`);card.append(subtitle,holder,scale,inspector);
    const map=new ResearchMap(holder,null,{coverageOnly:true});window.L.control.scale({imperial:false,metric:true}).addTo(map.map);disposals.push(()=>map.destroy());
    const scientificGroup=window.L.layerGroup().addTo(map.map);
    map.layers.addOverlay({id:'comparison-native',name:'Comparison native scientific values',layer:scientificGroup,group:'Science',description:'Recorded native cells for this selected acquisition. Hiding the display preserves the comparison, quantity, source and exported values.'});
    const column={card,mission,acquisition,quantity,subtitle,map,legend:scale,inspector,layer:null,overlay:null,items:[],offset:0,generation:0,loadVersion:0};columns.push(column);
    map.map.on('moveend',()=>{if(syncing||stopped)return;syncing=true;for(const other of columns){if(other===column||other.map.destroyed)continue;const center=map.map.getCenter();if(other.map.map.getCenter().distanceTo(center)>.1||other.map.map.getZoom()!==map.map.getZoom())other.map.map.setView(center,map.map.getZoom(),{animate:false});}syncing=false;});
    map.map.on('click',event=>findPoint(event.latlng));
    const actions=el('div',undefined,'action-row');card.append(actions);
    const analysis=link(actions,'Atmospheric / pass analysis',`#workspace/analysis?missions=${mission.value}`),exports=link(actions,'Export',`#workspace/results?missions=${mission.value}`);
    const details=disclosure(card,'Recorded values and source details'),values=el('div'),evidence=el('pre');details.append(values,evidence);
    async function chooseAcquisition(){
      const product=column.items.find(p=>p.id===acquisition.value);if(!product)return;
      if(product.instrumentId==='hyperspectral-imager')sources??=await api('/api/science/sources');
      if(stopped||acquisition.value!==product.id)return;
      const source=sources?.find(item=>item.source.id===product.sourceId)?.source,previous=quantity.value,options=measurementsFor(product.instrumentId,source);
      quantity.replaceChildren(...options.map(([value,label])=>{const option=el('option',label);option.value=value;return option;}));
      quantity.value=options.some(([value])=>value===previous)?previous:initialMeasurement(product.instrumentId,source);
      await show();
    }
    async function show(){
      const generation=++column.generation;scientificGroup.clearLayers();column.layer=null;values.replaceChildren();
      analysis.href=`#workspace/analysis?missions=${mission.value}`;exports.href=`#workspace/results?missions=${mission.value}`;
      if(!acquisition.value){scales();return;}
      subtitle.textContent='Loading saved geographic values…';
      try{
        const layer=await api(`/api/science/products/${acquisition.value}/map-layer?measurement=${encodeURIComponent(quantity.value)}`);if(stopped||generation!==column.generation)return;
        column.layer=layer;const product=column.items.find(p=>p.id===acquisition.value);
        column.overlay=window.L.geoJSON(layer,{style:()=>({stroke:false,fillOpacity:.85}),onEachFeature:(feature,path)=>{path.feature=feature;path.on('click',event=>findPoint(event.latlng));}}).addTo(scientificGroup);
        if(layer.features.length)map.map.fitBounds(column.overlay.getBounds(),{maxZoom:17,padding:[12,12]});
        subtitle.textContent=`${readableName(product?.instrumentId,catalog)} · Flight ${local(product?.flightPacketUtc)} · Historical observation date ${local(layer.source.observationUtc)} · ${layer.source.provider} · ${layer.features.length} valid native cells · ${product?.quality||'Saved quality unavailable'}`;
        table(values,'Dataset '+String.fromCharCode(65+index)+' recorded values',['Value','Units','Flight observation','Historical observation date'],layer.features.map(feature=>[number(feature.properties.value),feature.properties.unit,local(feature.properties.flightUtc),local(feature.properties.sourceUtc)]),{onSelect:i=>{const ring=layer.features[i].geometry.coordinates[0],point={lat:ring.slice(0,-1).reduce((v,p)=>v+p[1],0)/(ring.length-1),lng:ring.slice(0,-1).reduce((v,p)=>v+p[0],0)/(ring.length-1)};findPoint(point);inspector.tabIndex=-1;inspector.focus({preventScroll:true});}});
        evidence.textContent=JSON.stringify({source:layer.source,method:layer.method,provenance:layer.provenance},null,2);scales();
      }catch(error){if(!stopped&&generation===column.generation){subtitle.textContent=`This layer is unavailable: ${error.message} Choose a supported measurement; the recorded data is unchanged.`;scales();}}
    }
    async function load(append=false){
      const ownLoad=++column.loadVersion;column.generation++;if(!mission.value){subtitle.textContent='Choose a mission with recorded science.';return;}
      analysis.href=`#workspace/analysis?missions=${mission.value}`;exports.href=`#workspace/results?missions=${mission.value}`;
      if(!append){column.items=[];column.offset=0;acquisition.replaceChildren();}
      const selectedMission=mission.value,page=await api(`/api/science/products/page?missionId=${selectedMission}&offset=${column.offset}&limit=40`);if(stopped||selectedMission!==mission.value||ownLoad!==column.loadVersion)return;
      column.items.push(...page.items);column.offset+=page.items.length;
      for(const product of page.items){const option=el('option',`${readableName(product.instrumentId,catalog)} · Flight ${local(product.flightPacketUtc)} · Source ${local(product.sourceObservationUtc)}`);option.value=product.id;acquisition.append(option);}
      more.hidden=column.offset>=page.total;
      if(!column.items.length){subtitle.textContent='No recorded geographic acquisitions. Open atmospheric/pass analysis for actual packet profiles.';column.layer=null;scientificGroup.clearLayers();scales();return;}
      if(!append)await chooseAcquisition();
    }
    const more=button(details,'More recorded acquisitions',()=>load(true));
    mission.onchange=()=>load().catch(error=>subtitle.textContent='Could not load mission science: '+error.message);acquisition.onchange=()=>chooseAcquisition().catch(error=>subtitle.textContent=error.message);quantity.onchange=show;
    button(actions,'Show actual atmospheric profile',async()=>{const profile=el('section');profile.className='comparison-profile';card.append(profile);await atmosphere(profile,mission.value,api);});
    await load();
  }
  if(!missions.length){message.textContent='No archived missions yet. Complete a flight, then return to compare its science.';link(parent,'Open Mission History','#history');return;}
  await addColumn();await addColumn();message.textContent='Two recorded datasets loaded. Inspect the same location or compare compatible measurements.';
  const extra=disclosure(parent,'Advanced — additional comparison datasets');button(extra,'Add Comparison',addColumn);
  const comparisonControls=el('div',undefined,'workspace-controls');parent.append(comparisonControls);
  const field=select(comparisonControls,'Comparison measurement',measurements,columns[0].quantity.value);
  const result=el('section',undefined,'panel comparison-result');result.setAttribute('aria-label','Scientific comparison result');parent.append(result);
  let differenceMap;disposals.push(()=>differenceMap?.destroy());
  button(comparisonControls,'Compare Recorded Values',async()=>{
    result.replaceChildren(el('p','Checking scientific compatibility and common valid coverage…'));differenceMap?.destroy();differenceMap=null;
    const selected=columns.map(column=>column.acquisition.value);if(selected.some(value=>!value))throw Error('Choose recorded geographic science in both datasets. Atmospheric profiles remain available separately.');
    const compared=await api('/api/science/workspace/compare',{products:selected,measurement:field.value});if(stopped)return;
    result.replaceChildren(el('h3',compared.comparable?'Common native-pixel comparison':'Visual comparison only'));
    for(const reason of compared.reasons)result.append(el('p',reason));
    if(compared.comparable){
      const metrics=el('div',undefined,'review-kpis');result.append(metrics);
      metric(metrics,'Common valid coverage',number(compared.matchedNativeCells)+' native cells','Matched saved native support; not total survey area');
      metric(metrics,'Mean difference',number(compared.meanDelta)+' '+compared.units.join(', '),'Compared dataset minus Dataset A');
      metric(metrics,'Difference range',`${number(compared.minimumDelta)} to ${number(compared.maximumDelta)}`,compared.units.join(', '));
      const cells=compared.differenceCells||[];
      if(cells.length){
        const holder=el('div'),key=el('section');result.append(el('h4','Difference map — actual common valid cells'),holder,key);
        differenceMap=new ResearchMap(holder,null,{coverageOnly:true});const magnitude=Math.max(Math.abs(compared.minimumDelta),Math.abs(compared.maximumDelta));
        const deltaColor=value=>{if(!magnitude)return 'rgb(237,242,247)';const t=Math.min(1,Math.abs(value)/magnitude),end=value<0?[43,140,190]:[204,76,71];return `rgb(${end.map((v,i)=>Math.round([237,242,247][i]+(v-[237,242,247][i])*t)).join(',')})`;};
        const features=cells.filter(cell=>cell.productId===selected[1]).map(cell=>({type:'Feature',geometry:{type:'Polygon',coordinates:[cell.boundary.map(p=>[p.longitude,p.latitude])]},properties:cell}));
        const overlay=window.L.geoJSON({type:'FeatureCollection',features},{style:feature=>({stroke:false,fillOpacity:.9,fillColor:deltaColor(feature.properties.delta)})}).addTo(differenceMap.map);differenceMap.layers.addOverlay({id:'comparison-difference',name:'Scientific comparison differences',layer:overlay,group:'Science',description:'Dataset B minus Dataset A on actually comparable native cells. This derived difference is not an independent observation.',setOpacity:value=>overlay.setStyle({fillOpacity:value}),opacity:.9});if(features.length)differenceMap.map.fitBounds(overlay.getBounds(),{maxZoom:17});
        key.className='scientific-legend';key.append(el('strong','Dataset B − Dataset A'),el('p',magnitude?`${number(-magnitude)} ${compared.units[0]} (blue) · 0 (white) · ${number(magnitude)} ${compared.units[0]} (red)`:`Constant difference: 0 ${compared.units[0]}. No numeric gradient.`));
        const swatch=el('div',undefined,'legend-swatch');swatch.style.background=magnitude?'linear-gradient(90deg,#2b8cbe,#edf2f7,#cc4c47)':'#edf2f7';swatch.setAttribute('aria-hidden','true');key.append(swatch);
        chart(result,'Matched recorded values: Dataset A versus Dataset B',[{label:'Common native cells',data:features.map(f=>({x:f.properties.baseline,y:f.properties.compared})),backgroundColor:'#62dec6',pointRadius:3}],{type:'scatter',xLabel:'Dataset A ('+compared.units[0]+')',yLabel:'Dataset B ('+compared.units[0]+')'});
        table(result,'Matched scientific differences',['Dataset A','Compared value','Difference','Units','Compared dataset'],cells.map(cell=>[number(cell.baseline),number(cell.compared),number(cell.delta),compared.units[0],`Dataset ${String.fromCharCode(65+selected.indexOf(cell.productId))}`]));
        if(cells.length<compared.totalDifferenceValues)result.append(el('p',`Display shows ${cells.length} of ${compared.totalDifferenceValues} valid differences. The complete numeric summary uses all common valid native cells. Display geometry crossing the date line is omitted, not filled.`));
      }
    }
    const metadata=disclosure(result,'Advanced — source dates, quality, grids and comparison evidence');metadata.append(el('pre',JSON.stringify(compared.sources,null,2)));
  },'primary');
}
