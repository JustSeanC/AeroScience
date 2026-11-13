import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const session=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(r.ok(),await r.text()).toBe(true);return r.json();}
async function fixture(request){const f=await post(request,'/api/testing/lidar-workflow');const job=await(await request.get(`/api/lidar/jobs/${f.jobId}`)).json();await post(request,`/api/lidar/jobs/${f.jobId}/control`,{requestId:crypto.randomUUID(),expectedRevision:job.revision,action:'start'});await expect.poll(async()=>(await(await request.get(`/api/lidar/jobs/${f.jobId}`)).json()).state).toBe('PartiallyComplete');f.index=(await(await request.get(`/api/lidar/jobs/${f.jobId}`)).json()).resultArtifactId;return f;}
const root=page=>page.getByRole('region',{name:'Scientific Results',exact:true});
async function open(page,f){await page.goto(`/#workspace/results?missions=${f.missionId}&view=explore&product=${f.index}`);await expect(root(page)).toHaveAttribute('data-loaded-values','2');return root(page);}
test('real retained records render sparsely in 2D and 3D, preserve original attributes, and filter by actual class',async({page,request})=>{
 const f=await fixture(request),r=await open(page,f);await expect(r.locator('.survey-map')).toHaveAttribute('data-rendered-points','2');
 await r.getByRole('button',{name:'Inspect sample 1',exact:true}).click();await expect(r.locator('.survey-inspector pre')).toContainText('rawX');await expect(r.locator('.survey-inspector pre')).toContainText('12345');await expect(r.locator('.survey-inspector pre')).toContainText('configurationId');
 for(const mode of ['classification','intensity','return'])await r.getByLabel('Point coloring',{exact:true}).selectOption(mode);
 // OPTION has its own native disabled flag (Playwright's enabled matcher
 // retargets an OPTION to the owning SELECT). Check the actual flags and guard.
 for(const name of ['rgb','heightAboveGround']){const option=r.getByLabel('Point coloring',{exact:true}).locator(`option[value=${name}]`);await expect(option).toHaveAttribute('disabled','');expect(await option.evaluate(o=>o.disabled)).toBe(true);}
 await r.getByLabel('Point coloring',{exact:true}).evaluate(s=>{s.value='rgb';s.dispatchEvent(new Event('change'));});await expect(r.getByLabel('Point coloring',{exact:true})).toHaveValue('elevation');
 await r.getByLabel('Point view',{exact:true}).selectOption('cloud');await expect(r.locator('.survey-cloud')).toHaveAttribute('data-renderer','webgl1');await expect(r.locator('.survey-cloud')).toHaveAttribute('data-rendered-points','2');
 expect(Number(await r.locator('.survey-cloud').getAttribute('data-gpu-bytes'))).toBeLessThanOrEqual(1572864);const c=r.locator('.survey-cloud canvas');await c.focus();await c.press('ArrowRight');await c.press('Enter');await expect(r.locator('.survey-inspector pre')).toContainText('original');
 await r.getByLabel('Ground only (original class 2)',{exact:true}).check();await expect(r.locator('.survey-cloud')).toHaveAttribute('data-rendered-points','0');await expect(r.getByRole('table')).toContainText('No valid recorded values');
});
test('WebGL refusal and context loss retain the actual 2D/table evidence fallback',async({page,request})=>{
 const f=await fixture(request);await page.addInitScript(()=>{const get=HTMLCanvasElement.prototype.getContext;HTMLCanvasElement.prototype.getContext=function(type,...args){return type==='webgl'?null:get.call(this,type,...args);};});const r=await open(page,f);await r.getByLabel('Point view',{exact:true}).selectOption('cloud');await expect(r.locator('.survey-cloud')).toContainText('3D rendering is unavailable');await expect(r.locator('.survey-map')).toHaveAttribute('data-rendered-points','2');await expect(r.getByRole('table')).toBeVisible();
});
test('a real WebGL context loss releases the 3D view into the 2D/table fallback',async({page,request})=>{
 const f=await fixture(request),r=await open(page,f);await r.getByLabel('Point view',{exact:true}).selectOption('cloud');await expect(r.locator('.survey-cloud')).toHaveAttribute('data-renderer','webgl1');await r.locator('.survey-cloud canvas').evaluate(canvas=>canvas.getContext('webgl').getExtension('WEBGL_lose_context').loseContext());await expect(r.locator('.survey-cloud')).toContainText('3D rendering is unavailable');await expect(r.getByRole('table')).toBeVisible();
});
test('attribute availability and view-dependent LOD never manufacture absent attributes or averaged points',async({page})=>{
 await page.goto('/');const actual=await page.evaluate(async()=>{const m=await import('/survey-points.js'),source={dimensions:['X','Y','Z','Classification']},values=Array.from({length:4000},(_,i)=>({id:String(i),point:{original:{z:i,classification:2}}}));return {intensity:m.modeAvailable('intensity',source),rgb:m.modeAvailable('rgb',source),hag:m.modeAvailable('heightAboveGround',source),chosen:m.representatives(values,v=>[Number(v.id)%100,Math.floor(Number(v.id)/100),0],{width:100,height:100,cell:5,limit:50}).map(p=>p.value.id),ground:m.filteredPoints(values,{ground:true}).length};});expect(actual.intensity).toBe(false);expect(actual.rgb).toBe(false);expect(actual.hag).toBe(false);expect(actual.chosen).toHaveLength(50);expect(new Set(actual.chosen).size).toBe(50);expect(actual.ground).toBe(4000);
});
test('late source pages cannot replace a changed product, and dependent invalidation clears numerical displays',async({page,request})=>{
 const f=await fixture(request),r=await open(page,f);await page.route(`**/api/science/artifacts/${f.index}/survey-manifest`,route=>route.fulfill({status:409,json:{error:'SYNTHETIC deleted dependent original'}}));await page.evaluate(()=>window.dispatchEvent(new CustomEvent('aeroscience-sources-changed')));await expect(r).toContainText('Displayed numerical data was cleared');await expect(r.getByRole('table')).toHaveCount(0);await expect(r.locator('.survey-map')).toHaveCount(0);
});
for(const theme of ['light','dark'])test(`LiDAR ${theme}: 3D, keyboard, narrow view and accessible evidence`,async({page,request},info)=>{
 const f=await fixture(request);await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});const r=await open(page,f);await r.getByLabel('Point view',{exact:true}).selectOption('cloud');await expect(r.locator('.survey-cloud')).toHaveAttribute('data-rendered-points','2');
 for(const width of [1440,390]){await page.setViewportSize({width,height:1000});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect((await new AxeBuilder({page}).include('.survey-results').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze()).violations).toEqual([]);}
});
