import {test,expect,request as playwrightRequest} from '@playwright/test';
import {spawn} from 'node:child_process';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {resolve,join} from 'node:path';

test('a real companion process restart preserves approved queued Library work and browser review',async({browser})=>{
 test.setTimeout(60000);const folder=mkdtempSync(join(tmpdir(),'aeroscience-library-browser-restart-')),origin='http://127.0.0.1:5093';let child,client,context;
 const start=async()=>{
  child=spawn(process.env.DOTNET||'dotnet',[resolve('artifacts/browser-test-host/AeroScience.TestHost.dll')],{env:{...process.env,AeroScience__DataDirectory:folder,AeroScience__Port:'5093',AeroScience__EnableLan:'false',AeroScience__SamplingIntervalMs:'250'},stdio:'ignore'});
  await expect.poll(async()=>{try{return (await fetch(origin+'/api/health')).ok;}catch{return false;}},{timeout:15000}).toBeTruthy();
  client=await playwrightRequest.newContext({baseURL:origin});const session=await(await client.post('/api/session',{headers:{Origin:origin},data:{}})).json();
  return async(path,data={})=>{const r=await client.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();};
 };
 const stop=async()=>{if(child&&child.exitCode===null){const exited=new Promise(r=>child.once('exit',r));child.kill('SIGKILL');await exited;}await client?.dispose();client=null;};
 try{
  let post=await start();await post('/api/testing/library/setup');await post('/api/science/preparation/control',{action:'pause'});
  const area={west:-75.01,south:39.99,east:-74.99,north:40.01};const catalog=await post('/api/science/library/catalog',{provider:'usgs-3dep',area,refresh:true});const scene=catalog.items[0];
  const preview=await post('/api/science/library/preview',{version:1,areas:[area],scenes:[{provider:scene.provider,scene:scene.scene,revision:scene.revision,bands:scene.bands.map(b=>b.id)}]});
  await post(`/api/science/library/jobs/${preview.id}/approve`,{selectionHash:preview.preview.selectionHash});expect((await(await client.get(`/api/science/library/jobs/${preview.id}`)).json()).state).toBe('Queued');await stop();
  await start();await expect.poll(async()=>(await(await client.get(`/api/science/library/jobs/${preview.id}`)).json()).state,{timeout:15000}).toBe('Completed');
  context=await browser.newContext({baseURL:origin,storageState:await client.storageState()});const page=await context.newPage();await page.goto('/#library/download');await expect(page.locator(`[data-job-id="${preview.id}"]`)).toContainText('Completed');
  const complete=await(await client.get(`/api/science/library/jobs/${preview.id}`)).json();expect(complete.approvalId).toBeTruthy();expect(complete.items[0].sourceId).toBeTruthy();expect((await(await client.get('/api/state')).json()).activeMission).toBeNull();
 }finally{await context?.close();await stop();rmSync(folder,{recursive:true,force:true});}
});
