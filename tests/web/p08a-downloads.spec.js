import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {localeBundle} from '../../scripts/build/build-locales.mjs';
import {createTranslator} from '../../src/AeroScience.Engine/wwwroot/i18n-format.js';
const planId='08000000-0000-0000-0000-000000000001',MiB=1048576;
function fixture(name='Synthetic P08a research'){
 return {queue:{downloading:0,queued:24,paused:false,combinedNetworkBytes:150*MiB},jobs:[],libraryJobs:[],network:[],networkActivity:{sessionId:'synthetic-p08a-process',bytes:150*MiB,elapsedSeconds:30,activeTransfers:0},plans:[{planId,planName:name,status:'Awaiting future-flight approval',ready:23,awaiting:27,unavailable:4,retryable:1,scope:'entire',selectedUnits:54,selectedReady:23,selectedAwaiting:27,selectedUnavailable:4,message:'Synthetic status fixture; no source download or scientific acceptance.'}],planInstruments:[{planId,instruments:[{instrumentId:'terrain-scanner',name:'Terrain Scanner',status:'Awaiting future-flight approval',total:27,ready:14,awaiting:13,unavailable:0,reasons:[]},{instrumentId:'multispectral-imager',name:'Multispectral Imaging',status:'Awaiting future-flight approval',total:27,ready:9,awaiting:14,unavailable:4,reasons:['Unsupported provider metadata at assets.blue.href; no scientific values were guessed.']}]}]};
}
test('P08a activity exposes one read-only process identity and actual reader counters',async({request})=>{
 const first=await(await request.get('/api/science/activity')).json(),second=await(await request.get('/api/science/activity')).json();
 expect(first.networkActivity.sessionId).toMatch(/^[a-f0-9-]{36}$/);expect(second.networkActivity.sessionId).toBe(first.networkActivity.sessionId);
 expect(second.networkActivity.elapsedSeconds).toBeGreaterThanOrEqual(first.networkActivity.elapsedSeconds);expect(first.networkActivity.bytes).toBeGreaterThanOrEqual(0);expect(first.networkActivity.activeTransfers).toBeGreaterThanOrEqual(0);
});
for(const quick of [false,true])test(`P08a ${quick?'quick status':'Downloads'} keeps Retry separate from future-flight approval and repeated clicks`,async({page})=>{
 const data=fixture(),actions=[];let release,started;const held=new Promise(r=>release=r),received=new Promise(r=>started=r);
 await page.route('**/api/science/activity',r=>r.fulfill({json:data}));
 await page.route(`**/api/plans/${planId}/preparation/control`,async r=>{const action=r.request().postDataJSON().action;actions.push(action);if(action==='retry'){started();await held;}await r.fulfill({json:data.plans[0]});});
 await page.goto('/#downloads');if(quick)await page.getByRole('button',{name:createTranslator(localeBundle(),'en-US').text('ui.downloadWorkspace.messageQuickStatus'),exact:true}).click();
 const root=quick?page.getByRole('dialog',{name:'Scientific Download Activity',exact:true}):page.locator('.download-sessions');
 const retry=root.getByRole('button',{name:'Retry failed regions for '+data.plans[0].planName,exact:true});
 try{await retry.dblclick();await received;expect(actions).toEqual(['retry']);}finally{release();}
 await expect(retry).not.toHaveAttribute('aria-busy','true');await root.getByRole('button',{name:'Continue preparation for a future flight: '+data.plans[0].planName,exact:true}).click();await expect.poll(()=>actions).toEqual(['retry','continue']);
 await expect(page.locator('.instrument-preparation').first()).toContainText('Unsupported provider metadata at assets.blue.href');
 if(!quick){await expect(root).toContainText('23/54');await expect(root).toContainText('Flight completed. Additional scientific data preparation is paused until you approve continuing.');}
});
test('P08a combined speed expires locally during a held status request without faster network polling',async({page})=>{
 await page.clock.install();const data=fixture();data.plans=[];data.planInstruments=[];let polls=0,release;
 const held=new Promise(r=>release=r);
 await page.route('**/api/science/activity',async r=>{const sample=polls++;if(sample>=3)await held;await r.fulfill({json:{...data,networkActivity:{sessionId:'synthetic-sustained',bytes:sample*3*MiB,elapsedSeconds:sample*3,activeTransfers:2}}});});
 try{
  await page.goto('/#downloads');const live=page.getByRole('region',{name:'Live scientific network activity',exact:true});await expect(live).toContainText('0 B');
  await page.clock.fastForward(3000);await expect(live).toContainText('3 MiB');await page.clock.fastForward(3000);await expect(live).toContainText('1 MiB/s');await expect(live).toContainText('2 active network transfers');
  await page.clock.fastForward(9000);await expect(live).toContainText('Current network measurements are unavailable.');expect(polls).toBe(4);
 }finally{release();}
});
for(const locale of ['en-US','es-ES','fr-FR','de-DE','pt-BR'])test(`P08a ${locale} preserves candidate wording, source diagnostics and narrow keyboard operation`,async({page},info)=>{
 const t=createTranslator(localeBundle(),locale).text,name='SYNTHETIC Café 東京 — '+ 'longresearchname'.repeat(8),data=fixture(name),writes=[];
 await page.addInitScript(locale=>localStorage.setItem('AeroScience.presentation.v1',JSON.stringify({version:1,theme:'auto',locale})),locale);
 await page.route('**/api/science/activity',r=>r.fulfill({json:data}));page.on('request',r=>{if(r.method()==='POST'&&/start|collection|sharing|submission|approval/.test(new URL(r.url()).pathname))writes.push(r.url());});
 await page.goto('/#downloads');await expect(page.locator('html')).toHaveAttribute('lang',locale);await expect(page.getByRole('region',{name:t('network.title'),exact:true})).toBeVisible();
 const retry=page.getByRole('button',{name:t('preparation.retryNamed',{name}),exact:true});await retry.focus();await expect(retry).toBeFocused();
 await expect(page.locator('.preparation-session')).toContainText(t('preparation.postFlightHold'));await expect(page.locator('.instrument-preparation')).toContainText('Unsupported provider metadata at assets.blue.href');
 for(const theme of ['light','dark']){
  await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});await expect(page.locator('html')).toHaveAttribute('data-resolved-theme',theme);
  for(const width of [1440,390]){
   await page.setViewportSize({width,height:1000});await page.locator('html').evaluate(e=>e.style.fontSize='200%');
   const layout=await page.evaluate(()=>({width:innerWidth,x:scrollX,scroll:document.documentElement.scrollWidth,overflow:[...document.querySelectorAll('body *')].filter(e=>(e.getBoundingClientRect().right+scrollX>innerWidth+1||e.scrollWidth>e.clientWidth+1)&&e.getBoundingClientRect().width>0).slice(-12).map(e=>({tag:e.tagName,class:e.className,text:e.textContent.slice(0,90),right:e.getBoundingClientRect().right+scrollX,scroll:e.scrollWidth,client:e.clientWidth,overflow:getComputedStyle(e).overflowX}))}));expect(layout.scroll<=layout.width+1,JSON.stringify(layout)).toBe(true);await expect(retry).toBeFocused();expect((await new AxeBuilder({page}).include('.download-sessions').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze()).violations).toEqual([]);
   await page.screenshot({path:info.outputPath(`p08a-downloads-${locale}-${theme}-${width}.png`),fullPage:true});
  }
 }
 expect(writes).toEqual([]);
});
