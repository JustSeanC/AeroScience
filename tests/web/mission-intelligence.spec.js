import {test,expect} from '@playwright/test';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const s=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':s.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
async function begin(request){await post(request,'/api/mission-selection',{planId:null});return post(request,'/api/missions',{name:'Synthetic shared intelligence flight',mode:'realistic',instruments:[{instrumentId:'ambient-wind',enabled:true,intervalMs:1000}],armScience:true});}
test.afterEach(async({request})=>{const s=await(await request.get('/api/state')).json();if(s.activeMission)await post(request,`/api/missions/${s.activeMission.id}/end`);await post(request,'/api/instruments',[]);});
test('Control and touch-sized second browser share sessions, authority, genuine scalar counters and keyboard focus',async({page,context,request})=>{
 const flight=await begin(request);await page.goto('/#control');const panel=page.locator('#mission-intelligence');await expect(panel).toContainText('armed');
 const other=await context.newPage();await other.setViewportSize({width:768,height:1024});await other.goto('/#science');const second=other.locator('#science-sessions');
 await panel.getByRole('button',{name:'Start new science session',exact:true}).click();await expect(second).toContainText('collecting');
 const a=(await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state.currentSessionId;
 await expect(second).toContainText(a);await second.getByRole('button',{name:'Pause science collection',exact:true}).click();await expect(panel).toContainText('paused');
 const resume=panel.getByRole('button',{name:'Resume science collection',exact:true});await resume.focus();await page.waitForTimeout(2200);await expect(resume).toBeFocused();await resume.click();await expect(panel).toContainText('Science: collecting');
 await panel.getByRole('button',{name:'End current science session',exact:true}).click();await expect(second).toContainText('betweenSessions');await expect(second).toContainText('rearm required');
 await second.getByRole('button',{name:'Rearm automatic collection',exact:true}).click();await expect(panel).toContainText('Automation: automatic');
 await panel.getByRole('button',{name:'Start new science session',exact:true}).click();await expect(second).toContainText('Session 2');
 await panel.getByText('Effective instruments & operating evidence',{exact:true}).click();await expect(panel).toContainText('Operating');
 await panel.getByText('Provisional flight performance & scientific yield',{exact:true}).click();await expect(panel).toContainText('No final letter grade');await expect(panel).toContainText('good-data-values',{timeout:15000});
 expect((await(await request.get('/api/state')).json()).activeMission.id).toBe(flight.id);
 expect(await other.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();await other.close();
});
test('lost command reply retries the exact identity and session rather than applying an End twice',async({page,request})=>{
 const flight=await begin(request);await page.goto('/#control');const panel=page.locator('#mission-intelligence');await panel.getByRole('button',{name:'Start new science session',exact:true}).click();await expect(panel).toContainText('collecting');
 const bodies=[];await page.route(`**/api/missions/${flight.id}/collection/control`,async route=>{bodies.push(route.request().postDataJSON());if(bodies.length===1){await route.fetch();await route.abort('failed');}else await route.continue();});
 await panel.getByRole('button',{name:'End current science session',exact:true}).click();await expect(panel).toContainText('betweenSessions');expect(bodies).toHaveLength(2);expect(bodies[1]).toEqual(bodies[0]);
 const saved=await(await request.get(`/api/missions/${flight.id}/collection`)).json();expect(saved.sessions).toHaveLength(1);expect(saved.sessions[0].status).toBe('ended');
});
test('delayed End for session A cannot end a new session B; reconnect clears stale disabled controls',async({page,request})=>{
 const flight=await begin(request);await page.goto('/#control');const panel=page.locator('#mission-intelligence');await panel.getByRole('button',{name:'Start new science session',exact:true}).click();await expect(panel).toContainText('collecting');
 let release,seen;const arrived=new Promise(r=>seen=r);await page.route(`**/api/missions/${flight.id}/collection/control`,async route=>{seen();await new Promise(r=>release=r);await route.continue();});
 await panel.getByRole('button',{name:'End current science session',exact:true}).click();await arrived;
 async function control(action){const s=(await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state;return post(request,`/api/missions/${flight.id}/collection/control`,{version:1,missionId:flight.id,requestId:crypto.randomUUID(),expectedRevision:s.revision,expectedSessionId:s.currentSessionId,action});}
 await control('endData');const next=await control('startData');release();await expect(panel).toContainText('Session 2');expect((await(await request.get(`/api/missions/${flight.id}/collection`)).json()).state.currentSessionId).toBe(next.currentState.currentSessionId);
 await page.unroute(`**/api/missions/${flight.id}/collection/control`);
 await page.route('**/api/mission-intelligence',route=>route.abort('failed'));await expect(panel).toContainText('unavailable or stale');await expect(panel.getByRole('button',{name:'End current science session',exact:true})).toBeDisabled();
 await page.unroute('**/api/mission-intelligence');await expect(panel.getByRole('button',{name:'End current science session',exact:true})).toBeEnabled();
});
test('unknown metrics do not render a fabricated percentage and Library/History links use existing routes',async({page,request})=>{
 await begin(request);await page.goto('/#control');const panel=page.locator('#mission-intelligence');await expect(panel).toContainText('armed');
 const text=await page.evaluate(async()=>{const {metricText}=await import('/mission-intelligence.js');return metricText({id:'coverage',status:'unsupported',numerator:10,denominator:10,unit:'m²',missing:0,unassessed:1,method:'Unknown native support'});});expect(text).not.toContain('100%');expect(text).toContain('unsupported');
 await panel.getByRole('link',{name:'Browse & Download scientific sources',exact:true}).click();await expect(page.getByRole('heading',{name:'Browse & Download Data',exact:true})).toBeVisible();await page.goBack();await expect(panel).toBeVisible();
 await panel.getByRole('link',{name:'Review offline flight reports',exact:true}).click();await expect(page.locator('#history')).toBeVisible();
});
