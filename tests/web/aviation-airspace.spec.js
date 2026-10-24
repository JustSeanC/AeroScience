import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const s=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':s.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
test.afterEach(async({request})=>{await post(request,'/api/testing/aviation/normal');await post(request,'/api/aviation/configuration',{onlineEnabled:false});});
test('approved original Class airspace and SAA imports provide real bounded polygons, holes, altitude details and offline overlays',async({page,request})=>{
 await post(request,'/api/testing/aviation/airspace');await post(request,'/api/aviation/downloads/configuration',{unmeteredOnly:false});await post(request,'/api/aviation/configuration',{onlineEnabled:true});
 const sources=await(await request.get('/api/science/sources')).json();const catalog=await post(request,'/api/aviation/catalog/nasr/refresh');expect(catalog.charts).toHaveLength(2);
 for(const item of catalog.charts){const j=await post(request,'/api/aviation/downloads/preview',{kind:'nasr',itemId:item.id,maximumTransferBytes:1048576,maximumExpandedBytes:1048576});if(j.state!=='Completed'){await post(request,`/api/aviation/downloads/${j.id}/control`,{revision:j.revision,action:'approve',selectionHash:j.preview.selectionHash});await expect.poll(async()=>(await(await request.get('/api/aviation/downloads')).json()).find(x=>x.id===j.id).state).toBe('Completed');}await post(request,`/api/aviation/downloads/${j.id}/import-vectors`);}
 const datasets=await(await request.get('/api/aviation/vectors')).json(),d=datasets.find(x=>x.family==='CLASS'),sua=datasets.find(x=>x.family==='SUA');expect(d.features).toBe(2);expect(sua.features).toBe(1);
 const actual=await(await request.get(`/api/aviation/airspace/${d.id}?west=-76&south=39&east=-74&north=41&kind=class-b`)).json();expect(actual.features[0].polygons[0]).toHaveLength(2);expect(actual.features[0].lower.value).toBe(0);expect(actual.features[0].upper.value).toBe(10000);expect(actual.returnedVertices).toBeLessThanOrEqual(25000);
 const hole=await(await request.get(`/api/aviation/airspace/${d.id}?west=-75.2&south=39.8&east=-74.8&north=40.2`)).json();expect(hole.features).toHaveLength(0);expect(hole.boundingCandidates).toBe(1);
 await post(request,'/api/aviation/configuration',{onlineEnabled:false});await page.goto('/#planner');const planner=page.locator('#planner');await planner.getByLabel('Offline geographic location search').fill('39.2,-75.8');await planner.getByRole('button',{name:'Find',exact:true}).click();await planner.getByLabel('Search results').selectOption('0');
 await planner.getByRole('button',{name:'Overlays & Legend',exact:true}).click();const layers=page.getByRole('dialog',{name:'Map Overlays & Legend',exact:true}),label='FAA Class B airspace · 2026-10-01';await layers.getByLabel(label,{exact:true}).check();
 await expect.poll(async()=>JSON.parse(await planner.locator('.geographic-map').getAttribute('data-aviation-feature-counts')||'{}')[`faa-class-b-${d.id.slice(0,16)}`]).toBe(1);
 await layers.getByRole('button',{name:'View '+label+' references',exact:true}).click();const list=page.getByRole('dialog',{name:'FAA Reference List',exact:true});await expect(list).toContainText('not active restrictions');await list.getByRole('button',{name:'Synthetic Class B',exact:true}).click();const details=page.getByRole('dialog',{name:'FAA Reference Details',exact:true});await expect(details).toContainText('10000 FT · MSL');await expect(details).toContainText('activation is unverified');await expect(details.getByText('Original reference attributes',{exact:true})).toBeVisible();
 await page.setViewportSize({width:390,height:740});expect((await new AxeBuilder({page}).include('dialog[aria-label="FAA Reference Details"]').analyze()).violations).toEqual([]);expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();await page.keyboard.press('Escape');await expect(list.getByRole('button',{name:'Synthetic Class B',exact:true})).toBeFocused();await page.keyboard.press('Escape');await expect(layers.getByRole('button',{name:'View '+label+' references',exact:true})).toBeFocused();await page.keyboard.press('Escape');
 expect(await(await request.get('/api/science/sources')).json()).toEqual(sources);expect((await(await request.get('/api/state')).json()).activeMission).toBeNull();await page.reload();await planner.getByRole('button',{name:'Overlays & Legend',exact:true}).click();await expect(page.getByLabel(label,{exact:true})).toBeChecked();
});

test('full-circle SAA reference imports through approved jobs and exposes interpolation and independent local geometry offline',async({page,request})=>{
 await post(request,'/api/testing/aviation/airspace');await post(request,'/api/aviation/downloads/configuration',{unmeteredOnly:false});await post(request,'/api/aviation/configuration',{onlineEnabled:true});
 const catalog=await post(request,'/api/aviation/catalog/nasr/refresh');const item=catalog.charts.find(x=>x.originalFileName==='aixm5.0.zip');expect(item).toBeTruthy();
 const j=await post(request,'/api/aviation/downloads/preview',{kind:'nasr',itemId:item.id,maximumTransferBytes:1048576,maximumExpandedBytes:1048576});
 if(j.state!=='Completed'){await post(request,`/api/aviation/downloads/${j.id}/control`,{revision:j.revision,action:'approve',selectionHash:j.preview.selectionHash});await expect.poll(async()=>(await(await request.get('/api/aviation/downloads')).json()).find(x=>x.id===j.id).state).toBe('Completed');}
 const imported=await post(request,`/api/aviation/downloads/${j.id}/import-vectors`);await post(request,'/api/aviation/configuration',{onlineEnabled:false});
 const actual=await(await request.get(`/api/aviation/airspace/${imported.id}?west=-76&south=39&east=-74&north=41&kind=sua`)).json();
 expect(actual.features).toHaveLength(1);expect(actual.features[0].attributes.GEOMETRY_INTERPOLATION).toContain('0.5 m');expect(actual.features[0].polygons[0][0].length).toBeGreaterThan(36);
 await page.goto('/#planner');const planner=page.locator('#planner');await planner.getByLabel('Offline geographic location search').fill('40,-75');await planner.getByRole('button',{name:'Find',exact:true}).click();await planner.getByLabel('Search results').selectOption('0');
 await planner.getByRole('button',{name:'Overlays & Legend',exact:true}).click();const layers=page.getByRole('dialog',{name:'Map Overlays & Legend',exact:true});
 const label=`FAA Special-use airspace · 2026-10-01 · interpretation ${imported.id.slice(0,8)}`;await layers.getByLabel(label,{exact:true}).last().check();
 await layers.getByRole('button',{name:'View '+label+' references',exact:true}).last().click();const list=page.getByRole('dialog',{name:'FAA Reference List',exact:true});await list.getByRole('button',{name:'Synthetic Special Use',exact:true}).click();
 const details=page.getByRole('dialog',{name:'FAA Reference Details',exact:true});await expect(details).toContainText('10000 FT · MSL');await details.getByText('Original reference attributes',{exact:true}).click();await expect(details).toContainText('geodesic circle');
});

test('ordered SAA composites expose multipart islands, true holes and source relationships through existing offline overlays',async({page,request})=>{
 await post(request,'/api/testing/aviation/airspace-composite');await post(request,'/api/aviation/downloads/configuration',{unmeteredOnly:false});await post(request,'/api/aviation/configuration',{onlineEnabled:true});
 const sources=await(await request.get('/api/science/sources')).json(),catalog=await post(request,'/api/aviation/catalog/nasr/refresh'),item=catalog.charts.find(c=>c.originalFileName==='aixm5.0.zip');
 // A distinct fixture edition/source identity keeps the earlier circle's verified original immutable.
 // The host mode advertises this exact composite source separately; no silent replacement.
 const job=await post(request,'/api/aviation/downloads/preview',{kind:'nasr',itemId:item.id,maximumTransferBytes:1048576,maximumExpandedBytes:1048576});
 await post(request,`/api/aviation/downloads/${job.id}/control`,{revision:job.revision,action:'approve',selectionHash:job.preview.selectionHash});
 await expect.poll(async()=>(await(await request.get('/api/aviation/downloads')).json()).find(j=>j.id===job.id).state).toBe('Completed');
 const dataset=await post(request,`/api/aviation/downloads/${job.id}/import-vectors`);expect(dataset.features).toBe(2);expect(dataset.omittedRecords).toBe(0);
 const url=`/api/aviation/airspace/${dataset.id}`,region='?west=-76&south=39&east=-74&north=41&kind=sua';
 const feature=(await(await request.get(url+region)).json()).features.find(f=>f.attributes.GEOMETRY_COMPOSITION);
 expect(feature.polygons).toHaveLength(2);expect(feature.polygons.reduce((n,p)=>n+p.length,0)).toBe(3);
 expect(JSON.parse(feature.attributes.SOURCE_COMPONENTS).components.map(c=>c.operation)).toEqual(['BASE','SUBTR','UNION']);
 expect((await(await request.get(url+'?west=-75.4&south=39.6&east=-75.3&north=39.7')).json()).features.some(f=>f.id===feature.id)).toBe(false);
 expect((await(await request.get(url+'?west=-75.1&south=39.9&east=-74.9&north=40.1')).json()).features.some(f=>f.id===feature.id)).toBe(true);
 await post(request,'/api/aviation/configuration',{onlineEnabled:false});const before=(await(await request.get('/api/testing/aviation')).json()).calls;
 await page.goto('/#planner');const planner=page.locator('#planner');await planner.getByLabel('Offline geographic location search').fill('39.25,-75.75');await planner.getByRole('button',{name:'Find',exact:true}).click();await planner.getByLabel('Search results').selectOption('0');
 await planner.getByRole('button',{name:'Overlays & Legend',exact:true}).click();const layers=page.getByRole('dialog',{name:'Map Overlays & Legend',exact:true}),label=`FAA Special-use airspace · ${dataset.edition} · interpretation ${dataset.id.slice(0,8)}`;
 await layers.getByLabel(label,{exact:true}).check();await layers.getByRole('button',{name:'View '+label+' references',exact:true}).click();
 const list=page.getByRole('dialog',{name:'FAA Reference List',exact:true});await list.getByRole('button',{name:'Synthetic composite composite-nested',exact:true}).click();
 const detail=page.getByRole('dialog',{name:'FAA Reference Details',exact:true});await expect(detail).toContainText('10000 FT · MSL');await detail.getByText('Original reference attributes',{exact:true}).click();await expect(detail).toContainText('Derived CRS84 horizontal geometry');await expect(detail).toContainText('SOURCE_COMPONENTS');
 await page.setViewportSize({width:390,height:740});expect((await new AxeBuilder({page}).include('dialog[aria-label="FAA Reference Details"]').analyze()).violations).toEqual([]);await page.keyboard.press('Escape');await expect(list.getByRole('button',{name:'Synthetic composite composite-nested',exact:true})).toBeFocused();
 expect((await(await request.get('/api/testing/aviation')).json()).calls).toBe(before);expect(await(await request.get('/api/science/sources')).json()).toEqual(sources);expect((await(await request.get('/api/state')).json()).activeMission).toBeNull();
});
