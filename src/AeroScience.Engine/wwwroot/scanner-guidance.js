import {message as localizedCopy,setPresentationText} from './i18n.js';
import {node,numeric} from './workstation-ui.js';
import {t} from './i18n.js';
import {currentFlightPresentation,recordedScannerSpacing} from './current-flight-state.js';

export function scannerGuidance(parent){
  const root=node('section',undefined,'scanner-guidance');setPresentationText(root,localizedCopy('flight.scanner.title'),"aria-label");
  const title=node('h3',localizedCopy('flight.scanner.title')),prediction=node('p'),recorded=node('p'),source=node('p'),limits=node('p',localizedCopy('flight.scanner.limits'),'metadata');
  root.append(title,prediction,recorded,source,limits);parent.prepend(root);root.hidden=true;
  let latest=null,products=[],stale=false;
  function paint(){
    const card=latest?.sensorOperating?.instruments?.find(i=>i.instrumentId==='terrain-scanner'&&i.enabled);root.hidden=!card;if(!card)return;
    const model=currentFlightPresentation(latest,latest.flight,{stale}),g=card.scannerSpacing;
    const age=Date.now()-Date.parse(g?.evidenceUtc);
    const usable=model.science==='collecting'&&card.evidenceFresh&&g?.version===1&&g.missionId===model.flightId&&g.sessionId===model.sessionId&&g.configurationId===card.configuration?.id&&age>=-2000&&age<=6000;
    setPresentationText(prediction,usable&&Number.isFinite(g.predictedSpacingMetres)&&Number.isFinite(g.modeledAlongTrackMetres)
      ?localizedCopy(g.mayNotOverlap?'flight.scanner.gaps':'flight.scanner.prediction',{spacing:numeric(g.predictedSpacingMetres),footprint:numeric(g.modeledAlongTrackMetres),interval:numeric(g.configuredIntervalSeconds)})
      :localizedCopy('flight.scanner.unknown'));
    const actual=recordedScannerSpacing(products,{missionId:model.flightId,sessionId:model.sessionId});
    setPresentationText(recorded,actual?localizedCopy('flight.scanner.recorded',{seconds:numeric(actual.seconds),metres:numeric(actual.metres)}):localizedCopy('flight.scanner.noRecorded'));
    const d=card.acquisition;setPresentationText(source,localizedCopy('flight.scanner.source',{status:stale?t('common.unavailable'):card.scientificData?.status||t('common.unknown'),rejected:d?.rejectedObservations??t('common.unknown'),missed:d?.missingIntervals??t('common.unknown')}));
    root.dataset.prediction=usable&&g.mayNotOverlap===true?'gaps':usable?'modeled':'unknown';
  }
  return{update(data){latest=data;stale=false;paint();},products(value){products=value;paint();},stale(){stale=true;paint();}};
}
