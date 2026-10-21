import { test, expect } from '@playwright/test';
async function post(request,path,value={}){const origin='http://127.0.0.1:5080';const session=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const r=await request.post(path,{headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf},data:value});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
test.afterEach(async({request})=>{await post(request,'/api/testing/library/teardown');});
test('Hyperspectral uses existing geographic controls, actual backend catalog and honest support labels',async({page,request})=>{
 await post(request,'/api/testing/library/setup');await page.goto('/#library/download');
 await page.getByLabel('Data family',{exact:true}).selectOption('hyperspectral');
 await page.getByRole('button',{name:'Use bounding box',exact:true}).click();
 await page.getByRole('button',{name:'Search provider catalog',exact:true}).click();
 const results=page.getByLabel('Provider catalog results');await expect(results).toContainText('AVIRIS-Classic');await expect(results).toContainText('Guided import');await expect(results).toContainText('f080611');
 await results.getByText('Source metadata, license & limitations',{exact:true}).first().click();await expect(results.getByRole('checkbox').first()).toBeDisabled();await expect(results.getByRole('link',{name:'Authoritative source'}).first()).toHaveAttribute('target','_blank');
 await page.getByLabel('Scientific provider / product',{exact:true}).selectOption('nasa-ng');await page.getByRole('button',{name:'Search provider catalog',exact:true}).click();await expect(results).toContainText('AVIRIS-NG');await expect(results).toContainText('ang20140621');
 await page.getByLabel('Scientific provider / product',{exact:true}).selectOption('nasa-emit');await page.getByRole('button',{name:'Search provider catalog',exact:true}).click();await expect(results).toContainText('native scientific import not yet supported');await expect(results).toContainText('NetCDF-4');
 await page.getByLabel('Use cached catalog / boundaries only',{exact:true}).check();await page.getByRole('button',{name:'Search provider catalog',exact:true}).click();await expect(page.getByRole('status').first()).toContainText('cached, not a new live search');
});
