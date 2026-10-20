import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const session=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(response.ok(),await response.text()).toBeTruthy();return response.json();}
let fixture;
test.beforeAll(async({request})=>{fixture=await post(request,'/api/testing/workstation',{acquisitions:144,observations:720});});
test('All instruments represents the complete scope beyond old limits, with independent layers and direct record inspection',async({page,request})=>{
  const overview=await post(request,'/api/science/workspace/overview',{missions:fixture.missionIds});
  expect(overview.acquisitions).toBe(144);expect(overview.atmosphericValues).toBe(720);
  expect(overview.bins.length).toBeLessThanOrEqual(96*overview.categories.length);
  expect(new Set(overview.categories.map(item=>item.instrumentId))).toEqual(new Set(['terrain-scanner','thermal-imager','multispectral-imager','ambient-temperature','ambient-wind']));
  const network=[];page.on('request',request=>{if(request.url().includes('/api/science/prepare')||request.url().includes('/prepare'))network.push(request.url());});
  await page.goto('/#workspace/map?missions='+fixture.missionId);
  await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-acquisitions','142');
  await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-atmospheric-values','720');
  await expect(page.getByLabel('Science layers and inspection').locator('.independent-layer-control')).toHaveCount(5);
  await expect(page.locator('.workspace-status').first()).toContainText('Overview accounts for all');
  await expect(page.getByLabel('Show Thermal Imager',{exact:true})).toBeChecked();
  await expect(page.locator('.raster-legend')).toContainText('degC');await expect(page.locator('.raster-legend')).toContainText('m');
  await expect(page.getByRole('region',{name:'Browse all saved scientific records'})).toContainText('142 acquisitions available');
  await page.getByLabel('Archive page',{exact:true}).fill('5');await page.getByRole('button',{name:'Go to archive page',exact:true}).click();
  await expect(page.getByRole('region',{name:'Browse all saved scientific records'})).toContainText('Page 5 of 5');
  await page.getByRole('region',{name:'Browse all saved scientific records'}).getByRole('button',{name:'Inspect sample 1',exact:true}).click();
  await expect(page.getByRole('region',{name:'Science sample inspection',exact:true})).toContainText('Historical observation date');
  await expect(page.getByRole('region',{name:'Recorded scientific values',exact:true})).toContainText('acquisition');
  expect(network).toEqual([]);
});
test('more than 500 recorded simulator values load automatically, while whole-scope counts and archive access stay accurate',async({page})=>{
  await page.goto('/#workspace/map?missions='+fixture.missionId);
  await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-atmospheric-values','720');
  await expect(page.locator('.workspace-status').first()).toContainText('720 individual simulator values currently loaded');
  await page.getByLabel('Recorded science category',{exact:true}).selectOption('ambient-temperature|temperature');
  await expect(page.getByRole('region',{name:'Browse all saved scientific records'})).toContainText('360 environmental values available');
  await page.getByLabel('Archive page',{exact:true}).fill('12');await page.getByRole('button',{name:'Go to archive page',exact:true}).click();
  await expect(page.getByRole('region',{name:'Browse all saved scientific records'})).toContainText('Page 12 of 12');
  await page.getByRole('region',{name:'Browse all saved scientific records'}).getByRole('button',{name:'Inspect sample 1',exact:true}).click();
  await expect(page.getByRole('region',{name:'Science sample inspection',exact:true})).toContainText('Simulator environment');
});
test('comparison follows the current mission and renders honest shared scales, matched cells and a difference map',async({page})=>{
  await page.goto('/#workspace/compare?missions='+fixture.missionIds.join(','));
  await expect(page.locator('.comparison-card')).toHaveCount(2);
  const mission=page.getByLabel('Comparison mission 1',{exact:true});await mission.selectOption(fixture.missionIds[1]);
  await expect(page.locator('.comparison-card').first().getByRole('link',{name:'Atmospheric / pass analysis',exact:true})).toHaveAttribute('href','#workspace/analysis?missions='+fixture.missionIds[1]);
  await page.getByLabel('Comparison mission 2',{exact:true}).selectOption(fixture.missionIds[1]);
  // The two selectors must choose distinct recorded captures, not silently compare one record to itself.
  await page.getByLabel('Comparison mission 1',{exact:true}).selectOption(fixture.missionId);
  const thermal=await page.getByLabel('Acquisition 1',{exact:true}).locator('option').evaluateAll(options=>options.find(option=>option.textContent.startsWith('Thermal Imager')).value);
  await page.getByLabel('Acquisition 1',{exact:true}).selectOption(thermal);
  await page.getByLabel('Visual measurement 1',{exact:true}).selectOption('temperature');
  await page.getByLabel('Visual measurement 2',{exact:true}).selectOption('temperature');
  await expect(page.locator('.comparison-card').first()).toContainText('Historical observation date');
  await expect(page.getByLabel('Use shared numeric color scale')).toBeEnabled();await page.getByLabel('Use shared numeric color scale').check();
  await expect(page.locator('.scale-state')).toContainText('Shared numeric scale');
  await page.getByLabel('Comparison measurement',{exact:true}).selectOption('temperature');await page.getByRole('button',{name:'Compare Recorded Values',exact:true}).click();
  await expect(page.getByRole('region',{name:'Scientific comparison result',exact:true})).toContainText('Common native-pixel comparison');
  await expect(page.getByRole('region',{name:'Scientific comparison result',exact:true})).toContainText('Constant difference: 0 K');
});
test('tablet navigation is labeled, keyboard-operable and restores focus; core pages pass automated axe checks',async({page})=>{
  test.setTimeout(120000);
  await page.setViewportSize({width:820,height:1180});await page.goto('/#control');
  const menu=page.getByRole('button',{name:'☰ Menu',exact:true});await expect(menu).toBeVisible();
  await menu.focus();await page.keyboard.press('Enter');await expect(menu).toHaveAttribute('aria-expanded','true');
  await expect(page.getByRole('navigation',{name:'Main navigation'}).getByRole('link',{name:'Mission History',exact:true})).toBeVisible();
  await page.keyboard.press('Escape');await expect(menu).toBeFocused();await expect(menu).toHaveAttribute('aria-expanded','false');
  for(const route of ['#control','#workspace/map?missions='+fixture.missionId,'#workspace/analysis?missions='+fixture.missionId,'#workspace/compare?missions='+fixture.missionIds.slice(0,2).join(','),'#workspace/results?missions='+fixture.missionId,'#history','#downloads','#library','#help']){
    await page.goto('/'+route);await expect(page.locator('#workspace')).toBeVisible();await page.waitForTimeout(400);
    const results=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze();
    expect(results.violations.map(item=>({id:item.id,impact:item.impact,nodes:item.nodes.map(node=>({target:node.target,summary:node.failureSummary}))})),route).toEqual([]);
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),route).toBeTruthy();
  }
});
test('large complete scope uses bounded overview rendering, cancels stale viewport reads and preserves source hashes',async({page,request},testInfo)=>{
  test.setTimeout(90000);
  const large=await post(request,'/api/testing/workstation',{acquisitions:2100,observations:12000});
  const before=await(await request.get('/api/science/products/'+large.products[0])).json();
  const timings=[];page.on('response',response=>{if(response.url().includes('/api/science/workspace/overview'))timings.push(response.request().timing());});
  const session=await page.context().newCDPSession(page);await session.send('Performance.enable');
  const started=Date.now();await page.goto('/#workspace/map?missions='+large.missionIds.join(','));
  await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-acquisitions','2100');
  await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-atmospheric-values','12000');
  await expect(page.locator('.workspace-status').first()).toContainText('Overview accounts for all');
  const loaded=Date.now()-started;expect(loaded).toBeLessThan(15000);
  const renderedMapFeatures=Number(await page.locator('.workspace-map').getAttribute('data-rendered-features'));expect(renderedMapFeatures).toBeLessThan(1000);
  const renderedTableRows=await page.locator('tbody tr').count();expect(renderedTableRows).toBeLessThanOrEqual(55);const metrics=await session.send('Performance.getMetrics');
  const beforeClick=Date.now();await page.getByLabel('Show Thermal Imager',{exact:true}).uncheck();
  await expect(page.getByLabel('Show Thermal Imager',{exact:true})).not.toBeChecked();const feedback=Date.now()-beforeClick;
  expect(feedback).toBeLessThan(1000);
  await page.getByLabel('Archive page',{exact:true}).fill('70');await page.getByRole('button',{name:'Go to archive page',exact:true}).click();await expect(page.getByRole('region',{name:'Browse all saved scientific records'})).toContainText('Page 70 of 70');
  let pending;const received=new Promise(resolve=>pending=resolve);let wasCancelled=false;
  page.on('requestfailed',request=>{if(request.url().includes('/api/science/workspace/overview'))wasCancelled=true;});
  await page.route('**/api/science/workspace/overview',async route=>{pending();await new Promise(resolve=>setTimeout(resolve,700));try{await route.continue();}catch{}});
  const refresh=page.getByRole('button',{name:'Refresh Saved Science',exact:true});await refresh.click();await received;
  await page.goto('/#help');await expect(page.getByRole('heading',{name:'Help & Support',exact:true})).toBeVisible();await expect.poll(()=>wasCancelled).toBeTruthy();await expect(page.locator('.action-error')).toHaveCount(0);
  const after=await(await request.get('/api/science/products/'+large.products[0])).json();expect(after).toEqual(before);
  await session.detach();
  const report={fixture:'Synthetic UI/load only; repeated native support, not provider or real-flight acceptance',acquisitions:2100,atmosphericValues:12000,missions:3,loadedMilliseconds:loaded,checkboxFeedbackMilliseconds:feedback,renderedMapFeatures,renderedTableRows,overviewRequests:timings.length,chromeMetrics:metrics.metrics};
  await testInfo.attach('v042-large-scope-performance',{body:JSON.stringify(report,null,2),contentType:'application/json'});
});
test('desktop through narrow layouts and emulated 125/150/200 percent text zoom retain visible controls',async({page})=>{
  test.setTimeout(90000);
  for(const width of [1920,1440,1280,1024,820,768,390]){
    await page.setViewportSize({width,height:1000});await page.goto('/#workspace/map?missions='+fixture.missionId);await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-acquisitions','142');
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),String(width)).toBeTruthy();
    await expect(page.getByRole('button',{name:'Fit Data',exact:true})).toBeVisible();
    if(width<=1100){await page.getByRole('button',{name:'☰ Menu',exact:true}).click();await expect(page.getByRole('link',{name:'Download Activity',exact:true})).toBeVisible();await page.keyboard.press('Escape');}
  }
  await page.setViewportSize({width:1440,height:1000});
  for(const zoom of [1.25,1.5,2]){
    await page.goto('/#workspace/compare?missions='+fixture.missionIds.slice(0,2).join(','));await expect(page.locator('.comparison-card')).toHaveCount(2);
    await page.evaluate(value=>{document.documentElement.style.zoom=value;dispatchEvent(new Event('resize'));},zoom);
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),String(zoom)).toBeTruthy();
    await expect(page.getByRole('button',{name:'Compare Recorded Values',exact:true})).toBeVisible();
    await page.evaluate(()=>document.documentElement.style.zoom='');
  }
});
test('partial preparation states, small file sizes and task-based offline help remain truthful',async({page})=>{
 await page.goto('/#control');
 const result=await page.evaluate(async()=>{const {instrumentSummaries,renderInstrumentSummaries}=await import('/preparation-summary.js'),{bytes}=await import('/workstation-ui.js');const summaries=instrumentSummaries([{kind:'thermal',status:'Ready'},{kind:'thermal',status:'Queued'}]),parent=document.createElement('div');renderInstrumentSummaries(parent,summaries);const paused=parent.textContent;const now=new Date().toISOString();const active=instrumentSummaries([{kind:'thermal',status:'Ready'},{kind:'thermal',status:'Downloading',stage:'Downloading',downloadedBytes:100,updatedUtc:now}]);renderInstrumentSummaries(parent,active);return {paused,active:parent.textContent,small:bytes(584)};});
 expect(result.paused).toContain('Awaiting Remaining Data');expect(result.paused).not.toContain('Downloading Remaining Data');expect(result.active).toContain('Downloading Remaining Data');expect(result.small).toBe('584 B');
 await page.goto('/#help');await page.getByLabel('Search Help topics',{exact:true}).fill('compare');await expect(page.locator('.help-task:visible')).toHaveCount(1);await page.locator('.help-task:visible').click();await expect(page.locator('.help-guide')).toContainText('Compare');
 await page.goto('/#library');await page.getByLabel('Scientific category',{exact:true}).selectOption('thermal');await expect(page.locator('.library-collection')).toHaveCount(1);await expect(page.locator('.library-collection')).toContainText('Thermal');
});
test('instrument-compatible measurements, exact native values and composite opacity remain consistent',async({page})=>{
  test.setTimeout(45000);
  await page.goto('/#workspace/map?missions='+fixture.missionIds[1]+'&instrument=thermal-imager');
  await expect(page.locator('.workspace-status').first()).toContainText('1 geographically indexed');
  const control=page.locator('.independent-layer-control').filter({hasText:'Thermal Imager'});await control.locator('summary').click();
  const measurement=page.getByLabel('Thermal Imager measurement',{exact:true});await expect(measurement).toHaveValue('temperature-celsius');
  await measurement.selectOption('temperature');await expect(page.locator('.raster-legend')).toContainText('285.721 K');
  await expect(measurement.locator('option')).toHaveCount(2);
  await page.getByRole('region',{name:'Browse all saved scientific records'}).getByRole('button',{name:'Inspect sample 1',exact:true}).click();await expect(page.getByRole('region',{name:'Science sample inspection',exact:true})).toContainText('285.721 K');
  await page.getByText('Sources, dates & sampled support',{exact:true}).click();await page.getByText('Advanced — generate an exact native-grid composite',{exact:true}).click();await page.getByRole('button',{name:'Build Native Composite',exact:true}).click();
  await expect(page.locator('.derived-legends')).toContainText('Constant value: 285.721 K');await expect(page.locator('.workspace-map .leaflet-image-layer')).toHaveCount(1);
  const vectorOpacities=await page.getByLabel('Science layer opacity',{exact:true}).evaluate(input=>{const opacities=[],original=L.Path.prototype.setStyle;L.Path.prototype.setStyle=function(options){const result=original.call(this,options);if(this.feature?.properties.productId)opacities.push(this.options.fillOpacity);return result;};try{input.value='.2';input.dispatchEvent(new Event('input'));}finally{L.Path.prototype.setStyle=original;}return opacities;});
  await expect(page.locator('.workspace-map .leaflet-image-layer')).toHaveCSS('opacity','0.2');
  expect(vectorOpacities).toContain(.2);
  await page.getByRole('button',{name:'Next archive page',exact:true}).isDisabled().then(disabled=>expect(disabled).toBeTruthy());
});
test('archive search covers the selected scope and layer choices survive workspace navigation',async({page})=>{
 await page.goto('/#workspace/map?missions='+fixture.missionId);await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-acquisitions','142');
 await page.getByLabel('Show Thermal Imager',{exact:true}).uncheck();await page.getByLabel('Science layer opacity',{exact:true}).evaluate(input=>{input.value='.35';input.dispatchEvent(new Event('input'));});
 await page.getByRole('link',{name:'Research Analysis',exact:true}).click();await expect(page.locator('.instrument-result')).not.toHaveCount(0);await page.getByRole('link',{name:'Science Map',exact:true}).click();
 await expect(page.getByLabel('Show Thermal Imager',{exact:true})).not.toBeChecked();await expect(page.getByLabel('Science layer opacity',{exact:true})).toHaveValue('0.35');
 await page.getByLabel('Search recorded science',{exact:true}).fill('stale');await page.getByRole('button',{name:'Search recorded science',exact:true}).click();
 await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-acquisitions','0');await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-atmospheric-values','43');
 await page.getByLabel('Recorded science category',{exact:true}).selectOption('ambient-temperature|temperature');await expect(page.getByRole('region',{name:'Browse all saved scientific records'})).toContainText('22 environmental values');
 await page.getByRole('region',{name:'Browse all saved scientific records'}).getByRole('button',{name:'Inspect sample 1',exact:true}).click();await expect(page.getByRole('region',{name:'Science sample inspection',exact:true})).toContainText('stale');
});

test('live science charts expose the actual selected values in keyboard-readable tables',async({page})=>{
 await page.goto('/#control');const result=await page.evaluate(async()=>{const {scienceCharts}=await import('/science-display.js');const parent=document.createElement('section');parent.id='synthetic-chart-table-test';document.body.append(parent);const now=Date.parse('2026-10-19T12:00:00Z');scienceCharts(parent,[0,30,120].map((seconds,index)=>({measurementId:'temperature',quality:'valid',timestampUtc:new Date(now+seconds*1000).toISOString(),altitudeMslFeet:10000+index,value:10+index,unit:'degC',provenance:'Synthetic table fixture; not MSFS'})));const chart=[...parent.querySelectorAll('.science-chart')].find(card=>card.querySelector('h3').textContent==='Temperature over time');const select=chart.querySelector('select');select.value='60';select.dispatchEvent(new Event('change'));const rows=chart.querySelectorAll('tbody tr');return {rows:rows.length,text:chart.querySelector('.data-table-section').textContent,canvas:chart.querySelector('canvas').getAttribute('aria-label')};});expect(result.rows).toBe(1);expect(result.text).toContain('12:02:00');expect(result.text).toContain('Synthetic table fixture');expect(result.canvas).toBe('Temperature over time');
});
test('a delayed comparison reload cannot duplicate or replace the current acquisition choices',async({page})=>{
 await page.goto('/#workspace/compare?missions='+fixture.missionIds.slice(0,2).join(','));await expect(page.locator('.comparison-card')).toHaveCount(2);
 const mission=page.getByLabel('Comparison mission 1',{exact:true}),acquisition=page.getByLabel('Acquisition 1',{exact:true});await expect(acquisition.locator('option')).toHaveCount(30);
 let release,announce;const held=new Promise(resolve=>release=resolve),arrived=new Promise(resolve=>announce=resolve);let requests=0;
 const matches=url=>url.includes('/api/science/products/page?missionId='+fixture.missionId+'&');
 await page.route('**/api/science/products/page?**',async route=>{if(matches(route.request().url())&&++requests===1){announce();await held;}await route.continue();});
 await mission.selectOption(fixture.missionIds[1]);await mission.selectOption(fixture.missionId);await arrived;
 await mission.selectOption(fixture.missionIds[1]);await mission.selectOption(fixture.missionId);await expect(acquisition.locator('option')).toHaveCount(30);const current=await acquisition.locator('option').evaluateAll(options=>options.map(option=>option.value));
 const oldResponse=page.waitForResponse(response=>matches(response.url()));release();await(await oldResponse).finished();await page.evaluate(()=>new Promise(requestAnimationFrame));
 await expect(acquisition.locator('option')).toHaveCount(30);expect(await acquisition.locator('option').evaluateAll(options=>options.map(option=>option.value))).toEqual(current);
 const fallback=await page.evaluate(async()=>{const {select}=await import('/workstation-ui.js');const parent=document.createElement('div');return select(parent,'Restored wavelength',[['good-band','Good retained band']], 'unsupported-old-band').value;});expect(fallback).toBe('good-band');
});
test('All Missions clears additional mission selections and queries the whole archive',async({page,request})=>{
 const entire=await post(request,'/api/science/workspace/overview',{});await page.goto('/#workspace/map?missions='+fixture.missionIds.join(','));await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-acquisitions','144');
 await page.getByLabel('Mission scope',{exact:true}).selectOption('');await expect(page).not.toHaveURL(/missions=/);await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-total-acquisitions',String(entire.acquisitions));expect(await page.getByLabel('Additional missions',{exact:true}).locator('option:checked').count()).toBe(0);
});
