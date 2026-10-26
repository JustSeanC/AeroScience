import {node,button,table,chart,numeric,link,disclosure,dateTime} from './workstation-ui.js';

export const collectionOutcome=outcome=>({collectionComplete:'Completed collection — sufficient evidence',partialCollection:'Partial coverage',insufficientEvidence:'Insufficient or unavailable evidence'}[outcome]||'Unknown collection outcome');
export function atmosphericSummary(attempt){
 const root=node('article',undefined,'panel research-panel'),s=attempt.atmosphericProfile;
 root.append(node('h3',`${attempt.card.name} — ${collectionOutcome(attempt.outcome)}`),node('p',attempt.explanation));
 const temperature=s.quantities.find(q=>q.measurementId==='temperature');if(temperature?.validReadings)root.append(node('p',`Recorded air temperatures ranged from ${numeric(temperature.minimum)} to ${numeric(temperature.maximum)} degC along the sampled aircraft path. Band values below show where readings were actually retained; they do not establish conditions between sampled altitudes.`));
 root.append(node('p',`${s.validTemperatureReadings} valid temperature readings · sampled altitude ${s.minimumAltitudeFeetMsl==null?'Unavailable':numeric(s.minimumAltitudeFeetMsl)+'–'+numeric(s.maximumAltitudeFeetMsl)+' feet MSL'} · ${s.majorGaps} major gaps / pause edges · revision ${attempt.card.revision}`),node('p',`Source: ${attempt.evidence.source==='simulator'?'Recorded simulator observations':attempt.evidence.source==='simulated'?'Synthetic/demo observations — not an installed MSFS test':'Unknown; no simulator provenance inferred'}. ${dateTime(attempt.startUtc)} → ${dateTime(attempt.endUtc)} (local display; original evidence UTC retained).`));
 const reference=disclosure(root,'Scientific Evidence References');reference.append(node('p',`Evaluator ${attempt.evaluatorVersion} · Library ${s.libraryId} · Original observation/event SHA-256 ${attempt.evidence.sha256} · Frozen plan SHA-256 ${attempt.evidence.planSha256}`));
 return root;
}
const exact=value=>Number.isFinite(value)?String(value):'Unavailable';
const quantityNames={temperature:'Air temperature',pressure:'Ambient pressure','wind-speed':'Wind speed','wind-direction':'Wind direction (true)',density:'Air density'};
function profiles(parent,entries){
 const selection=node('label','Atmospheric measurement'),select=node('select');select.setAttribute('aria-label','Atmospheric measurement');selection.append(select);parent.append(selection);const visual=node('div');parent.append(visual);
 for(const [id,name]of Object.entries(quantityNames)){const option=node('option',name);option.value=id;select.append(option);}
 function draw(){visual.replaceChildren();const measurement=select.value;
  const originals=entries.filter(e=>e.review?.matchesSavedEvidence),unit=originals.flatMap(e=>e.review.preview).find(o=>o.measurementId===measurement)?.unit;
  if(!unit){visual.append(node('p','No verified retained observations for this quantity. No values or units were invented.'));return;}
  chart(visual,`${quantityNames[measurement]} versus Altitude`,originals.map((e,i)=>({label:`${e.label} · ${dateTime(e.result.startUtc)}`,showLine:false,pointRadius:3,borderColor:['#62dec6','#7fa4ed','#f4c36b','#e79fda'][i%4],data:e.review.preview.filter(o=>o.measurementId===measurement&&o.unit===unit).map(o=>({x:o.altitudeMslFeet,y:o.value}))})),{type:'scatter',xLabel:'Recorded aircraft altitude (feet MSL)',yLabel:`${quantityNames[measurement]} (${unit})`});
  visual.append(node('p','Each point is an actual science observation at the aircraft. No lines, missing-altitude interpolation or regional atmosphere are inferred. Bounded chart previews do not replace the full observation archive.'));
 }select.onchange=draw;draw();
}
export async function showAtmosphericResult(parent,api,attempt,isCurrent=()=>true){
 const root=node('section',undefined,'panel research-panel');root.setAttribute('aria-label','Atmospheric research profile');parent.replaceChildren(root);root.append(atmosphericSummary(attempt));
 const status=node('p','Verifying retained scientific observations…');status.setAttribute('role','status');root.append(status);
 const timeline=node('div');let inspection=0;
 const inspect=async at=>{const own=++inspection;timeline.replaceChildren();const {researchFlightTimeline}=await import('./personal-research-flight.js');if(isCurrent()&&root.isConnected&&own===inspection){await researchFlightTimeline(timeline,api,attempt.flightId,{at});if(own===inspection)timeline.scrollIntoView({block:'nearest'});}};
 try{const {review}=await api(`/api/research/attempts/${attempt.id}/atmospheric`);if(!root.isConnected||!isCurrent())return;status.textContent=review.message;
  if(review.matchesSavedEvidence){
   profiles(root,[{result:attempt,review,label:attempt.card.name}]);
   table(root,'Altitude sampling coverage',['MSL band (feet)','Valid temperature readings','Longest consecutive run','Coverage','Recorded temperature range (degC)'],review.retained.bands.map(b=>[`${numeric(b.lowerFeetMsl)}–${numeric(b.upperFeetMsl)} (upper bound excluded)`,b.temperatureReadings,b.longestContinuousReadings,b.covered?'Covered':b.temperatureReadings?'Partial':'Missing',b.minimumTemperatureCelsius==null?'Unavailable':exact(b.minimumTemperatureCelsius)+'–'+exact(b.maximumTemperatureCelsius)]));
   table(root,'Available atmospheric quantities',['Quantity','Original units','Valid readings','Excluded','Recorded minimum','Recorded maximum'],review.retained.quantities.map(q=>[quantityNames[q.measurementId],q.unit,q.validReadings,q.excludedReadings,exact(q.minimum),exact(q.maximum)]));
   table(root,'Original atmospheric observation preview',['Original UTC','Altitude (feet MSL)','Quantity','Original value','Original units','Quality','Source','Timeline'],review.preview.map(o=>{const action=node('div');button(action,'Inspect Reading',()=>inspect(o.timestampUtc));return[o.timestampUtc,numeric(o.altitudeMslFeet),quantityNames[o.measurementId],exact(o.value),o.unit,o.quality,o.source,action];}),{pageSize:20});
   root.append(node('p',review.downsampled?'This is a bounded preview of retained originals. Use Science Results & Analysis for all observations.':'Preview contains the verified compatible readings in this interval.'));
  }
  const limits=disclosure(root,'Interpretation and Sampling Limits');for(const text of review.retained.limitations)limits.append(node('p',text));
 }catch(error){if(isCurrent()&&root.isConnected)status.textContent='Original scientific evidence is unavailable: '+error.message+'. A saved summary is historical; no verified profile is plotted.';}
 link(root,'Open Scientific Results',`#workspace/results?missions=${attempt.flightId}`);link(root,'Open Recorded Flight Replay',`#workspace/replay?missions=${attempt.flightId}`);
 root.append(timeline);button(root,'Show Flight & Science Timeline',()=>inspect());
}
export function showAtmosphericComparison(parent,entries){
 parent.append(node('h3','Atmospheric Profiles — Like-for-Like Comparison'));
 table(parent,'Atmospheric collection comparison',['Flight / UTC interval','Collection outcome','Sampled altitude (feet MSL)','Valid temperature readings','Retained evidence'],entries.map(e=>{const s=e.attempt.result?.atmosphericProfile;return[e.attempt.flightName+' · '+e.attempt.startUtc,e.attempt.result?collectionOutcome(e.attempt.result.outcome):'Awaiting evaluation',s?.minimumAltitudeFeetMsl==null?'Unavailable':numeric(s.minimumAltitudeFeetMsl)+'–'+numeric(s.maximumAltitudeFeetMsl),s?.validTemperatureReadings??'Unknown',e.atmosphere?.matchesSavedEvidence?'Original hashes match':'Unavailable or mismatched — not plotted'];}));
 profiles(parent,entries.filter(e=>e.attempt.result).map(e=>({result:e.attempt.result,review:e.atmosphere,label:e.attempt.flightName})));
 parent.append(node('p','Different flights retain their own dates, locations, quantities and requirements. Similar or different temperatures do not establish a regional lapse rate or prove a hypothesis.'));
}
