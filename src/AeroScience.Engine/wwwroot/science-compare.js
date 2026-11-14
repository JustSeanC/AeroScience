import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {ResearchMap} from './map.js';
import {atmosphere} from './mission-workspace.js';
import {measurements,initialMeasurement,layerSignature,measurementsFor} from './science-map.js';
import {node as el,numeric as number,dateTime as local,select,button,link,status,legend,color,table,disclosure,metric,pointInCell,readableName,chart} from './workstation-ui.js';
import {displayReading,onUnitsChanged} from './science-display.js';
import {renderLayerContext,comparisonContext} from './scientific-context.js';

export async function comparison(parent,api,missions,ids,disposals,catalog){
  parent.append(el('h2',localizedCopy("ui.scienceCompare.messageCompareScientificResults")),el('p',localizedCopy("ui.scienceCompare.messageChooseTwoRecordedDatasetsSourceDatesStay")));
  const tools=el('div',undefined,'workspace-controls'),message=status(parent,localizedCopy("ui.scienceCompare.messageLoadingSavedComparisonDatasets"));parent.append(tools);
  const commonLabel=el('label',undefined,'checkbox-label'),common=el('input');common.type='checkbox';setPresentationText(common,localizedCopy("ui.scienceCompare.messageUseSharedNumericColorScale"),"aria-label");commonLabel.append(common,presentationTextNode(localizedCopy("ui.scienceCompare.messageUseSharedNumericColorScale")));tools.append(commonLabel);
  const scaleStatus=el('p',undefined,'scale-state');tools.append(scaleStatus);
  const compatibility=el('p',undefined,'scientific-comparison-context');compatibility.setAttribute('role','note');setPresentationText(compatibility,localizedCopy("ui.scienceCompare.messageScientificComparisonLimitations"),"aria-label");tools.append(compatibility);
  const grid=el('div',undefined,'comparison-grid');parent.append(grid);
  const reading=(measurement,unit,value)=>measurement==='elevation'&&unit==='m'?displayReading('elevation',value,'m'):{value,unit};
  const columns=[];let syncing=false,stopped=false,sources,lastPoint;disposals.push(()=>stopped=true);
  const findPoint=point=>{lastPoint=point;
    for(const column of columns){
      const feature=column.layer?.features.find(feature=>pointInCell(point,feature));
      const p=feature?.properties;column.inspector.replaceChildren(el('h4',localizedCopy("ui.scienceCompare.messageSameLocation",{value1:(String(number(point.lat,5))),value2:(String(number(point.lng,5)))})),el('p',p?localizedCopy("ui.scienceCompare.messageFlightHistoricalObservationDate",{value1:(String(number(reading(column.quantity.value,p.unit,p.value).value))),value2:(String(reading(column.quantity.value,p.unit,p.value).unit)),value3:(String(local(p.flightUtc))),value4:(String(local(p.sourceUtc)))}):localizedCopy("ui.scienceCompare.messageNoValidRecordedValueAtThisLocation")));
    }
  };
  function scales(){
    const active=columns.filter(column=>column.layer?.features.length);
    setPresentationText(compatibility,comparisonContext(columns.filter(column=>column.layer).map(column=>({...column.layer,measurement:column.quantity.value})))+(columns.some(column=>column.layer&&!column.layer.features.length)?localizedCopy("ui.scienceCompare.messageASelectedDatasetHasNoValid"):''));
    const compatible=active.length>=2&&new Set(active.map(column=>layerSignature(column.layer)+'|'+column.quantity.value)).size===1;
    if(common.checked&&!compatible)common.checked=false;
    common.disabled=!compatible;
    const ranges=active.flatMap(column=>[column.layer.minimum,column.layer.maximum]).filter(Number.isFinite);
    const low=Math.min(...ranges),high=Math.max(...ranges);
    setPresentationText(scaleStatus,common.checked?localizedCopy("ui.scienceCompare.messageSharedNumericScaleTheSameColor"):compatible?localizedCopy("ui.scienceCompare.messageIndependentScalesSelectSharedScaleForEquivalent"):localizedCopy("ui.scienceCompare.messageIndependentScalesQuantitiesUnitsWavelengthsOr"));
    for(const column of active){
      const min=common.checked?low:column.layer.minimum,max=common.checked?high:column.layer.maximum;
      column.overlay.eachLayer(path=>{const p=path.feature.properties;path.setStyle({fillColor:p.rgb?`rgb(${p.rgb.map(value=>Math.round(255*Math.max(0,Math.min(1,value/.3)))).join(',')})`:color(p.value,min,max)});});
      if(['natural-color','false-color'].includes(column.quantity.value))column.legend.replaceChildren(el('strong',localizedCopy("ui.scienceCompare.messageRGBReflectance")),el('p',localizedCopy("ui.scienceCompare.message003ReflectanceDisplayStretchNoScalarSubtraction")));
      else legend(column.legend,localizedCopy("ui.scienceCompare.messageScale",{value1:(String(common.checked?'Shared':'Independent'))}),reading(column.quantity.value,column.layer.unit,min).value,reading(column.quantity.value,column.layer.unit,max).value,reading(column.quantity.value,column.layer.unit,0).unit,column.layer.wavelengthNm?`${column.layer.wavelengthNm} nm`:'Transparent missing data | '+(column.layer.source.verticalDatum||'Unknown vertical reference'));
    }
  }
  common.onchange=scales;
  async function addColumn(){
    const index=columns.length;if(index>=8)return;
    const card=el('section',undefined,'comparison-card panel');setPresentationText(card,localizedCopy("ui.scienceCompare.messageDataset",{value1:(String(String.fromCharCode(65+index)))}),"aria-label");grid.append(card);card.append(el('h3',localizedCopy("ui.scienceCompare.messageDataset",{value1:(String(String.fromCharCode(65+index)))})));
    const mission=select(card,localizedCopy("ui.scienceCompare.messageComparisonMission",{value1:(String(index+1))}),missions.map(m=>[m.id,m.name]),ids[index]||missions[index]?.id||missions[0]?.id);
    const acquisition=select(card,localizedCopy("ui.scienceCompare.messageAcquisition",{value1:(String(index+1))}),[['',localizedCopy("ui.scienceCompare.messageChooseRecordedScience")]]);
    const quantity=select(card,localizedCopy("ui.scienceCompare.messageVisualMeasurement",{value1:(String(index+1))}),measurements,'elevation');
    const subtitle=el('p',undefined,'comparison-source'),holder=el('div'),scale=el('section'),inspector=el('section');inspector.className='sample-inspector';setPresentationText(inspector,localizedCopy("ui.scienceCompare.messageDatasetSelectedValue",{value1:(String(String.fromCharCode(65+index)))}),"aria-label");card.append(subtitle,holder,scale,inspector);
    const map=new ResearchMap(holder,null,{coverageOnly:true});window.L.control.scale({imperial:false,metric:true}).addTo(map.map);disposals.push(()=>map.destroy());
    const scientificGroup=window.L.layerGroup().addTo(map.map);
    map.layers.addOverlay({id:'comparison-native',name:'Comparison native scientific values',layer:scientificGroup,group:"Science",description:textForUI("ui.scienceCompare.messageRecordedNativeCellsForThisSelectedAcquisition"),describe:target=>{if(column.layer)renderLayerContext(target,{source:column.layer.source,context:column.layer.context,measurement:quantity.value,unit:column.layer.unit,method:column.layer.method});else target.append(el('p',localizedCopy("ui.scienceCompare.messageNoSupportedSourceLayerSelected")));}});
    const column={card,mission,acquisition,quantity,subtitle,map,legend:scale,inspector,layer:null,overlay:null,items:[],offset:0,generation:0,loadVersion:0};columns.push(column);
    map.map.on('moveend',()=>{if(syncing||stopped)return;syncing=true;for(const other of columns){if(other===column||other.map.destroyed)continue;const center=map.map.getCenter();if(other.map.map.getCenter().distanceTo(center)>.1||other.map.map.getZoom()!==map.map.getZoom())other.map.map.setView(center,map.map.getZoom(),{animate:false});}syncing=false;});
    map.map.on('click',event=>findPoint(event.latlng));
    const actions=el('div',undefined,'action-row');card.append(actions);
    const analysis=link(actions,localizedCopy("ui.scienceCompare.messageAtmosphericPassAnalysis"),`#workspace/analysis?missions=${mission.value}`),exports=link(actions,localizedCopy("ui.scienceAnalysis.messageExport"),`#workspace/results?missions=${mission.value}`);
    const details=disclosure(card,localizedCopy("ui.scienceCompare.messageRecordedValuesAndSourceDetails")),values=el('div'),evidence=el('pre');details.append(values,evidence);
    async function chooseAcquisition(){
      const product=column.items.find(p=>p.id===acquisition.value);if(!product)return;
      if(product.instrumentId==='hyperspectral-imager'){sources??=[];if(!sources.some(item=>item.source.id===product.sourceId)){const data=await api('/api/science/products/'+product.id);sources.push({source:data.source});}}
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
      setPresentationText(subtitle,localizedCopy("ui.scienceCompare.messageLoadingSavedGeographicValues"));
      try{
        const layer=await api(`/api/science/products/${acquisition.value}/map-layer?measurement=${encodeURIComponent(quantity.value)}`);if(stopped||generation!==column.generation)return;
        column.layer=layer;const product=column.items.find(p=>p.id===acquisition.value);
        column.overlay=window.L.geoJSON(layer,{style:()=>({stroke:false,fillOpacity:.85}),onEachFeature:(feature,path)=>{path.feature=feature;path.on('click',event=>findPoint(event.latlng));}}).addTo(scientificGroup);
        if(layer.features.length)map.map.fitBounds(column.overlay.getBounds(),{maxZoom:17,padding:[12,12]});
        setPresentationText(subtitle,localizedCopy("ui.scienceCompare.messageFlightHistoricalObservationDate6f26bc52",{value1:(String(readableName(product?.instrumentId,catalog))),value2:(String(local(product?.flightPacketUtc))),value3:(String(local(layer.source.observationUtc))),value4:(String(layer.source.provider)),value5:(String(layer.features.length)),value6:(String(product?.quality||'Saved quality unavailable'))}));
        column.renderValues=()=>{values.replaceChildren();if(column.layer!==layer)return;table(values,localizedCopy("ui.scienceCompare.messageDatasetRecordedValues",{value1:(String(String.fromCharCode(65+index)))}),[localizedCopy("ui.scienceAnalysis.messageValue"),localizedCopy("ui.campaignProgressUi.messageUnits"),localizedCopy("ui.scienceCompare.messageFlightObservation"),localizedCopy("ui.scienceCompare.messageHistoricalObservationDate")],layer.features.map(feature=>[number(reading(quantity.value,feature.properties.unit,feature.properties.value).value),reading(quantity.value,feature.properties.unit,feature.properties.value).unit,local(feature.properties.flightUtc),local(feature.properties.sourceUtc)]),{onSelect:i=>{const ring=layer.features[i].geometry.coordinates[0],point={lat:ring.slice(0,-1).reduce((v,p)=>v+p[1],0)/(ring.length-1),lng:ring.slice(0,-1).reduce((v,p)=>v+p[0],0)/(ring.length-1)};findPoint(point);inspector.tabIndex=-1;inspector.focus({preventScroll:true});}});};column.renderValues();
        setPresentationText(evidence,JSON.stringify({source:layer.source,method:layer.method,provenance:layer.provenance},null,2));scales();
      }catch(error){if(!stopped&&generation===column.generation){setPresentationText(subtitle,localizedCopy("ui.scienceCompare.messageThisLayerIsUnavailableChooseASupported",{value1:(String(error.message))}));scales();}}
    }
    async function load(append=false){
      const ownLoad=++column.loadVersion;column.generation++;if(!mission.value){setPresentationText(subtitle,localizedCopy("ui.scienceCompare.messageChooseAMissionWithRecordedScience"));return;}
      analysis.href=`#workspace/analysis?missions=${mission.value}`;exports.href=`#workspace/results?missions=${mission.value}`;
      if(!append){column.items=[];column.offset=0;acquisition.replaceChildren();}
      const selectedMission=mission.value,page=await api(`/api/science/products/page?missionId=${selectedMission}&offset=${column.offset}&limit=40`);if(stopped||selectedMission!==mission.value||ownLoad!==column.loadVersion)return;
      column.items.push(...page.items);column.offset+=page.items.length;
      for(const product of page.items){const option=el('option',localizedCopy("ui.scienceCompare.messageFlightSource",{value1:(String(readableName(product.instrumentId,catalog))),value2:(String(local(product.flightPacketUtc))),value3:(String(local(product.sourceObservationUtc)))}));option.value=product.id;acquisition.append(option);}
      more.hidden=column.offset>=page.total;
      if(!column.items.length){setPresentationText(subtitle,localizedCopy("ui.scienceCompare.messageNoRecordedGeographicAcquisitionsOpenAtmosphericPassAnalysis"));column.layer=null;scientificGroup.clearLayers();scales();return;}
      if(!append)await chooseAcquisition();
    }
    const more=button(details,localizedCopy("ui.scienceCompare.messageMoreRecordedAcquisitions"),()=>load(true));
    mission.onchange=()=>load().catch(error=>setPresentationText(subtitle,localizedCopy("ui.scienceCompare.messageCouldNotLoadMissionScience",{value1:(String(error.message))})));acquisition.onchange=()=>chooseAcquisition().catch(error=>setPresentationText(subtitle,error.message));quantity.onchange=show;
    button(actions,localizedCopy("ui.scienceCompare.messageShowActualAtmosphericProfile"),async()=>{const profile=el('section');profile.className='comparison-profile';card.append(profile);await atmosphere(profile,mission.value,api);});
    await load();
  }
  if(!missions.length){setPresentationText(message,localizedCopy("ui.scienceCompare.messageNoArchivedMissionsYetCompleteAFlight"));link(parent,localizedCopy("ui.scienceCompare.messageOpenMissionHistory"),'#history');return;}
  await addColumn();await addColumn();setPresentationText(message,localizedCopy("ui.scienceCompare.messageTwoRecordedDatasetsLoadedInspectTheSame"));
  const extra=disclosure(parent,localizedCopy("ui.scienceCompare.messageAdvancedAdditionalComparisonDatasets"));button(extra,localizedCopy("ui.scienceCompare.messageAddComparison"),addColumn);
  const comparisonControls=el('div',undefined,'workspace-controls');parent.append(comparisonControls);
  const field=select(comparisonControls,localizedCopy("ui.scienceCompare.messageComparisonMeasurement"),measurements,columns[0].quantity.value);
  const result=el('section',undefined,'panel comparison-result');setPresentationText(result,localizedCopy("ui.scienceCompare.messageScientificComparisonResult"),"aria-label");parent.append(result);
  let differenceMap,lastComparison,comparisonVersion=0;disposals.push(()=>differenceMap?.destroy());
  button(comparisonControls,localizedCopy("ui.scienceCompare.messageCompareRecordedValues"),async()=>{
    const own=++comparisonVersion;lastComparison=null;result.replaceChildren(el('p',localizedCopy("ui.scienceCompare.messageCheckingScientificCompatibilityAndCommonValidCoverage")));differenceMap?.destroy();differenceMap=null;
    const selected=columns.map(column=>column.acquisition.value);if(selected.some(value=>!value))throw Error(textForUI("ui.scienceCompare.messageChooseRecordedGeographicScienceInBothDatasets"));
    const measurement=field.value,compared=await api('/api/science/workspace/compare',{products:selected,measurement});if(stopped||own!==comparisonVersion)return;lastComparison={compared,selected,measurement};renderComparison();
  },'primary');
  function renderComparison(){
    if(!lastComparison||stopped)return;differenceMap?.destroy();differenceMap=null;
    const {compared,selected,measurement}=lastComparison,shown=value=>reading(measurement,compared.units[0],value),unit=shown(0).unit;
    result.replaceChildren(el('h3',compared.comparable?localizedCopy("ui.scienceCompare.messageCommonNativePixelComparison"):localizedCopy("ui.scienceCompare.messageVisualComparisonOnly")));
    for(const reason of compared.reasons)result.append(el('p',reason));
    if(compared.comparable){
      const metrics=el('div',undefined,'review-kpis');result.append(metrics);
      metric(metrics,localizedCopy("ui.scienceCompare.messageCommonValidCoverage"),localizedCopy("ui.scienceCompare.messageNativeCells",{value1:(String(number(compared.matchedNativeCells)))}),localizedCopy("ui.scienceCompare.messageMatchedSavedNativeSupportNotTotalSurvey"));
      metric(metrics,localizedCopy("ui.scienceCompare.messageMeanDifference"),number(shown(compared.meanDelta).value)+' '+unit,localizedCopy("ui.scienceCompare.messageComparedDatasetMinusDatasetA"));
      metric(metrics,localizedCopy("ui.scienceCompare.messageDifferenceRange"),localizedCopy("ui.scienceCompare.messageTo",{value1:(String(number(shown(compared.minimumDelta).value))),value2:(String(number(shown(compared.maximumDelta).value)))}),unit);
      const cells=compared.differenceCells||[];
      if(cells.length){
        const holder=el('div'),key=el('section');result.append(el('h4',localizedCopy("ui.scienceCompare.messageDifferenceMapActualCommonValidCells")),holder,key);
        differenceMap=new ResearchMap(holder,null,{coverageOnly:true});const magnitude=Math.max(Math.abs(compared.minimumDelta),Math.abs(compared.maximumDelta));
        const deltaColor=value=>{if(!magnitude)return 'rgb(237,242,247)';const t=Math.min(1,Math.abs(value)/magnitude),end=value<0?[43,140,190]:[204,76,71];return `rgb(${end.map((v,i)=>Math.round([237,242,247][i]+(v-[237,242,247][i])*t)).join(',')})`;};
        const features=cells.filter(cell=>cell.productId===selected[1]).map(cell=>({type:'Feature',geometry:{type:'Polygon',coordinates:[cell.boundary.map(p=>[p.longitude,p.latitude])]},properties:cell}));
        const overlay=window.L.geoJSON({type:'FeatureCollection',features},{style:feature=>({stroke:false,fillOpacity:.9,fillColor:deltaColor(feature.properties.delta)})}).addTo(differenceMap.map);differenceMap.layers.addOverlay({id:'comparison-difference',name:'Scientific comparison differences',layer:overlay,group:"Science",description:textForUI("ui.scienceCompare.messageDatasetBMinusDatasetAOnActually"),setOpacity:value=>overlay.setStyle({fillOpacity:value}),opacity:.9});if(features.length)differenceMap.map.fitBounds(overlay.getBounds(),{maxZoom:17});
        key.className='scientific-legend';key.append(el('strong',localizedCopy("ui.scienceCompare.messageDatasetBDatasetA")),el('p',magnitude?localizedCopy("ui.scienceCompare.messageBlue0WhiteRed",{value1:(String(number(shown(-magnitude).value))),value2:(String(unit)),value3:(String(number(shown(magnitude).value))),value4:(String(unit))}):localizedCopy("ui.scienceCompare.messageConstantDifference0NoNumericGradient",{value1:(String(unit))})));
        const swatch=el('div',undefined,'legend-swatch');swatch.style.background=magnitude?'linear-gradient(90deg,#2b8cbe,#edf2f7,#cc4c47)':'#edf2f7';swatch.setAttribute('aria-hidden','true');key.append(swatch);
        chart(result,localizedCopy("ui.scienceCompare.messageMatchedRecordedValuesDatasetAVersusDataset"),[{label:textForUI("ui.scienceCompare.messageCommonNativeCells"),data:features.map(f=>({x:shown(f.properties.baseline).value,y:shown(f.properties.compared).value})),backgroundColor:'#62dec6',pointRadius:3}],{type:'scatter',xLabel:localizedCopy("ui.scienceCompare.messageDatasetA404484ef",{value1:(String(unit))}),yLabel:localizedCopy("ui.scienceCompare.messageDatasetB",{value1:(String(unit))})});
        table(result,localizedCopy("ui.scienceCompare.messageMatchedScientificDifferences"),[localizedCopy("ui.scienceCompare.messageDatasetA"),localizedCopy("ui.scienceCompare.messageComparedValue"),localizedCopy("ui.scienceCompare.messageDifference"),localizedCopy("ui.campaignProgressUi.messageUnits"),localizedCopy("ui.scienceCompare.messageComparedDataset")],cells.map(cell=>[number(shown(cell.baseline).value),number(shown(cell.compared).value),number(shown(cell.delta).value),unit,textForUI("ui.scienceCompare.messageDataset",{value1:(String(String.fromCharCode(65+selected.indexOf(cell.productId))))})]));
        if(cells.length<compared.totalDifferenceValues)result.append(el('p',localizedCopy("ui.scienceCompare.messageDisplayShowsOfValidDifferencesTheComplete",{value1:(String(cells.length)),value2:(String(compared.totalDifferenceValues))})));
      }
    }
    const metadata=disclosure(result,localizedCopy("ui.scienceCompare.messageAdvancedSourceDatesQualityGridsAnd"));metadata.append(el('pre',JSON.stringify(compared.sources,null,2)));
  }
  disposals.push(onUnitsChanged(()=>{if(stopped)return;scales();for(const column of columns)column.renderValues?.();if(lastPoint)findPoint(lastPoint);renderComparison();}));
  const invalidate=()=>{comparisonVersion++;lastPoint=null;lastComparison=null;differenceMap?.destroy();differenceMap=null;for(const column of columns){column.generation++;column.loadVersion++;column.layer=null;column.overlay?.clearLayers();column.renderValues?.();column.inspector.replaceChildren();column.legend.replaceChildren();}result.replaceChildren(el('p',localizedCopy("ui.scienceCompare.messageSourceInventoryChangedCompareCurrentSavedAcquisitions")));};
  window.addEventListener('aeroscience-sources-changed',invalidate);disposals.push(()=>window.removeEventListener('aeroscience-sources-changed',invalidate));
}
