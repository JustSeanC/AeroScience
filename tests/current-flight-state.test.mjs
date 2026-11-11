import test from 'node:test';
import assert from 'node:assert/strict';
import {currentFlightPresentation,recordedScannerSpacing} from '../src/AeroScience.Engine/wwwroot/current-flight-state.js';
const now=Date.parse('2026-11-11T12:00:00Z'),time=seconds=>new Date(now+seconds*1000).toISOString();
const fixture=()=>({version:1,generatedUtc:time(0),flight:{activeMission:{id:'flight',startedUtc:time(-120)},connection:{state:'connected'},telemetry:{timestampUtc:time(0),isValid:true}},collection:{state:{missionId:'flight',phase:'collecting',currentSessionId:'session'},currentSession:{id:'session',sequence:2},actions:[{action:'pauseData',available:true},{action:'endData',available:false}]},retainedScience:{missionId:'flight',originalObservations:0,derivedObservations:4,remoteProducts:2}});
test('cockpit presents actual recording, phase, session, zero counts and duration independently',()=>{
 const d=fixture(),before=JSON.stringify(d),m=currentFlightPresentation(d,d.flight,{now});assert.equal(m.recording,'active');assert.equal(m.science,'collecting');assert.equal(m.sessionSequence,2);assert.equal(m.counts.originalObservations,0);assert.equal(m.elapsedSeconds,120);assert.deepEqual(m.scienceActions,['pauseData']);assert.equal(JSON.stringify(d),before);
 for(const phase of ['armed','paused','betweenSessions']){d.collection.state.phase=phase;assert.equal(currentFlightPresentation(d,d.flight,{now}).science,phase);assert.equal(currentFlightPresentation(d,d.flight,{now}).recording,'active');}
});
test('stale, disconnected, paused, invalid, wrong mission and future data cannot become current science',()=>{
 for(const change of [d=>d.generatedUtc=time(-7),d=>d.generatedUtc=time(3),d=>d.flight.connection.state='disconnected',d=>d.flight.telemetry.timestampUtc=time(-7),d=>d.flight.telemetry.isPaused=true,d=>d.flight.telemetry.isValid=false,d=>d.collection.state.missionId='other',d=>d.collection.state.currentSessionId=null,d=>d.collection.fault=true]){const d=fixture();change(d);const m=currentFlightPresentation(d,d.flight,{now});assert.notEqual(m.science,'collecting');assert.deepEqual(m.scienceActions,[]);}
 const d=fixture();assert.equal(currentFlightPresentation(d,{...d.flight,activeMission:{id:'other'}},{now}).recording,'unknown');assert.equal(currentFlightPresentation(d,d.flight,{now,stale:true}).counts,null);
 d.collection.currentSession.id='other';assert.equal(currentFlightPresentation(d,d.flight,{now}).sessionId,null);
});
test('idle and ended state never borrows a previous scientific session or saved counts',()=>{
 const d=fixture();d.flight.activeMission=null;const m=currentFlightPresentation(d,d.flight,{now});assert.equal(m.recording,'inactive');assert.equal(m.science,'notStarted');assert.equal(m.counts,null);assert.equal(m.sessionId,null);assert.equal(m.elapsedSeconds,null);
});
const product=(id,seconds,latitude)=>({id,intentId:id,sourceId:'source',missionId:'flight',sessionId:'session',instrumentId:'terrain-scanner',validSampleCount:10,flightPacketUtc:time(seconds),footprint:{center:{latitude,longitude:0}}});
test('saved spacing uses two distinct supported same-session/source products and exact packet time',()=>{
 const a=product('a',-10,0),b=product('b',-5,.001),m=recordedScannerSpacing([a,b],{missionId:'flight',sessionId:'session',now});assert.equal(m.seconds,5);assert.ok(Math.abs(m.metres-111.195)<.001);assert.equal(m.firstId,'a');assert.equal(m.lastId,'b');
 for(const invalid of [{sessionId:'other'},{missionId:'other'},{sourceId:'other'},{validSampleCount:0},{intentId:'a'},{flightPacketUtc:time(-10)},{footprint:{center:{latitude:NaN,longitude:0}}}])assert.equal(recordedScannerSpacing([a,{...b,...invalid}],{missionId:'flight',sessionId:'session',now}),null);
 assert.equal(recordedScannerSpacing([a],{missionId:'flight',sessionId:'session',now}),null);assert.equal(recordedScannerSpacing([a,b],{missionId:'flight',now}),null);
});
