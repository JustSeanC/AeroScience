import {test} from 'node:test';
import assert from 'node:assert/strict';
import {homePresentation} from '../src/AeroScience.Engine/wwwroot/home-state.js';
const now=Date.parse('2026-11-10T12:00:00Z'),utc=new Date(now).toISOString();
const fixture=()=>({intelligence:{version:1,generatedUtc:utc,flight:{activeMission:null,selectedPlanId:null,
  connection:{state:'connected',source:'simulated'},telemetry:{timestampUtc:utc,aircraft:'Synthetic fixture'}}}});
const project=value=>homePresentation(value,now);
test('Home precedence keeps active flight, draft and selected plan identities independent',()=>{
  const value=fixture();assert.equal(project(value).next,'idle');
  value.intelligence.flight.connection.state='disconnected';assert.equal(project(value).next,'connect');
  value.snapshot={selectedPlanId:'plan',activeMission:null};value.plan={id:'plan',name:'Later saved plan'};
  assert.equal(project(value).next,'plan');
  value.draft={name:'Unsaved draft'};assert.equal(project(value).next,'draft');
  value.intelligence.flight.activeMission={id:'active',name:'Original frozen flight'};value.snapshot.activeMission={id:'active'};
  const before=JSON.stringify(value),result=project(value);
  assert.equal(result.next,'active');assert.equal(result.recording,'active');assert.equal(result.science,'unavailable');
  assert.equal(result.activeFlight.name,'Original frozen flight');assert.equal(result.selectedPlan.name,'Later saved plan');assert.equal(result.draft.name,'Unsaved draft');
  assert.equal(JSON.stringify(value),before);assert.ok(Object.isFrozen(result));
});
test('failed, stale, future, unsupported and wrong-flight replies never confirm activity',()=>{
  for(const change of [v=>v.failed=true,v=>v.intelligence.version=2,v=>v.intelligence.generatedUtc=new Date(now-6001).toISOString(),
    v=>v.intelligence.generatedUtc=new Date(now+2001).toISOString(),v=>v.snapshot={activeMission:{id:'new-flight'}},v=>delete v.intelligence.flight]){
    const value=fixture();change(value);const result=project(value);
    assert.equal(result.next,'unavailable');assert.equal(result.recording,'unknown');assert.equal(result.science,'unknown');assert.equal(result.counts,null);
  }
});
test('paused, stale and disconnected simulator input never presents current science from a fresh companion reply',()=>{
  for(const [kind,change]of [['paused',v=>v.telemetry.isPaused=true],['stale',v=>v.telemetry.timestampUtc=new Date(now-6001).toISOString()],
    ['stale',v=>v.telemetry.timestampUtc=new Date(now+2001).toISOString()],['disconnected',v=>v.connection.state='disconnected']]){
    const value=fixture();change(value.intelligence.flight);value.intelligence.flight.activeMission={id:'flight'};
    value.intelligence.collection={state:{missionId:'flight',phase:'collecting',currentSessionId:'session'}};
    const result=project(value);assert.equal(result.next,'active');assert.equal(result.recording,'active');assert.equal(result.connection,kind);assert.equal(result.science,'unavailable');
  }
});
test('only fresh mission-bound session and retained original counts are presented, including real zero',()=>{
  const value=fixture();value.intelligence.flight.activeMission={id:'flight'};
  value.intelligence.collection={state:{missionId:'flight',phase:'collecting',currentSessionId:'session'}};
  value.intelligence.retainedScience={missionId:'flight',checkedUtc:utc,originalObservations:0,derivedObservations:2,remoteProducts:0,status:'Retained original records'};
  assert.equal(project(value).science,'collecting');assert.equal(project(value).counts.originalObservations,0);
  for(const change of [r=>r.missionId='other',r=>r.originalObservations=null,r=>r.remoteProducts=-1,r=>r.derivedObservations=NaN,r=>r.checkedUtc=new Date(now-6001).toISOString()]){
    const copy=structuredClone(value);change(copy.intelligence.retainedScience);assert.equal(project(copy).counts,null);
  }
  delete value.intelligence.collection.state.currentSessionId;assert.equal(project(value).science,'unknown');
  value.intelligence.collection.state.currentSessionId='session';value.intelligence.collection.fault='Storage unavailable';assert.equal(project(value).science,'unavailable');
  value.intelligence.collection.fault=false;value.intelligence.collection.state.holds=[{kind:'storage',reason:'Original storage hold'}];
  assert.equal(project(value).science,'unavailable');assert.equal(project(value).collectionIssue,true);
  value.intelligence.collection.state.missionId='old';assert.equal(project(value).science,'unknown');
});
test('missing selected plan stays selected; archive errors are distinct from confirmed empty results',()=>{
  const value=fixture();value.snapshot={selectedPlanId:'missing',activeMission:null};value.planStatus='unavailable';
  assert.equal(project(value).next,'plan');assert.equal(project(value).planUnavailable,true);assert.equal(project(value).selectedPlan,null);
  value.archive=[{id:'saved',name:'Real record',status:'completed'},{id:'current',name:'Still recording',status:'recording'}];
  for(const status of ['loading','unavailable']){value.archiveStatus=status;assert.deepEqual(project(value).results,[]);assert.equal(project(value).archiveStatus,status);}
  value.archiveStatus='available';assert.deepEqual(project(value).results.map(r=>r.id),['saved']);
  value.archive=[];assert.deepEqual(project(value).results,[]);assert.equal(project(value).archiveStatus,'available');
});
