import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {ResearchMap} from './map.js';
import {atmosphere} from './mission-workspace.js';
import {initialMeasurement} from './science-map.js';
import {node as el,numeric as number,dateTime as local,metric,chart,table,link,disclosure,color,readableName} from './workstation-ui.js';
import {displayReading,onUnitsChanged} from './science-display.js';
import {renderCategory,renderLayerContext} from './scientific-context.js';

export async function summaryView(parent,summary,api,instrument,{disposals=[],catalog=[],analysis=false}={}){
  const header=el('div',undefined,'research-mission-header');parent.append(header);
  const facts=el('dl',undefined,'mission-facts');header.append(facts);
  for(const [title,value] of [[textForUI("ui.personalResearchReview.messageAircraft"),summary.mission.aircraft||textForUI("ui.scienceAnalysis.messageNotRecorded")],[textForUI("ui.scienceAnalysis.messageFlightDate"),local(summary.mission.startedUtc)],[textForUI("ui.app.messageDuration"),textForUI("ui.scienceAnalysis.messageMinutes",{value1:(String(number(summary.mission.durationSeconds/60,1)))})],[textForUI("toolbar.flight"),summary.mission.status],[textForUI("ui.scienceAnalysis.messageScienceReview"),summary.review.status]])facts.append(el('dt',title),el('dd',value));
  if(summary.objective)header.append(el('p',summary.objective,'mission-objective'));
  const cards=el('div',undefined,'instrument-results');parent.append(cards);
  const chosen=summary.instruments.filter(item=>!instrument||item.instrumentId===instrument);
  for(const review of chosen){
    const card=el('article',undefined,'panel instrument-result');cards.append(card);card.append(el('h3',review.name));
    const remote=review.remoteProducts>0,findings=el('div',undefined,'instrument-findings');card.append(findings);
    // Instrument result groups retain their distinct source semantics; raw quality
    // and provenance remain available in the existing exact-evidence inspector.
    renderCategory(card,remote?'externalDataset':review.measurementValues>0?'simulatorReported':null);
    if(remote){metric(findings,localizedCopy("ui.scienceAnalysis.messageRecordedAcquisitions"),number(review.remoteProducts),localizedCopy("ui.scienceAnalysis.messageSavedValidSamplePositionsRepeatedPackets",{value1:(String(number(review.validGeographicSamples)))}));}
    else{metric(findings,localizedCopy("ui.missionOperations.messageRecordedMeasurements"),number(review.measurementValues),localizedCopy("ui.scienceAnalysis.messageActualSamplingTimestampsFieldsCanShare",{value1:(String(number(review.acquisitionTimestamps)))}));}
    if(review.diagnostics){metric(findings,localizedCopy("ui.scienceAnalysis.messageDataQuality"),review.diagnostics.quality||localizedCopy("ui.scienceAnalysis.messageNotAssessed"),localizedCopy("ui.scienceAnalysis.messageRejectedMissingIntervals",{value1:(String(number(review.diagnostics.rejectedObservations))),value2:(String(number(review.diagnostics.missingIntervals)))}));}
    const remoteStats=review.remoteStatistics||[],priorities=['temperature','elevation','ndvi'];
    const selected=remoteStats.filter(statistic=>priorities.includes(statistic.measurement));
    const primary=selected.length?selected:remoteStats.slice(0,2);
    for(const statistic of primary.slice(0,3)){
      const source=review.sources?.find(source=>source.sourceId===statistic.sourceId);
      const section=el('section',undefined,'result-visualization');card.append(section);
      const drawStatistic=()=>{section.replaceChildren();const shown=value=>statistic.measurement==='elevation'&&statistic.unit==='m'?displayReading('elevation',value,'m'):{value,unit:statistic.unit};const shownUnit=shown(0).unit;
      const title=readableName(statistic.measurement);section.append(el('h4',title),el('p',localizedCopy("ui.scienceAnalysis.messageSavedSampleValues",{value1:(String(number(shown(statistic.minimum).value))),value2:(String(number(shown(statistic.maximum).value))),value3:(String(shownUnit)),value4:(String(statistic.count))}),'scientific-range'));
      chart(section,localizedCopy("ui.scienceAnalysis.messageRecordedMinimumMeanAndMaximum",{value1:(String(title))}),[{label:shownUnit,data:[statistic.minimum,statistic.mean,statistic.maximum].map((value,index)=>({x:index,y:shown(value).value})),backgroundColor:['#7fa4ed','#62dec6','#e9bd79'],borderColor:'#62dec6'}],{type:'bar',categories:[localizedCopy("ui.scienceAnalysis.messageMinimum"),localizedCopy("ui.scienceAnalysis.messageMean"),localizedCopy("ui.scienceAnalysis.messageMaximum")],xLabel:localizedCopy("ui.scienceAnalysis.messageSavedSampleStatistics"),yLabel:shownUnit});
      table(section,localizedCopy("ui.scienceAnalysis.messageRecordedStatistics",{value1:(String(title))}),[localizedCopy("ui.scienceAnalysis.messageStatistic"),localizedCopy("ui.scienceAnalysis.messageValue"),localizedCopy("ui.campaignProgressUi.messageUnits"),localizedCopy("ui.scienceAnalysis.messageSampleValues")],[['Minimum',number(shown(statistic.minimum).value),shownUnit,statistic.count],['Mean',number(shown(statistic.mean).value),shownUnit,statistic.count],['Maximum',number(shown(statistic.maximum).value),shownUnit,statistic.count]]);
      section.append(el('p',localizedCopy("ui.scienceAnalysis.messageTheseSavedSamplesRangeFromTo",{value1:(String(number(shown(statistic.minimum).value))),value2:(String(number(shown(statistic.maximum).value))),value3:(String(shownUnit))}),'finding-interpretation'));
      section.append(el('p',localizedCopy("ui.scienceAnalysis.messageHistoricalObservationDate",{value1:(String(source?.provider||'Source provider unavailable')),value2:(String(local(source?.observedUtc))),value3:(String(source?.verticalDatum||'Vertical reference unavailable'))}),'source-date'));
      };drawStatistic();disposals.push(onUnitsChanged(drawStatistic));
    }
    if(!remote&&review.measurements.length){
      const rows=review.measurements.map(measurement=>{const low=displayReading(measurement.measurementId,measurement.minimum,measurement.unit),high=displayReading(measurement.measurementId,measurement.maximum,measurement.unit),mean=displayReading(measurement.measurementId,measurement.mean,measurement.unit);return [readableName(measurement.measurementId),number(low.value),number(mean.value),number(high.value),low.unit,measurement.count];});
      table(card,localizedCopy("ui.scienceAnalysis.messageRecordedEnvironmentalMeasurementRanges"),[localizedCopy("ui.missionTimeline.messageMeasurement"),localizedCopy("ui.scienceAnalysis.messageMinimum"),localizedCopy("ui.scienceAnalysis.messageMean"),localizedCopy("ui.scienceAnalysis.messageMaximum"),localizedCopy("ui.campaignProgressUi.messageUnits"),localizedCopy("ui.scienceAnalysis.messageValues")],rows);
      const measurement=review.measurements[0],low=displayReading(measurement.measurementId,measurement.minimum,measurement.unit),high=displayReading(measurement.measurementId,measurement.maximum,measurement.unit);
      metric(findings,localizedCopy("ui.scienceAnalysis.messageRange",{value1:(String(readableName(measurement.measurementId)))}),`${number(low.value)}–${number(high.value)} ${low.unit}`,localizedCopy("ui.scienceAnalysis.messageActualArchivedEnvironmentalReadings"));
    }
    if(remote&&analysis){
      const page=await api('/api/science/workspace/map',{missions:[summary.mission.id],instrument:review.instrumentId,limit:1});
      if(page.products[0]){
        const source=review.sources?.find(source=>source.sourceId===page.products[0].sourceId),measurement=initialMeasurement(review.instrumentId);
        if(measurement){const layer=await api(`/api/science/products/${page.products[0].id}/map-layer?measurement=${encodeURIComponent(measurement)}`);const holder=el('div',undefined,'analysis-map');card.append(holder);const map=new ResearchMap(holder,null,{coverageOnly:true});disposals.push(()=>map.destroy());
          const overlay=window.L.geoJSON(layer,{style:feature=>({stroke:false,fillOpacity:.85,fillColor:color(feature.properties.value,layer.minimum,layer.maximum)})}).addTo(map.map);map.layers.addOverlay({id:'analysis-native',name:'Analysis native scientific values',layer:overlay,group:"Science",description:layer.method||textForUI("ui.scienceAnalysis.messageSavedValidNativeSamplesSourceDatesUnits"),describe:target=>renderLayerContext(target,{source:layer.source,context:layer.context,measurement,unit:layer.unit,method:layer.method}),setOpacity:value=>overlay.setStyle({fillOpacity:value}),opacity:.85});if(layer.features.length)map.map.fitBounds(overlay.getBounds(),{maxZoom:17});card.append(el('p',localizedCopy("ui.scienceAnalysis.messageValidNativeCellsInTheDisplayed",{value1:(String(layer.features.length))})));}
        else card.append(el('p',localizedCopy("ui.scienceAnalysis.messageChooseARetainedWavelengthOnScienceMap")));
      }
    }
    if(!review.measurementValues&&!review.remoteProducts)card.append(el('p',localizedCopy("ui.scienceAnalysis.messageNoValidScientificMeasurementsWereSavedFor"),'empty-state'));
    const actions=el('div',undefined,'action-row');card.append(actions);link(actions,localizedCopy("ui.scienceAnalysis.messageViewOnScienceMap"),`#workspace/map?missions=${summary.mission.id}&instrument=${review.instrumentId}`);link(actions,localizedCopy("ui.scienceAnalysis.messageCompare"),`#workspace/compare?missions=${summary.mission.id}`);link(actions,localizedCopy("ui.scienceAnalysis.messageExport"),`#workspace/results?missions=${summary.mission.id}`);
    const advanced=disclosure(card,localizedCopy("ui.scienceAnalysis.messageAdvancedProvenanceMeasurementsExactUTC"),review.explanation);advanced.append(el('p',localizedCopy("ui.scienceAnalysis.messageGeographicAcquisitionsSavedValidSample",{value1:(String(number(review.remoteProducts))),value2:(String(number(review.validGeographicSamples)))})),el('pre',JSON.stringify(review,null,2)));
  }
  if(!chosen.length){const empty=el('div',undefined,'empty-state');empty.append(el('h3',localizedCopy("ui.scienceAnalysis.messageNoScientificMeasurementsInThisSelection")),el('p',localizedCopy("ui.scienceAnalysis.messageYourRecordedFlightRemainsAvailableChooseAnother")));link(empty,localizedCopy("ui.scienceAnalysis.messageReplayFlight"),`#workspace/replay?missions=${summary.mission.id}`);cards.append(empty);}
  if(!analysis)return;
  const interpretation=await api(`/api/missions/${summary.mission.id}/science-interpretation`);
  if(chosen.some(item=>item.measurementValues>0)){
    const profile=el('section',undefined,'panel atmospheric-results');parent.append(profile);await atmosphere(profile,summary.mission.id,api);
    for(const item of interpretation){const note=el('section',undefined,'finding-interpretation');note.append(el('h3',localizedCopy("ui.scienceAnalysis.messageWhatTheRecordedProfileSupports")),el('p',item.explanation));profile.append(note);}
  }else parent.append(el('p',localizedCopy("ui.scienceAnalysis.messageNoRecordedAtmosphericReadingsForThisFlight")));
  const patterns=await api(`/api/missions/${summary.mission.id}/transect-passes`);
  if(patterns.length){const section=el('section',undefined,'panel transect-results');section.append(el('h3',localizedCopy("ui.scienceAnalysis.messageActuallyFlownTransectPasses")));parent.append(section);const rows=[];
    const reading=(value,unit)=>Number.isFinite(value)?number(value)+' '+unit:textForUI("localization.unavailableValue");
    for(const pattern of patterns)for(const pass of pattern.passes){const quality=el('details');quality.append(el('summary',localizedCopy("ui.scienceAnalysis.messageViewQualityNotes")),el('p',pass.quality||localizedCopy("ui.scienceAnalysis.messageQualityNotAssessed")));rows.push([pass.number,pass.direction,pass.completed?textForUI("ui.scienceAnalysis.messageObserved"):textForUI("ui.scienceAnalysis.messageIncomplete"),pass.samples,reading(pass.meanAltitudeFeet,'ft MSL'),reading(pass.meanTemperatureCelsius,'°C'),reading(pass.meanWindSpeedKnots,'kt'),quality]);}
    table(section,localizedCopy("ui.scienceAnalysis.messageRecordedTransectComparison"),[localizedCopy("ui.scienceAnalysis.messagePass"),localizedCopy("ui.scienceAnalysis.messageDirection"),localizedCopy("ui.scienceAnalysis.messageObservedCompletion"),localizedCopy("ui.scienceAnalysis.messagePackets"),localizedCopy("ui.scienceAnalysis.messageMeanAltitude"),localizedCopy("ui.scienceAnalysis.messageMeanTemperature"),localizedCopy("ui.scienceAnalysis.messageMeanWindSpeed"),localizedCopy("ui.atmosphericResearch.messageQuality")],rows);
    const passes=patterns.flatMap(pattern=>pattern.passes);chart(section,localizedCopy("ui.scienceAnalysis.messageRecordedTransectTemperatureByActualPass"),[{label:textForUI("ui.scienceAnalysis.messageMeanRecordedTemperatureC"),data:passes.filter(pass=>Number.isFinite(pass.meanTemperatureCelsius)).map(pass=>({x:pass.number,y:pass.meanTemperatureCelsius})),borderColor:'#62dec6',pointRadius:4}],{xLabel:localizedCopy("ui.scienceAnalysis.messagePlannedPassNumberIncompletePassesRemain"),yLabel:localizedCopy("ui.scienceAnalysis.messageRecordedMeanTemperatureC")});
  }
}
