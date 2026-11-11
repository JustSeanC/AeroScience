import {test,expect} from '@playwright/test';
import {mountToolbar} from './helpers/toolbar.mjs';
test('companion defaults persist across browser contexts while explicit browser overrides remain local',async({page,request,browser})=>{
 const origin='http://127.0.0.1:5080';const session=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();
 const headers={Origin:origin,'X-AeroScience-CSRF':session.csrf};
 const original=(await(await request.get('/api/settings/presentation')).json()).preferences;
 async function save(theme,locale){const current=(await(await request.get('/api/settings/presentation')).json()).preferences;const r=await request.post('/api/settings/presentation',{headers,data:{expectedRevision:current.revision,theme,locale}});expect(r.ok(),await r.text()).toBe(true);}
 try{
  await page.goto('/#settings');await expect(page.getByLabel('Default appearance',{exact:true})).toHaveValue(original.theme);
  await page.getByLabel('Default appearance',{exact:true}).selectOption('dark');await page.getByLabel('Companion default language',{exact:true}).selectOption('pt-BR');
  await page.getByRole('button',{name:'Save companion defaults',exact:true}).click();await expect(page.locator('html')).toHaveAttribute('data-resolved-theme','dark');
  await expect(page.locator('html')).toHaveAttribute('data-requested-locale','pt-BR');await expect(page.locator('html')).toHaveAttribute('lang','en-US');
  const context=await browser.newContext({storageState:await request.storageState()});try{
   const other=await context.newPage();await other.goto(origin+'/#settings');await expect(other.getByLabel('Theme',{exact:true})).toHaveValue('dark');await expect(other.getByLabel('Language',{exact:true})).toHaveValue('pt-BR');
   await other.getByLabel('Theme',{exact:true}).selectOption('light');await other.reload();await expect(other.getByLabel('Theme',{exact:true})).toHaveValue('light');
   expect((await(await request.get('/api/settings/presentation')).json()).preferences.theme).toBe('dark');
   await other.getByRole('button',{name:'Use companion defaults in this browser',exact:true}).click();await expect(other.getByLabel('Theme',{exact:true})).toHaveValue('dark');
  }finally{await context.close();}
  await page.reload();await expect(page.getByLabel('Default appearance',{exact:true})).toHaveValue('dark');
 }finally{await save(original.theme,original.locale);}
});
test('native adapter accepts only current correlated replies and uses English for old or mismatched catalogs',async({page})=>{
 await mountToolbar(page,{autoReply:false});const catalogVersion=await page.evaluate(()=>AeroScienceLocaleData.manifest.catalogVersion);
 async function reply(metadata,requestId=null){await page.evaluate(({metadata,requestId})=>{const p=document.querySelector('ingamepanel-aeroscience');p.send('state');const req=window.bus.sent.filter(x=>x.command.op==='state').at(-1).command;
  p.receive(JSON.stringify({protocol:1,requestId:requestId||req.requestId,ok:true,state:window.bus.state,capabilities:['presentation-1'],presentation:metadata}));}, {metadata,requestId});}
 await reply({version:1,catalogVersion,locale:'fr-FR',revision:4});await expect(page.locator('#as-language-detail')).toContainText('Français preview uses bundled English');
 await reply({version:1,catalogVersion,locale:'pt-BR',revision:1});await expect(page.locator('#as-language-detail')).toContainText('Français');
 await reply({version:1,catalogVersion:999,locale:'pt-BR'},'00000000-0000-4000-8000-000000000009');await expect(page.locator('#as-language-detail')).toContainText('Français');
 await reply({version:1,catalogVersion:999,locale:'pt-BR',revision:5});await expect(page.locator('#as-language-detail')).toContainText('English fallback');
 await reply(null);await expect(page.locator('nav [data-native-section]')).toHaveText(['Flight','Science','Cards']);
 await expect(page.locator('ingamepanel-aeroscience')).toHaveAttribute('lang','en-US');
 expect(await page.evaluate(()=>window.bus.sent.filter(x=>x.command.op!=='state'&&!x.command.op.startsWith('map-')))).toEqual([]);
});
test('a late host read cannot revert a confirmed preference save',async({page,request})=>{
 const origin='http://127.0.0.1:5080',csrf=(await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json()).csrf;
 const original=(await(await request.get('/api/settings/presentation')).json()).preferences;let release,intercepted=false;
 const held=new Promise(resolve=>{release=resolve;});
 await page.goto('/#settings');await expect(page.getByLabel('Default appearance',{exact:true})).toHaveValue(original.theme);
 await page.route('**/api/settings/presentation',async route=>{
  if(route.request().method()!=='GET')return route.continue();const response=await route.fetch();intercepted=true;await held;await route.fulfill({response});
 });
 try{
  await page.evaluate(()=>window.dispatchEvent(new Event('focus')));await expect.poll(()=>intercepted).toBe(true);
  const target=original.theme==='dark'?'light':'dark';
  await page.getByLabel('Default appearance',{exact:true}).selectOption(target);await page.getByRole('button',{name:'Save companion defaults',exact:true}).click();
  await expect(page.locator('.host-presentation-settings')).toContainText('Companion defaults saved');
  const received=page.waitForResponse(r=>r.url().endsWith('/api/settings/presentation')&&r.request().method()==='GET');release();
  await (await received).finished();await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  await page.unroute('**/api/settings/presentation');await expect(page.getByLabel('Default appearance',{exact:true})).toHaveValue(target);
  await expect(page.locator('html')).toHaveAttribute('data-resolved-theme',target);
 }finally{release();const current=(await(await request.get('/api/settings/presentation')).json()).preferences;
  const r=await request.post('/api/settings/presentation',{headers:{Origin:origin,'X-AeroScience-CSRF':csrf},data:{expectedRevision:current.revision,theme:original.theme,locale:original.locale}});expect(r.ok()).toBe(true);}
});
test('paired presentation view is read-only while browser overrides remain available',async({page})=>{
 const writes=[];page.on('request',r=>{if(r.url().endsWith('/api/settings/presentation')&&r.method()==='POST')writes.push(r.url());});
 await page.route('**/api/settings/presentation',async route=>{const response=await route.fetch();await route.fulfill({response,json:{...(await response.json()),canWrite:false}});});
 await page.goto('/#settings');await expect(page.getByLabel('Default appearance',{exact:true})).toBeDisabled();
 await expect(page.getByRole('button',{name:'Save companion defaults',exact:true})).toBeDisabled();
 await expect(page.locator('.host-presentation-settings')).toContainText('read-only on paired devices');
 await page.getByLabel('Theme',{exact:true}).selectOption('dark');await expect(page.locator('html')).toHaveAttribute('data-resolved-theme','dark');expect(writes).toEqual([]);
});
