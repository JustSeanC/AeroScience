import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mountToolbar} from './helpers/toolbar.mjs';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const s=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const r=await request.post(path,{headers:{Origin:origin,'X-AeroScience-CSRF':s.csrf},data});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
async function cards(request){const f=(await(await request.get('/api/state')).json()).telemetry;const suffix=Date.now();return Promise.all([0,1,200].map((shift,i)=>post(request,'/api/research/cards',{name:`A-P02 Card ${i} ${suffix}`,flightRegime:{airspeedKnots:f.indicatedAirspeedKnots+shift,airspeedToleranceKnots:30,altitudeMslFeet:f.altitudeMslFeet,altitudeToleranceFeet:1000,holdSeconds:1}})));}
test.afterEach(async({request})=>{const state=await(await request.get('/api/state')).json();if(state.activeMission)await post(request,`/api/missions/${state.activeMission.id}/end`);await post(request,'/api/mission-selection',{planId:null});await post(request,'/api/instruments',[]);});
test('planned personal cards activate, record independent attempts, switch and review exact flight/science evidence',async({page,request})=>{
 await page.goto('/#planner');await expect(page.locator('#plan-form')).toBeVisible();const [a,b,c]=await cards(request);
 await page.reload();await page.getByLabel('Plan name',{exact:true}).fill('P02 continuous flight');
 const selection=page.locator('#plan-research-cards');for(const card of [a,b,c])await selection.locator('.plan-row').filter({hasText:card.name}).getByRole('button',{name:'Add Card',exact:true}).click();
 await expect(selection).toContainText('3 cards selected');await page.locator('#plan-form [data-instrument="ambient-temperature"]').check();await page.getByRole('button',{name:'Save mission plan',exact:true}).click();
 await page.locator('.plan-row').filter({hasText:'P02 continuous flight'}).getByRole('button',{name:'Select for Flight',exact:true}).click();await page.getByRole('button',{name:'Start mission',exact:true}).click();
 await expect(page.getByRole('button',{name:'End mission',exact:true})).toBeVisible();const flight=(await(await request.get('/api/state')).json()).activeMission;expect(flight).toBeTruthy();const science=(await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state;
 const panel=page.locator('#research-flight-controls'),ra=panel.getByRole('article',{name:a.name}),rb=panel.getByRole('article',{name:b.name}),rc=panel.getByRole('article',{name:c.name});
 await ra.getByRole('button',{name:'Activate Card',exact:true}).click();await expect(rc.getByRole('button',{name:'Activate Card',exact:true})).toBeDisabled();await expect(rc).toContainText('do not overlap');
 await rb.getByRole('button',{name:'Activate Card',exact:true}).click();await ra.getByRole('button',{name:'Begin Attempt',exact:true}).click();await rb.getByRole('button',{name:'Begin Attempt',exact:true}).click();
 await expect.poll(async()=>{const rows=await(await request.get(`/api/missions/${flight.id}/research-attempts`)).json();const frames=await(await request.get(`/api/missions/${flight.id}/telemetry?limit=1000`)).json();return Date.parse(frames.at(-1)?.timestampUtc)-Math.max(...rows.map(r=>Date.parse(r.startUtc)));}).toBeGreaterThan(1600);
 await ra.getByRole('button',{name:'End Attempt',exact:true}).click();await rb.getByRole('button',{name:'End Attempt',exact:true}).click();
 await ra.getByRole('button',{name:'Begin Attempt',exact:true}).click();await ra.getByRole('button',{name:'Deactivate Card',exact:true}).click();await rb.getByRole('button',{name:'Deactivate Card',exact:true}).click();
 await rc.getByRole('button',{name:'Activate Card',exact:true}).click();await rc.getByRole('button',{name:'Begin Attempt',exact:true}).click();await rc.getByRole('button',{name:'End Attempt',exact:true}).click();
 expect((await(await request.get('/api/state')).json()).activeMission.id).toBe(flight.id);const after=(await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state;expect(after.currentSessionId).toBe(science.currentSessionId);expect(after.phase).toBe('collecting');
 await page.setViewportSize({width:375,height:812});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();expect((await new AxeBuilder({page}).include('#research-flight-controls').analyze()).violations).toEqual([]);
 await page.getByRole('button',{name:'End mission',exact:true}).click();await page.goto('/#my-research?flight='+flight.id);
 const timeline=page.getByRole('region',{name:'Test-card activity and evidence',exact:true});await expect(timeline).toContainText('Interrupted');await timeline.getByRole('button',{name:'Evaluate Attempt',exact:true}).first().click();await expect(timeline).toContainText('— Pass');
 const event=timeline.locator('.research-panel').filter({hasText:'research-card-BeginAttempt'}).first();await event.getByRole('button',{name:'Show Recorded Evidence',exact:true}).click();await expect(timeline.getByRole('region',{name:'Recorded flight samples',exact:true})).toBeVisible();await expect(timeline).toContainText('Science collection:');
 const marked=await(await request.get(`/api/missions/${flight.id}/research-attempts`)).json();expect(marked).toHaveLength(4);expect(new Set(marked.map(a=>a.id)).size).toBe(4);expect(marked.every(a=>a.beginEventId&&a.endEventId)).toBeTruthy();
});

test('native toolbar and paired dashboard use the same flight-bound card commands and durable retry',async({page,context,request})=>{
 const [a]=await cards(request);const plan=await post(request,`/api/research/cards/${a.id}/plan`,{id:a.id,revision:1});const flight=await post(request,'/api/missions',{name:'P02 native continuous',planId:plan.id});
 await page.route('**/__native_p02',route=>route.fulfill({contentType:'text/html',body:'<!doctype html><html><head></head><body></body></html>'}));await page.goto('/__native_p02');await mountToolbar(page);
 await page.evaluate(async()=>{const s=await(await fetch('/api/session',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).json();window.bus.callSimConnect=function(event,payload){const command=JSON.parse(payload);this.sent.push({event,command});return fetch('/api/testing/toolbar',{method:'POST',headers:{'Content-Type':'application/json','X-AeroScience-CSRF':s.csrf},body:payload}).then(r=>r.text()).then(reply=>this.handlers['AeroScience.Reply.v1']?.(reply));};document.querySelector('ingamepanel-aeroscience').tick();});
 const native=page.locator('#as-research-cards');await native.getByRole('button',{name:'Activate Card',exact:true}).click();await expect(native.getByRole('button',{name:'Deactivate Card',exact:true})).toBeFocused();await native.getByRole('button',{name:'Begin Attempt',exact:true}).click();await expect(native).toContainText('Attempt recording');
 const dashboard=await context.newPage();await dashboard.goto('/#control');await expect(dashboard.locator('#research-flight-controls')).toContainText('Attempt recording');
 const begin=await page.evaluate(()=>window.bus.sent.find(x=>x.command.op==='research-control'&&x.command.researchControl.action==='beginAttempt').command);
 await post(request,'/api/testing/toolbar',begin);expect((await(await request.get(`/api/missions/${flight.id}/research-attempts`)).json())).toHaveLength(1);
 await dashboard.locator('#research-flight-controls').getByRole('button',{name:'End Attempt',exact:true}).click();await expect(native).toContainText('Active');expect((await(await request.get('/api/state')).json()).activeMission.id).toBe(flight.id);await dashboard.close();
});
