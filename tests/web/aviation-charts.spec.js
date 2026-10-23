import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const s=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':s.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
test.afterEach(async({request})=>{await post(request,'/api/testing/aviation/normal');await post(request,'/api/aviation/configuration',{onlineEnabled:false});});
async function acquire(request){
 await post(request,'/api/testing/aviation/charts');await post(request,'/api/aviation/downloads/configuration',{unmeteredOnly:false});await post(request,'/api/aviation/configuration',{onlineEnabled:true});
 const c=await post(request,'/api/aviation/catalog/vfr/refresh'),item=c.charts.find(i=>i.originalFileName==='Chart_Browser_Sheet.zip');expect(item).toBeTruthy();
 const job=await post(request,'/api/aviation/downloads/preview',{kind:'vfr',itemId:item.id,maximumTransferBytes:1048576,maximumExpandedBytes:1048576});
 if(job.state!=='Completed'){await post(request,`/api/aviation/downloads/${job.id}/control`,{revision:job.revision,action:'approve',selectionHash:job.preview.selectionHash});await expect.poll(async()=>(await(await request.get('/api/aviation/downloads')).json()).find(j=>j.id===job.id).state).toBe('Completed');}
 return job.id;
}
test('approved originals provide local chart overlays, independent science, offline tiles and exact edition selection',async({page,request})=>{
 const jobId=await acquire(request),sources=await(await request.get('/api/science/sources')).json();await post(request,'/api/aviation/configuration',{onlineEnabled:false});
 const inventory=await(await request.get('/api/aviation/charts')).json();const sheet=inventory.sheets.find(s=>s.jobId===jobId&&s.state==='Reviewed interior available');expect(sheet).toBeTruthy();
 const calls=(await(await request.get('/api/testing/aviation')).json()).calls;
 await page.goto('/#planner');const planner=page.locator('#planner');await planner.getByRole('button',{name:'Overlays & Legend',exact:true}).click();
 const layers=page.getByRole('dialog',{name:'Map Overlays & Legend',exact:true});await layers.getByRole('button',{name:'Retained FAA Charts',exact:true}).click();
 const dialog=page.getByRole('dialog',{name:'Retained FAA Charts',exact:true}),card=dialog.locator(`[data-chart-job="${jobId}"][data-chart-sheet="${sheet.id}"]`);
 await expect(card).toContainText('Synthetic browser chart');await expect(dialog).toContainText('Full chart edges are excluded');
 const inset=dialog.locator(`[data-chart-job="${jobId}"]`).filter({has:page.getByRole('heading',{name:'unreviewed-inset.tif',exact:true})});await expect(inset).toContainText('Main body not reviewed');expect(await inset.getByRole('button',{name:/Show/}).count()).toBe(0);
 const tile=page.waitForResponse(r=>r.url().includes(`/api/aviation/charts/${jobId}/${sheet.id}/`)&&r.ok());await card.getByRole('button',{name:'Show synthetic.tif',exact:true}).click();expect((await tile).headers()['content-type']).toContain('image/png');
 await expect.poll(()=>planner.locator('img.leaflet-tile-loaded[src*="/api/aviation/charts/"]').count()).toBeGreaterThan(0);
 await page.keyboard.press('Escape');await planner.getByRole('button',{name:'Overlays & Legend',exact:true}).click();await layers.getByRole('button',{name:'Retained FAA Charts',exact:true}).click();
 await dialog.locator(`[data-chart-job="${jobId}"][data-chart-sheet="${sheet.id}"]`).getByRole('button',{name:'Hide synthetic.tif',exact:true}).click();await expect(dialog.getByRole('status')).toContainText('Recording, observations and retained originals are unchanged');
 await dialog.getByRole('button',{name:'Close Retained FAA Charts',exact:true}).click();await expect(layers.getByRole('button',{name:'Retained FAA Charts',exact:true})).toBeFocused();
 expect((await(await request.get('/api/testing/aviation')).json()).calls).toBe(calls);expect(await(await request.get('/api/science/sources')).json()).toEqual(sources);expect((await(await request.get('/api/state')).json()).activeMission).toBeNull();
});
test('unknown runtime is honest and chart inventory remains accessible at tablet width without altering saved data',async({page,request})=>{
 const id=await acquire(request);await post(request,'/api/testing/aviation/normal');await post(request,'/api/aviation/configuration',{onlineEnabled:false});
 const inventory=await(await request.get('/api/aviation/charts')).json();expect(inventory.runtimeAvailable).toBe(false);expect(inventory.sheets.filter(s=>s.jobId===id).every(s=>s.mainBody.length===0)).toBe(true);
 await page.setViewportSize({width:390,height:740});await page.goto('/#planner');const planner=page.locator('#planner');await planner.getByRole('button',{name:'Overlays & Legend',exact:true}).click();
 const layers=page.getByRole('dialog',{name:'Map Overlays & Legend',exact:true});await layers.getByRole('button',{name:'Retained FAA Charts',exact:true}).click();const dialog=page.getByRole('dialog',{name:'Retained FAA Charts',exact:true});
 await expect(dialog).toContainText('Runtime unavailable');expect((await new AxeBuilder({page}).include('dialog[aria-label="Retained FAA Charts"]').analyze()).violations).toEqual([]);expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
 await page.keyboard.press('Escape');await expect(layers.getByRole('button',{name:'Retained FAA Charts',exact:true})).toBeFocused();await page.keyboard.press('Escape');await expect(planner.getByRole('button',{name:'Overlays & Legend',exact:true})).toBeFocused();
});
