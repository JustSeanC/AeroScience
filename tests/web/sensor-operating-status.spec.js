import {test,expect,request as playwrightRequest} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {spawn} from 'node:child_process';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {resolve,join} from 'node:path';
const origin='http://127.0.0.1:5097';let folder,child,client,csrf;
async function post(path,data={}){const r=await client.post(path,{headers:{Origin:origin,'X-AeroScience-CSRF':csrf},data});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
async function get(path){const r=await client.get(path);expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
const settings=(id='thermal-imager',extra={})=>({instrumentId:id,enabled:true,intervalMs:1000,...extra});
async function begin(instruments=[settings()],extra={}){return post('/api/missions',{name:'SYNTHETIC operating UX '+crypto.randomUUID(),mode:'realistic',instruments,...extra});}
async function control(id,action){const s=(await get(`/api/missions/${id}/collection`)).state;return post(`/api/missions/${id}/collection/control`,{version:1,missionId:id,requestId:crypto.randomUUID(),expectedRevision:s.revision,expectedSessionId:s.currentSessionId,action});}
async function card(id='thermal-imager'){return(await get('/api/instruments/operating-status')).instruments.find(c=>c.instrumentId===id);}
async function view(browser,route='science',width=1280){const context=await browser.newContext({baseURL:origin,storageState:await client.storageState(),viewport:{width,height:950}});const page=await context.newPage();await page.goto('/#'+route);const panel=page.locator(route==='science'?'#operating-reports':'#mission-intelligence-sensors');await expect(panel.getByRole('heading',{name:'Sensor Operating Status',exact:true})).toBeVisible();return{context,page,panel};}
test.beforeAll(async()=>{
 folder=mkdtempSync(join(tmpdir(),'aeroscience-sensor-ux-'));child=spawn(process.env.DOTNET||'dotnet',[resolve('artifacts/browser-test-host/AeroScience.TestHost.dll')],{env:{...process.env,AeroScience__DataDirectory:folder,AeroScience__Port:'5097',AeroScience__EnableLan:'false',AeroScience__SamplingIntervalMs:'250'},stdio:'ignore'});
 await expect.poll(async()=>{try{return(await fetch(origin+'/api/health')).ok;}catch{return false;}},{timeout:15000}).toBeTruthy();client=await playwrightRequest.newContext({baseURL:origin});csrf=(await(await client.post('/api/session',{headers:{Origin:origin},data:{}})).json()).csrf;
});
test.beforeEach(async()=>{const s=await get('/api/state');if(s.activeMission)await post(`/api/missions/${s.activeMission.id}/end`);await post('/api/mission-selection',{planId:null});await post('/api/instruments',[]);await post('/api/testing/operating/input',{});await expect.poll(async()=>{const state=await get('/api/state');return state.connection.state==='connected'&&!!state.telemetry&&Date.now()-Date.parse(state.telemetry.timestampUtc)<6000;}).toBeTruthy();});
test.afterEach(async()=>{const s=await get('/api/state');if(s.activeMission)await post(`/api/missions/${s.activeMission.id}/end`);});
test.afterAll(async()=>{await client?.dispose();if(child&&child.exitCode===null){const done=new Promise(r=>child.once('exit',r));child.kill('SIGKILL');await done;}if(folder)rmSync(folder,{recursive:true,force:true});});

test('both pages use real supported geometry with separate missing external data and collapsed technical evidence',async({browser})=>{
 await begin();await expect.poll(async()=>(await card()).operatingStatus).toBe('withinCheckedLimits');expect((await card()).scientificData.explanation).toContain('does not create a live camera image');
 const {context,page,panel}=await view(browser);try{const c=panel.locator('[data-sensor="thermal-imager"]');await expect(c).toContainText('Thermal Imager');await expect(c).toContainText('Operating Conditions: Within Checked Limits');await expect(c).not.toContainText('Scientific Data: Ready');await expect(c.locator('details')).not.toHaveAttribute('open','');
  await c.getByText('View Technical Details · thermal-imager',{exact:true}).click();await expect(c).toContainText('Derived validity:');await expect(c).toContainText('Source QA:');await expect(c).toContainText('Calibration:');
  const summary=c.locator('summary');await summary.focus();const reads=[];page.on('request',r=>reads.push(r.url()));await page.waitForTimeout(3400);await expect(summary).toBeFocused();await expect(c.locator('details')).toHaveAttribute('open','');expect(reads.filter(u=>u.endsWith('/api/instruments/operating-status'))).toHaveLength(0);expect(await page.getByRole('heading',{name:'Sensor Operating Status',exact:true}).count()).toBe(1);
  await page.goto('/#control');const compact=page.locator('#mission-intelligence-sensors');await expect(compact).toContainText('Operating Conditions: Within Checked Limits');await expect(compact).not.toContainText('Scientific Data: Ready');
 }finally{await context.close();}
});
test('valid original simulator channels do not establish undocumented hardware limits',async({browser})=>{
 await begin([settings('ambient-temperature')]);await expect.poll(async()=>(await card('ambient-temperature')).scientificData.status).toBe('ready');const {context,panel}=await view(browser);try{const c=panel.locator('[data-sensor="ambient-temperature"]');await expect(c).toContainText('Operating Conditions: Cannot Assess');await expect(c).toContainText('Scientific Data: Ready');await expect(c).toContainText('No documented operating checks');}finally{await context.close();}
});
test('documented internal temperature remains unknown while RIEGL minimum range gets actionable caution',async({browser})=>{
 const profile=(await get('/api/instrument-profiles')).profiles.find(p=>p.instrumentId==='terrain-scanner');await post('/api/testing/operating/input',{altitudeAglFeet:100});await begin([settings('terrain-scanner',{payload:{profile}})]);
 await expect.poll(async()=>(await card('terrain-scanner')).operatingStatus).toBe('caution');const {context,panel}=await view(browser);try{const c=panel.locator('[data-sensor="terrain-scanner"]');await expect(c).toContainText('100 m minimum');await expect(c).toContainText('Internal sensor temperature is unavailable');await expect(c).toContainText('not a sensor failure');await expect(c).toContainText('not measured laser range');await expect(c).not.toContainText('Scientific Data: Ready');
  await post('/api/testing/operating/input',{altitudeAglFeet:1000});await expect(c).toContainText('Operating Conditions: Cannot Assess');await expect(c).not.toContainText('Operating Conditions: Within Checked Limits');
 }finally{await context.close();}
});
test('invalid footprint and absent AGL have different defensible explanations without invented bank limits',async({browser})=>{
 await post('/api/testing/operating/input',{pitchDegrees:85});await begin();const {context,panel}=await view(browser);try{const c=panel.locator('[data-sensor="thermal-imager"]');await expect(c).toContainText('Operating Conditions: Caution');await expect(c).toContainText('cannot produce a defensible ground-intersecting footprint');await expect(c).toContainText('No unsupported bank, pitch');
  await post('/api/testing/operating/input',{altitudeAglFeet:null});await expect(c).toContainText('Operating Conditions: Cannot Assess');await expect(c).toContainText('MSL is not a substitute');
 }finally{await context.close();}
});
test('saved overlap and collection interval guidance comes from actual P04 plan assessment',async({browser})=>{
 const p=await post('/api/plans',{id:crypto.randomUUID(),name:'Synthetic spacing target',mode:'realistic',instruments:[settings('thermal-imager',{intervalMs:60000})],waypoints:[{name:'A',latitude:40,longitude:-75},{name:'B',latitude:40.01,longitude:-75}],sensorPlanning:{instrumentId:'thermal-imager',altitudeAglMetres:300,groundspeedMetresPerSecond:50,forwardOverlap:.9,sideOverlap:.9},patterns:[{id:crypto.randomUUID(),kind:'lawnmower',center:{latitude:40,longitude:-75},spacingMetres:1000}]});
 await post('/api/mission-selection',{planId:p.id});await post('/api/missions',{name:p.name,planId:p.id});const {context,panel}=await view(browser);try{const c=panel.locator('[data-sensor="thermal-imager"]');await expect(c).toContainText('Operating Conditions: Caution');await expect(c).toContainText('forward-overlap target');await expect(c).toContainText('collection interval');await expect(c).toContainText('track spacing');await expect(c).toContainText('not valid source-pixel coverage');}finally{await context.close();}
});
test('armed paused resumed ended and new sessions never reuse the wrong favorable assessment',async({browser})=>{
 const flight=await begin([settings()],{armScience:true});const {context,panel}=await view(browser,'control');try{const c=panel.locator('[data-sensor="thermal-imager"]');await expect(c).toContainText('Operating Conditions: Not Collecting');await expect(c).toContainText('Armed');
  await control(flight.id,'startData');await expect(c).toContainText('Operating Conditions: Within Checked Limits');const first=(await card()).sessionId;
  await control(flight.id,'pauseData');await expect(c).toContainText('Operating Conditions: Not Collecting');await expect(c).toContainText('Collection: Paused');
  await control(flight.id,'resumeData');await expect(c).toContainText('Operating Conditions: Within Checked Limits');
  await post('/api/instruments',[settings('thermal-imager',{intervalMs:2000})]);await expect.poll(async()=>(await card()).configuration.revision).toBe(2);await expect.poll(async()=>{const v=await card();return v.report?.assessment.configurationId===v.configuration.id;}).toBeTruthy();
  await control(flight.id,'endData');await expect(c).toContainText('Operating Conditions: Not Collecting');await control(flight.id,'startData');await expect.poll(async()=>(await card()).sessionId).not.toBe(first);await expect(c).toContainText((await card()).sessionId);await expect(c).toContainText('Operating Conditions: Within Checked Limits');
 }finally{await context.close();}
});
test('stale disconnected telemetry and lost presentation transport cannot retain favorable live status',async({browser})=>{
 await begin();const {context,page,panel}=await view(browser);try{const c=panel.locator('[data-sensor="thermal-imager"]');await expect(c).toContainText('Operating Conditions: Within Checked Limits');
  await page.route('**/api/mission-intelligence',r=>r.abort('failed'));await expect(c).toContainText('Operating Conditions: Cannot Assess');await expect(c).toContainText('Scientific Data: Unknown');await expect(panel).toContainText('unavailable');await page.unroute('**/api/mission-intelligence');await expect(c).toContainText('Operating Conditions: Within Checked Limits');
  await post('/api/testing/operating/input',{ageSeconds:20});await expect(c).not.toContainText('Operating Conditions: Within Checked Limits');await expect(c).toContainText('Evidence:');
  await post('/api/testing/operating/input',{disconnected:true});await expect(c).not.toContainText('Operating Conditions: Within Checked Limits');await expect(c).toContainText('Scientific Data: Unknown');
 }finally{await context.close();}
});
test('paired-browser sized layout and keyboard details expose color-independent labels without popups',async({browser})=>{
 await begin([settings(),settings('ambient-temperature')]);const {context,page,panel}=await view(browser,'science',390);try{await expect(panel.locator('[data-sensor="thermal-imager"]')).toContainText('Operating Conditions: Within Checked Limits');await expect(panel.locator('[data-sensor="ambient-temperature"]')).toContainText('Scientific Data: Ready');
  const summary=panel.locator('[data-sensor="thermal-imager"] summary');await summary.focus();await page.keyboard.press('Enter');await expect(summary.locator('..')).toHaveAttribute('open','');await expect(summary).toBeFocused();await expect(page.locator('dialog[open]')).toHaveCount(0);expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();expect((await new AxeBuilder({page}).include('#operating-reports').analyze()).violations).toEqual([]);await page.evaluate(()=>document.documentElement.style.fontSize='200%');expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
 }finally{await context.close();}
});
test('archive reports remain identical after live view configuration changes and End Flight',async({browser})=>{
 const flight=await begin();await expect.poll(async()=>(await card()).report!=null).toBeTruthy();const reports=await get(`/api/missions/${flight.id}/operating`),cutoff=reports[0].assessment.assessedUtc;
 const before=await get(`/api/missions/${flight.id}/operating?at=${encodeURIComponent(cutoff)}`);const {context,panel}=await view(browser);try{await expect(panel).toContainText('Sensor Operating Status');await post('/api/instruments',[settings('thermal-imager',{intervalMs:2000})]);await post(`/api/missions/${flight.id}/end`);await expect(panel.locator('[data-sensor="thermal-imager"]')).toContainText('Operating Conditions: Not Collecting');expect(await get(`/api/missions/${flight.id}/operating?at=${encodeURIComponent(cutoff)}`)).toEqual(before);}finally{await context.close();}
});
test('before flight and disabled instruments do not imply operating clearance',async({browser})=>{
 await post('/api/instruments',[settings()]);const {context,panel}=await view(browser);try{await expect(panel.locator('[data-sensor="thermal-imager"]')).toContainText('Operating Conditions: Not Collecting');await post('/api/instruments',[]);await expect(panel).toContainText('No instruments enabled');await panel.getByText('Disabled instruments (8)',{exact:true}).click();await expect(panel.locator('[data-sensor="thermal-imager"]')).toContainText('Operating Conditions: Disabled');}finally{await context.close();}
});
test('a stalled read expires favorable labels without waiting for a transport error',async({browser})=>{
 await begin();const {context,page,panel}=await view(browser);let release;try{const c=panel.locator('[data-sensor="thermal-imager"]');await expect(c).toContainText('Operating Conditions: Within Checked Limits');let seen,done;const arrived=new Promise(r=>seen=r),handled=new Promise(r=>done=r);await page.route('**/api/mission-intelligence',async route=>{seen();await new Promise(r=>release=r);try{await route.continue();}finally{done();}},{times:1});await arrived;await expect(c).toContainText('Operating Conditions: Cannot Assess',{timeout:8000});await expect(c).toContainText('Scientific Data: Unknown');await expect(panel).toContainText('expired');release();await handled;await expect(c).toContainText('Operating Conditions: Within Checked Limits');}finally{release?.();await context.close();}
});
