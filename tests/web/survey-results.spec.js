import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const session=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(r.ok(),await r.text()).toBe(true);return r.json();}
async function terrain(request){const f=await post(request,'/api/testing/terrain-completion');const job=await(await request.get(`/api/terrain-elevation/jobs/${f.jobId}`)).json();await post(request,`/api/terrain-elevation/jobs/${f.jobId}/control`,{requestId:crypto.randomUUID(),expectedRevision:job.revision,action:'start'});await expect.poll(async()=>(await(await request.get(`/api/terrain-elevation/jobs/${f.jobId}`)).json()).state).toBe('PartiallyComplete');return f;}
const root=page=>page.getByRole('region',{name:'Scientific Results',exact:true});
test('Results separates original science, modeled support and native source cells; original exports survive every view',async({page,request})=>{
 const f=await terrain(request);const before=await(await request.get(`/api/missions/${f.missionId}/science-summary`)).json(),writes=[];page.on('request',r=>{if(r.method()==='POST'&&/\/library\/|\/control$|\/terrain-surveys$/.test(r.url()))writes.push(r.url());});
 await page.goto(`/#workspace/results?missions=${f.missionId}&view=overview&custom=preserved`);
 await expect(root(page).getByText('Original in-flight measurement values',{exact:true})).toBeVisible();await expect(root(page).getByText('Source-backed DEM / DSM cells',{exact:true}).first()).toBeVisible();
 await root(page).getByRole('link',{name:'Explore',exact:true}).click();await expect(page).toHaveURL(/custom=preserved/);await expect(root(page).locator('.survey-map canvas.survey-canvas')).toBeVisible();await expect(root(page).locator('[data-displayed-cells]')).toHaveAttribute('data-displayed-cells',/^[1-9]/);
 const first=root(page).getByRole('button',{name:'Inspect sample 1',exact:true});await first.focus();await first.press('Enter');await expect(root(page).getByRole('heading',{name:'Selected original record'})).toBeVisible();await expect(root(page).locator('.survey-inspector pre')).toContainText('artifactSha256');
 await root(page).getByRole('link',{name:'Evidence',exact:true}).click();await expect(root(page).getByRole('table')).toBeVisible();await expect(page.getByRole('heading',{name:'Take your research with you'})).toBeVisible();
 expect(writes).toEqual([]);expect(await(await request.get(`/api/missions/${f.missionId}/science-summary`)).json()).toEqual(before);
});
test('modeled-only corridor is visible without claiming terrain values or launching downloads',async({page,request})=>{
 const f=await post(request,'/api/testing/terrain-completion');await page.goto(`/#workspace/results?missions=${f.missionId}&view=explore`);await expect(root(page).getByLabel('Retained survey product')).toHaveValue(f.geometryId);await expect(root(page).locator('.survey-map')).toBeVisible();await expect(root(page).getByRole('status')).toContainText('0 actual records loaded');await expect(root(page).getByRole('button',{name:'Load next retained page'})).toBeDisabled();
});
test('a delayed numerical page cannot reappear after navigation or a source selection change',async({page,request})=>{
 const f=await terrain(request);await page.addInitScript(()=>{const original=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith('/survey-page')){const body=await response.text();await new Promise(resolve=>{window.releaseSurveyPage=resolve;window.surveyPageReceived=true;});window.fetch=original;return new Response(body,{status:response.status,headers:response.headers});}return response;};});
 await page.goto(`/#workspace/results?missions=${f.missionId}&view=explore`);await expect.poll(()=>page.evaluate(()=>window.surveyPageReceived)).toBe(true);await page.evaluate(()=>location.hash='#history');await expect(page.locator('.survey-results')).toHaveCount(0);await page.evaluate(()=>window.releaseSurveyPage());await expect(page.locator('.survey-results')).toHaveCount(0);await expect(page.getByRole('heading',{name:'Recorded Flights',exact:true})).toBeVisible();
});
test('wide-zoom native-cell locations remain visible and native masks preserve holes',async({page,request})=>{
 const f=await terrain(request);await page.goto(`/#workspace/results?missions=${f.missionId}&view=explore`);await expect(root(page).locator('[data-displayed-cells]')).toHaveAttribute('data-displayed-cells',/^[1-9]/);await root(page).getByRole('button',{name:'Zoom out',exact:true}).click({clickCount:8});await expect(root(page).locator('.survey-map')).toHaveAttribute('data-representation','native-masks-and-location-overview');
 const contained=await page.evaluate(async()=>{const {contains}=await import('/survey-map.js');const shape={coordinates:[[[[0,0],[3,0],[3,3],[0,3],[0,0]],[[1,1],[2,1],[2,2],[1,2],[1,1]]]]};return [contains({latitude:.5,longitude:.5},shape),contains({latitude:1.5,longitude:1.5},shape)];});expect(contained).toEqual([true,false]);
});
for(const theme of ['light','dark'])test(`terrain Results ${theme}: native cells, narrow view, keyboard and accessibility`,async({page,request},info)=>{
 const f=await terrain(request);await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});await page.goto(`/#workspace/results?missions=${f.missionId}&view=explore`);await expect(root(page).locator('[data-displayed-cells]')).toHaveAttribute('data-displayed-cells',/^[1-9]/);
 for(const width of [1440,390]){await page.setViewportSize({width,height:1000});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);const report=await new AxeBuilder({page}).include('.survey-results').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze();expect(report.violations).toEqual([]);}
});
