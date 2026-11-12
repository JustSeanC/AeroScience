import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const session=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(r.ok(),await r.text()).toBe(true);return r.json();}
async function fixture(request){return post(request,'/api/testing/terrain-completion');}
async function open(page,f){await page.goto(`/#workspace/results?missions=${f.missionId}`);const root=page.getByRole('region',{name:'Post-flight terrain completion',exact:true});await expect(root).toBeVisible();return root;}

test('retained terrain processes through the real queue, survives navigation, and grants no original science',async({page,request})=>{
 const f=await fixture(request),root=await open(page,f),before=await(await request.get(`/api/missions/${f.missionId}/science-summary`)).json();
 let commands=0;page.on('request',r=>{if(r.method()==='POST'&&r.url().endsWith(`/api/terrain-elevation/jobs/${f.jobId}/control`))commands++;});
 const job=root.locator('details').filter({has:page.locator('summary',{hasText:f.jobId.slice(0,8)})}).first();await job.locator(':scope > summary').click();
 const process=job.getByRole('button',{name:'Process retained terrain',exact:true});await process.focus();await process.press('Enter');
 await expect(root.getByRole('link',{name:'Open derived terrain index',exact:true})).toBeVisible();expect(commands).toBe(1);
 const result=await(await request.get(`/api/terrain-elevation/jobs/${f.jobId}`)).json();expect(result.state).toBe('PartiallyComplete');expect(result.validCells).toBeGreaterThan(0);
 const index=await(await request.get(`/api/science/artifacts/${result.resultArtifactId}`)).json();expect(index.grantsOriginalScienceCredit).toBe(false);expect(index.geometryArtifactId).toBe(f.geometryId);expect(index.tiles.length).toBeGreaterThan(0);
 const after=await(await request.get(`/api/missions/${f.missionId}/science-summary`)).json();expect(after.datasets).toEqual(before.datasets);expect(after.review).toEqual(before.review);
 await page.goto('/#history');await open(page,f);await expect(root).toContainText('PartiallyComplete');expect(commands).toBe(1);
});

test('coverage review only prepares one explicitly selected Library search rectangle; it never starts discovery or transfer',async({page,request})=>{
 const f=await fixture(request),root=await open(page,f);const writes=[];page.on('request',r=>{if(r.method()==='POST'&&/\/api\/science\/library\/(search|preview|jobs)/.test(r.url()))writes.push(r.url());});
 await root.getByLabel('Modeled corridor',{exact:true}).selectOption(f.geometryId);await root.getByRole('button',{name:'Review Coverage',exact:true}).click();await expect(root).toContainText('valid pixel coverage is established only during processing');
 const data=await(await request.get(`/api/science/artifacts/${f.geometryId}/elevation-review`)).json();await root.getByRole('button',{name:'Prepare Missing Sources',exact:true}).click();
 await expect(root.getByRole('heading',{name:'Find & Download Data',exact:true})).toBeVisible();await expect(root.getByLabel('West longitude',{exact:true})).toHaveValue(String(data.searchWindows[0].west));
 await expect(root.getByRole('button',{name:'Find Available Data',exact:true})).toBeVisible();expect(writes).toEqual([]);await expect(root).toContainText('rectangle is not the survey footprint');
});

test('repeated clicks send one command and a late reply cannot replace a reopened authoritative job',async({page,request})=>{
 const f=await fixture(request),root=await open(page,f);let calls=0;page.on('request',r=>{if(r.method()==='POST'&&r.url().endsWith(`/api/terrain-elevation/jobs/${f.jobId}/control`))calls++;});
 // Hold an already received real response in the client. A routing fulfillment
 // after AbortController cancels a request would test Playwright's canceled-route
 // lifecycle, rather than the application's late-reply generation guard.
 await page.evaluate(id=>{const original=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith(`/api/terrain-elevation/jobs/${id}/control`)){const body=await response.text();await new Promise(resolve=>{window.releaseTerrainReply=resolve;window.terrainReplyReceived=true;});window.fetch=original;return new Response(body,{status:response.status,headers:response.headers});}return response;};},f.jobId);
 const job=root.locator('details').filter({has:page.locator('summary',{hasText:f.jobId.slice(0,8)})}).first();await job.locator(':scope > summary').click();
 try{await job.getByRole('button',{name:'Process retained terrain',exact:true}).dblclick();await expect.poll(()=>page.evaluate(()=>window.terrainReplyReceived)).toBe(true);expect(calls).toBe(1);await expect(root.getByRole('link',{name:'Open derived terrain index',exact:true})).toHaveCount(0);await page.goto('/#history');await expect(root).toHaveCount(0);}finally{await page.evaluate(()=>window.releaseTerrainReply?.());}
 await open(page,f);await expect(root).toContainText('PartiallyComplete');expect(calls).toBe(1);
});

test('unsupported preparation bounds fail before any catalog call or substituted area',async({page})=>{
 await page.goto('/');const result=await page.evaluate(async()=>{const {libraryDownload}=await import('/library-download.js');let calls=0;const parent=document.createElement('div');try{await libraryDownload(parent,async()=>{calls++;},()=>false,{area:{west:10,south:84.1,east:10.1,north:84.2}});return {error:null,calls};}catch(e){return {error:e.message,calls,nodes:parent.children.length};}});
 expect(result.error).toContain('No different region was substituted');expect(result.calls).toBe(0);expect(result.nodes).toBe(0);
});

test('a late coverage review cannot repopulate controls after its corridor selection changes',async({page,request})=>{
 const f=await fixture(request),root=await open(page,f);let release,received,finished;const gate=new Promise(r=>{release=r;}),ready=new Promise(r=>{received=r;}),done=new Promise(r=>{finished=r;});
 await page.route(`**/api/science/artifacts/${f.geometryId}/elevation-review?*`,async route=>{try{const response=await route.fetch();received();await gate;await route.fulfill({response});}finally{finished();}});
 await root.getByLabel('Modeled corridor',{exact:true}).selectOption(f.geometryId);await root.getByRole('button',{name:'Review Coverage',exact:true}).click();
 try{await ready;await root.getByLabel('Modeled corridor',{exact:true}).selectOption('');}finally{release();}await done;await expect(root.getByRole('button',{name:'Review Coverage',exact:true})).not.toHaveAttribute('aria-busy','true');
 await expect(root.getByRole('button',{name:'Verify sources and review processing',exact:true})).toHaveCount(0);await expect(root.getByRole('button',{name:'Prepare Missing Sources',exact:true})).toHaveCount(0);
});

test('an unavailable terrain job store cannot hide original Results and exports',async({page,request})=>{
 const f=await fixture(request);await page.route(`**/api/missions/${f.missionId}/terrain-elevation/jobs`,route=>route.fulfill({status:503,json:{error:'SYNTHETIC unavailable terrain job store; original results remain available.'}}));
 const root=await open(page,f);await expect(root.locator('..').getByRole('alert')).toContainText('unavailable terrain job store');await expect(page.getByRole('heading',{name:'Take your research with you',exact:true})).toBeVisible();await expect(page.getByText('Recorded instruments and source quality',{exact:true})).toBeVisible();
});

for(const theme of ['light','dark'])test(`terrain completion ${theme}: keyboard, narrow layout, safe locale fallback and labeled synthetic capture`,async({page,request},info)=>{
 const f=await fixture(request);await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});await page.addInitScript(()=>localStorage.setItem('AeroScience.presentation.v1',JSON.stringify({version:1,theme:'auto',locale:'de-DE'})));
 const root=await open(page,f);await expect(page.locator('html')).toHaveAttribute('data-resolved-theme',theme);
 await root.getByLabel('Modeled corridor',{exact:true}).selectOption(f.geometryId);const review=root.getByRole('button',{name:'Review Coverage',exact:true});await review.focus();await review.press('Enter');await expect(root.getByRole('button',{name:'Verify sources and review processing',exact:true})).toBeVisible();
 await root.evaluate((e,theme)=>e.prepend(Object.assign(document.createElement('p'),{textContent:`P05b · ${theme.toUpperCase()} · SYNTHETIC CHROMIUM FIXTURE · Not genuine source or installed Windows/MSFS acceptance`})),theme);
 for(const width of [1440,390]){await page.setViewportSize({width,height:1000});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect((await new AxeBuilder({page}).include('.terrain-completion').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze()).violations).toEqual([]);await page.screenshot({path:info.outputPath(`synthetic-browser-terrain-completion-${theme}-${width}.png`),fullPage:true});}
 await root.evaluate(e=>{e.style.fontSize='200%';for(const b of e.querySelectorAll('button'))b.textContent+=' — extended preview label';});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});
