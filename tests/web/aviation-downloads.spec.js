import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const auth=await request.post('/api/session',{data:{},headers:{Origin:origin}}),csrf=(await auth.json()).csrf;const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':csrf}});expect(response.ok(),await response.text()).toBeTruthy();return response.json();}

test('approved FAA original uses durable host jobs, closes independently, verifies offline and creates no science credit',async({page,request})=>{
 await post(request,'/api/testing/aviation/reset');await post(request,'/api/aviation/downloads/configuration',{unmeteredOnly:false});await post(request,'/api/aviation/configuration',{onlineEnabled:true});
 await post(request,'/api/aviation/catalog/vfr/refresh');const sources=await (await request.get('/api/science/sources')).json();
 await page.goto('/#settings/maps');await page.getByRole('button',{name:'FAA Catalog & Notices',exact:true}).click();
 const catalog=page.getByRole('dialog',{name:'FAA Catalog & Notices',exact:true});await catalog.getByLabel('FAA reference product').selectOption('vfr');const opener=catalog.getByRole('button',{name:'Download & Retain Original'}).first();await opener.click();
 let dialog=page.getByRole('dialog',{name:'FAA Downloads',exact:true});await expect(dialog.getByText(/Entire original ZIP/)).toBeVisible();
 const previewResponse=page.waitForResponse(r=>r.url().endsWith('/api/aviation/downloads/preview')&&r.request().method()==='POST');
 await dialog.getByRole('button',{name:'Review FAA Download'}).click();const selectedJob=await(await previewResponse).json();
 const reusable=dialog.getByText(/Exact verified original already retained/);
 await expect(dialog.getByRole('button',{name:'Approve FAA Download'}).or(reusable)).toBeVisible();
 if(await dialog.getByRole('button',{name:'Approve FAA Download'}).isVisible())await dialog.getByRole('button',{name:'Approve FAA Download'}).click();
 await dialog.getByRole('button',{name:'Close FAA Downloads',exact:true}).click();await expect(opener).toBeFocused();
 await expect.poll(async()=>{const jobs=await(await request.get('/api/aviation/downloads')).json();return jobs.find(j=>j.id===selectedJob.id)?.state;}).toBe('Completed');
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

test('actual recording preempts a live FAA transfer, telemetry continues and an explicit pause survives flight end',async({request},testInfo)=>{
 await post(request,'/api/testing/aviation/slow');await post(request,'/api/aviation/downloads/configuration',{unmeteredOnly:false});await post(request,'/api/aviation/configuration',{onlineEnabled:true});
 const catalog=await post(request,'/api/aviation/catalog/vfr/refresh'),item=catalog.charts[1];
 const preview=await post(request,'/api/aviation/downloads/preview',{kind:'vfr',itemId:item.id,maximumTransferBytes:16777216,maximumExpandedBytes:8388608});
 await post(request,`/api/aviation/downloads/${preview.id}/control`,{revision:preview.revision,action:'approve',selectionHash:preview.preview.selectionHash});
 const started=performance.now(),transitions=[];
 const state=async()=>{const job=(await(await request.get('/api/aviation/downloads')).json()).find(j=>j.id===preview.id);if(transitions.at(-1)?.revision!==job?.revision)transitions.push({elapsedMs:Math.round(performance.now()-started),state:job?.state,revision:job?.revision,downloadedBytes:job?.downloadedBytes,chargedBytes:job?.chargedBytes,error:job?.error});return job;};
 try{await expect.poll(async()=>(await state()).downloadedBytes).toBeGreaterThan(0);}catch(error){throw new Error(`FAA transfer did not start: ${JSON.stringify(await state())}`,{cause:error});}
 await post(request,'/api/mission-selection',{planId:null});const flight=await post(request,'/api/missions',{name:'Synthetic aviation transfer priority',mode:'universal',armScience:true});
 try{
  await expect.poll(async()=>(await state()).state).toBe('Waiting for Flight Priority');
  await expect.poll(async()=>{const frames=await(await request.get(`/api/missions/${flight.id}/telemetry?limit=10`)).json();return frames.length;}).toBeGreaterThan(1);
  const job=await state();await post(request,`/api/aviation/downloads/${job.id}/control`,{revision:job.revision,action:'pause'});
  await post(request,`/api/missions/${flight.id}/end`);expect((await state()).state).toBe('Paused by User');
  // Priority is already established above. Resume the identical 2-MiB asset without
  // artificial per-chunk network delay; this is a lifecycle test, not a throughput SLA.
  await post(request,'/api/testing/aviation/fast-resume');
  const paused=await state();await post(request,`/api/aviation/downloads/${paused.id}/control`,{revision:paused.revision,action:'resume'});
  // This includes a host-worker tick (2 s), charged durable writes for every
  // 64-KiB block, CRC/hash checks, flushed extraction and atomic publication.
  // The generic 5-s UI predicate is not a completion SLA for that whole lifecycle.
  // Keep a bounded 15-s deadline only here, with real revisions/bytes on failure.
  await expect.poll(async()=>(await state()).state,{timeout:15000,message:'Resumed approved FAA job must verify and durably publish the same original'}).toBe('Completed');
  const complete=await state();expect(complete.files[0].bytes).toBe(2097152);expect(complete.downloadedBytes).toBeGreaterThanOrEqual(2097152);
  expect(complete.id).toBe(preview.id);expect(complete.preview).toEqual(preview.preview);expect(complete.originalSha256).toMatch(/^[a-f0-9]{64}$/);expect(complete.files[0].sha256).toMatch(/^[a-f0-9]{64}$/);
  await post(request,'/api/aviation/configuration',{onlineEnabled:false});const before=(await(await request.get('/api/testing/aviation')).json()).calls;
  expect(await post(request,`/api/aviation/downloads/${complete.id}/verify`)).toEqual({verified:true});
  const reused=await post(request,'/api/aviation/downloads/preview',{kind:'vfr',itemId:item.id,maximumTransferBytes:16777216,maximumExpandedBytes:8388608});
  expect(reused).toEqual(complete);expect(await state()).toEqual(complete);expect((await(await request.get('/api/testing/aviation')).json()).calls).toBe(before);
 }finally{console.log('FAA priority/resume transitions:',JSON.stringify(transitions));await testInfo.attach('faa-priority-resume-transitions',{body:JSON.stringify(transitions,null,2),contentType:'application/json'});const current=await(await request.get('/api/state')).json();if(current.activeMission)await post(request,`/api/missions/${current.activeMission.id}/end`);await post(request,'/api/testing/aviation/normal');await post(request,'/api/aviation/configuration',{onlineEnabled:false});}
});

test('saved FAA jobs are bounded and keyboard-paged without changing any approval state',async({page,request})=>{
 await post(request,'/api/testing/aviation/reset');await post(request,'/api/aviation/configuration',{onlineEnabled:true});const catalog=await post(request,'/api/aviation/catalog/vfr/refresh'),item=catalog.charts[2];
 for(let i=0;i<51;i++)await post(request,'/api/aviation/downloads/preview',{kind:'vfr',itemId:item.id,maximumTransferBytes:1048576,maximumExpandedBytes:1048576});
 await page.goto('/#settings/maps');await page.getByRole('button',{name:'FAA Catalog & Notices',exact:true}).click();await page.getByRole('button',{name:'Manage FAA Downloads',exact:true}).click();const dialog=page.getByRole('dialog',{name:'FAA Downloads',exact:true});
 await expect(dialog.getByRole('region',{name:'Saved FAA download jobs'}).locator('article')).toHaveCount(50);await expect(dialog.getByRole('button',{name:'Previous Saved FAA Jobs'})).toBeDisabled();
 const next=dialog.getByRole('button',{name:'Next Saved FAA Jobs'});await next.focus();await page.keyboard.press('Enter');await expect(dialog.getByRole('heading',{name:'Saved FAA downloads · page 2'})).toBeVisible();await expect(dialog.getByRole('button',{name:'Previous Saved FAA Jobs'})).toBeEnabled();
 await dialog.getByRole('button',{name:'Previous Saved FAA Jobs'}).click();await expect(dialog.getByRole('heading',{name:'Saved FAA downloads · page 1'})).toBeVisible();const jobs=await(await request.get('/api/aviation/downloads')).json();expect(jobs.every(j=>j.state==='Awaiting Approval')).toBeTruthy();await post(request,'/api/aviation/configuration',{onlineEnabled:false});
});
