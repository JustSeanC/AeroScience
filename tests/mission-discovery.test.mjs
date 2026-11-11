import {test} from 'node:test';
import assert from 'node:assert/strict';
import {experimentPresentation,preparationPresentation} from '../src/AeroScience.Engine/wwwroot/mission-discovery.js';
import {resolveRoute} from '../src/AeroScience.Engine/wwwroot/navigation-routes.js';
const definitions=[{id:'ambient-temperature',name:'Original thermometer name'},{id:'terrain-scanner',name:'Original terrain name'}];
const atmosphere={version:1,question:'Original question',requiredBands:3,bandWidthFeet:500,minimumReadingsPerBand:4,maximumGapSeconds:5};
test('discovery reads real template instruments without changing a saved definition or implying readiness',()=>{
  const starter={id:'vertical-atmospheric-profile',version:1,name:'Original name',atmosphericExample:atmosphere};
  const templates=[{id:'atmosphericProfile',instruments:[{instrumentId:'ambient-temperature',enabled:true},{instrumentId:'terrain-scanner',enabled:false}]}];
  const before=JSON.stringify({starter,templates});const result=experimentPresentation(starter,templates,definitions);
  assert.equal(result.context.question,'Original question');assert.deepEqual(result.instruments.map(i=>i.name),['Original thermometer name']);
  assert.equal(result.instrumentsKnown,true);assert.equal(result.external,false);assert.equal(JSON.stringify({starter,templates}),before);
  assert.equal(experimentPresentation(starter,[],definitions).instrumentsKnown,false);
  assert.equal(experimentPresentation(starter,templates,[]).instrumentsKnown,false);
});
test('selected survey measurements change actual source and instrument requirements; empty or unknown inputs are unavailable',()=>{
  const survey={version:1,question:'Original survey question',measurements:['airTemperature'],minimumValidSamples:3,minimumLocations:2,locationSpacingMetres:100};
  const starter={id:'terrain-temperature-survey',version:1,name:'Survey',surveyExample:survey};
  assert.equal(experimentPresentation(starter,[],definitions).external,false);
  const changed={...survey,measurements:['terrainElevation','airTemperature']};const result=experimentPresentation(starter,[],definitions,changed);
  assert.equal(result.external,true);assert.deepEqual(result.instruments.map(i=>i.id),['terrain-scanner','ambient-temperature']);
  for(const measurements of [[],['unsupported']])assert.equal(experimentPresentation(starter,[],definitions,{...survey,measurements}).instrumentsKnown,false);
});
test('unsupported starter versions and unknown families cannot become a different supported experiment',()=>{
  for(const starter of [{id:'future-template',version:1},{id:'flight-regime',version:2,example:{}},{id:'vertical-atmospheric-profile',version:1}]){
    const result=experimentPresentation(starter,[],definitions);assert.equal(result.supported,false);assert.equal(result.instrumentsKnown,false);
  }
});
test('preparation keeps actual selection and active flight distinct, with stale/invalid telemetry qualified',()=>{
  const now=Date.now(),state={activeMission:null,selectedPlanId:'plan',connection:{state:'connected',source:'simulated'},telemetry:{timestampUtc:new Date(now).toISOString()}};
  const plan={id:'plan'};assert.equal(preparationPresentation(state,now,plan,now).matched,true);
  assert.equal(preparationPresentation(state,now-6001,plan,now).fresh,false);
  assert.equal(preparationPresentation({...state,selectedPlanId:'other'},now,plan,now).matched,false);
  assert.equal(preparationPresentation({...state,activeMission:{id:'original-flight'}},now,plan,now).active.id,'original-flight');
  for(const [telemetry,expected]of [[{...state.telemetry,isPaused:true},'paused'],[{...state.telemetry,isValid:false},'stale'],[{timestampUtc:new Date(now+2001).toISOString()},'stale']])assert.equal(preparationPresentation({...state,telemetry},now,plan,now).connection,expected);
  assert.equal(preparationPresentation({},now,plan,now).fresh,false);
});
test('canonical Explore and Prepare coexist with every legacy query continuation without selecting or starting',()=>{
  assert.equal(resolveRoute('#missions').page,'missions-explorer');assert.equal(resolveRoute('#missions/prepare?researchPlan=exact&future=a%3Fb').page,'mission-preparation');
  for(const key of ['card','flight','submissionDraft','connectOrganization','permission','researchPlan','section']){
    const route=resolveRoute('#missions?'+key+'=exact&future=a%3Fb');assert.ok(route.redirect);
    const legacy=resolveRoute(route.redirect);assert.equal(legacy.page,'my-research');assert.equal(legacy.query.get(key),'exact');assert.equal(legacy.query.get('future'),'a?b');
  }
  assert.equal(resolveRoute('#missions/templates').page,'my-research');assert.equal(resolveRoute('#missions/plans').page,'planner');
});
