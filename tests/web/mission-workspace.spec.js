import {test,expect} from '@playwright/test';
const origin='http://127.0.0.1:5080';
test.beforeEach(async({request})=>{const response=await request.post('/api/session',{data:{},headers:{Origin:origin}});expect(response.ok()).toBeTruthy();});
async function post(request,path,data={}){
  const session=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();
  const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});
  expect(response.ok(),await response.text()).toBeTruthy();return response.json();
}
test('sensor assistant uses reference optics and preserves canonical AGL/speed/geometry across display units',async({page,request})=>{
  const catalog=await(await request.get('/api/instrument-profiles')).json();const profile=catalog.profiles.find(p=>p.id==='micasense.rededge-p.multispectral');
  const definition={id:crypto.randomUUID(),kind:'lawnmower',center:{latitude:40,longitude:-75},headingDegrees:37,lengthMetres:1000,widthMetres:500,spacingMetres:100,radiusMetres:250,legs:6,orbits:1,clockwise:true,targetAltitudeFeet:3000};
  const preview=await post(request,'/api/patterns/preview',definition);
  const plan=await post(request,'/api/plans',{id:crypto.randomUUID(),name:'Sensor geometry canonical fixture',patterns:[preview.definition],waypoints:preview.waypoints,instruments:[{instrumentId:'multispectral-imager',enabled:true,intervalMs:1000,payload:{profile,geometryAlgorithm:'sensor-plane-2'}}],sensorPlanning:{instrumentId:'multispectral-imager',altitudeAglMetres:120,groundspeedMetresPerSecond:50,sideOverlap:.3,forwardOverlap:.6}});
  await page.goto('/#planner');await page.locator('.plan-row').filter({hasText:plan.name}).getByRole('button',{name:'Edit',exact:true}).click();
  await page.getByText('Flight pattern generator',{exact:true}).click();await page.getByRole('button',{name:'Edit parameters',exact:true}).click();
  const box=page.locator('.sensor-planning');await box.locator('summary').first().click();await expect(box.getByRole('status')).toContainText('MicaSense RedEdge-P');
  const response=page.waitForResponse(r=>r.url().endsWith('/api/sensor-planning')&&r.request().postDataJSON().sideOverlap===.4);
  await box.getByLabel('Side overlap (%)',{exact:true}).fill('40');const before=await(await response).json();
  expect(before.swathMetres).toBeCloseTo(2*120*Math.tan(49.6*Math.PI/360),8);expect(before.modeledSwaths.length).toBeGreaterThan(0);
  await page.getByLabel('Planning units',{exact:true}).selectOption('metric');await expect(box.getByLabel('Survey altitude above ground (m AGL)',{exact:true})).toHaveValue('120');
  const metric=page.waitForResponse(r=>r.url().endsWith('/api/sensor-planning'));await box.getByLabel('Forward overlap (%)',{exact:true}).fill('65');const after=await(await metric).json();
  expect(after.pattern.waypoints).toEqual(before.pattern.waypoints);expect(after.trackSpacingMetres).toBe(before.trackSpacingMetres);expect(after.nominalCrossTrackGsdMetres).toBe(before.nominalCrossTrackGsdMetres);
  await page.getByRole('button',{name:'Discard Preview',exact:true}).click();await page.getByRole('button',{name:'Save mission plan',exact:true}).click();await expect(page.getByText('Mission plan saved · Ready to start mission',{exact:false})).toBeVisible();
  const saved=await(await request.get('/api/plans/'+plan.id)).json();expect(saved.sensorPlanning.altitudeAglMetres).toBe(120);expect(saved.sensorPlanning.groundspeedMetresPerSecond).toBeCloseTo(50,10);expect(saved.sensorPlanning.sideOverlap).toBe(.4);expect(saved.instruments.find(c=>c.instrumentId==='multispectral-imager').payload.profile.id).toBe(profile.id);
});
test('completed mission workspace replays saved positions and builds geographic coverage/composites/exports offline',async({page,request})=>{
  const fixture=await post(request,'/api/testing/remote/Terrain');await page.goto('/#history');await page.getByRole('button',{name:'View mission',exact:true}).first().click();
  const workspace=page.locator('.mission-workspace');await expect(workspace.getByRole('tab')).toHaveCount(6);await workspace.getByRole('tab',{name:'Replay',exact:true}).click();
  const replay=page.locator('#workspace-panel-replay');await expect(replay.getByRole('status',{name:'Replay status'})).toContainText('Saved sample');await expect(replay).toContainText('1,000 ft');await expect(replay).toContainText('Generic terrain scanner');await expect(replay.locator('.geographic-map')).toBeVisible();
  await workspace.getByRole('tab',{name:'Coverage',exact:true}).click();const coverage=page.locator('#workspace-panel-coverage');await coverage.getByLabel('Coverage instrument',{exact:true}).selectOption('terrain-scanner');await coverage.getByRole('button',{name:'Calculate coverage',exact:true}).click();await expect(coverage.getByRole('status',{name:'Scientific analysis status'})).toContainText('Completed',{timeout:20000});await expect(coverage).toContainText('unavailable (no survey polygon)');await expect(coverage.getByRole('link',{name:'Download coverage analysis and geographic polygons'})).toBeVisible();
  await workspace.getByRole('tab',{name:'Imagery',exact:true}).click();const imagery=page.locator('#workspace-panel-imagery');await imagery.getByRole('button',{name:'Generate geographic product',exact:true}).click();await expect(imagery.getByRole('status').last()).toContainText('Completed',{timeout:20000});await imagery.getByRole('button',{name:'View on map',exact:true}).first().click();await expect(imagery.locator('.raster-legend')).toContainText('Display-only nearest-pixel');
  const metadata=await(await request.get(`/api/missions/${fixture.missionId}/artifacts`)).json();expect(metadata.some(a=>a.kind==='mission-samples'&&a.format==='tif')).toBeTruthy();
  await page.screenshot({path:'artifacts/v040-imagery-desktop.png',fullPage:true});
  await page.setViewportSize({width:820,height:1180});
  await workspace.getByRole('tab',{name:'Export',exact:true}).scrollIntoViewIfNeeded();
  await expect(workspace.getByRole('tab',{name:'Export',exact:true})).toBeInViewport();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth)).toBeTruthy();
  await workspace.getByRole('tab',{name:'Export',exact:true}).click();const link=workspace.getByRole('link',{name:'Download Research Package (ZIP)',exact:true});await expect(link).toBeVisible();const exported=await request.get(await link.getAttribute('href'));expect(exported.ok()).toBeTruthy();const bytes=await exported.body();expect(bytes.subarray(0,2).toString()).toBe('PK');
  await workspace.getByRole('tab',{name:'Analysis',exact:true}).click();await expect(page.locator('#workspace-panel-analysis')).toContainText('Atmospheric and terrain analysis');await expect(page.locator('#workspace-panel-analysis')).toContainText('Historical records missing auxiliary values remain unavailable.');
  await page.screenshot({path:'artifacts/v040-analysis-tablet.png',fullPage:true});
});
