import { test, expect } from '@playwright/test';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}) {
  const session=await (await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();
  const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});
  expect(response.ok(),await response.text()).toBeTruthy();return response.json();
}
test.afterEach(async({request})=>{
  await post(request,'/api/maps/settings',{onlineEnabled:false,layer:'offline',privacyAccepted:false});
});
test('fresh map disclosure blocks all external requests and offline choice persists',async({page,request})=>{
  await post(request,'/api/testing/maps/new-install');const external=[];
  page.on('request',r=>{if(!r.url().startsWith(origin))external.push(r.url());});
  await page.goto('/#planner');const dialog=page.getByRole('dialog',{name:'Choose your map privacy'});
  await expect(dialog).toBeVisible();expect(external).toEqual([]);
  await dialog.getByRole('button',{name:'Use offline-only maps'}).click();await expect(dialog).toHaveCount(0);
  await page.reload();await expect(page.getByRole('dialog',{name:'Choose your map privacy'})).toHaveCount(0);
  await expect(page.getByLabel('Map Layers').last()).toHaveValue('offline');expect(external).toEqual([]);
});
test('online choice enables OSM and provider failures retain route and saved preference',async({page,request})=>{
  await post(request,'/api/testing/maps/new-install');const tiles=[];
  await page.route('https://tile.openstreetmap.org/**',route=>{tiles.push(route.request().url());return route.abort();});
  await page.goto('/#planner');await page.getByRole('button',{name:'Continue with OpenStreetMap'}).click();
  await expect.poll(()=>tiles.length).toBeGreaterThan(0);
  await expect(page.locator('.geographic-map').last().locator('..')).toContainText('Online provider unavailable');
  const settings=await (await request.get('/api/maps/settings')).json();expect(settings.configuration.preferences.layer).toBe('streets');
  expect(settings.configuration.preferences.onlineEnabled).toBe(true);
  await expect(page.locator('#plan-form')).toBeVisible();
});
test('all four actual implemented viewers display stored synthetic fixture products and exports',async({page,request})=>{
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  for(const kind of ['thermal','multispectral','hyperspectral','terrain']) {
    const seeded=await post(request,'/api/testing/remote/'+kind);
    await page.goto('/#science');const section=page.locator('.remote-products');
    await section.getByLabel('Product instrument').selectOption(seeded.product.instrumentId);
    await section.getByRole('button',{name:'Refresh Results',exact:true}).click();
    await section.getByRole('button',{name:'View Data',exact:true}).first().click();
    await expect(section).toContainText('SYNTHETIC TEST FIXTURE');
    await expect(section).toContainText('Source observation:');
    if(kind==='hyperspectral') {
      await expect(section.getByLabel('Spectral sample index')).toBeVisible();
      await section.getByLabel('Spectral sample index').fill('20');
      await expect(section.locator('.remote-product-view')).toContainText('Sample 20');
    } else await expect(section.getByLabel('Colorized scientific footprint sample grid')).toBeVisible();
    if(kind==='multispectral') {await section.getByLabel('Scientific product view').selectOption('ndvi');await expect(section.locator('.remote-legend')).toContainText('0.5');}
    const exported=await request.get(`/api/science/products/${seeded.product.id}/export/evidence.json`);
    expect(exported.ok()).toBeTruthy();expect(await exported.text()).toContain('not tamper-proof');
    await section.screenshot({path:`artifacts/remote-${kind}-browser-fixture.png`});
  }
  expect(errors).toEqual([]);expect((await request.get('/help/remote-sensing/quick-start.md')).ok()).toBeTruthy();
});
test('changing a product filter removes stale acquisition controls before its asynchronous result arrives',async({page,request})=>{
  await post(request,'/api/testing/remote/thermal');await post(request,'/api/testing/remote/hyperspectral');
  await page.goto('/#science');const section=page.locator('.remote-products');
  await section.getByLabel('Product instrument').selectOption('thermal-imager');
  await expect(section.locator('.plan-row strong').first()).toHaveText('Thermal Imager');
  let release,entered;const gate=new Promise(resolve=>release=resolve),waiting=new Promise(resolve=>entered=resolve);
  await page.route('**/api/science/products/page?**',async route=>{if(new URL(route.request().url()).searchParams.get('instrumentId')==='hyperspectral-imager'){entered();await gate;}await route.continue();});
  try {
    await section.getByLabel('Product instrument').selectOption('hyperspectral-imager');await waiting;
    await expect(section.locator('.plan-row')).toHaveCount(0);release();
    await expect(section.locator('.plan-row strong').first()).toHaveText('Hyperspectral Imager');
    await section.getByRole('button',{name:'View Data',exact:true}).first().click();
    await expect(section.getByLabel('Spectral sample index')).toBeVisible();
  }finally{release();}
});
test('storage defaults limits cleanup cancellation and update settings are usable',async({page,request})=>{
  await post(request,'/api/storage/preferences',{missionLimitBytes:25e9,scienceLimitBytes:50e9,automaticCacheCleanup:false,protectPinned:true});
  await page.goto('/#settings/storage');const storage=page.locator('#storage-management');
  await expect(storage).toContainText('25 GB');await expect(storage).toContainText('50 GB');
  await expect(storage.getByLabel('Flight/Mission maximum preset')).toHaveValue('25');
  await expect(storage.getByLabel('Scientific Library maximum preset')).toHaveValue('50');
  await storage.getByRole('button',{name:'Preview Cleanup',exact:true}).click();
  await expect(storage).toContainText('Regenerable caches only');
  await storage.getByRole('button',{name:'Cancel',exact:true}).click();
  await page.getByRole("link",{name:"Updates",exact:true}).click();
  await expect(page.getByRole('heading',{name:'Updates',exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'Check for Updates',exact:true})).toBeVisible();
  await page.getByRole('link',{name:'Storage & Data',exact:true}).click();await storage.screenshot({path:'artifacts/remote-storage-browser-fixture.png'});
});
test('scientific consent stays separate from maps and pilot readiness needs no manual manifest',async({page,request})=>{
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await post(request,'/api/science/access',{onlineEnabled:false,privacyAccepted:false,maximumDownloadBytes:67108864,concurrentDownloads:1,backgroundRetrieval:true,selectionPolicy:'clear-recent',lookbackDays:730,maximumCloudPercent:30,unmeteredOnly:false});
  await page.goto('/#settings/science?section=access');
  const section=page.getByRole('heading',{name:'Automatic Scientific Data Access',exact:true}).locator('..');
  await expect(section).toContainText('separate from online basemaps');
  await expect(section.getByLabel('Allow automatic scientific-data discovery and downloads')).not.toBeChecked();
  await section.getByLabel('Maximum network transfer per preparation (MiB)').fill('8');
  await section.getByRole('button',{name:'Save scientific access preferences',exact:true}).click();
  await expect(section).toContainText('Offline-only scientific operation saved');
  const maps=await (await request.get('/api/maps/settings')).json();expect(maps.configuration.preferences.onlineEnabled).toBe(false);
  await section.getByLabel('Prepare latitude').fill('40.005');await section.getByLabel('Prepare longitude').fill('-74.995');
  await section.getByRole('button',{name:'Prepare a small area',exact:true}).click();
  await expect.poll(async()=> (await (await request.get('/api/science/readiness')).json()).length).toBeGreaterThan(0);
  await expect(section.locator('.scientific-readiness')).toContainText('Configure Scientific Data Access');
  // Verify persisted settings in a new document after releasing the old page's SSE
  // and preparation reads. Chromium can stall a reload with those requests alive;
  // a timeout there is not evidence about the stored preference.
  const context=page.context();await page.close();page=await context.newPage();
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto('/#settings/science?section=access',{waitUntil:'domcontentloaded'});
  await expect(page.getByLabel('Maximum network transfer per preparation (MiB)')).toHaveValue('8');
  const saved = await post(request, '/api/plans', {id:crypto.randomUUID(),name:'Bounded automatic preflight',waypoints:[{name:'A',latitude:40,longitude:-75},{name:'B',latitude:41,longitude:-74}],instruments:[{instrumentId:'thermal-imager',enabled:true,intervalMs:1000}]});
  await page.goto('/#planner');await expect(page.locator('#planner .scientific-readiness').first()).toBeVisible();
  await page.locator('#planner .scientific-readiness').first().getByText('Advanced — individual regions & diagnostics',{exact:true}).click();await expect(page.locator('#planner .scientific-readiness').first().getByRole('button',{name:'View Download Activity'})).toBeVisible();
  await page.goto('/#control');await expect(page.locator('#control .scientific-readiness')).toBeVisible();
  await page.goto('/#science');await expect(page.locator('#instrument-content .scientific-readiness')).toBeVisible();
  expect(errors).toEqual([]);
});
