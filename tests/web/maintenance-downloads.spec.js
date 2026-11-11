import {test,expect} from '@playwright/test';

test('measured byte windows expire and reset across pauses, processing, counters and attempts',async({page})=>{
 await page.goto('/#downloads');
 const results=await page.evaluate(async()=>{
  const {networkTransfer,preparationTransfer}=await import('/transfer-progress.js'),sessionId='synthetic-window-test',MiB=1048576;
  const sample=(bytes,elapsedSeconds,now,activeTransfers=1)=>networkTransfer({sessionId,bytes,elapsedSeconds,activeTransfers},now).bytesPerSecond;
  const first=sample(0,0,0),second=sample(3*MiB,3,3000),third=sample(6*MiB,6,6000),expired=sample(6*MiB,6,13000),stopped=sample(6*MiB,9,14000),paused=sample(6*MiB,12,15000,0),rollback=sample(1,15,18000),restart=networkTransfer({sessionId:'new-process',bytes:100*MiB,elapsedSeconds:1,activeTransfers:1},20000).bytesPerSecond;
  const job={key:'synthetic-job',attemptId:'first',status:'Downloading',stage:'Downloading',downloadedBytes:0,updatedUtc:'a'};
  preparationTransfer(job,0);preparationTransfer({...job,downloadedBytes:3*MiB,updatedUtc:'b'},3000);const regional=preparationTransfer({...job,downloadedBytes:6*MiB,updatedUtc:'c'},6000).bytesPerSecond;
  const retry=preparationTransfer({...job,attemptId:'second',downloadedBytes:9*MiB,updatedUtc:'d'},9000).bytesPerSecond,verifying=preparationTransfer({...job,stage:'Validating',downloadedBytes:10*MiB,updatedUtc:'e'},12000).bytesPerSecond;
  return {first,second,third,expired,stopped,paused,rollback,restart,regional,retry,verifying};
 });
 expect(results.third).toBe(1048576);expect(results.regional).toBe(1048576);
 for(const key of ['first','second','expired','stopped','paused','rollback','restart','retry','verifying'])expect(results[key],key).toBeNull();
});

test('post-flight maintenance status explains 23/54 and keeps continue and retry distinct',async({page})=>{
 const planId='00000000-0000-0000-0000-000000000071',actions=[];
 const data={queue:{downloading:0,queued:24,paused:false,combinedNetworkBytes:157286400},jobs:[],libraryJobs:[],network:[],networkActivity:{sessionId:'synthetic-paused',bytes:157286400,elapsedSeconds:30,activeTransfers:0},plans:[{planId,planName:'Synthetic tester status',status:'Awaiting future-flight approval',ready:23,awaiting:27,unavailable:4,retryable:1,scope:'entire',selectedUnits:54,selectedReady:23,selectedAwaiting:27,selectedUnavailable:4,message:'Presentation fixture; no real downloads.'}],planInstruments:[]};
 data.planInstruments=[{planId,instruments:[{instrumentId:'terrain-scanner',name:'Terrain Scanner',status:'Awaiting future-flight approval',total:27,ready:14,awaiting:13,unavailable:0,reasons:[]},{instrumentId:'multispectral-imager',name:'Multispectral Imaging',status:'Awaiting future-flight approval',total:27,ready:9,awaiting:14,unavailable:4,reasons:['Unsupported provider metadata at assets.blue.href; retry may not resolve incompatible metadata.']}]}];
 await page.route('**/api/science/activity',route=>route.fulfill({json:data}));
 await page.route(`**/api/plans/${planId}/preparation/control`,async route=>{actions.push(route.request().postDataJSON().action);await route.fulfill({json:data.plans[0]});});
 await page.goto('/#downloads');const card=page.locator('.preparation-session').filter({hasText:'Synthetic tester status'}),live=page.getByLabel('Live scientific network activity');
 await expect(card).toContainText('23/54 instrument-region units ready / 27 awaiting / 4 limited or unavailable');await expect(card).toContainText('Flight completed. Additional scientific data preparation is paused until you approve continuing.');
 await expect(page.getByLabel('Instrument readiness for Synthetic tester status',{exact:true})).toContainText('Unsupported provider metadata at assets.blue.href');
 await expect(live).toContainText('Paused after flight');await expect(live).toContainText('150 MiB actually transferred');await expect(live).toContainText('0 active network transfers');
 await page.getByRole('button',{name:'Retry failed regions for Synthetic tester status',exact:true}).click();await expect.poll(()=>actions).toEqual(['retry']);
 await page.getByRole('button',{name:'continue Data Preparation for Synthetic tester status',exact:true}).click();await expect.poll(()=>actions).toEqual(['retry','continue']);
 await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('active unknown-size numeric transfer has prominent individual status and indeterminate progress',async({page})=>{
 const job={key:'synthetic-active',attemptId:'one',kind:'terrain',status:'Downloading',stage:'Downloading',provider:'Synthetic USGS',downloadedBytes:1048576,updatedUtc:new Date().toISOString(),message:'Synthetic transport presentation',area:{west:0,south:0,east:.02,north:.02}};
 await page.route('**/api/science/activity',route=>route.fulfill({json:{queue:{downloading:1,queued:0,paused:false},jobs:[job],plans:[],libraryJobs:[],network:[],networkActivity:{sessionId:'synthetic-active',bytes:1048576,elapsedSeconds:1,activeTransfers:1}}}));
 await page.goto('/#downloads');const live=page.getByLabel('Live scientific network activity');await expect(live).toContainText('1 active network transfers');await expect(live).toContainText('Synthetic USGS');await expect(live).toContainText('Calculating / waiting');await expect(live.locator('progress')).toHaveCount(1);await expect(live.locator('progress')).not.toHaveAttribute('value');await expect(live).toContainText('completion time unknown');
});
