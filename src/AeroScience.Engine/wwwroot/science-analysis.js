import {ResearchMap} from './map.js';
import {atmosphere} from './mission-workspace.js';
import {initialMeasurement} from './science-map.js';
import {node as el,numeric as number,dateTime as local,metric,chart,table,link,disclosure,color,readableName} from './workstation-ui.js';
import {displayReading} from './science-display.js';

export async function summaryView(parent,summary,api,instrument,{disposals=[],catalog=[],analysis=false}={}){
  const header=el('div',undefined,'research-mission-header');parent.append(header);
  const facts=el('dl',undefined,'mission-facts');header.append(facts);
  for(const [title,value] of [['Aircraft',summary.mission.aircraft||'Not recorded'],['Flight date',local(summary.mission.startedUtc)],['Duration',`${number(summary.mission.durationSeconds/60,1)} minutes`],['Flight',summary.mission.status],['Science review',summary.review.status]])facts.append(el('dt',title),el('dd',value));
  if(summary.objective)header.append(el('p',summary.objective,'mission-objective'));
  const cards=el('div',undefined,'instrument-results');parent.append(cards);
  const chosen=summary.instruments.filter(item=>!instrument||item.instrumentId===instrument);
  for(const review of chosen){
    const card=el('article',undefined,'panel instrument-result');cards.append(card);card.append(el('h3',review.name));
    const remote=review.remoteProducts>0,findings=el('div',undefined,'instrument-findings');card.append(findings);
    if(remote){metric(findings,'Recorded acquisitions',number(review.remoteProducts),`${number(review.validGeographicSamples)} saved valid sample positions; repeated packets are separate samples`);}
    else{metric(findings,'Recorded measurements',number(review.measurementValues),`${number(review.acquisitionTimestamps)} actual sampling timestamps; fields can share a timestamp`);}
    if(review.diagnostics){metric(findings,'Data quality',review.diagnostics.quality||'Not assessed',`${number(review.diagnostics.rejectedObservations)} rejected · ${number(review.diagnostics.missingIntervals)} missing intervals`);}
    const remoteStats=review.remoteStatistics||[],priorities=['temperature','elevation','ndvi'];
    const selected=remoteStats.filter(statistic=>priorities.includes(statistic.measurement));
    const primary=selected.length?selected:remoteStats.slice(0,2);
    for(const statistic of primary.slice(0,3)){
      const source=review.sources?.find(source=>source.sourceId===statistic.sourceId);
      const section=el('section',undefined,'result-visualization');card.append(section);
      const title=readableName(statistic.measurement);section.append(el('h4',title),el('p',`${number(statistic.minimum)}–${number(statistic.maximum)} ${statistic.unit} · ${statistic.count} saved sample values`,'scientific-range'));
      chart(section,`${title} — recorded minimum, mean and maximum`,[{label:statistic.unit,data:[statistic.minimum,statistic.mean,statistic.maximum].map((value,index)=>({x:index,y:value})),backgroundColor:['#7fa4ed','#62dec6','#e9bd79'],borderColor:'#62dec6'}],{type:'bar',categories:['Minimum','Mean','Maximum'],xLabel:'Saved sample statistics',yLabel:statistic.unit});
      table(section,title+' recorded statistics',['Statistic','Value','Units','Sample values'],[['Minimum',number(statistic.minimum),statistic.unit,statistic.count],['Mean',number(statistic.mean),statistic.unit,statistic.count],['Maximum',number(statistic.maximum),statistic.unit,statistic.count]]);
      section.append(el('p',`These saved samples range from ${number(statistic.minimum)} to ${number(statistic.maximum)} ${statistic.unit}. Repeated positions may occur at different flight packets; this is not an area-wide mean.`,'finding-interpretation'));
      section.append(el('p',`${source?.provider||'Source provider unavailable'} · Historical observation date ${local(source?.observedUtc)} · ${source?.verticalDatum||'Vertical reference unavailable'}`,'source-date'));
    }
    if(!remote&&review.measurements.length){
      const rows=review.measurements.map(measurement=>{const low=displayReading(measurement.measurementId,measurement.minimum,measurement.unit),high=displayReading(measurement.measurementId,measurement.maximum,measurement.unit),mean=displayReading(measurement.measurementId,measurement.mean,measurement.unit);return [readableName(measurement.measurementId),number(low.value),number(mean.value),number(high.value),low.unit,measurement.count];});
      table(card,'Recorded environmental measurement ranges',['Measurement','Minimum','Mean','Maximum','Units','Values'],rows);
      const measurement=review.measurements[0],low=displayReading(measurement.measurementId,measurement.minimum,measurement.unit),high=displayReading(measurement.measurementId,measurement.maximum,measurement.unit);
      metric(findings,readableName(measurement.measurementId)+' range',`${number(low.value)}–${number(high.value)} ${low.unit}`,'Actual archived environmental readings');
    }
    if(remote&&analysis){
      const page=await api('/api/science/workspace/map',{missions:[summary.mission.id],instrument:review.instrumentId,limit:1});
      if(page.products[0]){
        const source=review.sources?.find(source=>source.sourceId===page.products[0].sourceId),measurement=initialMeasurement(review.instrumentId);
        if(measurement){const layer=await api(`/api/science/products/${page.products[0].id}/map-layer?measurement=${encodeURIComponent(measurement)}`);const holder=el('div',undefined,'analysis-map');card.append(holder);const map=new ResearchMap(holder,null,{coverageOnly:true});disposals.push(()=>map.destroy());
          const overlay=window.L.geoJSON(layer,{style:feature=>({stroke:false,fillOpacity:.85,fillColor:color(feature.properties.value,layer.minimum,layer.maximum)})}).addTo(map.map);map.layers.addOverlay({id:'analysis-native',name:'Analysis native scientific values',layer:overlay,group:'Science',description:layer.method||'Saved valid native samples; source dates, units and QA remain in the report.',setOpacity:value=>overlay.setStyle({fillOpacity:value}),opacity:.85});if(layer.features.length)map.map.fitBounds(overlay.getBounds(),{maxZoom:17});card.append(el('p',`${layer.features.length} valid native cells in the displayed acquisition. Missing geographic support remains transparent; this is historical source data.`));}
        else card.append(el('p','Choose a retained wavelength on Science Map to inspect the actual hyperspectral measurements.'));
      }
    }
    if(!review.measurementValues&&!review.remoteProducts)card.append(el('p','No valid scientific measurements were saved for this instrument. Flight telemetry remains available. Check preparation, enabled settings and source coverage before another flight.','empty-state'));
    const actions=el('div',undefined,'action-row');card.append(actions);link(actions,'View on Science Map',`#workspace/map?missions=${summary.mission.id}&instrument=${review.instrumentId}`);link(actions,'Compare',`#workspace/compare?missions=${summary.mission.id}`);link(actions,'Export',`#workspace/results?missions=${summary.mission.id}`);
    const advanced=disclosure(card,'Advanced — provenance, measurements & exact UTC',review.explanation);advanced.append(el('p',`${number(review.remoteProducts)} geographic acquisitions · ${number(review.validGeographicSamples)} saved valid sample positions`),el('pre',JSON.stringify(review,null,2)));
  }
  if(!chosen.length){const empty=el('div',undefined,'empty-state');empty.append(el('h3','No scientific measurements in this selection'),el('p','Your recorded flight remains available. Choose another instrument or replay/export the telemetry.'));link(empty,'Replay flight',`#workspace/replay?missions=${summary.mission.id}`);cards.append(empty);}
  if(!analysis)return;
  const interpretation=await api(`/api/missions/${summary.mission.id}/science-interpretation`);
  if(chosen.some(item=>item.measurementValues>0)){
    const profile=el('section',undefined,'panel atmospheric-results');parent.append(profile);await atmosphere(profile,summary.mission.id,api);
    for(const item of interpretation){const note=el('section',undefined,'finding-interpretation');note.append(el('h3','What the recorded profile supports'),el('p',item.explanation));profile.append(note);}
  }else parent.append(el('p','No recorded atmospheric readings for this flight. Geographic results above retain their own source dates.'));
  const patterns=await api(`/api/missions/${summary.mission.id}/transect-passes`);
  if(patterns.length){const section=el('section',undefined,'panel transect-results');section.append(el('h3','Actually flown transect passes'));parent.append(section);const rows=[];
    for(const pattern of patterns)for(const pass of pattern.passes)rows.push([pass.number,pass.direction,pass.completed?'Observed':'Incomplete',pass.samples,number(pass.meanAltitudeFeet)+' ft MSL',number(pass.meanTemperatureCelsius)+' °C',number(pass.meanWindSpeedKnots)+' kt',pass.quality]);
    table(section,'Recorded transect comparison',['Pass','Direction','Observed completion','Packets','Mean altitude','Mean temperature','Mean wind speed','Quality'],rows);
    const passes=patterns.flatMap(pattern=>pattern.passes);chart(section,'Recorded transect temperature by actual pass',[{label:'Mean recorded temperature (°C)',data:passes.filter(pass=>Number.isFinite(pass.meanTemperatureCelsius)).map(pass=>({x:pass.number,y:pass.meanTemperatureCelsius})),borderColor:'#62dec6',pointRadius:4}],{xLabel:'Planned pass number — incomplete passes remain labeled',yLabel:'Recorded mean temperature (°C)'});
  }
}
