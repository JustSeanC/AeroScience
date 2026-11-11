import {researchContext} from './scientific-context.js';

const families={'flight-regime':'example','vertical-atmospheric-profile':'atmosphericExample',
  'atmospheric-transect':'transectExample','terrain-temperature-survey':'surveyExample',
  'scientific-observation-flight':'observationExample','experimental-sensor-checkout':'sensorExample'};
const surveyInstruments={airTemperature:'ambient-temperature',terrainElevation:'terrain-scanner',landSurfaceTemperature:'thermal-imager'};
// Metadata preview, never aircraft approval, readiness or a second template engine.
// Defaults are read from the same /api/templates and /api/instruments catalogs.
export function experimentPresentation(starter,templates=[],definitions=[],survey=starter?.surveyExample) {
  const family=families[starter?.id];
  let supported=starter?.version===1 && !!family && !!starter[family];
  const card={name:starter?.name,templateId:starter?.id,flightRegime:starter?.example,
    atmosphericProfile:starter?.atmosphericExample,atmosphericTransect:starter?.transectExample,
    survey,observationFlight:starter?.observationExample,experimentalSensor:starter?.sensorExample};
  let ids=[],known=supported;
  if(family==='sensorExample')ids=['ambient-temperature'];
  if(family==='observationExample')ids=starter.observationExample.selectedInstruments || [];
  if(family==='surveyExample'){
    ids=(survey?.measurements || []).map(id=>surveyInstruments[id]);
    known=known && ids.length>0 && ids.every(Boolean);
  }
  if(['atmosphericExample','transectExample'].includes(family)){
    const template=templates.find(t=>t.id===(family==='atmosphericExample'?'atmosphericProfile':'atmosphericTransect'));
    known=known && Array.isArray(template?.instruments);
    ids=(template?.instruments||[]).filter(i=>i.enabled).map(i=>i.instrumentId);
  }
  const instruments=ids.map(id=>definitions.find(d=>d.id===id));
  known=known && instruments.every(Boolean);
  const external=ids.some(id=>['terrain-scanner','thermal-imager','multispectral-imager','hyperspectral-imager'].includes(id));
  let context=null;try{if(supported)context=researchContext(card);}catch{supported=false;known=false;}
  return {supported,card,context,instrumentsKnown:known,
    instruments:instruments.filter(Boolean),external,
    experience:family==='sensorExample'?'model':external?'sources':family==='transectExample'?'locations':family==='atmosphericExample'?'altitudes':'guided',
    input:family==='example'?'flight':family==='observationExample'?'notes':family==='sensorExample'?'model':family==='surveyExample'?'survey':'atmosphere'};
}

export function preparationPresentation(state,checkedAt,plan,now=Date.now()) {
  const age=now-checkedAt;
  const fresh=!!state && Object.hasOwn(state,'activeMission') && Number.isFinite(age) && age>=0 && age<=6000;
  const inputAge=now-Date.parse(state?.telemetry?.timestampUtc);
  const connection=!fresh?'unknown':state.connection?.state!=='connected'?'disconnected'
    :state.telemetry?.isValid===false||!Number.isFinite(inputAge)||inputAge < -2000||inputAge>6000?'stale':state.telemetry.isPaused?'paused':'connected';
  return {fresh,connection,active:fresh?state.activeMission:null,matched:fresh && !!plan && state.selectedPlanId===plan.id,
    synthetic:fresh && state.connection?.source==='simulated'};
}
