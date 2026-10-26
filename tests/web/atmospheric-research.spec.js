import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {spawn} from 'node:child_process';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {resolve,join} from 'node:path';
import {mountToolbar} from './helpers/toolbar.mjs';
const origin='http://127.0.0.1:5099';let root,child;
test.use({baseURL:origin});
async function boot(){child=spawn(process.env.DOTNET||'dotnet',[resolve('artifacts/browser-test-host/AeroScience.TestHost.dll')],{env:{...process.env,AeroScience__DataDirectory:root,AeroScience__Port:'5099',AeroScience__EnableLan:'false',AeroScience__SamplingIntervalMs:'1000'},stdio:'ignore'});await expect.poll(async()=>{try{return(await fetch(origin+'/api/health')).ok;}catch{return false;}},{timeout:15000}).toBeTruthy();}
async function stop(){if(child?.exitCode===null){const exit=new Promise(r=>child.once('exit',r));child.kill('SIGKILL');await exit;}}
async function post(request,path,data={}){const paired=await request.post('/api/session',{headers:{Origin:origin},data:{}});expect(paired.ok()).toBeTruthy();const {csrf}=await paired.json();const response=await request.post(path,{headers:{Origin:origin,'X-AeroScience-CSRF':csrf},data});expect(response.ok(),await response.text()).toBeTruthy();return response.json();}
async function get(request,path){const response=await request.get(path);expect(response.ok(),await response.text()).toBeTruthy();return response.json();}
const starter=page=>page.locator('#research-starters').getByRole('article',{name:'Vertical Atmospheric Profile starter',exact:true});
const cardRow=page=>page.locator('#my-research > article').filter({has:page.getByRole('heading',{name:'Vertical Atmospheric Profile',exact:true})});
test.beforeAll(async()=>{root=mkdtempSync(join(tmpdir(),'aeroscience-atmospheric-ui-'));await boot();});
test.beforeEach(async({request})=>{const resetAt=Date.now();await post(request,'/api/testing/operating/input',{researchAtmosphere:true,altitudeMslFeet:1100,temperatureCelsius:20,density:null});
 // Updating the provider is asynchronous. In particular, the prior offline test
 // leaves the last authoritative packet disconnected until the next provider tick.
 await expect.poll(async()=>{const s=await get(request,'/api/state');return s.connection.state==='connected'&&s.telemetry?.altitudeMslFeet===1100&&Date.parse(s.telemetry.timestampUtc)>=resetAt;}).toBeTruthy();
 const state=await get(request,'/api/state');if(state.activeMission)await post(request,`/api/missions/${state.activeMission.id}/end`);await post(request,'/api/mission-selection',{planId:null});});
test.afterEach(async({request},info)=>{const state=await get(request,'/api/state');if(info.status!==info.expectedStatus&&state.activeMission){const id=state.activeMission.id;await info.attach('retained-atmospheric-collection-diagnostics',{body:JSON.stringify({state,science:await get(request,`/api/missions/${id}/science`),events:await get(request,`/api/missions/${id}/replay/events?after=0`)}),contentType:'application/json'});}if(state.activeMission)await post(request,`/api/missions/${state.activeMission.id}/end`);await post(request,'/api/mission-selection',{planId:null});});
test.afterAll(async()=>{await stop();if(root)rmSync(root,{recursive:true,force:true});});

test('Fly Now collects actual atmospheric instruments and reviews original temperature versus altitude without inventing density',async({page,request})=>{
 await page.goto('/#my-research');await expect(starter(page)).toContainText('Collect temperature, pressure, wind and air-density');await starter(page).getByRole('button',{name:'Fly Now',exact:true}).click();
 const guide=page.locator('#research-launch-guide');await expect(guide).toContainText('3 neighboring 500-foot MSL bands');await expect(guide).toContainText('ensure science is Collecting');expect((await get(request,'/api/state')).activeMission).toBeNull();
 const plan=await get(request,'/api/plans/'+(await get(request,'/api/state')).selectedPlanId);expect(plan.template).toBe('atmosphericProfile');expect(plan.instruments.filter(i=>i.enabled)).toHaveLength(4);const definition=(await get(request,`/api/research/cards/${plan.researchCard.id}?revision=${plan.researchCard.revision}`)).atmosphericProfile;
 await page.getByRole('button',{name:'Start mission',exact:true}).click();await expect(page.getByRole('button',{name:'End mission',exact:true})).toBeVisible();const flight=(await get(request,'/api/state')).activeMission;
 for(const [altitude,temp]of [[1100,20],[1600,17],[2100,14]]){
  await post(request,'/api/testing/operating/input',{researchAtmosphere:true,altitudeMslFeet:altitude,temperatureCelsius:temp,density:null});
  // A startup/session edge can split the first retained reading from the next run.
  // Collect a fourth real reading; keep the three-consecutive-reading product rule.
  // Allow four real acquisition intervals plus the card's bounded gap allowance;
  // the default five-second assertion deadline barely fits four 1 Hz readings.
  // The 30-second test deadline and final consecutive-reading/gap checks remain.
  const collectionDeadline=4*plan.instruments.find(i=>i.instrumentId==='ambient-temperature').intervalMs+definition.maximumGapSeconds*1000;
  await expect.poll(async()=>(await get(request,`/api/missions/${flight.id}/science`)).observations.filter(o=>o.measurementId==='temperature'&&o.altitudeMslFeet===altitude&&o.quality==='valid').length,{timeout:collectionDeadline}).toBeGreaterThanOrEqual(4);
 }
 await page.getByRole('button',{name:'End mission',exact:true}).click();await expect(page.getByRole('button',{name:'Start mission',exact:true})).toBeVisible();await page.goto('/#my-research?flight='+flight.id);
 await page.getByRole('button',{name:'Evaluate Atmospheric Profile',exact:true}).click();const result=page.getByRole('region',{name:'Atmospheric research profile',exact:true});await expect(result).toContainText('Completed collection — sufficient evidence');await expect(result).toContainText('Original observation, definition, event and frozen plan hashes match');
 const points=await result.locator('canvas').evaluate(canvas=>window.Chart.getChart(canvas).data.datasets[0].data);expect(new Set(points.map(p=>p.y))).toEqual(new Set([20,17,14]));expect(new Set(points.map(p=>p.x))).toEqual(new Set([1100,1600,2100]));
 const saved=(await get(request,`/api/research/attempts?flightId=${flight.id}`))[0],original=await get(request,`/api/research/attempts/${saved.id}/atmospheric`);
 expect(points).toEqual(original.review.preview.filter(o=>o.measurementId==='temperature').map(o=>({x:o.altitudeMslFeet,y:o.value})));
 await result.getByLabel('Atmospheric measurement',{exact:true}).selectOption('density');await expect(result).toContainText('No verified retained observations for this quantity');expect((await get(request,'/api/state')).activeMission).toBeNull();
 await result.getByRole('button',{name:'Inspect Reading',exact:true}).first().click();await expect(result).toContainText('Original Evidence Near');await expect(result.getByRole('region',{name:'Retained scientific observations',exact:true})).toBeVisible();
});

test('Customize edits independent atmospheric criteria in the full Planner with accessible narrow-screen controls',async({page,request})=>{
 await page.setViewportSize({width:390,height:844});await page.goto('/#my-research');await starter(page).getByRole('button',{name:'Customize',exact:true}).focus();await page.keyboard.press('Enter');const form=page.locator('#plan-form');
 await form.getByLabel('Experiment name',{exact:true}).fill('My attainable atmospheric experiment');await form.getByLabel('Experiment: Altitude band width (feet)',{exact:true}).fill('200');await form.getByLabel('Experiment: Neighboring bands required',{exact:true}).fill('2');await form.getByLabel('Experiment: Scientific question',{exact:true}).fill('How does the sampled air change during my climb?');
 await form.getByLabel('Plan name',{exact:true}).fill('My attainable atmosphere profile');await form.getByRole('button',{name:'Save mission plan',exact:true}).click();await expect(page.getByText('Mission plan saved · Ready to start mission',{exact:false})).toBeVisible();
 const planId=new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('researchPlan'),plan=await get(request,'/api/plans/'+planId),card=await get(request,`/api/research/cards/${plan.researchCard.id}`);
 expect(card.name).toBe('My attainable atmospheric experiment');expect(card.atmosphericProfile.bandWidthFeet).toBe(200);expect(card.atmosphericProfile.requiredBands).toBe(2);expect(card.flightRegime).toBeUndefined();expect(card.revision).toBe(2);expect((await get(request,`/api/research/cards/${card.id}?revision=1`)).atmosphericProfile.bandWidthFeet).toBe(500);
 expect((await new AxeBuilder({page}).include('#research-starter-targets').analyze()).violations).toEqual([]);expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
});

test('retained atmospheric profiles reopen offline after process restart and compare only original like-for-like quantities',async({page,request})=>{
 const results=[];for(const bands of [3,2]){const f=await post(request,'/api/testing/research/atmosphere/'+bands);results.push(await post(request,'/api/research/attempts',{requestId:crypto.randomUUID(),flightId:f.missionId,startUtc:f.startUtc,endUtc:f.endUtc,cardId:f.cardId}));}
 expect(results[0].outcome).toBe('collectionComplete');expect(results[1].outcome).toBe('partialCollection');await stop();await boot();await post(request,'/api/testing/operating/input',{disconnected:true});await page.route('https://**',r=>r.abort());await page.goto('/#my-research');
 await cardRow(page).getByRole('button',{name:'Overview & Compare',exact:true}).click();const review=page.getByRole('region',{name:'Experiment overview and comparison',exact:true});await expect(review).toContainText('Completed / Partial / Insufficient');const totals=await get(request,`/api/research/cards/${results[0].card.id}/overview`);await expect(review).toContainText(`${totals.completedCollections} / ${totals.partialCollections} / ${totals.insufficientEvidence}`);
 for(const check of await review.getByRole('checkbox',{name:/Synthetic atmospheric profile/}).all())await check.check();await review.getByRole('button',{name:'Compare Selected Attempts',exact:true}).click();await expect(review).toContainText('Atmospheric Profiles — Like-for-Like Comparison');await expect(review.getByRole('table',{name:'Atmospheric collection comparison',exact:true})).toContainText('Original hashes match');
 expect(await review.locator('canvas').evaluate(canvas=>window.Chart.getChart(canvas).data.datasets.length)).toBe(2);expect((await get(request,'/api/state')).activeMission).toBeNull();
 const original=await get(request,`/api/research/attempts/${results[0].id}/atmospheric`);expect(original.result.evidence.sha256).toBe(results[0].evidence.sha256);expect(original.review.matchesSavedEvidence).toBe(true);expect(original.review.preview.some(o=>o.measurementId==='temperature'&&o.value===20)).toBe(true);
});

test('native toolbar renders atmospheric card criteria and keeps independent attempt controls',async({page,request})=>{
 const prepared=await post(request,'/api/research/starters/prepare',{version:1,requestId:crypto.randomUUID(),starterId:'vertical-atmospheric-profile',starterVersion:1,intent:'flyNow'});
 const flight=await post(request,'/api/missions',{name:'Native atmospheric controls',planId:prepared.plan.id});
 await page.route('**/__native_atmosphere',r=>r.fulfill({contentType:'text/html',body:'<!doctype html><html><head></head><body></body></html>'}));await page.goto('/__native_atmosphere');await mountToolbar(page);
 await page.evaluate(async()=>{const s=await(await fetch('/api/session',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).json();window.bus.callSimConnect=function(event,payload){const command=JSON.parse(payload);this.sent.push({event,command});return fetch('/api/testing/toolbar',{method:'POST',headers:{'Content-Type':'application/json','X-AeroScience-CSRF':s.csrf},body:payload}).then(r=>r.text()).then(reply=>this.handlers['AeroScience.Reply.v1']?.(reply));};document.querySelector('ingamepanel-aeroscience').tick();});
 const native=page.locator('#as-research-cards');await expect(native).toContainText('3 neighboring 500-foot MSL bands');await expect(native).not.toContainText('undefined');await native.getByRole('button',{name:'Activate Card',exact:true}).click();await native.getByRole('button',{name:'Begin Attempt',exact:true}).click();await expect(native).toContainText('Attempt recording');
 await native.getByRole('button',{name:'End Attempt',exact:true}).click();expect((await get(request,'/api/state')).activeMission.id).toBe(flight.id);expect((await get(request,`/api/missions/${flight.id}/collection`)).state.phase).toBe('collecting');
});
