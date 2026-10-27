import {organizationAssignmentSummary} from './organization-assignment-ui.js';
import {node,button,table,chart,numeric,link,disclosure,dateTime,color} from './workstation-ui.js';
import {ResearchMap} from './map.js';

export const collectionOutcome=outcome=>({collectionComplete:'Completed collection — sufficient evidence',partialCollection:'Partial coverage',insufficientEvidence:'Insufficient or unavailable evidence'}[outcome]||'Unknown collection outcome');
export function atmosphericSummary(attempt){
 const root=node('article',undefined,'panel research-panel'),s=attempt.atmosphericProfile;
 root.append(node('h3',`${attempt.card.name} — ${collectionOutcome(attempt.outcome)}`),node('p',attempt.explanation));
 if(attempt.card.atmosphericTransect)root.append(node('p','Research question: '+attempt.card.atmosphericTransect.question));
 const temperature=s.quantities.find(q=>q.measurementId==='temperature');if(temperature?.validReadings)root.append(node('p',`Recorded air temperatures ranged from ${numeric(temperature.minimum)} to ${numeric(temperature.maximum)} degC along the sampled aircraft path. ${s.transect?'Sampled locations do not establish the conditions between them or in surrounding areas.':'Band values below show where readings were actually retained; they do not establish conditions between sampled altitudes.'}`));
 if(s.transect){const t=s.transect,r=attempt.card.atmosphericTransect;root.append(node('p',`${t.coveredStations} covered sampling locations (${r.requiredStations} required) · ${numeric(t.linkedDistanceMetres)} m supported path · greatest leg extent ${numeric(t.greatestLegExtentMetres)} m (${numeric(r.minimumExtentMetres)} m required) · ${t.totalLegs} supported legs. Both goals must be met in one leg; revisits do not add distinct locations.`));}
 root.append(node('p',`${s.validTemperatureReadings} valid temperature readings · sampled altitude ${s.minimumAltitudeFeetMsl==null?'Unavailable':numeric(s.minimumAltitudeFeetMsl)+'–'+numeric(s.maximumAltitudeFeetMsl)+' feet MSL'} · ${s.majorGaps} major gaps / pause edges · revision ${attempt.card.revision}`),node('p',`Source: ${attempt.evidence.source==='simulator'?'Recorded simulator observations':attempt.evidence.source==='simulated'?'Synthetic/demo observations — not an installed MSFS test':'Unknown; no simulator provenance inferred'}. ${dateTime(attempt.startUtc)} → ${dateTime(attempt.endUtc)} (local display; original evidence UTC retained).`));
 root.append(organizationAssignmentSummary(attempt.card,attempt));
 const reference=disclosure(root,'Scientific Evidence References');reference.append(node('p',`Evaluator ${attempt.evaluatorVersion} · Library ${s.libraryId} · Original observation/event SHA-256 ${attempt.evidence.sha256} · Frozen plan SHA-256 ${attempt.evidence.planSha256}`));
 return root;
}
const exact=value=>Number.isFinite(value)?String(value):'Unavailable';
const quantityNames={temperature:'Air temperature',pressure:'Ambient pressure','wind-speed':'Wind speed','wind-direction':'Wind direction (true)',density:'Air density'};
function profiles(parent,entries,inspect){
 const selection=node('label','Atmospheric measurement'),select=node('select');select.setAttribute('aria-label','Atmospheric measurement');selection.append(select);parent.append(selection);const visual=node('div');parent.append(visual);
 for(const [id,name]of Object.entries(quantityNames)){const option=node('option',name);option.value=id;select.append(option);}
 function draw(){visual.replaceChildren();const measurement=select.value;
  const originals=entries.filter(e=>e.review?.matchesSavedEvidence),unit=originals.flatMap(e=>e.review.preview).find(o=>o.measurementId===measurement)?.unit;
  if(!unit){visual.append(node('p','No verified retained observations for this quantity. No values or units were invented.'));return;}
  const transect=originals.some(e=>e.result.card.atmosphericTransect);
  chart(visual,`${quantityNames[measurement]} versus ${transect?'Sampled Distance':'Altitude'}`,originals.map((e,i)=>{const points=new Map((e.review.transectPoints||[]).map(p=>[p.observationId,p]));return {label:`${e.label} · ${dateTime(e.result.startUtc)}`,showLine:false,pointRadius:3,borderColor:['#62dec6','#7fa4ed','#f4c36b','#e79fda'][i%4],data:e.review.preview.filter(o=>o.measurementId===measurement&&o.unit===unit&&(!transect||points.get(o.id)?.distanceMetres!=null)).map(o=>({x:transect?points.get(o.id).distanceMetres:o.altitudeMslFeet,y:o.value}))};}),{type:'scatter',xLabel:transect?'Distance along supported sampled links (m); gaps excluded':'Recorded aircraft altitude (feet MSL)',yLabel:`${quantityNames[measurement]} (${unit})`});
  visual.append(node('p',transect?'Points are actual original measurements. Distance excludes unmeasured gaps. Optional readings require a matching original temperature position/time/session to appear along distance; they remain inspectable in the table. Altitude, elapsed time and changing weather can confound geographic comparisons.':'Each point is an actual science observation at the aircraft. No lines, missing-altitude interpolation or regional atmosphere are inferred. Bounded chart previews do not replace the full observation archive.'));
  if(transect)transectMap(visual,originals,measurement,unit,inspect);
 }select.onchange=draw;draw();
}
export async function showAtmosphericResult(parent,api,attempt,isCurrent=()=>true){
 const root=node('section',undefined,'panel research-panel');root.setAttribute('aria-label',attempt.card.atmosphericTransect?'Atmospheric transect report':'Atmospheric research profile');parent.replaceChildren(root);root.append(atmosphericSummary(attempt));
 const status=node('p','Verifying retained scientific observations…');status.setAttribute('role','status');root.append(status);
 const timeline=node('div');let inspection=0;
 const inspect=async at=>{const own=++inspection;timeline.replaceChildren();const {researchFlightTimeline}=await import('./personal-research-flight.js');if(isCurrent()&&root.isConnected&&own===inspection){await researchFlightTimeline(timeline,api,attempt.flightId,{at});if(own===inspection)timeline.scrollIntoView({block:'nearest'});}};
 try{const {review}=await api(`/api/research/attempts/${attempt.id}/atmospheric`);if(!root.isConnected||!isCurrent())return;status.textContent=review.message;
  if(review.matchesSavedEvidence){
   profiles(root,[{result:attempt,review,label:attempt.card.name}],(_,at)=>inspect(at));
   if(review.retained.transect){const t=review.retained.transect;
    table(root,'Geographic sampling coverage',['Location','Anchor observation','Valid temperature readings','Longest consecutive run','Coverage'],t.stations.map(s=>[s.index+1,s.anchorObservationId,s.temperatureReadings,s.longestContinuousReadings,s.covered?'Covered':'Needs more consecutive readings']),{pageSize:20});
    table(root,'Supported sampling legs',['Leg','Temperature readings','Covered locations','Supported distance (m)','Extent from first sample (m)'],t.legs.map(l=>[l.id,l.temperatureReadings,l.coveredStations,exact(l.linkedDistanceMetres),exact(l.extentFromFirstSampleMetres)]),{pageSize:20});
    root.append(node('p',t.detailsTruncated?'Coverage totals include all supported samples; location/leg details are a bounded preview. No omitted location is inferred to be covered.':'Location and leg counts describe sampled points, not measured surrounding areas. Missing geographic goals and gaps remain incomplete.'));
   }else table(root,'Altitude sampling coverage',['MSL band (feet)','Valid temperature readings','Longest consecutive run','Coverage','Recorded temperature range (degC)'],review.retained.bands.map(b=>[`${numeric(b.lowerFeetMsl)}–${numeric(b.upperFeetMsl)} (upper bound excluded)`,b.temperatureReadings,b.longestContinuousReadings,b.covered?'Covered':b.temperatureReadings?'Partial':'Missing',b.minimumTemperatureCelsius==null?'Unavailable':exact(b.minimumTemperatureCelsius)+'–'+exact(b.maximumTemperatureCelsius)]));
   table(root,'Available atmospheric quantities',['Quantity','Original units','Valid readings','Excluded','Recorded minimum','Recorded maximum'],review.retained.quantities.map(q=>[quantityNames[q.measurementId],q.unit,q.validReadings,q.excludedReadings,exact(q.minimum),exact(q.maximum)]));
   const geographic=!!review.retained.transect;
   table(root,'Original atmospheric observation preview',['Original UTC',...(geographic?['Latitude','Longitude']:[]),'Altitude (feet MSL)','Quantity','Original value','Original units','Quality','Source','Timeline'],review.preview.map(o=>{const action=node('div');button(action,'Inspect Reading',()=>inspect(o.timestampUtc));return[o.timestampUtc,...(geographic?[exact(o.latitude),exact(o.longitude)]:[]),numeric(o.altitudeMslFeet),quantityNames[o.measurementId],exact(o.value),o.unit,o.quality,o.source,action];}),{pageSize:20});
   root.append(node('p',review.downsampled?'This is a bounded preview of retained originals. Use Science Results & Analysis for all observations.':'Preview contains the verified compatible readings in this interval.'));
  }
  const limits=disclosure(root,'Interpretation and Sampling Limits');for(const text of review.retained.limitations)limits.append(node('p',text));
 }catch(error){if(isCurrent()&&root.isConnected)status.textContent='Original scientific evidence is unavailable: '+error.message+'. A saved summary is historical; no verified profile is plotted.';}
 link(root,'Open Scientific Results',`#workspace/results?missions=${attempt.flightId}`);link(root,'Open Recorded Flight Replay',`#workspace/replay?missions=${attempt.flightId}`);
 root.append(timeline);button(root,'Show Flight & Science Timeline',()=>inspect());
}
export function showAtmosphericComparison(parent,entries){
 const transect=entries.some(e=>e.attempt.card.atmosphericTransect);parent.append(node('h3',transect?'Atmospheric Transects — Like-for-Like Comparison':'Atmospheric Profiles — Like-for-Like Comparison'));
 table(parent,'Atmospheric collection comparison',['Flight / UTC interval','Collection outcome','Sampled altitude (feet MSL)','Valid temperature readings','Retained evidence'],entries.map(e=>{const s=e.attempt.result?.atmosphericProfile;return[e.attempt.flightName+' · '+e.attempt.startUtc,e.attempt.result?collectionOutcome(e.attempt.result.outcome):'Awaiting evaluation',s?.minimumAltitudeFeetMsl==null?'Unavailable':numeric(s.minimumAltitudeFeetMsl)+'–'+numeric(s.maximumAltitudeFeetMsl),s?.validTemperatureReadings??'Unknown',e.atmosphere?.matchesSavedEvidence?'Original hashes match':'Unavailable or mismatched — not plotted'];}));
 profiles(parent,entries.filter(e=>e.attempt.result).map(e=>({result:e.attempt.result,review:e.atmosphere,label:e.attempt.flightName})));
 if(transect)table(parent,'Transect coverage and measurement context',['Flight / original UTC','Covered locations','Greatest leg extent (m)','Supported path (m)','Planned instruments / intervals','Retained datasets / sessions'],entries.map(e=>{const t=e.attempt.result?.atmosphericProfile?.transect;return[e.attempt.flightName+' · '+e.attempt.startUtc+' → '+e.attempt.endUtc,t?.coveredStations??'Unknown',t?exact(t.greatestLegExtentMetres):'Unknown',t?exact(t.linkedDistanceMetres):'Unknown',e.atmosphere?.plannedInstruments?.filter(i=>i.enabled).map(i=>`${quantityNames[i.instrumentId.replace('ambient-','')]||(i.instrumentId==='ambient-wind'?'Ambient wind':i.instrumentId)} · ${i.intervalMs} ms`).join('; ')||'Unknown; inspect original timeline',t?`${t.datasetCount} / ${t.sessionCount}`:'Unknown'];}));
 parent.append(node('p','Different flights retain their own dates, locations, quantities and requirements. Similar or different temperatures do not establish a regional lapse rate or prove a hypothesis.'));
}

function transectMap(parent,entries,measurement,unit,inspect){
 const holder=node('section',undefined,'workspace-map');holder.setAttribute('aria-label','Recorded atmospheric transect map');parent.append(holder);
 const map=new ResearchMap(holder,null,{coverageOnly:true}),L=window.L,points=L.layerGroup().addTo(map.map),paths=L.layerGroup().addTo(map.map),all=[];
 map.layers.addOverlay({id:'transect-values',name:'Actual atmospheric sample points',layer:points,group:'Science'});map.layers.addOverlay({id:'transect-path',name:'Supported sampled path links',layer:paths,group:'Science'});
 const rows=entries.flatMap(e=>e.review.preview.filter(o=>o.measurementId===measurement&&o.unit===unit)),values=rows.map(o=>o.value),low=Math.min(...values),high=Math.max(...values);
 for(const entry of entries){const metadata=new Map((entry.review.transectPoints||[]).map(p=>[p.observationId,p]));let line=[],leg=null,longitude;
  const flush=()=>{if(line.length>1)L.polyline(line,{color:'#a7bacb',weight:2,dashArray:'5 5',interactive:false}).addTo(paths);line=[];};
  for(const o of entry.review.preview.filter(o=>o.measurementId===measurement&&o.unit===unit)){
   if(Math.abs(o.latitude)>85.0511){flush();leg=null;continue;}
   const at=metadata.get(o.id);if(at?.legId!==leg||at?.legId==null){flush();longitude=undefined;leg=at?.legId;}
   longitude=longitude==null?o.longitude:longitude+window.AeroScienceGeo.wrap(o.longitude-longitude);const position=[o.latitude,longitude];all.push(position);if(at?.legId!=null)line.push(position);
   const popup=node('div');popup.append(node('strong',entry.label),node('p',`${exact(o.value)} ${o.unit} · ${o.timestampUtc} · ${exact(o.altitudeMslFeet)} feet MSL`));if(inspect)button(popup,'Inspect Reading',()=>inspect(entry.result.flightId,o.timestampUtc));
   L.circleMarker(position,{radius:5,color:color(o.value,low,high),fillOpacity:.85}).bindPopup(popup).addTo(points);
  }flush();
 }
 if(all.length)map.map.fitBounds(all,{padding:[28,28],maxZoom:12});
 parent.append(node('p',`${quantityNames[measurement]}: ${exact(low)}–${exact(high)} ${unit}. Markers are actual valid retained samples; dashed links show recorded order within supported legs, not measured values between points. Gaps remain disconnected. ${rows.some(o=>Math.abs(o.latitude)>85.0511)?'Polar samples outside the Mercator map remain available at their exact coordinates in the table.':''}`));
 const observer=new MutationObserver(()=>{if(!holder.isConnected){map.destroy();observer.disconnect();}});observer.observe(document.body,{childList:true,subtree:true});
}
