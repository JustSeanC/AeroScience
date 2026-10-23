import {test,expect} from '@playwright/test';
import {openLibrary} from './helpers/library-ui.js';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const s=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const r=await request.post(path,{headers:{Origin:origin,'X-AeroScience-CSRF':s.csrf},data});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
async function overlays(page,scope){await scope.getByRole('button',{name:'Overlays & Legend',exact:true}).click();return page.getByRole('dialog',{name:'Map Overlays & Legend',exact:true});}
const visible=async map=>JSON.parse(await map.getAttribute('data-visible-overlays')||'[]');

test('Library chooser controls actual catalog and selection layers without changing selected areas or discovery',async({page,request})=>{
 await post(request,'/api/testing/library/setup');await post(request,'/api/testing/library/mode/normal');
 try{
  await openLibrary(page);await page.getByRole('button',{name:'Use bounding box',exact:true}).click();await page.getByRole('button',{name:'Find Available Data',exact:true}).click();await expect(page.getByLabel('Select scene 3dep-composite',{exact:true})).toBeVisible();
  await page.getByLabel('Select scene 3dep-composite',{exact:true}).check();
  const workspace=page.locator('#library'),map=workspace.locator('.geographic-map');await expect(map).toBeVisible();
  const center=await map.getAttribute('data-center');expect(await visible(map)).toContain('library-remote');
  const dialog=await overlays(page,workspace);await expect(dialog.getByLabel('Library provider catalog',{exact:true})).toBeChecked();await expect(dialog.getByLabel('Library geographic selection',{exact:true})).toBeChecked();
  await dialog.getByLabel('Science overlays',{exact:true}).uncheck();await expect.poll(()=>visible(map)).not.toContain('library-remote');
  await dialog.getByRole('button',{name:'Close Map Overlays & Legend'}).click();await expect(page.getByLabel('Select scene 3dep-composite',{exact:true})).toBeChecked();expect(await map.getAttribute('data-center')).toBe(center);
  await workspace.getByRole('button',{name:'Overlays & Legend',exact:true}).click();await page.getByRole('dialog',{name:'Map Overlays & Legend',exact:true}).getByLabel('Science overlays',{exact:true}).check();
  await expect.poll(()=>visible(map)).toContain('library-remote');expect((await(await request.get('/api/state')).json()).activeMission).toBeNull();
 }finally{await post(request,'/api/testing/library/teardown');}
});

test('comparison layer visibility survives acquisition reload while exact archived numerical evidence is unchanged',async({page,request})=>{
 const first=await post(request,'/api/testing/remote/Terrain'),second=await post(request,'/api/testing/remote/Terrain');
 const original=await(await request.get(`/api/science/products/${first.product.id}/export/evidence.json`)).text();
 await page.goto(`/#workspace/compare?missions=${first.missionId},${second.missionId}`);const firstCard=page.getByRole('region',{name:'Dataset A',exact:true}),map=firstCard.locator('.geographic-map');
 await expect(firstCard.locator('.comparison-source')).toContainText('valid native cells');expect(await visible(map)).toContain('comparison-native');
 let dialog=await overlays(page,firstCard);await dialog.getByLabel('Comparison native scientific values',{exact:true}).uncheck();await dialog.getByRole('button',{name:'Close Map Overlays & Legend'}).click();expect(await visible(map)).not.toContain('comparison-native');
 await firstCard.getByLabel('Comparison mission 1',{exact:true}).selectOption(second.missionId);await expect(firstCard.locator('.comparison-source')).toContainText('valid native cells');expect(await visible(map)).not.toContain('comparison-native');
 dialog=await overlays(page,firstCard);await expect(dialog.getByLabel('Comparison native scientific values',{exact:true})).not.toBeChecked();await dialog.getByLabel('Comparison native scientific values',{exact:true}).check();await dialog.getByRole('button',{name:'Close Map Overlays & Legend'}).click();await expect.poll(()=>visible(map)).toContain('comparison-native');
 expect(await(await request.get(`/api/science/products/${first.product.id}/export/evidence.json`)).text()).toBe(original);
});

test('analysis provides independent native-value opacity and visibility with keyboard focus return',async({page,request})=>{
 const fixture=await post(request,'/api/testing/remote/Terrain');await page.goto(`/#workspace/analysis?missions=${fixture.missionId}`);
 const map=page.locator('#science-workspace .analysis-map .geographic-map').first();await expect(map).toBeVisible();expect(await visible(map)).toContain('analysis-native');
 const holder=page.locator('#science-workspace .analysis-map').first(),opener=holder.getByRole('button',{name:'Overlays & Legend',exact:true});const dialog=await overlays(page,holder);
 await dialog.getByLabel('Analysis native scientific values opacity',{exact:true}).fill('0.35');await dialog.getByLabel('Analysis native scientific values',{exact:true}).uncheck();await expect.poll(()=>visible(map)).not.toContain('analysis-native');
 await page.keyboard.press('Escape');await expect(opener).toBeFocused();await opener.click();await expect(page.getByLabel('Analysis native scientific values opacity',{exact:true})).toHaveValue('0.35');
});
