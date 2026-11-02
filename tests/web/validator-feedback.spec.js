import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {revealControl} from './helpers/disclosures.mjs';
import {mountToolbar} from './helpers/toolbar.mjs';
const origin='http://127.0.0.1:5080';
async function get(request,path){const r=await request.get(path);expect(r.ok(),await r.text()).toBe(true);return r.json();}
async function post(request,path,data={}){const session=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(r.ok(),await r.text()).toBe(true);return r.json();}
test.afterEach(async({request})=>{const s=await get(request,'/api/state');if(s.activeMission)await post(request,`/api/missions/${s.activeMission.id}/end`);await post(request,'/api/mission-selection',{planId:null});await post(request,'/api/instruments',[]);});
test('armed science starts directly in the main flight workflow and counts original observations',async({page,request})=>{
 await page.route('https://**',r=>r.abort());await page.goto('/#my-research');await page.getByRole('article',{name:'Vertical Atmospheric Profile starter',exact:true}).getByRole('button',{name:'Fly Now',exact:true}).click();
 const armed=page.getByLabel('Arm science without starting collection (flight recording starts normally)');await revealControl(armed);await armed.check();await page.getByRole('button',{name:'Start Recording',exact:true}).click();
 const journey=page.locator('#science-journey'),start=journey.getByRole('button',{name:'Start Science Collection',exact:true});await expect(journey).toContainText('Science collection has not started.');await expect(start).toBeVisible();await expect(start).toBeEnabled();
 const id=(await get(request,'/api/state')).activeMission.id;expect((await get(request,`/api/missions/${id}/science`)).observations).toHaveLength(0);
 await start.click();await expect(journey).toContainText('Science collection: Collecting');await expect.poll(async()=>(await get(request,`/api/missions/${id}/science`)).observations.length).toBeGreaterThan(0);
 const sensor=page.locator('#mission-intelligence-sensors [data-sensor=ambient-temperature]');await expect(sensor).toContainText(/Measurements: [1-9]/);expect((await sensor.innerText()).length).toBeLessThan(500);await expect(sensor.getByText('Sensor Details',{exact:true})).toBeVisible();await expect(sensor.getByText('Advanced Diagnostics',{exact:true})).toBeVisible();expect(await sensor.innerText()).not.toContain('Effective configuration');
 await page.getByRole('button',{name:'Pause science collection',exact:true}).click();await expect(journey).toContainText('Science collection: Paused');expect((await get(request,'/api/state')).activeMission.id).toBe(id);await page.getByRole('button',{name:'Resume science collection',exact:true}).click();
 await page.setViewportSize({width:390,height:844});expect((await new AxeBuilder({page}).include('#science-journey').analyze()).violations).toEqual([]);
 await page.getByRole('button',{name:'End mission',exact:true}).click();await expect(page.getByRole('link',{name:'Review Results',exact:true})).toHaveAttribute('href',`#workspace/map?missions=${id}`);
});
test('airport search creates actual departure/destination waypoints and preserves custom coordinates through save and reload',async({page,request})=>{
 await page.goto('/#planner');const name='Airport route '+crypto.randomUUID();await page.getByLabel('Plan name',{exact:true}).fill(name);
 async function airport(query,role){await page.getByRole('button',{name:'Add airport waypoint',exact:true}).click();await page.getByRole('searchbox',{name:'Offline geographic location search',exact:true}).fill(query);await page.locator('#plan-editor').getByRole('button',{name:'Find',exact:true}).click();await page.getByRole('listbox',{name:'Search results',exact:true}).selectOption('0');await page.getByRole('button',{name:'Use Airport as Waypoint',exact:true}).click();await page.getByRole('combobox',{name:'Airport role',exact:true}).selectOption(role);await page.getByRole('button',{name:'Save Waypoint',exact:true}).click();}
 await airport('KEDW','departure');await expect(page.locator('.waypoint-row')).toContainText('Edwards Air Force Base');
 await page.getByRole('button',{name:'Add waypoint',exact:true}).click();await page.getByLabel('Waypoint name',{exact:true}).fill('Custom science point');await page.getByLabel('Latitude (degrees north)',{exact:true}).fill('34.95');await page.getByLabel('Longitude (degrees east)',{exact:true}).fill('-117.8');await page.getByRole('button',{name:'Save Waypoint',exact:true}).click();
 await airport('LAX','destination');await page.getByRole('button',{name:'Save mission plan',exact:true}).click();const saved=(await get(request,'/api/plans')).find(p=>p.name===name);const plan=await get(request,'/api/plans/'+saved.id);
 expect(plan.departure).toBe('KEDW');expect(plan.destination).toBe('KLAX');expect(plan.waypoints).toHaveLength(3);expect(plan.waypoints[0]).toMatchObject({name:'Edwards Air Force Base',navigationIdentifier:'KEDW',latitude:34.910781,longitude:-117.886391});expect(plan.waypoints[1]).toMatchObject({name:'Custom science point',latitude:34.95,longitude:-117.8});expect(plan.waypoints[2].navigationIdentifier).toBe('KLAX');
 await page.reload();await page.locator('.plan-row').filter({hasText:name}).getByRole('button',{name:'Edit',exact:true}).click();await expect(page.locator('.waypoint-row')).toHaveCount(3);await expect(page.locator('.waypoint-row').first()).toContainText('Edwards Air Force Base');
});
test('generated ES2017 native panel loads the SDK without a callback and recovers a partial listener failure',async({page})=>{
 const errors=[];page.on('pageerror',e=>errors.push(e.message));await mountToolbar(page,{autoReply:true,includeCallback:false,busDelay:150,registrationFailures:1});await expect(page.locator('#as-source')).toHaveText('SIMULATED TEST DATA');expect(await page.evaluate(()=>window.listenerRegistrations)).toBe(2);expect(await page.evaluate(()=>window.bus.unregistered)).toBe(1);await expect(page.locator('#as-altitude')).toContainText('60');expect(errors).toEqual([]);
});
test('airport reference markers can be selected from the map by keyboard without copying an arbitrary map coordinate',async({page,request})=>{
 await page.goto('/#planner');const name='Map airport '+crypto.randomUUID();await page.getByLabel('Plan name',{exact:true}).fill(name);
 const search=page.getByRole('searchbox',{name:'Offline geographic location search',exact:true}),results=page.getByRole('listbox',{name:'Search results',exact:true});
 for(const q of ['KEDW','34.91, -117.89']){await search.fill(q);await page.locator('#plan-editor').getByRole('button',{name:'Find',exact:true}).click();await results.selectOption('0');}
 await expect(page.getByRole('button',{name:'Use Airport as Waypoint',exact:true})).toBeHidden();
 const airport=page.getByRole('button',{name:/Select airport Edwards Air Force Base \(KEDW/});await airport.focus();await page.keyboard.press('Enter');await page.getByRole('button',{name:'Use Airport as Waypoint',exact:true}).click();
 await expect(page.getByLabel('Latitude (degrees north)',{exact:true})).toHaveValue('34.910781');await expect(page.getByLabel('Longitude (degrees east)',{exact:true})).toHaveValue('-117.886391');await page.getByRole('button',{name:'Save Waypoint',exact:true}).click();await expect(page.locator('.waypoint-row')).toContainText('KEDW');await page.getByRole('button',{name:'Save mission plan',exact:true}).click();
 const plan=(await get(request,'/api/plans')).find(p=>p.name===name);expect(plan.waypoints[0].navigationIdentifier).toBe('KEDW');
});
test('armed automatic science names the actual pending trigger and still requires a real manual override',async({page,request})=>{
 const id=crypto.randomUUID(),target=crypto.randomUUID();
 const plan=await post(request,'/api/plans',{id,name:'Waiting for waypoint '+id,waypoints:[{id:target,name:'Research start',latitude:0,longitude:0}],instruments:[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000}],collection:{version:1,missionPlanId:id,initialPolicy:'armed',triggers:[{id:crypto.randomUUID(),occurrenceId:crypto.randomUUID(),action:'startData',kind:'waypoint',targetId:target,pass:1,toleranceMetres:100}],continuity:{maximumGapSeconds:6,maximumPositionAgeSeconds:6}}});
 const flight=await post(request,'/api/missions',{name:plan.name,planId:id,armScience:true});await page.goto('/#operations');const journey=page.locator('#science-journey');await expect(journey).toContainText('waiting for a waypoint crossing');
 expect((await get(request,`/api/missions/${flight.id}/collection`)).state.phase).toBe('armed');expect((await get(request,`/api/missions/${flight.id}/science`)).observations).toHaveLength(0);
 await journey.getByRole('button',{name:'Start Science Collection',exact:true}).click();await expect(journey).toContainText('Science collection: Collecting');const collection=await get(request,`/api/missions/${flight.id}/collection`);expect(collection.state.authority).toBe('manual');expect(collection.state.manualAutomationHold).toBe(true);
});
test('native connection uses SDK lifecycle callbacks when the host lacks Node.isConnected',async({page})=>{
 await mountToolbar(page,{autoReply:true,legacyConnectivity:true,includeCallback:false});await expect(page.locator('#as-source')).toHaveText('SIMULATED TEST DATA');await expect(page.locator('#as-altitude')).toContainText('60');expect(await page.evaluate(()=>window.listenerRegistrations)).toBe(1);
});
