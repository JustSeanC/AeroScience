import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node as el,button,select,disclosure,numeric} from './workstation-ui.js';
import {displayReading,onUnitsChanged} from './science-display.js';

export async function terrainProfileView(parent,api,researchMap,ids,sources,disposals,preferredSource=''){
 const root=el('section',undefined,'panel terrain-profile');setPresentationText(root,localizedCopy("ui.terrainProfile.messageElevationProfile"),"aria-label");root.append(el('h2',localizedCopy("ui.terrainProfile.messageElevationProfile")));parent.append(root);
 const tools=el('div',undefined,'workspace-controls');root.append(tools);
 const path=select(tools,localizedCopy("ui.terrainProfile.messageElevationProfilePath"),[['track',localizedCopy("ui.terrainProfile.messageRecordedFlightPath")],['plan',localizedCopy("ui.terrainProfile.messageSavedMissionPlanRoute")],['line',localizedCopy("ui.terrainProfile.messageDrawnABLine")]],'track');
 const flights=await api('/api/missions?limit=100');
 if(!root.isConnected||researchMap.destroyed)return root;
 const flightChoices=flights.map(f=>[f.id,f.name]);for(const id of ids)if(!flightChoices.some(([key])=>key===id))flightChoices.push([id,textForUI("ui.terrainProfile.messageSelectedArchivedFlight")]);
 const flight=select(tools,localizedCopy("ui.terrainProfile.messageProfileFlight"),[['',localizedCopy("ui.terrainProfile.messageChooseAFlight")],...flightChoices],ids[0]||'');
 const terrain=sources.filter(s=>s.source.manifest.kind==='terrain');
 const source=select(tools,localizedCopy("ui.terrainProfile.messageProfileDEMSource"),[['',localizedCopy("ui.terrainProfile.messageNoRetainedDEMSelected")],...terrain.map(s=>[s.source.id,s.source.manifest.name+' | '+s.source.manifest.verticalDatum])],preferredSource||terrain[0]?.source.id||'');
 const planTools=el('div',undefined,'workspace-controls'),plans=await api('/api/plans?limit=100');root.append(planTools);
 if(!root.isConnected||researchMap.destroyed)return root;
 const plan=select(planTools,localizedCopy("ui.terrainProfile.messageProfileSavedPlan"),[['',localizedCopy("ui.terrainProfile.messageRecordedPlanSnapshot")],...plans.map(p=>[p.id,p.name])],'');
 const leg=select(planTools,localizedCopy("ui.terrainProfile.messageProfileRouteLeg"),[['',localizedCopy("ui.terrainProfile.messageWholeRoute")]],'');
 const segment=disclosure(root,localizedCopy("ui.terrainProfile.messageChooseARecordedFlightSegmentOptional"));
 function dateInput(label){const box=el('label',label),input=el('input');input.type='datetime-local';input.step=1;setPresentationText(input,label,"aria-label");box.append(input);segment.append(box);return input;}
 const from=dateInput(localizedCopy("ui.terrainProfile.messageProfileSegmentStartUTC")),to=dateInput(localizedCopy("ui.terrainProfile.messageProfileSegmentEndUTC"));
 const lineTools=el('div',undefined,'workspace-controls');root.append(lineTools);const inputs=[];
 for(const name of ['A latitude','A longitude','B latitude','B longitude']){const label=el('label',name),input=el('input');input.type='number';input.step='any';input.min=name.includes('latitude')?-90:-180;input.max=name.includes('latitude')?90:180;setPresentationText(input,localizedCopy("ui.terrainProfile.messageProfile",{value1:(name)}),"aria-label");label.append(input);lineTools.append(label);inputs.push(input);}
 const contextLabel=el('label',undefined,'checkbox-label'),context=el('input');context.type='checkbox';contextLabel.append(context,presentationTextNode(localizedCopy("ui.terrainProfile.messageShowMSFSTerrainSimulatedContext")));root.append(contextLabel);
 const message=el('p',localizedCopy("ui.terrainProfile.messageChooseAPathAndGenerateItsTerrain"));message.setAttribute('role','status');root.append(message);
 const summary=el('p'),chartHolder=el('div',undefined,'science-chart chart-viewport'),canvas=el('canvas');setPresentationText(canvas,localizedCopy("ui.terrainProfile.messageTerrainElevationVersusRouteDistance"),"aria-label");canvas.setAttribute('role','img');chartHolder.append(canvas);root.append(summary,chartHolder);
 const details=disclosure(root,localizedCopy("ui.terrainProfile.messageProfileSourcesGapsAndMethod")),evidence=el('div');details.append(evidence);
 const location=el('p');location.setAttribute('role','status');root.append(location);
 let chart=null,result=null,version=0,stopped=false,drawing=[],lineLayer=null,cursor=null;
 const map=researchMap.map;
 function coordinates(){return [{latitude:Number(inputs[0].value),longitude:Number(inputs[1].value)},{latitude:Number(inputs[2].value),longitude:Number(inputs[3].value)}];}
 function showLine(){lineLayer?.remove();if(inputs.every(i=>i.value!==''))lineLayer=window.L.polyline(coordinates().map(p=>[p.latitude,p.longitude]),{color:'#f5cb75',weight:3,dashArray:'6 3'}).addTo(map);}
 function highlight(point){if(!point?.position)return;const latlng=[point.position.latitude,point.position.longitude];if(!cursor)cursor=window.L.circleMarker(latlng,{radius:7,color:'#fff1b6',fillOpacity:.2}).addTo(map);else cursor.setLatLng(latlng);setPresentationText(location,localizedCopy("ui.terrainProfile.messageProfilePosition",{value1:(String(point.position.latitude.toFixed(6))),value2:(String(point.position.longitude.toFixed(6)))}));}
 const click=event=>{if(!map.terrainProfileDrawing)return;drawing.push(event.latlng);const offset=(drawing.length-1)*2;inputs[offset].value=event.latlng.lat.toFixed(7);inputs[offset+1].value=event.latlng.lng.toFixed(7);if(drawing.length===2){map.terrainProfileDrawing=false;showLine();setPresentationText(message,localizedCopy("ui.terrainProfile.messageLineSelectedChooseGenerateElevationProfile"));}};
 map.on('click',click);
 button(lineTools,localizedCopy("ui.terrainProfile.messageDrawProfileLine"),()=>{drawing=[];map.terrainProfileDrawing=true;setPresentationText(message,localizedCopy("ui.terrainProfile.messageClickPointAAndPointBOn"));});
 for(const input of inputs)input.onchange=showLine;
 function visibility(){planTools.hidden=path.value!=='plan';segment.hidden=path.value!=='track';lineTools.hidden=path.value!=='line';context.disabled=path.value!=='track';if(context.disabled)context.checked=false;map.terrainProfileDrawing=false;}
 path.onchange=visibility;visibility();
 async function legs(){const own=++version;const selected=plan.value?plans.find(p=>p.id===plan.value):flight.value?(await api(`/api/missions/${flight.value}/research`)).planSnapshot:null;if(own!==version||stopped)return;leg.replaceChildren();for(const [value,label]of [['',textForUI("ui.terrainProfile.messageWholeRoute")],...(selected?.waypoints||[]).slice(0,-1).map((p,i)=>[i,p.name+' to '+selected.waypoints[i+1].name])]){const option=el('option',label);option.value=value;leg.append(option);}}
 plan.onchange=()=>legs().catch(error=>setPresentationText(message,error.message));flight.onchange=plan.onchange;
 function download(blob,name){const a=el('a');a.href=URL.createObjectURL(blob);a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);}
 function draw(){
  chart?.destroy();chart=null;if(!result||stopped)return;
  const elevation=value=>displayReading('elevation',value,'m'),distance=value=>displayReading('distance',value,'m');
  const datum=result.source?.verticalDatum||textForUI("ui.terrainProfile.messageUnknownNoDEMReferenceInferred"),points=[];
  for(const p of result.points){if(points.length&&!p.connect)points.push({x:distance(p.distanceMetres).value,y:null});points.push({x:distance(p.distanceMetres).value,y:elevation(p.elevationMetres).value,position:p.position});}
  const sets=[{label:textForUI("ui.terrainProfile.messageDEMElevationHistoricalScientificSource"),data:points,borderColor:'#e9c574',backgroundColor:'#e9c57433',fill:true,spanGaps:false,pointRadius:2,borderWidth:2}];
  if(context.checked)sets.push({label:textForUI("ui.terrainProfile.messageMSFSTerrainSimulatedContextApproximate"),data:result.simulatorContext.map(p=>({x:distance(p.distanceMetres).value,y:elevation(p.approximateElevationMetres).value,position:p.position})),showLine:false,pointStyle:'crossRot',pointRadius:4,borderColor:'#abbce8',backgroundColor:'#abbce8',spanGaps:false,fill:false});
  chart=new window.Chart(canvas,{
   type:'line',data:{datasets:sets},
   plugins:[{id:'terrainProfileBackground',beforeDraw:c=>{c.ctx.save();c.ctx.fillStyle='#101e2b';c.ctx.fillRect(0,0,c.width,c.height);c.ctx.restore();}}],
   options:{animation:false,maintainAspectRatio:false,parsing:false,spanGaps:false,
    onHover:(_event,elements)=>{const hit=elements[0];if(hit)highlight(sets[hit.datasetIndex].data[hit.index]);},
    plugins:{legend:{labels:{color:'#c9d6e4'}},title:{display:true,color:'#c9d6e4',text:[result.source?.name||textForUI("ui.terrainProfile.messageDEMUnavailable"),textForUI("ui.terrainProfile.messageSourceObservedDerivedDisplayAnalysis",{value1:(String(result.source?.observationUtc||'Unknown'))})]},
     tooltip:{callbacks:{afterLabel:item=>item.datasetIndex===0?datum:textForUI("ui.terrainProfile.messageApproximateSimulatorMSLMinusAGLReferenceUnverified")}}},
    scales:{x:{type:'linear',min:0,max:distance(result.distanceMetres).value,ticks:{color:'#a6bbce'},grid:{color:'#284052'},title:{display:true,color:'#c9d6e4',text:textForUI("ui.terrainProfile.messageHorizontalDistance",{value1:(String(distance(0).unit))})}},
     y:{ticks:{color:'#a6bbce'},grid:{color:'#284052'},title:{display:true,color:'#c9d6e4',text:textForUI("ui.terrainProfile.messageTerrainElevation",{value1:(String(elevation(0).unit)),value2:(String(datum))})}}}
   }
  });
  setPresentationText(summary,localizedCopy("ui.terrainProfile.messageDistanceSampledMinimumMaximum",{value1:(String(numeric(distance(result.distanceMetres).value))),value2:(String(distance(0).unit)),value3:(String(numeric(elevation(result.minimumMetres).value))),value4:(String(numeric(elevation(result.maximumMetres).value))),value5:(String(numeric(elevation(result.netChangeMetres).value))),value6:(String(elevation(0).unit)),value7:(String(datum))}));
  evidence.replaceChildren(el('p',result.source?localizedCopy("ui.terrainProfile.messageObserved",{value1:(String(result.source.name)),value2:(String(result.source.provider)),value3:(String(result.source.observationUtc||'Unknown'))}):localizedCopy("ui.terrainProfile.messageNoRetainedDEMSource")),el('p',localizedCopy("ui.terrainProfile.messageDerivedSourceAnalysisNotAdditionalCollectedScience")),el('p',result.method),...result.warnings.map(w=>el('p',w)));
  if(result.source)evidence.append(el('p',result.source.citation),el('p',localizedCopy("ui.terrainProfile.messageDEMOrDSMSurfaceIdentityAndVertical")));
  setPresentationText(message,result.points.some(p=>p.elevationMetres!=null)?localizedCopy("ui.terrainProfile.messageElevationProfileReadyMissingOrUnverifiedSupport"):localizedCopy("ui.terrainProfile.messageNoValidDEMSupportOnThisPath"));
 }
 context.onchange=()=>{if(result&&(!context.checked||result.simulatorContext.length))draw();else setPresentationText(message,localizedCopy("ui.terrainProfile.messageChooseGenerateElevationProfileToIncludeRecorded"));};
 const generate=button(tools,localizedCopy("ui.terrainProfile.messageGenerateElevationProfile"),async()=>{const own=++version;result=null;chart?.destroy();chart=null;setPresentationText(summary,'');evidence.replaceChildren();setPresentationText(message,localizedCopy("ui.terrainProfile.messageReadingVerifiedRetainedTerrain"));
  try{if(path.value==='line'&&inputs.some(i=>i.value===''))throw Error(textForUI("ui.terrainProfile.messageChooseTwoGeographicPointsForTheProfile"));const value=await api('/api/science/workspace/terrain-profile',{missionId:flight.value||null,sourceId:source.value||null,path:path.value,planId:plan.value||null,leg:leg.value===''?null:Number(leg.value),line:path.value==='line'?coordinates():null,fromUtc:from.value?from.value+'Z':null,toUtc:to.value?to.value+'Z':null,simulatorContext:context.checked});if(stopped||own!==version)return;result=value;draw();}catch(error){if(own===version&&!stopped)setPresentationText(message,error.message);}
 });
 button(root,localizedCopy("ui.terrainProfile.messageDownloadProfileDisplayPNG"),()=>{if(!chart)throw Error(textForUI("ui.terrainProfile.messageGenerateAProfileFirst"));download(dataUrlBlob(chart.toBase64Image()),'AeroScience-elevation-profile-display.png');});
 button(root,localizedCopy("ui.terrainProfile.messageDownloadProfileDisplayCSV"),()=>{if(!result)throw Error(textForUI("ui.terrainProfile.messageGenerateAProfileFirst"));const unit=displayReading('elevation',0).unit,du=displayReading('distance',0,'m').unit;const rows=[[textForUI("ui.terrainProfile.messageSeries"),textForUI("ui.terrainProfile.messageDistance",{value1:(String(du))}),textForUI("ui.atmosphericResearch.messageLatitude"),textForUI("ui.atmosphericResearch.messageLongitude"),textForUI("ui.terrainProfile.messageElevation",{value1:(String(unit))}),textForUI("ui.scienceMapView.messageVerticalReference"),textForUI("ui.terrainProfile.messageRawSourceMetres"),textForUI("ui.surveyExports.messageSourceID"),textForUI("ui.surveyResearch.messageSourceObservationUTC"),textForUI("ui.terrainProfile.messageSupport")]];for(const p of result.points)rows.push([textForUI("ui.terrainProfile.messageHistoricalDEM"),displayReading('distance',p.distanceMetres,'m').value,p.position.latitude,p.position.longitude,displayReading('elevation',p.elevationMetres).value,result.source?.verticalDatum||textForUI("localization.unknownValue"),p.elevationMetres,result.sourceId,result.source?.observationUtc,p.elevationMetres==null?textForUI("localization.unavailableValue"):p.connect?textForUI("ui.terrainProfile.messageOriginalDEMValueSupportedConnection"):textForUI("ui.terrainProfile.messageOriginalDEMValuePointOnly")]);if(context.checked)for(const p of result.simulatorContext)rows.push([textForUI("ui.terrainProfile.messageSimulatedContextApproximate"),displayReading('distance',p.distanceMetres,'m').value,p.position.latitude,p.position.longitude,displayReading('elevation',p.approximateElevationMetres).value,textForUI("ui.terrainProfile.messageUnverifiedSimulatorReference"),'','',p.timestampUtc,textForUI("ui.terrainProfile.messageApproximateRecordedSimulatorContext")]);const cell=v=>'"'+String(typeof v==='string'&&/^[=+@-]/.test(v)?"'"+v:v??'').replaceAll('"','""')+'"';download(new Blob([rows.map(row=>row.map(cell).join(',')).join('\r\n')],{type:'text/csv'}),'AeroScience-elevation-profile-display.csv');});
 // Keep cursor inspection available to keyboard users as well as chart hover.
 const inspect=el('input');inspect.type='range';inspect.min=0;inspect.max=1024;inspect.value=0;setPresentationText(inspect,localizedCopy("ui.terrainProfile.messageInspectElevationProfileLocation"),"aria-label");inspect.oninput=()=>{if(result)highlight(result.points[Math.round(Number(inspect.value)/1024*(result.points.length-1))]);};root.append(inspect);
 disposals.push(onUnitsChanged(draw));disposals.push(()=>{stopped=true;version++;map.terrainProfileDrawing=false;map.off('click',click);chart?.destroy();lineLayer?.remove();cursor?.remove();});
 const clearSource=()=>{version++;result=null;chart?.destroy();chart=null;setPresentationText(summary,'');evidence.replaceChildren();cursor?.remove();cursor=null;setPresentationText(message,localizedCopy("ui.terrainProfile.messageSourceInventoryChangedGenerateANewProfile"));};
 window.addEventListener('aeroscience-sources-changed',clearSource);disposals.push(()=>window.removeEventListener('aeroscience-sources-changed',clearSource));
 await legs();return root;
}
function dataUrlBlob(url){const [header,body]=url.split(',');const bytes=Uint8Array.from(atob(body),c=>c.charCodeAt(0));return new Blob([bytes],{type:header.slice(5,header.indexOf(';'))});}
