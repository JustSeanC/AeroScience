import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {spawn} from 'node:child_process';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {resolve,join} from 'node:path';
const origin='http://127.0.0.1:5098';let child,root;
test.use({baseURL:origin});
async function pair(request){const response=await request.post('/api/session',{headers:{Origin:origin},data:{}});expect(response.ok()).toBeTruthy();return response.json();}
async function post(request,path,data={}){const session=await pair(request);const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(response.ok(),await response.text()).toBeTruthy();return response.json();}
async function state(request){return(await request.get('/api/state')).json();}
test.beforeAll(async()=>{
 root=mkdtempSync(join(tmpdir(),'aeroscience-starter-ui-'));let diagnostic='';
 child=spawn(process.env.DOTNET||'dotnet',[resolve('artifacts/browser-test-host/AeroScience.TestHost.dll')],{env:{...process.env,AeroScience__DataDirectory:root,AeroScience__Port:'5098',AeroScience__EnableLan:'false',AeroScience__SamplingIntervalMs:'250'},stdio:['ignore','pipe','pipe']});
 const capture=data=>{diagnostic=(diagnostic+data.toString()).slice(-16000);};child.stdout.on('data',capture);child.stderr.on('data',capture);child.on('error',error=>capture(error.message));
 try{await expect.poll(async()=>{try{return(await fetch(origin+'/api/health')).ok;}catch{return false;}},{timeout:15000}).toBeTruthy();}
 catch(error){throw new Error(`Research fixture did not become healthy (exit ${child.exitCode}): ${diagnostic}`,{cause:error});}
});
test.beforeEach(async({request})=>{await pair(request);const current=await state(request);if(current.activeMission)await post(request,`/api/missions/${current.activeMission.id}/end`);await post(request,'/api/mission-selection',{planId:null});await post(request,'/api/testing/operating/input',{});await expect.poll(async()=>(await state(request)).connection.state).toBe('connected');});
test.afterEach(async({request})=>{const current=await state(request);if(current.activeMission)await post(request,`/api/missions/${current.activeMission.id}/end`);await post(request,'/api/mission-selection',{planId:null});});
test.afterAll(async()=>{if(child?.exitCode===null){const exit=new Promise(r=>child.once('exit',r));child.kill('SIGKILL');await exit;}if(root)rmSync(root,{recursive:true,force:true});});
const starter=page=>page.locator('#research-starters').getByRole('article',{name:'Flight-Regime Validation starter',exact:true});

test('Fly Now uses prepared defaults, normal readiness/start, independent attempts and recorded review',async({page,request})=>{
 await page.goto('/#my-research');const choice=starter(page);await expect(choice).toContainText('Example targets');await expect(choice).toContainText('No extra scientific instrument');
 await page.getByLabel('Test card name',{exact:true}).fill('Keep my unsaved card draft');
 let launches=0;page.on('request',r=>{if(r.method()==='POST'&&new URL(r.url()).pathname==='/api/missions')launches++;});
 await choice.getByRole('button',{name:'Fly Now',exact:true}).click();const guide=page.locator('#research-launch-guide');await expect(guide).toContainText('120 ± 5 knots IAS');await expect(guide).toContainText('30 second hold');expect(launches).toBe(0);expect((await state(request)).activeMission).toBeNull();
 const planId=(await state(request)).selectedPlanId;expect(planId).toBeTruthy();
 let release,captured;const held=new Promise(resolve=>captured=resolve),gate=new Promise(resolve=>release=resolve);
 await page.route('**/api/plans/readiness',async route=>{const response=await route.fetch();captured();await gate;await route.fulfill({response});});
 try{
  await guide.getByText('Review Science and Storage Readiness',{exact:true}).click();await guide.getByRole('button',{name:'Evaluate science readiness',exact:true}).click();await held;
  await page.getByRole('button',{name:'Refresh Missions',exact:true}).click();await expect(page.locator('.mission-selection > [role="status"]')).toHaveText('Plan selected · Recording has not started');await expect(guide).toContainText('Assessing draft…');
  release();await expect(guide).toContainText('No data was downloaded');
 }finally{release();await page.unroute('**/api/plans/readiness');}
 let readiness=0;page.on('request',r=>{if(new URL(r.url()).pathname===`/api/plans/${planId}/preparation/start-readiness`)readiness++;});
 await page.getByRole('button',{name:'Start mission',exact:true}).click();await expect(page.getByRole('button',{name:'End mission',exact:true})).toBeVisible();expect(readiness).toBe(1);expect(launches).toBe(1);
 const flight=(await state(request)).activeMission;const controls=page.locator('#research-flight-controls');await controls.getByRole('button',{name:'Activate Card',exact:true}).click();await controls.getByRole('button',{name:'Begin Attempt',exact:true}).click();await controls.getByRole('button',{name:'End Attempt',exact:true}).click();expect((await state(request)).activeMission.id).toBe(flight.id);
 const ended=page.waitForResponse(r=>r.request().method()==='POST'&&new URL(r.url()).pathname===`/api/missions/${flight.id}/end`);await page.getByRole('button',{name:'End mission',exact:true}).click();expect((await ended).ok()).toBeTruthy();
 await page.goto('/#my-research?flight='+flight.id);await expect(page.getByLabel('Test card name',{exact:true})).toHaveValue('Keep my unsaved card draft');const timeline=page.getByRole('region',{name:'Test-card activity and evidence',exact:true});await timeline.getByRole('button',{name:'Evaluate Attempt',exact:true}).click();await expect(timeline).toContainText('Insufficient Evidence');
 const recorded=(await(await request.get(`/api/missions/${flight.id}/research`)).json()).planSnapshot;expect(recorded.id).toBe(planId);expect(recorded.researchStarter.starterId).toBe('flight-regime');expect(recorded.instruments.every(i=>!i.enabled)).toBeTruthy();
});

test('Customize edits an independent card inside the full planner and a failed save preserves its draft and revision',async({page,request})=>{
 await page.goto('/#my-research');await starter(page).getByRole('button',{name:'Customize',exact:true}).click();const form=page.locator('#plan-form');
 await expect(form.getByLabel('Experiment: Indicated airspeed target (knots)',{exact:true})).toHaveValue('120');await expect(form.getByLabel('Experiment: Altitude target (feet MSL)',{exact:true})).toHaveValue('3000');await expect(form.getByRole('button',{name:'Add waypoint',exact:true})).toBeVisible();
 const planId=new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('researchPlan'),before=await(await request.get(`/api/plans/${planId}`)).json();
 await form.getByLabel('Plan name',{exact:true}).fill('My customized research');await form.getByLabel('Experiment name',{exact:true}).fill('My independent experiment');await form.getByLabel('Experiment: Indicated airspeed target (knots)',{exact:true}).fill('140');await form.getByLabel('Experiment: Required continuous hold (seconds)',{exact:true}).fill('5');const notes=form.getByLabel('Preflight notes',{exact:true});await notes.locator('xpath=ancestor::details[1]').locator('summary').first().click();await notes.fill('Keep my planned science');await form.locator('[data-instrument="ambient-temperature"]').check();
 let blocked=false;await page.route('**/api/plans',async route=>{if(route.request().method()==='POST'&&!blocked){blocked=true;await route.fulfill({status:503,contentType:'application/json',body:JSON.stringify({error:'Synthetic one-time plan save failure'})});}else await route.continue();});
 await form.getByRole('button',{name:'Save mission plan',exact:true}).click();await expect(page.locator('#notice')).toContainText('Synthetic one-time plan save failure');await expect(form.getByLabel('Plan name',{exact:true})).toHaveValue('My customized research');await expect(form.getByLabel('Experiment: Indicated airspeed target (knots)',{exact:true})).toHaveValue('140');
 await form.getByRole('button',{name:'Save mission plan',exact:true}).click();await expect(page.getByText('Mission plan saved · Ready to start mission',{exact:false})).toBeVisible();const saved=await(await request.get(`/api/plans/${planId}`)).json();expect(saved.researchCard.revision).toBe(2);expect(saved.instruments.some(i=>i.instrumentId==='ambient-temperature'&&i.enabled)).toBeTruthy();expect(saved.preflightNotes).toBe('Keep my planned science');
 const original=await(await request.get(`/api/research/cards/${before.researchCard.id}?revision=1`)).json();expect(original.flightRegime.airspeedKnots).toBe(120);expect((await(await request.get(`/api/research/cards/${before.researchCard.id}`)).json()).revision).toBe(2);expect((await state(request)).activeMission).toBeNull();
 await form.getByRole('link',{name:'Back to My Research',exact:true}).click();await expect(starter(page)).toBeVisible();
});

test('prepared experiment targets remain accessible and return to My Research at narrow widths',async({page})=>{
 await page.setViewportSize({width:375,height:812});await page.goto('/#my-research');await starter(page).getByRole('button',{name:'Customize',exact:true}).click();
 const targets=page.locator('#research-starter-targets');await expect(targets.getByLabel('Experiment: Indicated airspeed target (knots)',{exact:true})).toHaveValue('120');
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();expect((await new AxeBuilder({page}).include('#research-starter-targets').analyze()).violations).toEqual([]);
 await page.getByRole('link',{name:'Back to My Research',exact:true}).click();await expect(starter(page)).toBeVisible();
});

test('a lost preparation response, reload and repeated clicks reuse one card/plan without auto-starting',async({page,request})=>{
 await page.goto('/#my-research');const bodies=[];let lose=true;
 await page.route('**/api/research/starters/prepare',async route=>{bodies.push(route.request().postDataJSON());const response=await route.fetch();if(lose){lose=false;await route.abort('failed');}else await route.fulfill({response});});
 await starter(page).getByRole('button',{name:'Fly Now',exact:true}).click();await expect(starter(page).getByRole('status')).not.toHaveText('Preparing your local experiment…');expect((await state(request)).activeMission).toBeNull();
 await page.reload();await starter(page).getByRole('button',{name:'Fly Now',exact:true}).dblclick();await expect(page.locator('#research-launch-guide')).toBeVisible();expect(bodies).toHaveLength(2);expect(bodies[0].requestId).toBe(bodies[1].requestId);
 const id=bodies[0].requestId,plan=await(await request.get(`/api/plans/${id}`)).json();expect(plan.researchStarter.requestId).toBe(id);expect((await state(request)).selectedPlanId).toBe(id);expect((await state(request)).activeMission).toBeNull();
 const card=await(await request.get(`/api/research/cards/${plan.researchCard.id}`)).json();expect(card.revision).toBe(plan.researchCard.revision);
});

test('Customize preserves a declined mission draft and reopens the same prepared copy when retried',async({page,request})=>{
 await page.goto('/#planner');await page.getByLabel('Plan name',{exact:true}).fill('Do not discard my mission draft');await page.getByRole('link',{name:'My Research',exact:true}).first().click();
 const ids=[];page.on('request',r=>{if(r.method()==='POST'&&new URL(r.url()).pathname==='/api/research/starters/prepare')ids.push(r.postDataJSON().requestId);});
 page.once('dialog',d=>d.dismiss());await starter(page).getByRole('button',{name:'Customize',exact:true}).click();await expect(page).toHaveURL(/#planner$/);await expect(page.locator('#plan-form')).toBeVisible();await expect(page.getByLabel('Plan name',{exact:true})).toHaveValue('Do not discard my mission draft');
 await page.getByRole('link',{name:'My Research',exact:true}).first().click();page.once('dialog',d=>d.accept());await starter(page).getByRole('button',{name:'Customize',exact:true}).click();await expect(page.locator('#research-starter-targets')).toBeVisible();expect(ids).toHaveLength(2);expect(ids[0]).toBe(ids[1]);expect((await state(request)).activeMission).toBeNull();
});

test('disconnected pilots can prepare offline and get honest guidance without bypassing recording requirements',async({page,request})=>{
 await post(request,'/api/testing/operating/input',{disconnected:true});await expect.poll(async()=>(await state(request)).connection.state).toBe('disconnected');await page.route('https://**',r=>r.abort());
 await page.setViewportSize({width:390,height:844});await page.goto('/#my-research');const choice=starter(page);await choice.getByRole('button',{name:'Fly Now',exact:true}).focus();await page.keyboard.press('Tab');await expect(choice.getByRole('button',{name:'Customize',exact:true})).toBeFocused();expect((await new AxeBuilder({page}).include('#research-starters').analyze()).violations).toEqual([]);
 await choice.getByRole('button',{name:'Fly Now',exact:true}).click();await expect(page.locator('#research-launch-guide')).toContainText('Connect MSFS 2024 and load an aircraft');await expect(page.getByRole('button',{name:'Start mission',exact:true})).toBeDisabled();expect((await state(request)).activeMission).toBeNull();
 await page.getByRole('button',{name:'Edit in Mission Planner',exact:true}).click();await expect(page.locator('#plan-form')).toContainText('Personal Research test card');await expect(page.getByRole('link',{name:'Back to My Research',exact:true})).toBeVisible();expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
});
