// Disposable synthetic host data. These checks do not establish installed MSFS acceptance.
import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080',home=page=>page.locator('#home');
async function get(request,path){const response=await request.get(path);expect(response.ok(),await response.text()).toBe(true);return response.json();}
async function post(request,path,data={}){
  const {csrf}=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();
  const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':csrf}});
  expect(response.ok(),await response.text()).toBe(true);return response.json();
}
async function reset(request){const state=await get(request,'/api/state');if(state.activeMission)await post(request,`/api/missions/${state.activeMission.id}/end`);
  await post(request,'/api/mission-selection',{planId:null});await post(request,'/api/instruments',[]);await post(request,'/api/testing/operating/input',{});}
const go=async(page,hash)=>{await page.evaluate(value=>{location.hash=value;},hash);};
test.beforeEach(async({page,request})=>{await reset(request);await page.route('https://**',route=>route.abort());
  await expect.poll(async()=>{const state=await get(request,'/api/state');return state.connection.state==='connected'&&Date.now()-Date.parse(state.telemetry?.timestampUtc)<6000;}).toBe(true);});
test.afterEach(async({request})=>{await reset(request);});

test('empty Home is the default landing and connected idle action only opens existing Missions',async({page})=>{
  const writes=[];page.on('request',r=>{if(r.method()!=='GET'&&new URL(r.url()).pathname!=='/api/session')writes.push(r.url());});
  await page.route('**/api/missions?*',r=>r.fulfill({json:[]}));await page.goto('/');
  await expect(home(page)).toHaveAttribute('data-home-state','idle');await expect(home(page)).toContainText('No saved flights to show yet');
  await expect(home(page).locator('a.primary')).toHaveCount(1);await expect(home(page)).toContainText('Flight recording inactive');
  await expect(home(page)).toContainText('Not started');await expect(home(page)).not.toContainText('Retained original observations: 0');
  await home(page).getByRole('link',{name:'Explore science missions',exact:true}).click();await expect(page.locator('#missions-explorer')).toBeVisible();
  await expect(page.getByRole('article',{name:'Vertical Atmospheric Profile starter',exact:true})).toBeVisible();expect(writes).toEqual([]);
});
test('an unsaved draft survives Home and resumes the original full planner without replacement',async({page})=>{
  await page.goto('/#missions/plans');const name=page.locator('#plan-name');await name.fill('Zoë 東京 — unfinished µm plan');
  await go(page,'#home');await expect(home(page)).toHaveAttribute('data-home-state','draft');await expect(home(page)).toContainText('Unsaved planner draft: Zoë 東京');
  await home(page).getByRole('link',{name:'Resume mission setup',exact:true}).click();await expect(name).toBeVisible();await expect(name).toHaveValue('Zoë 東京 — unfinished µm plan');
  await expect(page.locator('#missions-workspace > header h1')).toBeFocused();
});
test('a saved selected plan continues original setup; missing selection never becomes unplanned',async({page,request})=>{
  await page.goto('/#missions');await page.getByRole('article',{name:'Flight-Regime Validation starter',exact:true}).getByRole('button',{name:'Fly Now',exact:true}).click();
  await expect.poll(async()=>(await get(request,'/api/state')).selectedPlanId).toBeTruthy();
  const state=await get(request,'/api/state');expect(state.selectedPlanId).toBeTruthy();expect(state.activeMission).toBeNull();
  const plan=await get(request,'/api/plans/'+state.selectedPlanId);
  await go(page,'#home');await expect(home(page)).toHaveAttribute('data-home-state','plan');await expect(home(page)).toContainText('Selected saved plan: '+plan.name);
  await home(page).getByRole('link',{name:'Continue mission setup',exact:true}).click();await expect(page.locator('#mission-preparation')).toBeVisible();await page.locator('#mission-preparation').getByRole('button',{name:'Review recording controls',exact:true}).click();await expect(page.locator('#operations')).toBeVisible();
  expect((await get(request,'/api/state')).selectedPlanId).toBe(state.selectedPlanId);
  await page.route('**/api/plans/'+state.selectedPlanId,r=>r.fulfill({status:404,json:{error:'Synthetic unavailable selection'}}));
  await go(page,'#home');await expect(home(page)).toContainText('no unplanned flight was substituted');await expect(home(page)).toHaveAttribute('data-home-state','plan');
  expect((await get(request,'/api/state')).activeMission).toBeNull();
});
test('Home navigation preserves one stream and exactly one original recording command per click',async({page,request})=>{
  const starts=[],collection=[],streams=[];page.on('request',r=>{const path=new URL(r.url()).pathname;
    if(path==='/api/events')streams.push(r.url());if(r.method()==='POST'&&path==='/api/missions')starts.push(r.postDataJSON());
    if(r.method()==='POST'&&path.endsWith('/collection/control'))collection.push(r.postDataJSON());});
  await page.goto('/#home');await expect(home(page)).toHaveAttribute('data-home-state','idle');
  for(let round=0;round<3;round++)for(const hash of ['#missions','#control','#home']){await go(page,hash);await expect(page.locator('#'+(hash==='#missions'?'missions-explorer':hash.slice(1)))).toBeVisible();}
  await go(page,'#control');await page.getByLabel('MISSION NAME',{exact:true}).fill('P03a single command fixture');
  await page.getByRole('button',{name:'Start Recording',exact:true}).click();await expect(page.getByRole('button',{name:'End mission',exact:true})).toBeVisible();
  const flight=(await get(request,'/api/state')).activeMission;expect(starts).toHaveLength(1);expect(collection).toEqual([]);expect(streams).toHaveLength(1);
  await go(page,'#home');await expect(home(page)).toHaveAttribute('data-home-state','active');await expect(home(page)).toContainText('Active flight: '+flight.name);
  await expect(home(page)).toContainText('Retained original observations: 0');
  expect((await new AxeBuilder({page}).include('#home').withTags(['wcag2a','wcag2aa','wcag21aa']).analyze()).violations).toEqual([]);
  expect((await get(request,`/api/missions/${flight.id}/science`)).observations).toHaveLength(0);
  await home(page).getByRole('link',{name:'Open Current Flight',exact:true}).click();await expect(page.locator('#operations')).toBeVisible();
  expect((await get(request,'/api/state')).activeMission.id).toBe(flight.id);expect(starts).toHaveLength(1);expect(collection).toEqual([]);expect(streams).toHaveLength(1);
});
test('disconnected and paused simulator keep recording truth separate and provide connection guidance',async({page,request})=>{
  await post(request,'/api/testing/operating/input',{disconnected:true});await page.goto('/#home');
  await expect(home(page)).toHaveAttribute('data-home-state','connect');await expect(home(page)).toContainText('Simulator disconnected');
  await home(page).getByRole('link',{name:'Connection steps',exact:true}).click();await expect(page.locator('#help')).toBeVisible();
  await post(request,'/api/testing/operating/input',{});await expect.poll(async()=>{const s=await get(request,'/api/state');return s.connection.state==='connected'&&Date.now()-Date.parse(s.telemetry?.timestampUtc)<6000;}).toBe(true);const flight=await post(request,'/api/missions',{name:'Paused synthetic flight',mode:'universal'});
  await post(request,'/api/testing/operating/input',{paused:true});await go(page,'#home');
  await expect(home(page)).toHaveAttribute('data-home-state','active');await expect(home(page)).toContainText('Flight recording active');
  // The synthetic host supports paused input; it must not be described as paused recording.
  await expect(home(page)).toContainText('Simulator is paused');await expect(home(page)).toContainText('current simulator input is unavailable or paused');
  expect(await home(page).locator('.home-warning').evaluate(node=>!!(node.compareDocumentPosition(document.querySelector('#home .home-grid'))&Node.DOCUMENT_POSITION_FOLLOWING))).toBe(true);
  expect((await get(request,'/api/state')).activeMission.id).toBe(flight.id);
});
test('stale, future and failed reads show unconfirmed activity, while archive failure is never an empty result',async({page})=>{
  await page.goto('/#home');await expect(home(page)).toHaveAttribute('data-home-state','idle');
  for(const age of [-20000,20000]){
    await page.route('**/api/mission-intelligence',async r=>{const response=await r.fetch(),value=await response.json();value.generatedUtc=new Date(Date.now()+age).toISOString();await r.fulfill({json:value});});
    await home(page).getByRole('button',{name:'Refresh status',exact:true}).click();await expect(home(page)).toHaveAttribute('data-home-state','unavailable');await expect(home(page)).toContainText('Flight recording unavailable');
    await page.unroute('**/api/mission-intelligence');
  }
  await page.route('**/api/mission-intelligence',r=>r.abort());await page.route('**/api/missions?*',r=>r.abort());
  await home(page).getByRole('button',{name:'Refresh status',exact:true}).click();await expect(home(page)).toContainText('Saved results may still exist');
  await expect(home(page).locator('.home-recent a')).toHaveCount(0);await expect(home(page)).not.toContainText('No saved flights to show yet');
  await page.unroute('**/api/mission-intelligence');await page.unroute('**/api/missions?*');
  await home(page).getByRole('button',{name:'Refresh status',exact:true}).click();await expect(home(page)).toHaveAttribute('data-home-state','idle');
});
test('late replies after navigation cannot overwrite Home or archived flight context; polling preserves keyboard focus',async({page})=>{
  let reads=0;page.on('request',r=>{if(new URL(r.url()).pathname==='/api/mission-intelligence')reads++;});
  await page.goto('/#home');await expect(home(page)).toHaveAttribute('data-home-state','idle');const primary=home(page).locator('a.primary');await primary.focus();const before=reads;
  await expect.poll(()=>reads).toBeGreaterThan(before);await expect(primary).toBeFocused();
  let release,entered;const gate=new Promise(r=>release=r),held=new Promise(r=>entered=r);
  await page.route('**/api/mission-intelligence',async route=>{const response=await route.fetch();entered();await gate;await route.fulfill({response}).catch(()=>{});});
  await home(page).getByRole('button',{name:'Refresh status',exact:true}).click();await held;
  await go(page,'#results?future=retain');await expect(page.locator('#history')).toBeVisible();release();
  await expect(page).toHaveURL(/#results\?future=retain$/);await expect(home(page)).toBeHidden();await page.unroute('**/api/mission-intelligence');
  await page.goBack();await expect(home(page)).toBeVisible();await expect(home(page)).toHaveAttribute('data-home-state','idle');await expect(home(page).getByRole('heading',{name:'Home',exact:true})).toBeFocused();
});
test('Organizations is always discoverable before connection without granting permission on navigation',async({page})=>{
  await page.route('**/api/organizations',r=>r.fulfill({json:{connections:[],vaultAvailable:false}}));await page.goto('/#home');
  const nav=page.getByRole('navigation',{name:'Main navigation',exact:true});await expect(nav.getByRole('link',{name:'Organizations',exact:true})).toBeVisible();expect(await nav.locator('a:not([hidden])').allTextContents()).toEqual(['Home','Missions','Current Flight','Results & History','Organizations']);
  const writes=[];page.on('request',r=>{if(r.method()!=='GET'&&r.url().includes('/api/organizations'))writes.push(r.url());});
  await home(page).getByRole('link',{name:'Connect or view organizations',exact:true}).click();await expect(page.locator('#research-organizations')).toBeVisible();await expect(page.locator('#research-organizations h2')).toBeFocused();
  await expect(nav.getByRole('link',{name:'Organizations',exact:true})).toHaveAttribute('aria-current','page');await expect(nav.getByRole('link',{name:'Missions',exact:true})).not.toHaveAttribute('aria-current','page');await expect(nav.locator('[aria-current=page]')).toHaveCount(1);
  await page.route('**/api/organizations',r=>r.fulfill({json:{connections:[{id:'synthetic',name:'Synthetic configured connection',origin:'https://example.invalid',expiresUtc:'2030-01-01T00:00:00Z',permission:'submitEvidence'}],vaultAvailable:false}}));
  await go(page,'#home');await go(page,'#organizations?future=keep');await expect(nav.getByRole('link',{name:'Organizations',exact:true})).toBeVisible();await expect(nav.getByRole('link',{name:'Organizations',exact:true})).toHaveAttribute('aria-current','page');await expect(page).toHaveURL(/future=keep&section=organizations$/);expect(writes).toEqual([]);
});
for(const theme of ['light','dark'])test(`Home ${theme} is accessible and responsive; labeled synthetic browser screenshots`,async({page},info)=>{
  test.setTimeout(60000);await page.emulateMedia({colorScheme:theme});await page.goto('/#home');await expect(home(page)).toHaveAttribute('data-home-state','idle');
  await expect(page.locator('html')).toHaveAttribute('data-theme','auto');await expect(page.locator('html')).toHaveAttribute('data-resolved-theme',theme);
  await page.evaluate(theme=>{const caption=document.createElement('p');caption.textContent=`P03a · ${theme.toUpperCase()} · SYNTHETIC BROWSER FIXTURE · Not installed Windows/MSFS acceptance`;caption.style.fontWeight='700';document.querySelector('#home').prepend(caption);},theme);
  for(const width of [1440,820,390]){
    await page.setViewportSize({width,height:1000});
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),String(width)).toBe(true);
    const audit=await new AxeBuilder({page}).include('#home').include('.sidebar').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze();
    expect(audit.violations.map(v=>({id:v.id,nodes:v.nodes.map(n=>({target:n.target,summary:n.failureSummary}))})),String(width)).toEqual([]);
    await page.screenshot({path:info.outputPath(`synthetic-browser-home-${theme}-${width}.png`),fullPage:true});
  }
  await page.setViewportSize({width:1440,height:1000});await page.evaluate(()=>{
    document.documentElement.style.zoom='2';for(const el of document.querySelectorAll('#home a,#home h2'))el.textContent+=' — extended localization stress label';
  });expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  await page.evaluate(()=>{document.documentElement.style.zoom='1';});await page.setViewportSize({width:390,height:844});
  await page.getByRole('button',{name:'Menu',exact:false}).click();await page.getByRole('navigation',{name:'Main navigation',exact:true}).getByRole('link',{name:'Missions',exact:true}).focus();await page.keyboard.press('Enter');
  await expect(page.locator('#missions-explorer')).toBeVisible();await expect(page.getByRole('button',{name:'Menu',exact:false})).toHaveAttribute('aria-expanded','false');
});
