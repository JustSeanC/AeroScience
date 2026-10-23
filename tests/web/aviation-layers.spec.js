import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const auth=await request.post('/api/session',{data:{},headers:{Origin:origin}}),csrf=(await auth.json()).csrf;const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':csrf}});expect(response.ok(),await response.text()).toBeTruthy();return response.json();}
test.beforeEach(async({request})=>{await post(request,'/api/testing/aviation/reset');await post(request,'/api/maps/settings',{onlineEnabled:false,layer:'offline'});});
test.afterEach(async({request})=>{await post(request,'/api/aviation/configuration',{onlineEnabled:false});await post(request,'/api/maps/settings',{onlineEnabled:false,layer:'offline'});});
async function references(page){await page.goto('/#settings/maps');await page.getByRole('button',{name:'FAA Catalog & Notices',exact:true}).click();return page.getByRole('dialog',{name:'FAA Catalog & Notices',exact:true});}
async function enable(dialog){await dialog.getByLabel('Allow FAA metadata retrieval').check();await dialog.getByRole('button',{name:'Save FAA Access'}).click();await expect(dialog.getByRole('button',{name:'Refresh FAA References'})).toBeEnabled();}

test('FAA references use real persisted metadata, bounded pages, explicit consent and offline reopening without science credit',async({page,request})=>{
 const sources=await (await request.get('/api/science/sources')).json();let dialog=await references(page);
 expect((await (await request.get('/api/testing/aviation')).json()).calls).toBe(0);
 await expect(dialog.getByRole('button',{name:'Refresh FAA References'})).toBeDisabled();await enable(dialog);
 await dialog.getByRole('button',{name:'Refresh FAA References'}).click();await expect(dialog.getByRole('status')).toContainText('61 catalog records');
 await expect(dialog.locator('.aviation-reference-records article')).toHaveCount(50);await expect(dialog.getByText('Geographic coverage / CRS / neatline: Not verified').first()).toBeVisible();
 await dialog.getByRole('button',{name:'Next FAA Page'}).click();await expect(dialog.locator('.aviation-reference-records article')).toHaveCount(11);
 await expect(dialog.getByRole('button',{name:'Next FAA Page'})).toBeDisabled();
 await dialog.getByLabel('FAA reference product').selectOption('tfr');await dialog.getByRole('button',{name:'Refresh FAA References'}).click();
 await expect(dialog.getByRole('heading',{name:'NOTAM 6/9001 · SYNTHETIC TEST'})).toBeVisible();await expect(dialog.getByText(/No polygon has been drawn/)).toBeVisible();
 await expect(dialog.getByText(/cannot clear a route/)).toBeVisible();
 await dialog.getByLabel('Allow FAA metadata retrieval').uncheck();await dialog.getByRole('button',{name:'Save FAA Access'}).click();await expect(dialog.getByRole('status')).toContainText('Offline cached');
 await dialog.getByRole('button',{name:'Close FAA Catalog & Notices',exact:true}).click();await page.reload();dialog=await references(page);
 await dialog.getByLabel('FAA reference product').selectOption('tfr');await expect(dialog.getByRole('heading',{name:'NOTAM 6/9001 · SYNTHETIC TEST'})).toBeVisible();
 expect(await (await request.get('/api/science/sources')).json()).toEqual(sources);expect((await (await request.get('/api/state')).json()).activeMission).toBeNull();
});

test('provider failure preserves successful FAA inventory and shows last attempt separately',async({page,request})=>{
 const dialog=await references(page);await enable(dialog);await dialog.getByRole('button',{name:'Refresh FAA References'}).click();await expect(dialog.getByRole('status')).toContainText('61 catalog records');
 await post(request,'/api/testing/aviation/outage');await dialog.getByRole('button',{name:'Refresh FAA References'}).click();await expect(dialog.getByRole('status')).toContainText('ProviderUnavailable');
 await expect(dialog.locator('.aviation-reference-records article')).toHaveCount(50);await expect(dialog.getByText(/Last successful retrieval:/)).toBeVisible();
});

test('unified basemap and overlay controls preserve geographic geometry and science filters, close and restore keyboard focus',async({page,request})=>{
 const plan=await post(request,'/api/plans',{id:crypto.randomUUID(),name:'Aviation layer geometry fixture',cruisingAltitudeFeet:7500,waypoints:[{name:'A',latitude:42.3,longitude:-71},{name:'B',latitude:42.4,longitude:-70.9}]});
 await page.goto('/#planner');await expect(page.locator('.map-layer-manager').first()).toBeVisible();const choice=page.getByLabel('Map Layers — Basemap').first();await expect(choice).toHaveValue('offline');
 await expect(choice.locator('option[value="esri-outdoor"]')).toHaveCount(1);await expect(choice.locator('option[value="esri-navigation"]')).toHaveCount(1);
 const map=page.locator('.geographic-map').first(),center=await map.getAttribute('data-center');const opener=page.getByRole('button',{name:'Overlays & Legend'}).first();await opener.click();
 let dialog=page.getByRole('dialog',{name:'Map Overlays & Legend',exact:true});await dialog.getByLabel('Mission overlays', {exact:true}).uncheck();await dialog.getByLabel('Science overlays',{exact:true}).uncheck();
 await expect(dialog.getByLabel('Planned route',{exact:true})).not.toBeChecked();await expect(dialog.getByLabel('Science footprints',{exact:true})).not.toBeChecked();
 await expect(dialog.getByText(/TFR polygons: unverified/)).toBeVisible();await page.keyboard.press('Escape');await expect(dialog).not.toBeVisible();await expect(opener).toBeFocused();
 expect(await map.getAttribute('data-center')).toBe(center);const after=await (await request.get('/api/plans/'+plan.id)).json();expect(after.waypoints).toEqual(plan.waypoints);
 await opener.click();dialog=page.getByRole('dialog',{name:'Map Overlays & Legend',exact:true});await expect(dialog.getByLabel('Mission overlays',{exact:true})).not.toBeChecked();
 await dialog.getByRole('button',{name:'FAA Catalog & Notices',exact:true}).click();const faa=page.getByRole('dialog',{name:'FAA Catalog & Notices',exact:true});await faa.getByRole('button',{name:'Close FAA Catalog & Notices',exact:true}).click();
 await expect(dialog.getByRole('button',{name:'FAA Catalog & Notices',exact:true})).toBeFocused();await dialog.getByRole('button',{name:'Close Map Overlays & Legend'}).click();await expect(opener).toBeFocused();
});

test('narrow paired-browser style dialogs retain touch targets, visible close buttons and accessible semantics',async({page})=>{
 await page.setViewportSize({width:390,height:740});const dialog=await references(page);const close=dialog.getByRole('button',{name:'Close FAA Catalog & Notices',exact:true});
 const box=await close.boundingBox();expect(box.width).toBeGreaterThanOrEqual(44);expect(box.height).toBeGreaterThanOrEqual(44);
 await expect(dialog).toBeVisible();expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
 const violations=(await new AxeBuilder({page}).include('dialog[open]').analyze()).violations;expect(violations).toEqual([]);
 await page.screenshot({path:'artifacts/browser-tests/aviation-reference-narrow.png'});await page.keyboard.press('Escape');await expect(dialog).not.toBeVisible();
 await expect(page.getByRole('button',{name:'FAA Catalog & Notices',exact:true})).toBeFocused();
});

test('science-map overlay visibility leaves native quantities, filters and archived evidence unchanged',async({page,request})=>{
 const fixture=await post(request,'/api/testing/remote/Terrain');const evidenceBefore=await (await request.get('/api/science/products/'+fixture.product.id+'/export/evidence.json')).text();
 await page.goto('/#workspace/map?missions='+fixture.missionId+'&instrument=terrain-scanner');
 await expect(page.getByLabel('Science layer opacity',{exact:true})).toBeVisible();await page.getByLabel('Science layer opacity',{exact:true}).fill('0.35');
 const center=await page.locator('.geographic-map').first().getAttribute('data-center');
 await page.getByRole('button',{name:'Overlays & Legend'}).click();const dialog=page.getByRole('dialog',{name:'Map Overlays & Legend',exact:true});
 await dialog.getByLabel('Science overlays',{exact:true}).uncheck();await expect(dialog.getByLabel('Saved scientific observations',{exact:true})).not.toBeChecked();
 await expect(dialog.getByLabel('Verified scientific raster views',{exact:true})).not.toBeChecked();
 await dialog.getByLabel('Science overlays',{exact:true}).check();await dialog.getByRole('button',{name:'Close Map Overlays & Legend'}).click();
 await expect(page.getByLabel('Science layer opacity',{exact:true})).toHaveValue('0.35');expect(page.url()).toContain('instrument=terrain-scanner');
 expect(await page.locator('.geographic-map').first().getAttribute('data-center')).toBe(center);
 expect(await (await request.get('/api/science/products/'+fixture.product.id+'/export/evidence.json')).text()).toBe(evidenceBefore);
});
