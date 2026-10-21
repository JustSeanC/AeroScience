import {test,expect} from '@playwright/test';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const session=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
test.afterEach(async({request})=>{const s=await(await request.get('/api/state')).json();if(s.activeMission)await post(request,`/api/missions/${s.activeMission.id}/end`);await post(request,'/api/instruments',[]);});
test('pilot arms, collects two science sessions and keeps one continuous flight',async({page,request})=>{
 await page.addInitScript(()=>Object.defineProperty(crypto,'randomUUID',{value:undefined,configurable:true}));
 await post(request,'/api/instruments',[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000}]);await post(request,'/api/mission-selection',{planId:null});
 const flight=await post(request,'/api/missions',{name:'Synthetic browser multi-session flight',armScience:true});
 await page.goto('/#science');const panel=page.locator('#science-sessions');await expect(panel).toContainText('armed');
 await expect(page.locator('#instruments h1')).toBeFocused();
 const start=panel.getByRole('button',{name:'Start new science session',exact:true});await start.focus();await page.waitForTimeout(2100);await expect(start).toBeFocused();
 await page.waitForTimeout(1100);expect((await(await request.get(`/api/missions/${flight.id}/science`)).json()).observations).toHaveLength(0);
 await panel.getByRole('button',{name:'Start new science session',exact:true}).click();await expect(panel).toContainText('collecting');
 await expect.poll(async()=>((await(await request.get(`/api/missions/${flight.id}/science`)).json()).observations).length).toBeGreaterThan(0);
 await panel.getByRole('button',{name:'Pause science collection',exact:true}).click();await expect(panel).toContainText('paused');await panel.getByRole('button',{name:'Resume science collection',exact:true}).click();await expect(panel).toContainText('collecting');
 const a=(await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state.currentSessionId;
 await panel.getByRole('button',{name:'End current science session',exact:true}).click();await expect(panel).toContainText('betweenSessions');
 await panel.getByRole('button',{name:'Start new science session',exact:true}).click();await expect(panel).toContainText('Session 2');
 const state=(await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state;expect(state.currentSessionId).not.toBe(a);expect(state.manualAutomationHold).toBeTruthy();
 await expect.poll(async()=>((await(await request.get(`/api/missions/${flight.id}/science`)).json()).observations).some(r=>r.sessionId===state.currentSessionId)).toBeTruthy();
 const current=await(await request.get('/api/state')).json();expect(current.activeMission.id).toBe(flight.id);
 await post(request,`/api/missions/${flight.id}/end`);const archive=await(await request.get(`/api/missions/${flight.id}/export`)).json();expect(new Set(archive.science.observations.map(r=>r.sessionId)).size).toBe(2);
});
test('Planner configures and preserves bounded collection actions without JSON editing',async({page,request})=>{
 await page.addInitScript(()=>Object.defineProperty(crypto,'randomUUID',{value:undefined,configurable:true}));
 const id=crypto.randomUUID(),a=crypto.randomUUID(),b=crypto.randomUUID();
 await post(request,'/api/plans',{id,name:'Automation GUI fixture',waypoints:[{id:a,name:'Start gate',latitude:40,longitude:-75},{id:b,name:'End gate',latitude:40,longitude:-74.99}],surveyAreas:[{name:'Research region',vertices:[{latitude:39.999,longitude:-75.001},{latitude:39.999,longitude:-74.999},{latitude:40.001,longitude:-74.999},{latitude:40.001,longitude:-75.001}]}]});
 await page.goto('/#planner');await page.locator('.plan-row').filter({hasText:'Automation GUI fixture'}).getByRole('button',{name:'Edit',exact:true}).click();
 const panel=page.locator('#collection-planner');await panel.locator('summary').click();await panel.getByLabel('Configure scientific collection automation',{exact:true}).check();
 await panel.getByLabel('Trigger geometry',{exact:true}).selectOption('areaEntry');await panel.getByLabel('Planned session key (optional, e.g. A or B)',{exact:true}).fill('A');await panel.getByRole('button',{name:'Add collection action',exact:true}).click();
 await panel.getByLabel('Collection action',{exact:true}).selectOption('endData');await panel.getByLabel('Trigger geometry',{exact:true}).selectOption('areaExit');await panel.getByRole('button',{name:'Add collection action',exact:true}).click();await expect(panel.getByRole('status')).toContainText('Automatic End requires');
 await panel.getByLabel('Trigger geometry',{exact:true}).selectOption('directionalGate');await panel.getByLabel('Saved geographic target',{exact:true}).selectOption(b);await panel.getByRole('button',{name:'Add collection action',exact:true}).click();
 await panel.getByRole('button',{name:'Refresh and review automation targets',exact:true}).click();await expect(panel.getByRole('status')).toContainText('Reviewed 2 actions');
 await page.getByRole('button',{name:'Save mission plan',exact:true}).click();await expect(page.getByText('Mission plan saved · Ready to start mission',{exact:false})).toBeVisible();
 const saved=await(await request.get('/api/plans/'+id)).json();expect(saved.collection.initialPolicy).toBe('armed');expect(saved.collection.triggers).toHaveLength(2);expect(saved.collection.triggers[1].targetId).toBe(b);expect(saved.collection.surveyAreas[0].geometrySha256).toHaveLength(64);
 await page.reload({waitUntil:'domcontentloaded'});await page.locator('.plan-row').filter({hasText:'Automation GUI fixture'}).getByRole('button',{name:'Edit',exact:true}).click();await page.locator('#collection-planner summary').click();await expect(page.locator('#collection-planner')).toContainText('Start Data · Research region');await expect(page.locator('#collection-planner')).toContainText('End current science session · End gate');
});
test('authoritative action availability prevents session start without instruments and leaves flight active',async({page,request})=>{
 await post(request,'/api/instruments',[]);await post(request,'/api/mission-selection',{planId:null});
 const flight=await post(request,'/api/missions',{name:'Synthetic no-instrument start',armScience:true});await page.goto('/#science');const panel=page.locator('#science-sessions');
 const start=panel.getByRole('button',{name:'Start new science session',exact:true});await expect(start).toBeDisabled();await expect(start).toHaveAttribute('title',/enabled instrument/);
 expect((await(await request.get(`/api/missions/${flight.id}/collection`)).json()).sessions).toHaveLength(0);expect((await(await request.get('/api/state')).json()).activeMission.id).toBe(flight.id);
});

test('LAN-compatible identities remain unique RFC 4122 v4 IDs without randomUUID',async({page})=>{
 await page.addInitScript(()=>Object.defineProperty(crypto,'randomUUID',{value:undefined,configurable:true}));await page.goto('/');
 const values=await page.evaluate(async()=>{const {newIdentity}=await import('/workstation-ui.js');return Array.from({length:1000},()=>newIdentity());});
 expect(new Set(values).size).toBe(1000);for(const id of values)expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
});
