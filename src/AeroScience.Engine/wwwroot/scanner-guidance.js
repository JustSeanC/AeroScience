import {node,numeric} from './workstation-ui.js';
import {t} from './i18n.js';
import {currentFlightPresentation,recordedScannerSpacing} from './current-flight-state.js';

export function scannerGuidance(parent){
  const root=node('section',undefined,'scanner-guidance');root.setAttribute('aria-label',t('flight.scanner.title'));
  const title=node('h3',t('flight.scanner.title')),prediction=node('p'),recorded=node('p'),source=node('p'),limits=node('p',t('flight.scanner.limits'),'metadata');
  root.append(title,prediction,recorded,source,limits);parent.prepend(root);root.hidden=true;
  let latest=null,products=[],stale=false;
  function paint(){
    const card=latest?.sensorOperating?.instruments?.find(i=>i.instrumentId==='terrain-scanner'&&i.enabled);root.hidden=!card;if(!card)return;
    const model=currentFlightPresentation(latest,latest.flight,{stale}),g=card.scannerSpacing;
    const age=Date.now()-Date.parse(g?.evidenceUtc);
    const usable=model.science==='collecting'&&card.evidenceFresh&&g?.version===1&&g.missionId===model.flightId&&g.sessionId===model.sessionId&&g.configurationId===card.configuration?.id&&age>=-2000&&age<=6000;
    prediction.textContent=usable&&Number.isFinite(g.predictedSpacingMetres)&&Number.isFinite(g.modeledAlongTrackMetres)
      ?t(g.mayNotOverlap?'flight.scanner.gaps':'flight.scanner.prediction',{spacing:numeric(g.predictedSpacingMetres),footprint:numeric(g.modeledAlongTrackMetres),interval:numeric(g.configuredIntervalSeconds)})
      :t('flight.scanner.unknown');
    const actual=recordedScannerSpacing(products,{missionId:model.flightId,sessionId:model.sessionId});
    recorded.textContent=actual?t('flight.scanner.recorded',{seconds:numeric(actual.seconds),metres:numeric(actual.metres)}):t('flight.scanner.noRecorded');
    const d=card.acquisition;source.textContent=t('flight.scanner.source',{status:stale?t('common.unavailable'):card.scientificData?.status||t('common.unknown'),rejected:d?.rejectedObservations??t('common.unknown'),missed:d?.missingIntervals??t('common.unknown')});
    root.dataset.prediction=usable&&g.mayNotOverlap===true?'gaps':usable?'modeled':'unknown';
  }
  return{update(data){latest=data;stale=false;paint();},products(value){products=value;paint();},stale(){stale=true;paint();}};
}
