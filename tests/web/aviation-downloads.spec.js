import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const auth=await request.post('/api/session',{data:{},headers:{Origin:origin}}),csrf=(await auth.json()).csrf;const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':csrf}});expect(response.ok(),await response.text()).toBeTruthy();return response.json();}

test('approved FAA original uses durable host jobs, closes independently, verifies offline and creates no science credit',async({page,request})=>{
 await post(request,'/api/testing/aviation/reset');await post(request,'/api/aviation/downloads/configuration',{unmeteredOnly:false});await post(request,'/api/aviation/configuration',{onlineEnabled:true});
 await post(request,'/api/aviation/catalog/vfr/refresh');const sources=await (await request.get('/api/science/sources')).json();
 await page.goto('/#settings/maps');await page.getByRole('button',{name:'FAA Catalog & Notices',exact:true}).click();
 const catalog=page.getByRole('dialog',{name:'FAA Catalog & Notices',exact:true});const opener=catalog.getByRole('button',{name:'Download & Retain Original'}).first();await opener.click();
 let dialog=page.getByRole('dialog',{name:'FAA Downloads',exact:true});await expect(dialog.getByText(/Entire original ZIP/)).toBeVisible();
 await dialog.getByRole('button',{name:'Review FAA Download'}).click();
 const reusable=dialog.getByText(/Exact verified original already retained/);
 await expect(dialog.getByRole('button',{name:'Approve FAA Download'}).or(reusable)).toBeVisible();
 if(await dialog.getByRole('button',{name:'Approve FAA Download'}).isVisible())await dialog.getByRole('button',{name:'Approve FAA Download'}).click();
 await dialog.getByRole('button',{name:'Close FAA Downloads',exact:true}).click();await expect(opener).toBeFocused();
 await expect.poll(async()=>{const jobs=await(await request.get('/api/aviation/downloads')).json();return jobs.some(j=>j.state==='Completed');}).toBeTruthy();
 await catalog.getByRole('button',{name:'Manage FAA Downloads'}).click();dialog=page.getByRole('dialog',{name:'FAA Downloads',exact:true});
 await expect(dialog.getByText(/Verified original SHA-256/).first()).toBeVisible();await dialog.getByRole('button',{name:'Verify Retained Original'}).first().click();await expect(dialog.getByRole('status')).toContainText('match their recorded hashes');
 await page.setViewportSize({width:390,height:740});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
 expect((await new AxeBuilder({page}).include('dialog[aria-label="FAA Downloads"]').analyze()).violations).toEqual([]);
 const close=dialog.getByRole('button',{name:'Close FAA Downloads',exact:true}),box=await close.boundingBox();expect(box.width).toBeGreaterThanOrEqual(44);expect(box.height).toBeGreaterThanOrEqual(44);
 await page.keyboard.press('Escape');await expect(dialog).not.toBeVisible();await post(request,'/api/aviation/configuration',{onlineEnabled:false});await page.reload();
 await page.getByRole('button',{name:'FAA Catalog & Notices',exact:true}).click();await page.getByRole('dialog',{name:'FAA Catalog & Notices',exact:true}).getByRole('button',{name:'Manage FAA Downloads'}).click();
 await expect(page.getByRole('dialog',{name:'FAA Downloads',exact:true}).getByText(/Verified original SHA-256/).first()).toBeVisible();
 expect(await(await request.get('/api/science/sources')).json()).toEqual(sources);expect((await(await request.get('/api/state')).json()).activeMission).toBeNull();
});
