import {test,expect} from '@playwright/test';
import {mountToolbar} from './helpers/toolbar.mjs';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const s=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':s.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
async function mountRealEngine(page){
 // The simulator SDK document is a separate host, not the dashboard CSP. Supply a
 // test-only document for native inline wrappers; production CSP remains unchanged.
 await page.route('**/__native_sdk_fixture',route=>route.fulfill({contentType:'text/html',body:'<!doctype html><html><head></head><body></body></html>'}));await page.goto('/__native_sdk_fixture');await mountToolbar(page);await page.evaluate(async()=>{
 const session=await(await fetch('/api/session',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).json();
 window.bus.callSimConnect=function(event,payload){const command=JSON.parse(payload);this.sent.push({event,command});return fetch('/api/testing/toolbar',{method:'POST',headers:{'Content-Type':'application/json','X-AeroScience-CSRF':session.csrf},body:payload}).then(r=>r.text()).then(reply=>this.handlers['AeroScience.Reply.v1']?.(reply));};
 document.querySelector('ingamepanel-aeroscience').tick();
 });await expect(page.locator('#as-source')).toHaveText('SIMULATED TEST DATA');}
test.afterEach(async({request})=>{const s=await(await request.get('/api/state')).json();if(s.activeMission)await post(request,`/api/missions/${s.activeMission.id}/end`);await post(request,'/api/instruments',[]);});
test('mocked SDK transports real session controls between toolbar and dashboard without retargeting',async({page,context,request})=>{
 await post(request,'/api/mission-selection',{planId:null});
 const flight=await post(request,'/api/missions',{name:'Synthetic real-handler native test',instruments:[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000}],armScience:true});
 await mountRealEngine(page);await expect(page.locator('#as-session-phase')).toContainText('armed');
 const dashboard=await context.newPage();await dashboard.goto('/#control');const intelligence=dashboard.locator('#mission-intelligence');
 await intelligence.getByRole('button',{name:'Start new science session',exact:true}).click();await expect(page.locator('#as-session-phase')).toContainText('Session 1');
 await page.getByText('Science session controls & evidence',{exact:true}).click();await page.locator('[data-science-action=pauseData]').click();await expect(intelligence).toContainText('Science: paused');
 await intelligence.getByRole('button',{name:'End current science session',exact:true}).click();await expect(page.locator('#as-session-phase')).toContainText('betweenSessions');
 await page.locator('[data-science-action=startData]').click();await expect(page.locator('#as-session-phase')).toContainText('Session 2');await expect(intelligence).toContainText('Session 2');
 const state=(await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state;await expect(page.locator('#as-session-identity')).toContainText(state.currentSessionId);
 const sent=await page.evaluate(()=>window.bus.sent.filter(s=>s.command.op==='science-control').map(s=>s.command));expect(sent).toHaveLength(2);expect(sent[0].control.expectedSessionId).not.toBe(state.currentSessionId);expect(sent[1].control.expectedSessionId).toBeNull();for(const c of sent){expect(c.requestId).toBe(c.control.requestId);expect(JSON.stringify(c).length).toBeLessThanOrEqual(1024);}
 await expect(page.locator('#as-next-trigger')).toContainText('Next crossing/order/ETA not assessed');await dashboard.close();
});
test('native timeout retries identical persisted work and cannot create a duplicate session',async({page,request})=>{
 await post(request,'/api/mission-selection',{planId:null});const flight=await post(request,'/api/missions',{name:'Synthetic native lost reply',instruments:[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000}],armScience:true});
 await mountRealEngine(page);await page.getByText('Science session controls & evidence',{exact:true}).click();
 await page.evaluate(()=>{const original=window.bus.handlers['AeroScience.Reply.v1'];let drop=true;window.bus.handlers['AeroScience.Reply.v1']=data=>{const reply=JSON.parse(data);if(reply.receipt&&drop){drop=false;return;}original(data);};});
 await page.locator('[data-science-action=startData]').click();await expect.poll(async()=>((await(await request.get(`/api/missions/${flight.id}/collection`)).json()).sessions).length).toBe(1);
 await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');for(const request of panel.pending.values())if(request.op==='science-control')request.sent=Date.now()-5001;panel.tick();});
 await expect.poll(()=>page.evaluate(()=>window.bus.sent.filter(x=>x.command.op==='science-control').length)).toBe(2);
 const commands=await page.evaluate(()=>window.bus.sent.filter(x=>x.command.op==='science-control').map(x=>x.command));expect(commands[1]).toEqual(commands[0]);await expect(page.locator('#as-session-phase')).toContainText('Session 1');
 expect((await(await request.get(`/api/missions/${flight.id}/collection`)).json()).sessions).toHaveLength(1);
});
test('Compact VR changes presentation independently of tabs, selection, unsaved inputs and backend state',async({page})=>{
 await mountToolbar(page,{autoReply:true});await page.locator('#as-name').fill('Keep this unsaved name');await page.locator('#as-mode').selectOption('realistic');
 await page.locator('#as-payload-page').click();await page.locator('#as-compact').click();await expect(page.locator('#as-compact')).toHaveAttribute('aria-pressed','true');await expect(page.locator('#as-payload-view')).toBeVisible();
 await page.locator('#as-map-page').click();await page.locator('#as-compact').click();await expect(page.locator('#as-map-view')).toBeVisible();await page.locator('#as-mission-page').click();await expect(page.locator('#as-name')).toHaveValue('Keep this unsaved name');await expect(page.locator('#as-mode')).toHaveValue('realistic');
 expect(await page.evaluate(()=>window.bus.sent.filter(x=>x.command.op!=='state'&&!x.command.op.startsWith('map-')))).toEqual([]);
 await expect(page.locator('#as-session-phase')).toContainText('unsupported');await expect(page.locator('[data-science-action=startData]')).toBeDisabled();
});
test('late replies cannot replace a newer authoritative native projection and UTF-8 byte bounds are enforced',async({page})=>{
 await mountToolbar(page);await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');panel.send('state');const requests=window.bus.sent.filter(s=>s.command.op==='state');const base={protocol:1,ok:true,engineInstance:'11111111-1111-4111-8111-111111111111',capabilities:['mission-intelligence-1','session-bound-controls-1'],state:{connection:{source:'simulated',message:'fixture'},activeMission:null,telemetry:null},intelligence:{version:1,generatedUtc:new Date().toISOString(),missionId:null,revision:5,controlRevision:5,sessionId:null,phase:'betweenSessions',authority:'manual',manualHold:true,actions:[],metrics:[],warnings:[],gradeStatus:'notGraded',triggerStatus:'No predicted next trigger'}};
 window.bus.handlers['AeroScience.Reply.v1'](JSON.stringify({...base,requestId:requests.at(-1).command.requestId}));window.bus.handlers['AeroScience.Reply.v1'](JSON.stringify({...base,requestId:requests[0].command.requestId,intelligence:{...base.intelligence,phase:'collecting',revision:1}}));
 });await expect(page.locator('#as-session-phase')).toContainText('betweenSessions');
 const bytes=await page.evaluate(()=>document.querySelector('ingamepanel-aeroscience').utf8Bytes('研究😀'));expect(bytes).toBe(10);
 await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');panel.send('state');const id=window.bus.sent.filter(s=>s.command.op==='state').at(-1).command.requestId;panel.receive(JSON.stringify({protocol:1,requestId:id,ok:true,state:{connection:{source:'simulated',message:'😀'.repeat(2200)},activeMission:null,telemetry:null}}));});
 await expect(page.locator('#as-session-phase')).toContainText('betweenSessions');
 await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');panel.send('state');const id=window.bus.sent.filter(s=>s.command.op==='state').at(-1).command.requestId;panel.receive(JSON.stringify({protocol:1,requestId:id,ok:true,capabilities:['mission-intelligence-1','session-bound-controls-1'],state:{connection:{source:'simulated',message:'Future fixture'},activeMission:null,telemetry:null},intelligence:{version:2,missionId:null}}));});
 await expect(page.locator('#as-session-phase')).toContainText('unsupported');await expect(page.locator('[data-science-action=endData]')).toBeDisabled();
});
