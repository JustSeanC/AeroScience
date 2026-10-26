import {test,expect,request as playwrightRequest} from '@playwright/test';
import {spawn} from 'node:child_process';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {resolve,join} from 'node:path';

test('process-killed flight retains frozen card markers and evidence without auto-resuming or passing',async()=>{
 test.setTimeout(60000);const root=mkdtempSync(join(tmpdir(),'aeroscience-card-restart-')),origin='http://127.0.0.1:5095';let child,client;
 async function start(){
  child=spawn(process.env.DOTNET||'dotnet',[resolve('artifacts/browser-test-host/AeroScience.TestHost.dll')],{env:{...process.env,AeroScience__DataDirectory:root,AeroScience__Port:'5095',AeroScience__EnableLan:'false',AeroScience__SamplingIntervalMs:'250'},stdio:'ignore'});
  await expect.poll(async()=>{try{return(await fetch(origin+'/api/health')).ok;}catch{return false;}},{timeout:15000}).toBeTruthy();
  client=await playwrightRequest.newContext({baseURL:origin});const session=await(await client.post('/api/session',{data:{},headers:{Origin:origin}})).json();
  return async(path,data={})=>{const r=await client.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();};
 }
 async function stop(){if(child&&child.exitCode===null){const done=new Promise(r=>child.once('exit',r));child.kill('SIGKILL');await done;}await client?.dispose();client=null;}
 try{
  let post=await start();await expect.poll(async()=>(await(await client.get('/api/state')).json()).telemetry).not.toBeNull();
  const f=(await(await client.get('/api/state')).json()).telemetry;
  const card=await post('/api/research/cards',{name:'Restart frozen card',flightRegime:{airspeedKnots:f.indicatedAirspeedKnots,airspeedToleranceKnots:30,altitudeMslFeet:f.altitudeMslFeet,altitudeToleranceFeet:1000,holdSeconds:1}});
  const plan=await post(`/api/research/cards/${card.id}/plan`,{id:card.id,revision:1}),flight=await post('/api/missions',{name:'Interrupted continuous flight',planId:plan.id});
  let state=await(await client.get(`/api/missions/${flight.id}/research-operations`)).json();
  await post('/api/research/control',{version:1,flightId:flight.id,requestId:crypto.randomUUID(),expectedRevision:state.revision,action:'activate',cardId:card.id,expectedAttemptId:null});
  state=await(await client.get(`/api/missions/${flight.id}/research-operations`)).json();
  const begin={version:1,flightId:flight.id,requestId:crypto.randomUUID(),expectedRevision:state.revision,action:'beginAttempt',cardId:card.id,expectedAttemptId:null};
  const marked=await post('/api/research/control',begin);
  await expect.poll(async()=>{const samples=await(await client.get(`/api/missions/${flight.id}/telemetry?limit=1000`)).json();return samples.filter(s=>Date.parse(s.timestampUtc)>=Date.parse(marked.receipt.timestampUtc)).length;}).toBeGreaterThan(5);
  const before=await(await client.get(`/api/missions/${flight.id}/telemetry?limit=1000`)).json();
  await post('/api/research/cards',{id:card.id,expectedRevision:1,name:card.name,flightRegime:{...card.flightRegime,airspeedKnots:400}});
  await stop();post=await start();state=await(await client.get(`/api/missions/${flight.id}/research-operations`)).json();
  expect(state.recording).toBe(false);expect(state.cards[0].active).toBe(false);expect(state.cards[0].openAttempt).toBeNull();expect(state.cards[0].card.revision).toBe(1);
  const attempts=await(await client.get(`/api/missions/${flight.id}/research-attempts`)).json();expect(attempts).toHaveLength(1);expect(attempts[0].status).toBe('interrupted');expect(attempts[0].beginEventId).toBe(marked.receipt.eventId);
  const result=await post(`/api/research/recorded-attempts/${attempts[0].id}/evaluate`,{requestId:crypto.randomUUID()});expect(result.outcome).toBe('insufficientEvidence');expect(result.card.revision).toBe(1);
  const duplicate=await post('/api/research/control',begin);expect(duplicate.receipt).toEqual(marked.receipt);expect(duplicate.state.cards[0].openAttempt).toBeNull();
  const after=await(await client.get(`/api/missions/${flight.id}/telemetry?limit=1000`)).json();expect(after.slice(0,before.length)).toEqual(before);
  expect((await(await client.get('/api/state')).json()).activeMission).toBeNull();expect(await(await client.get(`/api/missions/${flight.id}/research-attempts`)).json()).toHaveLength(1);
 }finally{await stop();rmSync(root,{recursive:true,force:true});}
});
