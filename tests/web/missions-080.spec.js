// Disposable synthetic host records only; not installed Windows/MSFS acceptance.
import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080',explorer=page=>page.locator('#missions-explorer'),prepare=page=>page.locator('#mission-preparation');
const starter=(page,name='Vertical Atmospheric Profile')=>explorer(page).getByRole('article',{name:name+' starter',exact:true});
async function get(request,path){const response=await request.get(path);expect(response.ok(),await response.text()).toBe(true);return response.json();}
async function post(request,path,data={}){const {csrf}=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':csrf}});expect(response.ok(),await response.text()).toBe(true);return response.json();}
async function reset(request){const state=await get(request,'/api/state');if(state.activeMission)await post(request,`/api/missions/${state.activeMission.id}/end`);await post(request,'/api/mission-selection',{planId:null});await post(request,'/api/instruments',[]);await post(request,'/api/testing/operating/input',{});}
async function ready(page){await expect(starter(page).getByRole('button',{name:'Fly Now',exact:true})).toBeEnabled();}
const go=(page,hash)=>page.evaluate(value=>{location.hash=value;},hash);
test.beforeEach(async({page,request})=>{await reset(request);await page.route('https://**',r=>r.abort());await expect.poll(async()=>{const state=await get(request,'/api/state');return state.connection.state==='connected'&&Date.now()-Date.parse(state.telemetry?.timestampUtc)<6000;}).toBe(true);});
test.afterEach(async({request})=>{await reset(request);});

test('beginner discovery shows real question, difficulty, instruments and sources without creating work',async({page,request})=>{
  const writes=[];page.on('request',r=>{if(r.method()==='POST'&&new URL(r.url()).pathname!=='/api/session')writes.push(r.url());});
  await page.goto('/#missions');await ready(page);await expect(explorer(page).locator('#missions-catalog > article')).toHaveCount(6);
  await expect(starter(page)).toContainText('Explore temperature with altitude');await expect(starter(page)).toContainText('Suggested experience');
  const definitions=await get(request,'/api/instruments'),templates=await get(request,'/api/templates');
  for(const item of templates.find(t=>t.id==='atmosphericProfile').instruments.filter(i=>i.enabled))await expect(starter(page)).toContainText(definitions.find(d=>d.id===item.instrumentId).name);
  await expect(starter(page,'Experimental Sensor Checkout')).toContainText('Fictional modeled output');await expect(starter(page,'Scientific Observation Flight')).toContainText('not a sensor measurement');
  await explorer(page).getByLabel('Find a starter experiment',{exact:true}).fill('transect');await expect(explorer(page).locator('#missions-catalog > article:visible')).toHaveCount(1);
  await page.getByLabel('Find a starter experiment',{exact:true}).fill('no such experiment');await expect(explorer(page)).toContainText('No matching experiments');
  expect(writes).toEqual([]);expect((await get(request,'/api/state')).activeMission).toBeNull();
});
test('guided preparation uses exact revisions and original readiness, with separately explicit recording and science',async({page,request})=>{
  const mutations=[];page.on('request',r=>{if(r.method()==='POST')mutations.push({path:new URL(r.url()).pathname,body:r.postDataJSON()});});
  await page.goto('/#missions');await ready(page);await starter(page).getByRole('button',{name:'Fly Now',exact:true}).click();await expect(prepare(page)).toHaveAttribute('data-preparation-state','connected');
  const state=await get(request,'/api/state'),plan=await get(request,'/api/plans/'+state.selectedPlanId);expect(state.activeMission).toBeNull();expect(await page.evaluate(()=>scrollY)).toBe(0);
  await expect(prepare(page)).toContainText(plan.id);await expect(prepare(page)).toContainText('card revision '+plan.researchCards[0].revision);
  const assessment=page.waitForRequest(r=>new URL(r.url()).pathname==='/api/plans/readiness');await prepare(page).getByRole('button',{name:'Evaluate science readiness',exact:true}).click();const assessed=await assessment;
  expect(assessed.postDataJSON()).toEqual(plan);await expect(prepare(page)).toContainText('Assessed');
  expect(mutations.filter(r=>r.path==='/api/missions'||r.path.endsWith('/collection/control'))).toEqual([]);
  await prepare(page).getByRole('button',{name:'Review recording controls',exact:true}).click();await expect(page.locator('#operations')).toBeVisible();await expect(page.locator('#arm-science')).toBeChecked();
  await page.getByRole('button',{name:'Start Recording',exact:true}).click();await expect(page.getByRole('button',{name:'End mission',exact:true})).toBeVisible();
  const flight=(await get(request,'/api/state')).activeMission;const starts=mutations.filter(r=>r.path==='/api/missions');expect(starts).toHaveLength(1);expect(starts[0].body.planId).toBe(plan.id);expect(starts[0].body.armScience).toBe(true);
  await expect(page.locator('#science-journey')).toContainText('Armed — science has not started');expect((await get(request,`/api/missions/${flight.id}/science`)).observations).toHaveLength(0);
  expect(mutations.filter(r=>r.path.endsWith('/collection/control'))).toHaveLength(0);
  await page.getByRole('button',{name:'Start Science Collection',exact:true}).click();await expect(page.locator('#science-journey')).toContainText('Science collection: Collecting');
  expect(mutations.filter(r=>r.path.endsWith('/collection/control'))).toHaveLength(1);
  expect((await get(request,`/api/missions/${flight.id}/research`)).planSnapshot.id).toBe(plan.id);
});
test('measurement selection exposes real external prerequisites and never approves a download',async({page,request})=>{
  const transfers=[];page.on('request',r=>{if(r.method()==='POST'&&/\/api\/.*\/(?:start|approve|execute|download)(?:\/|$)/.test(new URL(r.url()).pathname))transfers.push(r.url());});
  await page.goto('/#missions');await ready(page);const survey=starter(page,'Terrain & Temperature Survey');await survey.getByText('Requirements & measurement details',{exact:true}).click();
  for(const name of ['Terrain Elevation','Land Surface Temperature'])await survey.getByLabel(name,{exact:true}).uncheck();await survey.getByLabel('Air Temperature',{exact:true}).check();
  await expect(survey).toContainText('without an external scene');await survey.getByLabel('Terrain Elevation',{exact:true}).check();await expect(survey).toContainText('Compatible retained external datasets are required');await expect(survey).toContainText('Terrain Scanner (DEM Elevation)');
  await survey.getByRole('button',{name:'Fly Now',exact:true}).click();await expect(prepare(page)).toHaveAttribute('data-preparation-state','connected');
  const selected=(await get(request,'/api/state')).selectedPlanId,plan=await get(request,'/api/plans/'+selected);expect(plan.instruments.map(i=>i.instrumentId)).toContain('terrain-scanner');
  const responsePromise=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/plans/readiness');await prepare(page).getByRole('button',{name:'Evaluate science readiness',exact:true}).click();const result=await(await responsePromise).json();await expect(prepare(page)).toContainText('Assessed');
  for(const check of result.checks.filter(c=>['unavailable','notYetAssessed'].includes(c.status)))await expect(prepare(page)).toContainText(check.reasons[0]);
  expect(transfers).toEqual([]);expect((await get(request,'/api/state')).activeMission).toBeNull();
});
test('Customize preserves the dirty full-planner draft and retry identity when discard is declined',async({page,request})=>{
  await page.goto('/#planner');await page.locator('#plan-name').fill('Preserve my custom route draft');
  await go(page,'#missions');await ready(page);await expect(explorer(page).getByRole('link',{name:'Resume unsaved plan',exact:true})).toBeVisible();
  const ids=[];page.on('request',r=>{if(new URL(r.url()).pathname==='/api/research/starters/prepare'&&r.method()==='POST')ids.push(r.postDataJSON().requestId);});
  page.once('dialog',d=>d.dismiss());await starter(page,'Flight-Regime Validation').getByRole('button',{name:'Customize',exact:true}).click();await expect(page.locator('#plan-name')).toHaveValue('Preserve my custom route draft');await expect(page).toHaveURL(/#planner$/);
  await go(page,'#missions');await ready(page);page.once('dialog',d=>d.accept());await starter(page,'Flight-Regime Validation').getByRole('button',{name:'Customize',exact:true}).click();await expect(page.locator('#research-starter-targets')).toBeVisible();expect(ids).toHaveLength(2);expect(ids[0]).toBe(ids[1]);expect((await get(request,'/api/state')).activeMission).toBeNull();
  await expect(page.getByRole('button',{name:'Add waypoint',exact:true})).toBeVisible();await expect(page.getByText('Flight pattern generator',{exact:true})).toBeVisible();
});
test('lost preparation response reuses the same saved plan and leaves existing plans and revisions untouched',async({page,request})=>{
  const older=await post(request,'/api/research/starters/prepare',{version:1,requestId:crypto.randomUUID(),starterId:'flight-regime',starterVersion:1,intent:'customize'});const oldPlan=await get(request,'/api/plans/'+older.plan.id),oldCard=await get(request,`/api/research/cards/${older.card.id}?revision=${older.card.revision}`);
  let dropped=false;const ids=[];await page.route('**/api/research/starters/prepare',async route=>{ids.push(route.request().postDataJSON().requestId);if(!dropped){dropped=true;await route.fetch();await route.abort();}else await route.continue();});
  await page.goto('/#missions');await ready(page);await starter(page).getByRole('button',{name:'Fly Now',exact:true}).click();await expect(starter(page).getByRole('button',{name:'Start a New Preparation',exact:true})).toBeVisible();
  await page.reload();await ready(page);await starter(page).getByRole('button',{name:'Fly Now',exact:true}).click();await expect(prepare(page)).toHaveAttribute('data-preparation-state','connected');expect(ids).toHaveLength(2);expect(ids[0]).toBe(ids[1]);
  expect((await get(request,'/api/state')).selectedPlanId).toBe(ids[0]);expect((await get(request,'/api/state')).activeMission).toBeNull();expect(await get(request,'/api/plans/'+oldPlan.id)).toEqual(oldPlan);expect(await get(request,`/api/research/cards/${oldCard.id}?revision=${oldCard.revision}`)).toEqual(oldCard);
});
test('pending survey preparation locks its original choices and commits only that captured definition',async({page,request})=>{
  let release,entered;const gate=new Promise(r=>release=r),held=new Promise(r=>entered=r);
  await page.route('**/api/research/starters/prepare',async route=>{const response=await route.fetch();entered();await gate;await route.fulfill({response});});
  await page.goto('/#missions');await ready(page);const survey=starter(page,'Terrain & Temperature Survey');await survey.getByText('Requirements & measurement details',{exact:true}).click();
  await survey.getByLabel('Terrain Elevation',{exact:true}).check();await expect(survey).toContainText('Air Temperature + Terrain Elevation');
  await survey.getByRole('button',{name:'Fly Now',exact:true}).click();await held;await expect(survey.getByLabel('Terrain Elevation',{exact:true})).toBeDisabled();release();
  await expect(prepare(page)).toHaveAttribute('data-preparation-state','connected');const id=(await get(request,'/api/state')).selectedPlanId,plan=await get(request,'/api/plans/'+id);
  const ref=plan.researchCards[0],card=await get(request,`/api/research/cards/${ref.id}?revision=${ref.revision}`);expect(card.survey.measurements).toContain('terrainElevation');expect((await get(request,'/api/state')).activeMission).toBeNull();
});
test('incomplete, missing and unselected preparation links never silently select a plan',async({page,request})=>{
  const saved=await post(request,'/api/research/starters/prepare',{version:1,requestId:crypto.randomUUID(),starterId:'flight-regime',starterVersion:1,intent:'customize'}),writes=[];
  page.on('request',r=>{if(r.method()==='POST'&&new URL(r.url()).pathname!=='/api/session')writes.push(r.url());});
  await page.goto('/#missions/prepare');await expect(prepare(page)).toContainText('Choose an experiment or saved mission plan first');await expect(prepare(page).getByRole('button',{name:'Review recording controls',exact:true})).toBeDisabled();
  await go(page,'#missions/prepare?researchPlan='+saved.plan.id);await expect(prepare(page)).toHaveAttribute('data-preparation-state','unselected');await expect(prepare(page)).toContainText('will not silently change the selection');
  await go(page,'#missions/prepare?researchPlan='+crypto.randomUUID());await expect(prepare(page)).toHaveAttribute('data-preparation-state','unavailable');await expect(prepare(page).getByRole('button',{name:'Review recording controls',exact:true})).toBeDisabled();expect(writes).toEqual([]);expect((await get(request,'/api/state')).selectedPlanId).toBeNull();
});
test('offline preparation is retained and invalid readiness or wrong-card replies never become confirmed',async({page,request})=>{
  await post(request,'/api/testing/operating/input',{disconnected:true});await page.goto('/#missions');await ready(page);await starter(page).getByRole('button',{name:'Fly Now',exact:true}).click();await expect(prepare(page)).toHaveAttribute('data-preparation-state','disconnected');
  const id=(await get(request,'/api/state')).selectedPlanId;expect(id).toBeTruthy();
  await page.route('**/api/plans/readiness',r=>r.fulfill({json:{version:99,checks:[],limitations:[],assessedUtc:new Date().toISOString()}}));await prepare(page).getByRole('button',{name:'Evaluate science readiness',exact:true}).click();await expect(prepare(page)).toContainText('readiness reply is invalid or unsupported');
  await page.route('**/api/research/cards/*?revision=*',async r=>{const response=await r.fetch(),card=await response.json();await r.fulfill({json:{...card,revision:card.revision+1}});});
  await go(page,'#missions');await ready(page);await go(page,'#missions/prepare?researchPlan='+id);await expect(prepare(page)).toHaveAttribute('data-preparation-state','unavailable');await expect(prepare(page)).toContainText('does not match its saved card identity and revision');expect((await get(request,'/api/state')).activeMission).toBeNull();
});
test('unsupported catalog and mismatched preparation receipts cannot select or start another mission',async({page,request})=>{
  await page.route('**/api/research/starters',async r=>{const response=await r.fetch();await r.fulfill({json:(await response.json()).map(s=>({...s,version:99}))});});await page.goto('/#missions');await expect(starter(page)).toContainText('unsupported here');await expect(starter(page).getByRole('button',{name:'Fly Now',exact:true})).toBeDisabled();
  await page.unroute('**/api/research/starters');await explorer(page).getByRole('button',{name:'Refresh experiments',exact:true}).click();await ready(page);
  await page.route('**/api/research/starters/prepare',async r=>{const response=await r.fetch(),reply=await response.json();reply.plan.id=crypto.randomUUID();await r.fulfill({json:reply});});const mutations=[];page.on('request',r=>{if(r.method()==='POST'&&/\/api\/(mission-selection|missions)$/.test(new URL(r.url()).pathname))mutations.push(r.url());});
  await starter(page).getByRole('button',{name:'Fly Now',exact:true}).click();await expect(starter(page)).toContainText('preparation reply does not match this request');expect(mutations).toEqual([]);expect((await get(request,'/api/state')).selectedPlanId).toBeNull();
});
test('leaving during preparation rejects late navigation/selection, and legacy query continuations keep original workflows',async({page,request})=>{
  let release,entered;const gate=new Promise(r=>release=r),held=new Promise(r=>entered=r);await page.route('**/api/research/starters/prepare',async route=>{const response=await route.fetch();entered();await gate;await route.fulfill({response});});
  await page.goto('/#missions');await ready(page);await starter(page).getByRole('button',{name:'Fly Now',exact:true}).click();await held;
  await go(page,'#settings');await expect(page.locator('#settings')).toBeVisible();release();await expect(page).toHaveURL(/#settings$/);expect((await get(request,'/api/state')).selectedPlanId).toBeNull();
  await page.unroute('**/api/research/starters/prepare');await go(page,'#missions?permission=submitEvidence&future=a%3Fb');await expect(page.locator('#my-research')).toBeVisible();await expect(page).toHaveURL(/#my-research\?permission=submitEvidence&future=a%3Fb$/);
  await go(page,'#missions/templates');await expect(page.locator('#my-research #my-templates')).toBeVisible();
});
test('saved plan changes invalidate an open assessment without replacing keyboard focus or changing identity',async({page,request})=>{
  await page.goto('/#missions');await ready(page);await starter(page).getByRole('button',{name:'Fly Now',exact:true}).click();await expect(prepare(page)).toHaveAttribute('data-preparation-state','connected');
  const assessment=prepare(page).getByRole('button',{name:'Evaluate science readiness',exact:true});await assessment.click();await expect(prepare(page)).toContainText('Assessed');await assessment.focus();
  const id=(await get(request,'/api/state')).selectedPlanId,plan=await get(request,'/api/plans/'+id);await post(request,'/api/plans',{...plan,name:'Saved revision changed in another view'});
  await expect(prepare(page).getByRole('heading',{name:'Saved revision changed in another view',exact:true})).toBeVisible({timeout:9000});await expect(prepare(page)).toContainText('Stale · plan or context changed');await expect(assessment).toBeFocused();expect((await get(request,'/api/state')).selectedPlanId).toBe(id);
});
for(const theme of ['light','dark'])test(`Missions ${theme}: keyboard, responsive accessibility and labeled synthetic browser captures`,async({page},info)=>{
  test.setTimeout(60000);await page.emulateMedia({colorScheme:theme});await page.goto('/#missions');await ready(page);await expect(page.locator('html')).toHaveAttribute('data-theme','auto');
  const caption=async root=>root.evaluate((element,theme)=>{const p=document.createElement('p');p.className='mission-capture-label';p.textContent=`P03b · ${theme.toUpperCase()} · SYNTHETIC BROWSER FIXTURE · Not installed Windows/MSFS acceptance`;element.prepend(p);},theme);
  await caption(explorer(page));
  for(const width of [1440,820,390]){await page.setViewportSize({width,height:1000});await page.evaluate(()=>scrollTo(0,0));expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect((await new AxeBuilder({page}).include('#missions-workspace').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze()).violations).toEqual([]);if(width!==820)await page.screenshot({path:info.outputPath(`synthetic-browser-explore-${theme}-${width}.png`),fullPage:true});}
  await starter(page).getByRole('button',{name:'Fly Now',exact:true}).focus();await page.keyboard.press('Enter');await expect(prepare(page)).toHaveAttribute('data-preparation-state','connected');await caption(prepare(page));
  for(const width of [1440,390]){await page.setViewportSize({width,height:1000});await page.evaluate(()=>scrollTo(0,0));expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect((await new AxeBuilder({page}).include('#missions-workspace').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze()).violations).toEqual([]);await page.screenshot({path:info.outputPath(`synthetic-browser-prepare-${theme}-${width}.png`),fullPage:true});}
  await page.setViewportSize({width:1440,height:1000});await page.evaluate(()=>{document.documentElement.style.zoom='2';for(const a of document.querySelectorAll('#mission-preparation .button'))a.textContent+=' — extended localization stress label';});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});
