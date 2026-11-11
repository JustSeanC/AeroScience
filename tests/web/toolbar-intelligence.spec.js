import {test,expect} from '@playwright/test';
import {mountToolbar,mountRealEngine} from './helpers/toolbar.mjs';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const s=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':s.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
test.afterEach(async({request})=>{const s=await(await request.get('/api/state')).json();if(s.activeMission)await post(request,`/api/missions/${s.activeMission.id}/end`);await post(request,'/api/instruments',[]);});
test('mocked SDK transports real session controls between toolbar and dashboard without retargeting',async({page,context,request})=>{
 await post(request,'/api/mission-selection',{planId:null});
 const flight=await post(request,'/api/missions',{name:'Synthetic real-handler native test',instruments:[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000}],armScience:true});
 await mountRealEngine(page);await expect(page.locator('#as-session-phase')).toContainText('Science: Armed');
 const dashboard=await context.newPage();await dashboard.goto('/#control');const intelligence=dashboard.locator('#mission-intelligence');
 await dashboard.getByRole('button',{name:'Start Science Collection',exact:true}).click();await expect(page.locator('#as-session-phase')).toContainText('Session 1');
 await page.locator('#as-payload-page').click();await page.getByText('Science session controls & evidence',{exact:true}).click();await page.locator('[data-science-action=pauseData]').click();await expect(intelligence).toContainText('Science: paused');
 await dashboard.getByRole('button',{name:'End current science session',exact:true}).click();await expect(page.locator('#as-session-phase')).toContainText('Between sessions');
 await page.locator('[data-science-action=startData]').click();await expect(page.locator('#as-session-phase')).toContainText('Session 2');await expect(intelligence).toContainText('Session 2');
 const state=(await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state;await expect(page.locator('#as-session-identity')).toContainText(state.currentSessionId);
 const sent=await page.evaluate(()=>window.bus.sent.filter(s=>s.command.op==='science-control').map(s=>s.command));expect(sent).toHaveLength(2);expect(sent[0].control.expectedSessionId).not.toBe(state.currentSessionId);expect(sent[1].control.expectedSessionId).toBeNull();for(const c of sent){expect(c.requestId).toBe(c.control.requestId);expect(JSON.stringify(c).length).toBeLessThanOrEqual(1024);}
 await expect(page.locator('#as-next-trigger')).toContainText('Next crossing/order/ETA not assessed');await dashboard.close();
});
test('an already open native panel follows real dashboard mission selection without starting recording',async({page,context,request})=>{
 await post(request,'/api/mission-selection',{planId:null});const plans=[];
 for(const name of ['First synchronized research plan','Second synchronized research plan'])plans.push(await post(request,'/api/plans',{name,mode:'universal',template:'generalResearch',waypoints:[],surveyAreas:[],patterns:[],instruments:[]}));
 await mountRealEngine(page);const dashboard=await context.newPage();await dashboard.goto('/#operations');
 for(const plan of plans){await dashboard.getByRole('combobox',{name:'Choose Mission',exact:true}).selectOption(plan.id);await expect(page.locator('#as-map-plan')).toHaveValue(plan.id);await expect(page.locator('#as-start-plan')).toContainText(plan.name);await expect(page.locator('#as-name')).toHaveValue(plan.name);expect((await(await request.get('/api/state')).json()).activeMission).toBeNull();}
 expect(await page.evaluate(()=>window.bus.sent.filter(x=>['start','science-control'].includes(x.command.op)))).toEqual([]);await dashboard.close();
});
test('real engine confirms native interval changes and retained dashboard values survive panel reopen',async({page,request})=>{
 await post(request,'/api/mission-selection',{planId:null});const flight=await post(request,'/api/missions',{name:'Native interval integration fixture',instruments:[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000},{instrumentId:'ambient-pressure',enabled:true,intervalMs:5000}],armScience:true});
 await mountRealEngine(page);await page.locator('#as-payload-page').click();const interval=page.getByLabel('Ambient temperature collection interval',{exact:true});await interval.selectOption('2000');await page.getByRole('button',{name:'Apply Ambient temperature interval',exact:true}).click();await expect(page.locator('#as-instruments')).toContainText('Confirmed: 2 s.');
 const current=await(await request.get('/api/instruments/configuration')).json();expect(current.find(i=>i.instrumentId==='ambient-temperature').intervalMs).toBe(2000);expect(current.find(i=>i.instrumentId==='ambient-pressure').intervalMs).toBe(5000);
 await post(request,'/api/instruments',current.map(i=>i.instrumentId==='ambient-temperature'?{...i,intervalMs:3500}:i));await expect(interval).toHaveValue('3500');
 await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');panel.remove();document.body.appendChild(panel);});await page.locator('#as-payload-page').click();await expect(page.getByLabel('Ambient temperature collection interval',{exact:true})).toHaveValue('3500');
 const state=await(await request.get('/api/state')).json(),collection=await(await request.get(`/api/missions/${flight.id}/collection`)).json();expect(state.activeMission.id).toBe(flight.id);expect(collection.state.phase).toBe('armed');expect(collection.sessions).toEqual([]);
});
test('native timeout retries identical persisted work and cannot create a duplicate session',async({page,request})=>{
 await post(request,'/api/mission-selection',{planId:null});const flight=await post(request,'/api/missions',{name:'Synthetic native lost reply',instruments:[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000}],armScience:true});
 await mountRealEngine(page);await page.locator('#as-payload-page').click();await page.getByText('Science session controls & evidence',{exact:true}).click();
 await page.evaluate(()=>{const original=window.bus.handlers['AeroScience.Reply.v1'];let drop=true;window.bus.handlers['AeroScience.Reply.v1']=data=>{const reply=JSON.parse(data);if(reply.receipt&&drop){drop=false;return;}original(data);};});
 await page.locator('[data-science-action=startData]').click();await expect.poll(async()=>((await(await request.get(`/api/missions/${flight.id}/collection`)).json()).sessions).length).toBe(1);
 await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');for(const request of panel.pending.values())if(request.op==='science-control')request.sent=Date.now()-5001;panel.tick();});
 await expect.poll(()=>page.evaluate(()=>window.bus.sent.filter(x=>x.command.op==='science-control').length)).toBe(2);
 const commands=await page.evaluate(()=>window.bus.sent.filter(x=>x.command.op==='science-control').map(x=>x.command));expect(commands[1]).toEqual(commands[0]);await expect(page.locator('#as-session-phase')).toContainText('Session 1');
 expect((await(await request.get(`/api/missions/${flight.id}/collection`)).json()).sessions).toHaveLength(1);
});
test('normal responsive toolbar preserves tabs and unsaved inputs without an obsolete Compact VR toggle',async({page})=>{
 await mountToolbar(page,{autoReply:true});await expect(page.getByRole('button',{name:/Compact VR/})).toHaveCount(0);await expect(page.locator('#as-compact')).toHaveCount(0);await page.locator('#as-name').fill('Keep this unsaved name');await page.locator('#as-mode').selectOption('realistic');
 await page.locator('#as-payload-page').click();await expect(page.locator('#as-payload-view')).toBeVisible();await page.locator('#as-map-page').click();await expect(page.locator('#as-map-view')).toBeVisible();await page.locator('#as-mission-page').click();await expect(page.locator('#as-name')).toHaveValue('Keep this unsaved name');await expect(page.locator('#as-mode')).toHaveValue('realistic');
 expect(await page.evaluate(()=>window.bus.sent.filter(x=>x.command.op!=='state'&&!x.command.op.startsWith('map-')))).toEqual([]);await expect(page.locator('#as-session-phase')).toContainText('unsupported');await expect(page.locator('[data-science-action=startData]')).toBeDisabled();
});
test('late template initialization recovers without a null-control exception or duplicate CommBus subscriptions',async({page})=>{
 const errors=[];page.on('pageerror',e=>errors.push(e.message));await mountToolbar(page,{autoReply:true,templateDelay:150});await expect(page.locator('ingamepanel-aeroscience')).toHaveAttribute('data-initialization-state','ready');expect(errors).toEqual([]);expect(await page.evaluate(()=>window.listenerRegistrations)).toBe(1);
 await page.getByText('Connection & Build Details',{exact:true}).click();await expect(page.locator('#as-build-identity')).toContainText('production toolbar');await expect(page.locator('#as-link-detail')).toContainText('Matching engine reply received');await page.locator('#as-payload-page').click();await expect(page.locator('#as-payload-view')).toBeVisible();
 await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');panel.remove();document.body.append(panel);});await expect.poll(()=>page.evaluate(()=>window.listenerRegistrations)).toBe(2);expect(await page.evaluate(()=>window.bus.unregistered)).toBe(1);expect(errors).toEqual([]);
});
test('missing template initialization is bounded and disconnect clears its pending retry',async({page})=>{
 await mountToolbar(page,{autoReply:true});await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');p.remove();p.querySelector('[data-aeroscience-template-ready]').remove();document.body.append(p);p.initializationAttempts=49;p.initialize();});await expect(page.locator('ingamepanel-aeroscience')).toHaveAttribute('data-initialization-state','template-unavailable');await expect(page.locator('#as-status')).toContainText('Close and reopen this panel');expect(await page.evaluate(()=>window.listenerRegistrations)).toBe(1);
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');p.remove();document.body.append(p);p.remove();const marker=document.createElement('span');marker.dataset.aeroscienceTemplateReady='';p.append(marker);window.detachedPanel=p;});await page.waitForTimeout(150);expect(await page.evaluate(()=>window.listenerRegistrations)).toBe(1);await page.evaluate(()=>document.body.append(window.detachedPanel));await expect.poll(()=>page.evaluate(()=>window.listenerRegistrations)).toBe(2);
});
test('late replies cannot replace a newer authoritative native projection and UTF-8 byte bounds are enforced',async({page})=>{
 await mountToolbar(page);await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');panel.send('state');const requests=window.bus.sent.filter(s=>s.command.op==='state');const base={protocol:1,ok:true,engineInstance:'11111111-1111-4111-8111-111111111111',capabilities:['mission-intelligence-1','session-bound-controls-1'],state:{connection:{source:'simulated',message:'fixture'},activeMission:null,telemetry:null},intelligence:{version:1,generatedUtc:new Date().toISOString(),missionId:null,revision:5,controlRevision:5,sessionId:null,phase:'betweenSessions',authority:'manual',manualHold:true,actions:[],metrics:[],warnings:[],gradeStatus:'notGraded',triggerStatus:'No predicted next trigger'}};
 window.bus.handlers['AeroScience.Reply.v1'](JSON.stringify({...base,requestId:requests.at(-1).command.requestId}));window.bus.handlers['AeroScience.Reply.v1'](JSON.stringify({...base,requestId:requests[0].command.requestId,intelligence:{...base.intelligence,phase:'collecting',revision:1}}));
 });await expect(page.locator('#as-session-phase')).toContainText('Between sessions');
 const bytes=await page.evaluate(()=>document.querySelector('ingamepanel-aeroscience').utf8Bytes('研究😀'));expect(bytes).toBe(10);
 await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');panel.send('state');const id=window.bus.sent.filter(s=>s.command.op==='state').at(-1).command.requestId;panel.receive(JSON.stringify({protocol:1,requestId:id,ok:true,state:{connection:{source:'simulated',message:'😀'.repeat(2200)},activeMission:null,telemetry:null}}));});
 await expect(page.locator('#as-session-phase')).toContainText('Between sessions');
 await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');panel.send('state');const id=window.bus.sent.filter(s=>s.command.op==='state').at(-1).command.requestId;panel.receive(JSON.stringify({protocol:1,requestId:id,ok:true,capabilities:['mission-intelligence-1','session-bound-controls-1'],state:{connection:{source:'simulated',message:'Future fixture'},activeMission:null,telemetry:null},intelligence:{version:2,missionId:null}}));});
 await expect(page.locator('#as-session-phase')).toContainText('unsupported');await expect(page.locator('[data-science-action=endData]')).toBeDisabled();
});

test('real native handler synchronizes off-page saved plans, preview and recording across panel reopen',async({page,request})=>{
 await post(request,'/api/mission-selection',{planId:null});const plans=[];
 for(let index=0;index<12;index++)plans.push(await post(request,'/api/plans',{name:'Native repair '+index,mode:'universal',template:'generalResearch',waypoints:[{name:'First',latitude:40,longitude:-75},{name:'Second',latitude:40.1,longitude:-75.1}],surveyAreas:[],patterns:[],instruments:[]}));
 await mountRealEngine(page);await expect(page.locator('#as-map-plan option')).toHaveCount(11);
 await page.locator('#as-map-more-plans').click();await expect(page.locator('#as-plan-status')).toContainText('Saved plans 11-');
 await page.locator('#as-map-previous-plans').click();await expect(page.locator('#as-plan-status')).toContainText('Saved plans 1-10');
 const target=plans[0];await post(request,'/api/mission-selection',{planId:target.id});
 await expect(page.locator('#as-map-plan')).toHaveValue(target.id);await expect(page.locator('#as-start-plan')).toContainText(target.name);await expect(page.locator('#as-name')).toHaveValue(target.name);
 expect(await page.evaluate(()=>window.bus.sent.filter(s=>s.command.op==='select-plan'))).toEqual([]);
 await page.locator('#as-map-plans').click();await expect(page.locator('#as-plan-status')).toContainText('Saved plans 1-10');expect(await page.evaluate(()=>window.bus.sent.filter(s=>s.command.op==='select-plan'))).toEqual([]);
 // Native selection is an explicit acknowledged command; it never records by itself.
 await page.locator('#as-map-plan').selectOption('');await expect(page.locator('#as-start-plan')).toContainText('Unplanned Mission');
 await page.locator('#as-map-plan').selectOption(plans[11].id);await expect(page.locator('#as-start-plan')).toContainText(plans[11].name);
 expect((await(await request.get('/api/state')).json()).activeMission).toBeNull();
 await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');panel.remove();document.body.appendChild(panel);Object.defineProperty(panel,'isConnected',{value:undefined});});
 await expect(page.locator('#as-map-plan')).toHaveValue(plans[11].id);await expect(page.locator('#as-name')).toHaveValue(plans[11].name);
 await page.locator('#as-plan-map').click();await expect(page.locator('#as-map-waypoint option')).toHaveText(['1. First','2. Second']);await expect(page.locator('#as-map-status')).toContainText('Route synchronized');
 await page.locator('#as-mission-page').click();await page.locator('#as-start').click();await expect(page.locator('#as-active-name')).toHaveText(plans[11].name);await expect(page.locator('#as-map-plan')).toBeHidden();
 const flight=(await(await request.get('/api/state')).json()).activeMission;const research=await(await request.get(`/api/missions/${flight.id}/research`)).json();expect(research.planSnapshot.id).toBe(plans[11].id);expect(research.planSnapshot.waypoints).toHaveLength(2);
 await page.locator('#as-map-page').click();await expect(page.locator('#as-map-status')).toContainText('Route synchronized');
});
