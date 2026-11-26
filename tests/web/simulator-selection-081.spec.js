import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const origin='http://127.0.0.1:5080',path='/api/settings/simulator';
async function save(request,id,xPlanePort){
  const {csrf}=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();
  const view=await(await request.get(path)).json();
  return request.post(path,{headers:{Origin:origin,'X-AeroScience-CSRF':csrf},data:{expectedRevision:view.preferences.revision,id,xPlanePort}});
}
test('Settings saves the simulator with an explicit restart and retains the effective MSFS provider',async({page,request})=>{
  const original=(await(await request.get(path)).json()).preferences;
  const operations=[];page.on('request',r=>{if(r.method()==='POST'&&!r.url().endsWith(path)&&!r.url().endsWith('/api/session'))operations.push(r.url());});
  try{
    await page.goto('/#settings/general');const section=page.locator('.simulator-settings'),selector=section.getByLabel('Simulator',{exact:true});
    await expect(selector).toHaveValue(original.id);await expect(section.getByText('Active simulator: Microsoft Flight Simulator 2024.',{exact:true})).toBeVisible();
    await selector.selectOption('xplane12');await section.getByText('Advanced Settings',{exact:true}).click();
    await section.getByLabel('X-Plane local API port',{exact:true}).fill('9012');await section.getByRole('button',{name:'Save simulator',exact:true}).click();
    await expect(section).toContainText('Saved simulator: X-Plane 12.');await expect(section).toContainText('Restart AeroScience');
    await expect(section).toContainText('Active simulator: Microsoft Flight Simulator 2024.');
    expect((await(await request.get('/api/settings')).json()).simulatorId).toBe('msfs2024');
    await page.reload();await expect(page.locator('.simulator-settings select')).toHaveValue('xplane12');
    await expect(page.locator('.simulator-settings')).toContainText('Restart AeroScience');expect(operations).toEqual([]);
  }finally{expect((await save(request,original.id,original.xPlanePort)).ok()).toBe(true);}
});
test('refresh preserves keyboard focus and an unsaved source/port draft; paired devices are read-only',async({page})=>{
  await page.goto('/#settings/general');const section=page.locator('.simulator-settings'),selector=section.getByLabel('Simulator',{exact:true});
  await selector.focus();await page.keyboard.press('End');await page.keyboard.press('Tab');
  await expect(selector).toHaveValue('xplane12');await selector.focus();await page.evaluate(()=>window.dispatchEvent(new Event('focus')));
  await expect(selector).toBeFocused();await expect(selector).toHaveValue('xplane12');
  await section.getByRole('button',{name:'Reload saved simulator',exact:true}).click();await expect(selector).toHaveValue('msfs2024');
  await page.route('**/api/settings/simulator',async route=>{const response=await route.fetch();await route.fulfill({response,json:{...(await response.json()),canWrite:false}});});
  await section.getByRole('button',{name:'Reload saved simulator',exact:true}).click();await expect(selector).toBeDisabled();
  await expect(section.getByRole('button',{name:'Save simulator',exact:true})).toBeDisabled();await expect(section).toContainText('companion computer');
});
test('source settings remain reachable at 390px, dark, 200% text and candidate-language English fallback',async({page})=>{
  await page.setViewportSize({width:390,height:844});await page.emulateMedia({colorScheme:'dark',reducedMotion:'reduce'});
  await page.goto('/#settings/general');await page.getByLabel('Language',{exact:true}).selectOption('de-DE');
  const section=page.locator('.simulator-settings');await expect(section.getByLabel('Simulator',{exact:true})).toBeVisible();
  // Use the same CSSOM text-scaling fixture as Current Flight; do not relax CSP.
  await page.evaluate(()=>{document.documentElement.style.fontSize='200%';});
  expect(await page.locator('html').evaluate(el=>getComputedStyle(el).fontSize)).toBe('32px');
  await section.getByText('Advanced Settings',{exact:true}).click();const port=section.getByLabel('X-Plane local API port',{exact:true});await port.focus();await expect(port).toBeFocused();
  await page.keyboard.press('Tab');await expect(section.getByRole('button',{name:'Save simulator',exact:true})).toBeFocused();
  await expect(section.getByRole('button',{name:'Save simulator',exact:true})).toBeInViewport();
  const dimensions=await section.evaluate(el=>({scroll:el.scrollWidth,width:el.clientWidth}));expect(dimensions.scroll).toBeLessThanOrEqual(dimensions.width+2);
  const a11y=await new AxeBuilder({page}).include('.simulator-settings').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze();expect(a11y.violations).toEqual([]);
  await page.screenshot({path:'artifacts/p03-settings-narrow-dark-synthetic-browser.png',fullPage:true});
});
test('synthetic X-Plane read-only presentation separates connection from recording and hides MSFS setup',async({page})=>{
  // Presentation fixture only. Authoritative X-Plane refusal is exercised by
  // SimulatorIntegrationTests through the real engine and API controllers.
  const availability={version:1,id:'xplane12',productionRecordingReady:false,scienceIntegrationReady:false,reasonKey:'simulator.recordingDisabled'};
  await page.addInitScript(availability=>{
    const Original=window.EventSource;
    window.EventSource=class extends Original{
      addEventListener(type,listener,options){if(type!=='state')return super.addEventListener(type,listener,options);
        return super.addEventListener(type,event=>{const state=JSON.parse(event.data);state.simulator=availability;listener(new MessageEvent('state',{data:JSON.stringify(state)}));},options);}
    };
  },availability);
  await page.route('**/api/mission-intelligence',async route=>{const response=await route.fetch(),data=await response.json();data.flight.simulator=availability;await route.fulfill({response,json:data});});
  await page.route('**/api/settings',async route=>{const response=await route.fetch();await route.fulfill({response,json:{...(await response.json()),simulatorId:'xplane12',simConnect:null,toolbarStatus:null}});});
  await page.route('**/api/settings/simulator',async route=>{const response=await route.fetch(),value=await response.json();await route.fulfill({response,json:{...value,effective:{...value.effective,id:'xplane12'},preferences:{...value.preferences,id:'xplane12'},productionRecordingReady:false,scienceIntegrationReady:false,statusKey:'simulator.readOnlyConnected'}});});
  await page.goto('/#flight');await expect(page.locator('#simulator-read-only')).toContainText('not yet enabled for X-Plane');await expect(page.locator('#start-button')).toBeDisabled();
  await expect(page.locator('#connection-text')).toContainText('X-Plane connected');
  await page.goto('/#settings/general');await expect(page.locator('.simulator-settings')).toContainText('Active simulator: X-Plane 12.');
  await expect(page.locator('a[href="#settings/toolbar"]')).toBeHidden();
  await page.goto('/#settings/toolbar');await expect(page.locator('#settings-toolbar')).toContainText('MSFS toolbar is unavailable');
  await expect(page.getByRole('button',{name:'Install / Update / Repair toolbar',exact:true})).toHaveCount(0);
  await page.screenshot({path:'artifacts/p03-xplane-isolation-light-synthetic-browser.png',fullPage:true});
});
test('late source-settings reads cannot revert a confirmed save',async({page,request})=>{
  const original=(await(await request.get(path)).json()).preferences;let release,intercepted=false;const held=new Promise(resolve=>{release=resolve;});
  await page.goto('/#settings/general');const section=page.locator('.simulator-settings');await expect(section.locator('select')).toHaveValue(original.id);
  await page.route('**/api/settings/simulator',async route=>{if(route.request().method()!=='GET')return route.continue();const response=await route.fetch();intercepted=true;await held;await route.fulfill({response});});
  try{
    await page.evaluate(()=>window.dispatchEvent(new Event('focus')));await expect.poll(()=>intercepted).toBe(true);
    await section.locator('select').selectOption('xplane12');await section.getByRole('button',{name:'Save simulator',exact:true}).click();await expect(section).toContainText('Saved simulator: X-Plane 12.');
    const completed=page.waitForResponse(r=>r.url().endsWith(path)&&r.request().method()==='GET');release();await(await completed).finished();
    await expect(section.locator('select')).toHaveValue('xplane12');
  }finally{release();expect((await save(request,original.id,original.xPlanePort)).ok()).toBe(true);}
});
