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
