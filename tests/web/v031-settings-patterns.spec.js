import {test,expect} from '@playwright/test';
const origin='http://127.0.0.1:5080';
async function post(request,path,data){const {csrf}=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':csrf}});expect(r.ok()).toBeTruthy();return r.json();}
test('settings categories deep links back forward and unsaved fields remain usable on mobile',async({page})=>{
 await page.setViewportSize({width:744,height:1000});await page.goto('/#settings/maps');
 await expect(page.getByRole('heading',{name:'Maps & Basemaps',exact:true})).toBeVisible();await page.getByLabel('Public Esri API key',{exact:true}).fill('unsaved-local-test-value');
 await page.getByRole('link',{name:'Scientific Data Sources',exact:true}).click();await expect(page.getByRole('heading',{name:'Terrain & Elevation',exact:true})).toBeVisible();
 await page.goBack();await expect(page.getByLabel('Public Esri API key',{exact:true})).toHaveValue('unsaved-local-test-value');await page.goForward();await expect(page.getByRole('heading',{name:'Hyperspectral Imaging',exact:true})).toBeVisible();
 await page.locator('#source-terrain').getByRole('link',{name:'Enable Scientific Data Access',exact:true}).click();await expect(page.locator('#scientific-access-controls')).toHaveAttribute('open','');
 await page.getByRole('link',{name:'Units & Display',exact:true}).click();await page.getByText('Science display units',{exact:true}).click();await expect(page.getByRole('combobox',{name:'Planning units',exact:true})).toHaveCount(1);await expect(page.getByRole('combobox',{name:'Planning units',exact:true}).locator('option')).toHaveCount(2);
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth+1)).toBeTruthy();
});
test('per-instrument source modes persist independently and no-dataset guidance opens the real wizard',async({page,request})=>{
 await page.goto('/#settings/science');await page.getByLabel('Terrain & Elevation source mode',{exact:true}).selectOption('local');await expect(page.locator('#source-terrain')).toContainText('Saved');
 await page.getByLabel('Thermal Imaging source mode',{exact:true}).selectOption('prefer-local');await expect(page.locator('#source-thermal')).toContainText('Saved');
 await page.reload();await expect(page.getByLabel('Terrain & Elevation source mode',{exact:true})).toHaveValue('local');await expect(page.getByLabel('Thermal Imaging source mode',{exact:true})).toHaveValue('prefer-local');
 const card=page.locator('#source-hyperspectral');await expect(card).toContainText('User-supplied');await card.getByRole('button',{name:'Import Hyperspectral Dataset',exact:true}).click();await expect(page.getByRole('dialog',{name:'Import Hyperspectral Dataset',exact:true})).toBeVisible();await expect(page.getByText('Step 1 — Choose Your Files',{exact:true})).toBeVisible();await page.locator('#hyperspectral-import').getByRole('button',{name:'Close wizard',exact:true}).click();
 await post(request,'/api/science/source-mode',{kind:'terrain',mode:'automatic'});await post(request,'/api/science/source-mode',{kind:'thermal',mode:'automatic'});
});
for(const kind of ['lawnmower','creepingLine','expandingSquare','racetrack','orbit','box','radialSpokes','straightTransect'])test(`real editor ${kind}: preview add save reopen and preserve manual waypoint`,async({page,request})=>{
 const errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto('/#planner');await page.getByRole('button',{name:'Add waypoint',exact:true}).click();await page.getByLabel('Latitude (degrees north)',{exact:true}).fill('40');await page.getByLabel('Longitude (degrees east)',{exact:true}).fill('-75');await page.getByRole('button',{name:'Save Waypoint',exact:true}).click();
 await page.getByText('Flight pattern generator',{exact:true}).click();const panel=page.locator('.pattern-editor');await panel.getByLabel('Pattern',{exact:true}).selectOption(kind);await panel.getByLabel('Center/start latitude',{exact:true}).fill('40');await panel.getByLabel('Center/start longitude',{exact:true}).fill('-75');await expect(panel).toContainText('Preview Ready.');
 await expect.poll(async()=>Number(await page.locator('#plan-editor .geographic-map').getAttribute('data-preview-points'))).toBeGreaterThan(0);
 const countBefore=await page.locator('.waypoint-row').count();let previewRequests=0;page.on('request',r=>{if(r.url().endsWith('/api/patterns/preview'))previewRequests++;});
 await panel.getByRole('button',{name:'Add Pattern to Mission',exact:true}).click();await expect(panel).toContainText('Pattern added to mission draft');const count=await page.locator('.waypoint-row').count();expect(count).toBeGreaterThan(countBefore);expect(previewRequests).toBe(0);
 await panel.getByRole('button',{name:'Add Pattern to Mission',exact:true}).click();await expect(page.locator('.waypoint-row')).toHaveCount(count);
 const name='031 '+kind+' '+crypto.randomUUID();await page.getByLabel('Plan name',{exact:true}).fill(name);await page.getByRole('button',{name:'Save mission plan',exact:true}).click();const row=page.locator('.plan-row').filter({hasText:name});await expect(row).toBeVisible();await row.getByRole('button',{name:'Edit',exact:true}).click();await expect(page.locator('.waypoint-row')).toHaveCount(count);const plan=(await(await page.request.get('/api/plans?limit=50')).json()).find(p=>p.name===name);expect(plan.patterns).toHaveLength(1);expect(plan.waypoints[0].latitude).toBe(40);expect(plan.waypoints[0].patternId).toBeNull();expect(plan.patterns[0].kind).toBe(kind);
 await page.getByText('Flight pattern generator',{exact:true}).click();await panel.getByRole('button',{name:'Edit parameters',exact:true}).click();await panel.getByLabel('Heading (degrees true)',{exact:true}).fill('37');await panel.getByRole('button',{name:'Regenerate',exact:true}).click();await expect(panel).toContainText('Pattern updated in mission draft');await expect(page.locator('.waypoint-row')).toHaveCount(count);
 await page.locator('#plan-form').getByRole('button',{name:'Export / Share Route',exact:true}).click();const exportDialog=page.locator('dialog[open]');await exportDialog.getByLabel('Export format').selectOption('csv');await exportDialog.getByRole('button',{name:'Preview export',exact:true}).click();await expect(exportDialog.getByLabel('Export text preview')).toHaveValue(/40/);await exportDialog.getByRole('button',{name:'Close',exact:true}).click();
 // The saved-plan row is already visible while the asynchronous update is pending.
 // Hold the lawnmower update to exercise that window instead of depending on timing.
 let releaseSave, saveStarted;
 if(kind==='lawnmower'){
  const gate=new Promise(resolve=>{releaseSave=resolve;});
  let announceSave;saveStarted=new Promise(resolve=>{announceSave=resolve;});
  await page.route('**/api/plans',async route=>{
   if(route.request().method()==='POST'){announceSave(route.request().postDataJSON());await gate;}
   await route.continue();
  });
 }
 const savedResponse=page.waitForResponse(response=>new URL(response.url()).pathname==='/api/plans'&&response.request().method()==='POST');
 const saveButton=page.getByRole('button',{name:'Save mission plan',exact:true});
 await saveButton.click();
 if(saveStarted){
  try{
   const submitted=await saveStarted;expect(submitted.patterns[0].headingDegrees).toBe(37);
   await expect(row).toBeVisible();await expect(saveButton).toBeDisabled();
   expect((await(await page.request.get('/api/plans/'+plan.id)).json()).patterns[0].headingDegrees).toBe(0);
  }finally{releaseSave();}
 }
 expect((await savedResponse).ok()).toBeTruthy();
 await expect(page.locator('#plan-editor .planner-status')).toContainText('Mission plan saved');
 await expect(row).toBeVisible();const updated=await(await page.request.get('/api/plans/'+plan.id)).json();expect(updated.patterns[0].id).toBe(plan.patterns[0].id);expect(updated.patterns[0].headingDegrees).toBe(37);expect(updated.waypoints[0].patternId).toBeNull();expect(errors).toEqual([]);
});
test('field-specific aviation errors retain last valid preview and recover after correction',async({page})=>{
 await page.goto('/#planner');await page.getByText('Flight pattern generator',{exact:true}).click();const panel=page.locator('.pattern-editor');await expect(panel).toContainText('Choose a center on the map');await panel.getByLabel('Center/start latitude',{exact:true}).fill('40');await panel.getByLabel('Center/start longitude',{exact:true}).fill('-75');await expect(panel).toContainText('Preview Ready.');
 await panel.getByLabel('Track Spacing (NM)',{exact:true}).fill('0.001');await expect(panel.getByLabel('Track Spacing (NM)',{exact:true})).toHaveAttribute('aria-invalid','true');await expect(panel).toContainText('NM');await expect(panel).toContainText('Preview outdated');await expect.poll(async()=>Number(await page.locator('#plan-editor .geographic-map').getAttribute('data-preview-points'))).toBeGreaterThan(0);
 await panel.getByLabel('Track Spacing (NM)',{exact:true}).fill('0.25');await expect(panel).toContainText('Preview Ready.');await expect(panel.getByLabel('Track Spacing (NM)',{exact:true})).not.toHaveAttribute('aria-invalid','true');await panel.getByLabel('Pattern',{exact:true}).selectOption('orbit');await expect(panel.getByLabel('Track Spacing (NM)',{exact:true})).toBeHidden();await expect(panel.getByLabel('Orbits',{exact:true})).toBeVisible();
});

test('actual four-step import wizard validates synthetic files imports atomically and shows ready coverage',async({page,request})=>{
 await page.goto('/#settings/science');await page.locator('#source-hyperspectral').getByRole('button',{name:'Import Hyperspectral Dataset',exact:true}).click();const wizard=page.locator('#hyperspectral-import');await expect(wizard).toContainText('Step 1 — Choose Your Files');await wizard.screenshot({path:'artifacts/v031-import-step1-synthetic-ui.png'});
 await post(request,'/api/testing/guided/hyperspectral',{});await expect(wizard).toContainText('Step 2 — Verify the Dataset');await expect(wizard).toContainText('16 bands');await expect(wizard).toContainText('400.00–550.00 nm');await wizard.screenshot({path:'artifacts/v031-import-step2-synthetic-ui.png'});await wizard.getByText('View geographic coverage on map',{exact:true}).click();await expect(wizard.locator('.geographic-map')).toBeVisible();await wizard.getByText('View geographic coverage on map',{exact:true}).click();
 await wizard.getByRole('button',{name:'Continue to Import',exact:true}).click();await expect(wizard).toContainText('Step 3 — Confirm Import');await expect(wizard).toContainText('SYNTHETIC UI TEST FIXTURE');await wizard.screenshot({path:'artifacts/v031-import-step3-synthetic-ui.png'});await wizard.getByRole('button',{name:'Import Dataset',exact:true}).click();await expect(wizard).toContainText('Step 4 — Dataset Ready');await expect(wizard.getByRole('button',{name:'Enable Hyperspectral Imager',exact:true})).toBeVisible();await wizard.screenshot({path:'artifacts/v031-import-step4-synthetic-ui.png'});await wizard.getByRole('button',{name:'Close wizard',exact:true}).click();await expect(page.locator('#source-hyperspectral')).toContainText('SYNTHETIC UI TEST FIXTURE');
});
