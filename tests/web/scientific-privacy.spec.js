import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {openLibrary} from './helpers/library-ui.js';
const origin='http://127.0.0.1:5080',title='Online Access & Privacy';
async function post(request,path,data={}){const session=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
async function get(request,path){return (await request.get(path)).json();}
async function preferences(request){return (await get(request,'/api/science/access')).configuration.preferences;}
async function permissions(request){return {maps:(await get(request,'/api/maps/settings')).configuration,science:await preferences(request),aviation:(await get(request,'/api/aviation/configuration')).configuration,tfr:await get(request,'/api/aviation/tfr/configuration')};}
async function blockedSearch(page){await openLibrary(page);await page.getByRole('button',{name:'Use bounding box',exact:true}).click();await page.getByRole('button',{name:'Find Available Data',exact:true}).click();const review=page.getByRole('button',{name:'Review Privacy Consent',exact:true});await expect(review).toBeVisible();return review;}
async function openPrompt(page,review){await review.click();const d=page.getByRole('dialog',{name:title,exact:true});await expect(d.getByRole('button',{name:'Allow Online Access',exact:true})).toBeFocused();return d;}
test.beforeEach(async({request})=>{
 await post(request,'/api/testing/library/setup');await post(request,'/api/testing/library/mode/normal');
 await post(request,'/api/science/access',{...await preferences(request),onlineEnabled:false,privacyAccepted:false,backgroundRetrieval:false});
 await post(request,'/api/maps/settings',{onlineEnabled:false,layer:'offline',privacyAccepted:false});
 await post(request,'/api/aviation/tfr/configuration',{onlineEnabled:false});await post(request,'/api/aviation/configuration',{onlineEnabled:false});
});
test.afterEach(async({request})=>{
 await post(request,'/api/aviation/tfr/configuration',{onlineEnabled:false});await post(request,'/api/aviation/configuration',{onlineEnabled:false});
 await post(request,'/api/maps/settings',{onlineEnabled:false,layer:'offline',privacyAccepted:false});await post(request,'/api/testing/library/teardown');
});

test('real consent conflict offers one combined disclosure; Not Now, X and Escape grant nothing and preserve search and jobs',async({page,request})=>{
 const before=await permissions(request),jobs=await get(request,'/api/science/library/jobs');
 const review=await blockedSearch(page),bounds=await page.getByLabel('Geographic selection',{exact:true}).getAttribute('data-bounds');
 let writes=0;page.on('request',r=>{if(/\/api\/(maps\/settings|science\/access|aviation\/(configuration|tfr\/configuration))$/.test(r.url())&&r.method()==='POST')writes++;});
 for(const close of ['Not Now','Escape','X']){
  const d=await openPrompt(page,review);await expect(d.getByRole('checkbox')).toHaveCount(0);await expect(d).toContainText('NASA Earthdata authorization remains a separate connection');
  if(close==='Escape')await page.keyboard.press('Escape');else await d.getByRole('button',{name:close==='X'?'Close '+title:close,exact:true}).click();
  await expect(d).not.toBeVisible();await expect(review).toBeFocused();
 }
 expect(writes).toBe(0);expect(await permissions(request)).toEqual(before);expect((await get(request,'/api/science/library/jobs')).jobs).toEqual(jobs.jobs);expect(await page.getByLabel('Geographic selection',{exact:true}).getAttribute('data-bounds')).toBe(bounds);await expect(page).toHaveURL(/#library\/download/);
});

test('one explicit choice enables maps, science and automatic FAA/TFR while preserving latest budgets and filters without approving sources',async({page,request})=>{
 await openLibrary(page);await page.getByLabel('Scientific provider / product',{exact:true}).selectOption('landsat');await page.getByLabel('Scene date from (UTC)',{exact:true}).fill('2024-09-01');await page.getByLabel('Scene date to (UTC)',{exact:true}).fill('2024-09-10');await page.getByLabel('Maximum scene cloud (%)',{exact:true}).fill('23');await page.getByRole('button',{name:'Use bounding box',exact:true}).click();
 let catalogCalls=0,approvals=0,scienceWrites=0;page.on('request',r=>{if(r.method()!=='POST')return;if(r.url().endsWith('/api/science/library/catalog'))catalogCalls++;if(/\/api\/science\/library\/(preview|nasa\/preview)|\/control$/.test(r.url()))approvals++;if(r.url().endsWith('/api/science/access'))scienceWrites++;});
 await page.getByRole('button',{name:'Find Available Data',exact:true}).click();const review=page.getByRole('button',{name:'Review Privacy Consent',exact:true}),d=await openPrompt(page,review);
 // Another local client updates a budget while the disclosure is open.
 await post(request,'/api/science/access',{...await preferences(request),maximumDownloadBytes:12582912});const latest=await permissions(request);
 await d.getByRole('button',{name:'Allow Online Access',exact:true}).dblclick();await expect(d).not.toBeVisible();await expect(review).toBeFocused();
 const after=await permissions(request);expect(after.science).toEqual({...latest.science,onlineEnabled:true,privacyAccepted:true});expect(after.maps.preferences).toEqual({...latest.maps.preferences,onlineEnabled:true});expect(after.maps.keyConfigured).toBe(latest.maps.keyConfigured);expect(after.maps.requiresPrivacyChoice).toBe(false);expect(after.aviation.onlineEnabled).toBe(true);expect(after.tfr.tfrMonitoringEnabled).toBe(true);expect(catalogCalls).toBe(1);expect(approvals).toBe(0);expect(scienceWrites).toBe(1);
 await expect(page.getByLabel('Scene date from (UTC)',{exact:true})).toHaveValue('2024-09-01');await expect(page.getByLabel('Scene date to (UTC)',{exact:true})).toHaveValue('2024-09-10');await expect(page.getByLabel('Maximum scene cloud (%)',{exact:true})).toHaveValue('23');await expect(page.getByLabel('Scientific provider / product',{exact:true})).toHaveValue('landsat');
 await page.getByRole('button',{name:'Find Available Data',exact:true}).click();await expect(page.getByLabel(/Select scene LC08/)).toBeVisible();expect(catalogCalls).toBe(2);expect(approvals).toBe(0);await expect(review).toHaveCount(0);
});

test('partial permission failure stays visible and retry completes without resaving already allowed science or approving transfers',async({page,request})=>{
 const jobs=await get(request,'/api/science/library/jobs');
 let fail=true,scienceWrites=0;page.on('request',r=>{if(r.url().endsWith('/api/science/access')&&r.method()==='POST')scienceWrites++;});
 await page.route('**/api/aviation/configuration',route=>route.request().method()==='POST'&&fail?route.fulfill({status:503,json:{error:'Controlled FAA settings failure.'}}):route.continue());
 const review=await blockedSearch(page),d=await openPrompt(page,review);await d.getByRole('button',{name:'Allow Online Access',exact:true}).click();await expect(d.getByRole('alert')).toContainText('Some settings may have saved');await expect(d.getByRole('alert')).toContainText('Controlled FAA settings failure');
 const partial=await permissions(request);expect(partial.science.privacyAccepted).toBe(true);expect(partial.maps.preferences.onlineEnabled).toBe(true);expect(partial.aviation.onlineEnabled).toBe(false);expect(partial.tfr.tfrMonitoringEnabled).toBe(false);await expect(page.getByText('Scientific access choices saved',{exact:true})).toHaveCount(0);
 fail=false;await d.getByRole('button',{name:'Allow Online Access',exact:true}).click();await expect(d).not.toBeVisible();expect(scienceWrites).toBe(1);expect((await permissions(request)).tfr.tfrMonitoringEnabled).toBe(true);expect((await get(request,'/api/science/library/jobs')).jobs).toEqual(jobs.jobs);
});

test('paired disclosure is read-only, keyboard accessible and keeps header and actions visible at narrow widths',async({page,request})=>{
 await page.route('**/api/science/access',async route=>{if(route.request().method()!=='GET')return route.continue();const r=await route.fetch();await route.fulfill({response:r,json:{...await r.json(),canConfigure:false}});});
 const before=await permissions(request);await page.setViewportSize({width:390,height:740});const review=await blockedSearch(page);await review.click();const d=page.getByRole('dialog',{name:title,exact:true});await expect(d.getByRole('button',{name:'Allow Online Access',exact:true})).toBeDisabled();await expect(d.getByText(/Paired devices can review/)).toBeFocused();
 await d.getByText('Current permissions and full provider disclosures',{exact:true}).click();await d.locator('.online-access-content').evaluate(n=>n.scrollTop=n.scrollHeight);
 expect((await new AxeBuilder({page}).include('dialog[aria-label="Online Access & Privacy"]').analyze()).violations).toEqual([]);expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();const close=d.getByRole('button',{name:'Close '+title,exact:true});await expect(close).toBeInViewport();await expect(d.getByRole('button',{name:'Not Now',exact:true})).toBeInViewport();const box=await close.boundingBox();expect(box.width).toBeGreaterThanOrEqual(44);expect(box.height).toBeGreaterThanOrEqual(44);await page.keyboard.press('Escape');await expect(review).toBeFocused();expect(await permissions(request)).toEqual(before);
});

test('unrelated provider conflict with valid consent retains its error instead of offering consent recovery',async({page,request})=>{
 await post(request,'/api/science/access',{...await preferences(request),onlineEnabled:true,privacyAccepted:true});
 await page.route('**/api/science/library/catalog',route=>route.fulfill({status:409,json:{error:'Controlled provider conflict; consent is already valid.'}}));await openLibrary(page);await page.getByRole('button',{name:'Use bounding box',exact:true}).click();await page.getByRole('button',{name:'Find Available Data',exact:true}).click();await expect(page.getByRole('alert')).toContainText('Controlled provider conflict');await expect(page.getByRole('button',{name:'Review Privacy Consent',exact:true})).toHaveCount(0);
});

test('combined choice is also available in science settings and synchronizes individual consent controls',async({page,request})=>{
 await page.goto('/#settings/science?section=access');const section=page.getByRole('heading',{name:'Automatic Scientific Data Access',exact:true}).locator('..');await section.getByRole('button',{name:'Review Combined Online Access',exact:true}).click();const d=page.getByRole('dialog',{name:title,exact:true});await expect(d.getByRole('button',{name:'Allow Online Access',exact:true})).toBeEnabled();await d.getByRole('button',{name:'Allow Online Access',exact:true}).click();await expect(d).not.toBeVisible();await expect(section.getByRole('button',{name:'Review Combined Online Access',exact:true})).toBeFocused();await expect(section.getByLabel('Allow automatic scientific-data discovery and downloads',{exact:true})).toBeChecked();await expect(section.getByLabel('I understand and accept the separate scientific-data privacy disclosure above',{exact:true})).toBeChecked();expect((await permissions(request)).tfr.tfrMonitoringEnabled).toBe(true);
});
