import {node as el,button,select,disclosure,numeric} from './workstation-ui.js';
import {displayReading,onUnitsChanged} from './science-display.js';

export async function terrainProfileView(parent,api,researchMap,ids,sources,disposals,preferredSource=''){
 const root=el('section',undefined,'panel terrain-profile');root.setAttribute('aria-label','Elevation Profile');root.append(el('h2','Elevation Profile'));parent.append(root);
 const tools=el('div',undefined,'workspace-controls');root.append(tools);
 const path=select(tools,'Elevation profile path',[['track','Recorded flight path'],['plan','Saved mission-plan route'],['line','Drawn A-B line']],'track');
 const flights=await api('/api/missions?limit=100');
 if(!root.isConnected||researchMap.destroyed)return root;
 const flightChoices=flights.map(f=>[f.id,f.name]);for(const id of ids)if(!flightChoices.some(([key])=>key===id))flightChoices.push([id,'Selected archived flight']);
 const flight=select(tools,'Profile flight',[['','Choose a flight'],...flightChoices],ids[0]||'');
 const terrain=sources.filter(s=>s.source.manifest.kind==='terrain');
 const source=select(tools,'Profile DEM source',[['','No retained DEM selected'],...terrain.map(s=>[s.source.id,s.source.manifest.name+' | '+s.source.manifest.verticalDatum])],preferredSource||terrain[0]?.source.id||'');
 const planTools=el('div',undefined,'workspace-controls'),plans=await api('/api/plans?limit=100');root.append(planTools);
 if(!root.isConnected||researchMap.destroyed)return root;
 const plan=select(planTools,'Profile saved plan',[['','Recorded plan snapshot'],...plans.map(p=>[p.id,p.name])],'');
 const leg=select(planTools,'Profile route leg',[['','Whole route']],'');
 const segment=disclosure(root,'Choose a recorded flight segment (optional)');
 function dateInput(label){const box=el('label',label),input=el('input');input.type='datetime-local';input.step=1;input.setAttribute('aria-label',label);box.append(input);segment.append(box);return input;}
 const from=dateInput('Profile segment start (UTC)'),to=dateInput('Profile segment end (UTC)');
 const lineTools=el('div',undefined,'workspace-controls');root.append(lineTools);const inputs=[];
 for(const name of ['A latitude','A longitude','B latitude','B longitude']){const label=el('label',name),input=el('input');input.type='number';input.step='any';input.min=name.includes('latitude')?-90:-180;input.max=name.includes('latitude')?90:180;input.setAttribute('aria-label','Profile '+name);label.append(input);lineTools.append(label);inputs.push(input);}
 const contextLabel=el('label',undefined,'checkbox-label'),context=el('input');context.type='checkbox';contextLabel.append(context,'Show MSFS Terrain - Simulated Context');root.append(contextLabel);
 const message=el('p','Choose a path and generate its terrain profile.');message.setAttribute('role','status');root.append(message);
 const summary=el('p'),chartHolder=el('div',undefined,'science-chart chart-viewport'),canvas=el('canvas');canvas.setAttribute('aria-label','Terrain elevation versus route distance');canvas.setAttribute('role','img');chartHolder.append(canvas);root.append(summary,chartHolder);
 const details=disclosure(root,'Profile sources, gaps and method'),evidence=el('div');details.append(evidence);
 const location=el('p');location.setAttribute('role','status');root.append(location);
 let chart=null,result=null,version=0,stopped=false,drawing=[],lineLayer=null,cursor=null;
 const map=researchMap.map;
 function coordinates(){return [{latitude:Number(inputs[0].value),longitude:Number(inputs[1].value)},{latitude:Number(inputs[2].value),longitude:Number(inputs[3].value)}];}
 function showLine(){lineLayer?.remove();if(inputs.every(i=>i.value!==''))lineLayer=window.L.polyline(coordinates().map(p=>[p.latitude,p.longitude]),{color:'#f5cb75',weight:3,dashArray:'6 3'}).addTo(map);}
 function highlight(point){if(!point?.position)return;const latlng=[point.position.latitude,point.position.longitude];if(!cursor)cursor=window.L.circleMarker(latlng,{radius:7,color:'#fff1b6',fillOpacity:.2}).addTo(map);else cursor.setLatLng(latlng);location.textContent='Profile position: '+point.position.latitude.toFixed(6)+', '+point.position.longitude.toFixed(6);}
 const click=event=>{if(!map.terrainProfileDrawing)return;drawing.push(event.latlng);const offset=(drawing.length-1)*2;inputs[offset].value=event.latlng.lat.toFixed(7);inputs[offset+1].value=event.latlng.lng.toFixed(7);if(drawing.length===2){map.terrainProfileDrawing=false;showLine();message.textContent='Line selected. Choose Generate Elevation Profile.';}};
 map.on('click',click);
 button(lineTools,'Draw profile line',()=>{drawing=[];map.terrainProfileDrawing=true;message.textContent='Click point A and point B on the map. Coordinates can also be entered by keyboard.';});
 for(const input of inputs)input.onchange=showLine;
 function visibility(){planTools.hidden=path.value!=='plan';segment.hidden=path.value!=='track';lineTools.hidden=path.value!=='line';context.disabled=path.value!=='track';if(context.disabled)context.checked=false;map.terrainProfileDrawing=false;}
 path.onchange=visibility;visibility();
 async function legs(){const own=++version;const selected=plan.value?plans.find(p=>p.id===plan.value):flight.value?(await api(`/api/missions/${flight.value}/research`)).planSnapshot:null;if(own!==version||stopped)return;leg.replaceChildren();for(const [value,label]of [['','Whole route'],...(selected?.waypoints||[]).slice(0,-1).map((p,i)=>[i,p.name+' to '+selected.waypoints[i+1].name])]){const option=el('option',label);option.value=value;leg.append(option);}}
 plan.onchange=()=>legs().catch(error=>message.textContent=error.message);flight.onchange=plan.onchange;
 function download(blob,name){const a=el('a');a.href=URL.createObjectURL(blob);a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);}
 function draw(){
  chart?.destroy();chart=null;if(!result||stopped)return;
  const elevation=value=>displayReading('elevation',value,'m'),distance=value=>displayReading('distance',value,'m');
  const datum=result.source?.verticalDatum||'Unknown - no DEM reference inferred',points=[];
  for(const p of result.points){if(points.length&&!p.connect)points.push({x:distance(p.distanceMetres).value,y:null});points.push({x:distance(p.distanceMetres).value,y:elevation(p.elevationMetres).value,position:p.position});}
  const sets=[{label:'DEM Elevation - Historical Scientific Source',data:points,borderColor:'#e9c574',backgroundColor:'#e9c57433',fill:true,spanGaps:false,pointRadius:2,borderWidth:2}];
  if(context.checked)sets.push({label:'MSFS Terrain - Simulated Context (approximate)',data:result.simulatorContext.map(p=>({x:distance(p.distanceMetres).value,y:elevation(p.approximateElevationMetres).value,position:p.position})),showLine:false,pointStyle:'crossRot',pointRadius:4,borderColor:'#abbce8',backgroundColor:'#abbce8',spanGaps:false,fill:false});
  chart=new window.Chart(canvas,{
   type:'line',data:{datasets:sets},
   plugins:[{id:'terrainProfileBackground',beforeDraw:c=>{c.ctx.save();c.ctx.fillStyle='#101e2b';c.ctx.fillRect(0,0,c.width,c.height);c.ctx.restore();}}],
   options:{animation:false,maintainAspectRatio:false,parsing:false,spanGaps:false,
    onHover:(_event,elements)=>{const hit=elements[0];if(hit)highlight(sets[hit.datasetIndex].data[hit.index]);},
    plugins:{legend:{labels:{color:'#c9d6e4'}},title:{display:true,color:'#c9d6e4',text:[result.source?.name||'DEM unavailable','Source observed: '+(result.source?.observationUtc||'Unknown')+' | Derived display analysis']},
     tooltip:{callbacks:{afterLabel:item=>item.datasetIndex===0?datum:'Approximate simulator MSL minus AGL; reference unverified'}}},
    scales:{x:{type:'linear',min:0,max:distance(result.distanceMetres).value,ticks:{color:'#a6bbce'},grid:{color:'#284052'},title:{display:true,color:'#c9d6e4',text:'Horizontal distance ('+distance(0).unit+')'}},
     y:{ticks:{color:'#a6bbce'},grid:{color:'#284052'},title:{display:true,color:'#c9d6e4',text:'Terrain elevation ('+elevation(0).unit+') | '+datum}}}
   }
  });
  summary.textContent='Distance '+numeric(distance(result.distanceMetres).value)+' '+distance(0).unit+' | Sampled minimum '+numeric(elevation(result.minimumMetres).value)+' | maximum '+numeric(elevation(result.maximumMetres).value)+' | net change '+numeric(elevation(result.netChangeMetres).value)+' '+elevation(0).unit+' | '+datum;
  evidence.replaceChildren(el('p',result.source?result.source.name+' | '+result.source.provider+' | observed '+(result.source.observationUtc||'Unknown'):'No retained DEM source'),el('p','Derived source analysis, not additional collected science. Original source elevations remain in meters.'),el('p',result.method),...result.warnings.map(w=>el('p',w)));
  if(result.source)evidence.append(el('p',result.source.citation),el('p','DEM or DSM surface identity and vertical reference remain those of the selected provider. No bare-earth, surface-height or datum conversion is inferred.'));
  message.textContent=result.points.some(p=>p.elevationMetres!=null)?'Elevation profile ready. Missing or unverified support stays a gap.':'No valid DEM support on this path. Scientific elevations remain unavailable.';
 }
 context.onchange=()=>{if(result&&(!context.checked||result.simulatorContext.length))draw();else message.textContent='Choose Generate Elevation Profile to include recorded simulator context.';};
 const generate=button(tools,'Generate Elevation Profile',async()=>{const own=++version;result=null;chart?.destroy();chart=null;summary.textContent='';evidence.replaceChildren();message.textContent='Reading verified retained terrain...';
  try{if(path.value==='line'&&inputs.some(i=>i.value===''))throw Error('Choose two geographic points for the profile line.');const value=await api('/api/science/workspace/terrain-profile',{missionId:flight.value||null,sourceId:source.value||null,path:path.value,planId:plan.value||null,leg:leg.value===''?null:Number(leg.value),line:path.value==='line'?coordinates():null,fromUtc:from.value?from.value+'Z':null,toUtc:to.value?to.value+'Z':null,simulatorContext:context.checked});if(stopped||own!==version)return;result=value;draw();}catch(error){if(own===version&&!stopped)message.textContent=error.message;}
 });
 button(root,'Download profile display PNG',()=>{if(!chart)throw Error('Generate a profile first.');download(dataUrlBlob(chart.toBase64Image()),'AeroScience-elevation-profile-display.png');});
 button(root,'Download profile display CSV',()=>{if(!result)throw Error('Generate a profile first.');const unit=displayReading('elevation',0).unit,du=displayReading('distance',0,'m').unit;const rows=[['Series','Distance ('+du+')','Latitude','Longitude','Elevation ('+unit+')','Vertical reference','Raw source metres','Source ID','Source observation UTC','Support']];for(const p of result.points)rows.push(['Historical DEM',displayReading('distance',p.distanceMetres,'m').value,p.position.latitude,p.position.longitude,displayReading('elevation',p.elevationMetres).value,result.source?.verticalDatum||'Unknown',p.elevationMetres,result.sourceId,result.source?.observationUtc,p.elevationMetres==null?'Unavailable':p.connect?'Original DEM value - supported connection':'Original DEM value - point only']);if(context.checked)for(const p of result.simulatorContext)rows.push(['Simulated context - approximate',displayReading('distance',p.distanceMetres,'m').value,p.position.latitude,p.position.longitude,displayReading('elevation',p.approximateElevationMetres).value,'Unverified simulator reference','','',p.timestampUtc,'Approximate recorded simulator context']);const cell=v=>'"'+String(typeof v==='string'&&/^[=+@-]/.test(v)?"'"+v:v??'').replaceAll('"','""')+'"';download(new Blob([rows.map(row=>row.map(cell).join(',')).join('\r\n')],{type:'text/csv'}),'AeroScience-elevation-profile-display.csv');});
 // Keep cursor inspection available to keyboard users as well as chart hover.
 const inspect=el('input');inspect.type='range';inspect.min=0;inspect.max=1024;inspect.value=0;inspect.setAttribute('aria-label','Inspect elevation profile location');inspect.oninput=()=>{if(result)highlight(result.points[Math.round(Number(inspect.value)/1024*(result.points.length-1))]);};root.append(inspect);
 disposals.push(onUnitsChanged(draw));disposals.push(()=>{stopped=true;version++;map.terrainProfileDrawing=false;map.off('click',click);chart?.destroy();lineLayer?.remove();cursor?.remove();});
 const clearSource=()=>{version++;result=null;chart?.destroy();chart=null;summary.textContent='';evidence.replaceChildren();cursor?.remove();cursor=null;message.textContent='Source inventory changed. Generate a new profile to verify current original availability.';};
 window.addEventListener('aeroscience-sources-changed',clearSource);disposals.push(()=>window.removeEventListener('aeroscience-sources-changed',clearSource));
 await legs();return root;
}
function dataUrlBlob(url){const [header,body]=url.split(',');const bytes=Uint8Array.from(atob(body),c=>c.charCodeAt(0));return new Blob([bytes],{type:header.slice(5,header.indexOf(';'))});}
