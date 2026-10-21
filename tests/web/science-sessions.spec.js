import {test,expect} from '@playwright/test';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const session=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
test.afterEach(async({request})=>{const s=await(await request.get('/api/state')).json();if(s.activeMission)await post(request,`/api/missions/${s.activeMission.id}/end`);await post(request,'/api/instruments',[]);});
test('pilot arms, collects two science sessions and keeps one continuous flight',async({page,request})=>{
 await post(request,'/api/instruments',[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000}]);await post(request,'/api/mission-selection',{planId:null});
 const flight=await post(request,'/api/missions',{name:'Synthetic browser multi-session flight',armScience:true});
 await page.goto('/#science');const panel=page.locator('#science-sessions');await expect(panel).toContainText('armed');
 await page.waitForTimeout(1100);expect((await(await request.get(`/api/missions/${flight.id}/science`)).json()).observations).toHaveLength(0);
 await panel.getByRole('button',{name:'Start new science session',exact:true}).click();await expect(panel).toContainText('collecting');
 await expect.poll(async()=>((await(await request.get(`/api/missions/${flight.id}/science`)).json()).observations).length).toBeGreaterThan(0);
 await panel.getByRole('button',{name:'Pause data',exact:true}).click();await expect(panel).toContainText('paused');await panel.getByRole('button',{name:'Resume data',exact:true}).click();await expect(panel).toContainText('collecting');
 const a=(await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state.currentSessionId;
 await panel.getByRole('button',{name:'End current science session',exact:true}).click();await expect(panel).toContainText('betweenSessions');
 await panel.getByRole('button',{name:'Start new science session',exact:true}).click();await expect(panel).toContainText('Session 2');
 const state=(await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state;expect(state.currentSessionId).not.toBe(a);expect(state.manualAutomationHold).toBeTruthy();
 await expect.poll(async()=>((await(await request.get(`/api/missions/${flight.id}/science`)).json()).observations).some(r=>r.sessionId===state.currentSessionId)).toBeTruthy();
 const current=await(await request.get('/api/state')).json();expect(current.activeMission.id).toBe(flight.id);
 await post(request,`/api/missions/${flight.id}/end`);const archive=await(await request.get(`/api/missions/${flight.id}/export`)).json();expect(new Set(archive.science.observations.map(r=>r.sessionId)).size).toBe(2);
});
test('Planner configures and preserves bounded collection actions without JSON editing',async({page,request})=>{
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
 await page.reload({waitUntil:'domcontentloaded'});await page.locator('.plan-row').filter({hasText:'Automation GUI fixture'}).getByRole('button',{name:'Edit',exact:true}).click();await page.locator('#collection-planner summary').click();await expect(page.locator('#collection-planner')).toContainText(saved.collection.triggers[0].targetId);
});
