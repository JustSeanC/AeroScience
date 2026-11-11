import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function get(r,p){const response=await r.get(p);expect(response.ok(),await response.text()).toBe(true);return response.json();}
async function post(r,p,data={}){const {csrf}=await(await r.post('/api/session',{data:{},headers:{Origin:origin}})).json();const response=await r.post(p,{data,headers:{Origin:origin,'X-AeroScience-CSRF':csrf}});expect(response.ok(),await response.text()).toBe(true);return response.json();}
async function flight(request){await post(request,'/api/mission-selection',{planId:null});return post(request,'/api/missions',{name:'P04 synthetic research flight',armScience:true,instruments:[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000}]});}
test.beforeEach(async({page})=>{await page.route('https://**',r=>r.abort());});
test.afterEach(async({request})=>{const s=await get(request,'/api/state');if(s.activeMission)await post(request,`/api/missions/${s.activeMission.id}/end`);await post(request,'/api/mission-selection',{planId:null});await post(request,'/api/instruments',[]);});
test('Current Flight reuses one controller through navigation, preserves explicit actions and authoritative outcomes',async({page,request})=>{
 const m=await flight(request),commands=[];page.on('request',r=>{if(r.method()==='POST'&&/\/collection\/control$/.test(new URL(r.url()).pathname))commands.push(r.postDataJSON());});
 await page.goto('/#flight?future=preserved');const cockpit=page.locator('#operations #science-journey');await expect(cockpit).toHaveAttribute('data-science','armed');await expect(cockpit).toContainText(m.name);await expect(cockpit).toContainText('Retained original scientific observations: 0');
 for(const route of ['#control','#operations','#flight/details','#flight']){await page.evaluate(h=>location.hash=h,route);await expect(page.locator('#science-journey')).toHaveCount(1);}
 await expect(cockpit).toBeVisible();const start=cockpit.getByRole('button',{name:'Start Science Collection',exact:true});await expect(start).toHaveClass(/primary/);
 let release,entered;const held=new Promise(r=>entered=r),gate=new Promise(r=>release=r);
 await page.route('**/collection/control',async route=>{const response=await route.fetch();entered();await gate;await route.fulfill({response});});
 await start.click();await held;await expect(start).toBeDisabled();await expect(cockpit).not.toHaveAttribute('data-science','collecting');release();await page.unroute('**/collection/control');
 await expect(cockpit).toHaveAttribute('data-science','collecting');await expect(cockpit).toHaveAttribute('data-recording','active');expect(commands).toHaveLength(1);
 const s=(await get(request,`/api/missions/${m.id}/collection`)).state;await expect(cockpit).toHaveAttribute('data-session-id',s.currentSessionId);
 await cockpit.getByRole('button',{name:'Pause science collection',exact:true}).click();await expect(cockpit).toHaveAttribute('data-science','paused');await expect(cockpit).toHaveAttribute('data-recording','active');expect((await get(request,'/api/state')).activeMission.id).toBe(m.id);
 const resume=cockpit.getByRole('button',{name:'Resume science collection',exact:true});await resume.focus();await page.waitForTimeout(2200);await expect(resume).toBeFocused();await resume.press('Enter');await expect(cockpit).toHaveAttribute('data-science','collecting');
 await cockpit.getByRole('button',{name:'End current science session',exact:true}).click();await expect(cockpit).toHaveAttribute('data-science','betweenSessions');await cockpit.getByRole('button',{name:'Start Science Collection',exact:true}).click();await expect(cockpit).toContainText('Science session 2');expect((await get(request,`/api/missions/${m.id}/collection`)).state.currentSessionId).not.toBe(s.currentSessionId);
 expect(commands).toHaveLength(5);await page.getByRole('button',{name:'Record a pilot observation',exact:true}).click();await expect(page.locator('#marker-note')).toBeVisible();await page.getByRole('button',{name:'Map, route & waypoints',exact:true}).click();await expect(page.locator('#operations #mission-map')).toBeVisible();
});
test('Current Flight expires stale science without inventing source readiness or changing recording',async({page,request})=>{
 const m=await flight(request);await page.goto('/#operations');const cockpit=page.locator('#operations #science-journey');await expect(cockpit).toHaveAttribute('data-science','armed');
 await page.route('**/api/mission-intelligence',r=>r.abort());await expect(cockpit).toHaveAttribute('data-science','unknown');await expect(cockpit).toContainText('Last checked count');await expect(cockpit.getByRole('button',{name:'Start Science Collection',exact:true})).toBeDisabled();expect((await get(request,'/api/state')).activeMission.id).toBe(m.id);
 await page.unroute('**/api/mission-intelligence');await expect(cockpit).toHaveAttribute('data-science','armed');
});
test('scanner spacing is advisory, source-aware and withheld for mismatched or stale configuration evidence',async({page})=>{
 await page.goto('/#settings');await page.evaluate(async()=>{
  const {scannerGuidance}=await import('/scanner-guidance.js');const host=document.createElement('section');host.id='p04-scanner-fixture';document.querySelector('#settings').append(host);const view=scannerGuidance(host),now=new Date().toISOString();
  const card={instrumentId:'terrain-scanner',enabled:true,evidenceFresh:true,configuration:{id:'configuration'},scientificData:{status:'unavailable'},acquisition:{rejectedObservations:2,missingIntervals:3},scannerSpacing:{version:1,missionId:'flight',sessionId:'session',configurationId:'configuration',evidenceUtc:now,predictedSpacingMetres:500,modeledAlongTrackMetres:100,configuredIntervalSeconds:5,mayNotOverlap:true}};
  const data={version:1,generatedUtc:now,flight:{activeMission:{id:'flight',startedUtc:now},connection:{state:'connected'},telemetry:{timestampUtc:now,isValid:true}},collection:{state:{missionId:'flight',phase:'collecting',currentSessionId:'session'},currentSession:{id:'session',sequence:1}},sensorOperating:{instruments:[card]}};
  window.scannerFixture={view,data,card};view.update(data);
 });
 const root=page.locator('#p04-scanner-fixture');await expect(root).toContainText('individual acquisitions may not overlap');await expect(root).toContainText('Scientific source: unavailable');await expect(root).toContainText('rejected acquisitions: 2');await expect(root).toContainText('missed intervals: 3');await expect(root).toContainText('Saved product spacing is unavailable');await expect(root).toContainText('not measured coverage');
 await page.evaluate(()=>{window.scannerFixture.card.scannerSpacing.configurationId='other';window.scannerFixture.view.update(window.scannerFixture.data);});await expect(root).toContainText('Predicted spacing is unavailable');
 await page.evaluate(()=>{window.scannerFixture.card.scannerSpacing.configurationId='configuration';window.scannerFixture.view.stale();});await expect(root).toContainText('Predicted spacing is unavailable');await expect(root).not.toContainText('individual acquisitions may not overlap');
});
for(const theme of ['light','dark'])test(`Current Flight ${theme}: accessible desktop/narrow, high text scaling and labeled synthetic capture`,async({page,request},info)=>{
 await flight(request);await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});await page.goto('/#operations');const root=page.locator('#operations'),cockpit=root.locator('#science-journey');await expect(cockpit).toHaveAttribute('data-science','armed');await expect(page.locator('html')).toHaveAttribute('data-resolved-theme',theme);
 await root.evaluate((element,theme)=>{const label=document.createElement('p');label.textContent=`P04a · ${theme.toUpperCase()} · SYNTHETIC CHROMIUM FIXTURE · Not installed Windows/MSFS acceptance`;label.className='metadata';element.prepend(label);},theme);
 for(const width of [1440,820,390]){await page.setViewportSize({width,height:1000});await page.evaluate(()=>scrollTo(0,0));expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect((await new AxeBuilder({page}).include('#operations').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze()).violations).toEqual([]);if(width!==820)await page.screenshot({path:info.outputPath(`synthetic-browser-current-flight-${theme}-${width}.png`),fullPage:true});}
 await page.setViewportSize({width:1440,height:1000});await page.evaluate(()=>{document.documentElement.style.zoom='2';for(const e of document.querySelectorAll('.current-flight-tools>*'))e.textContent+=' — extended translated label';});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);await expect(cockpit.getByRole('button',{name:'Start Science Collection',exact:true})).toBeVisible();
});
