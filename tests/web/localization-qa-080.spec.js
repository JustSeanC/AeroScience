import{test,expect}from'@playwright/test';
import AxeBuilder from'@axe-core/playwright';
import{mountRealEngine}from'./helpers/toolbar.mjs';
import{createTranslator}from'../../src/AeroScience.Engine/wwwroot/i18n-format.js';
import{localeBundle}from'../../scripts/build/build-locales.mjs';
import{readFile}from'node:fs/promises';
const bundle=localeBundle(),origin='http://127.0.0.1:5080';
const applicationErrors=new WeakMap();
test.beforeEach(async({page})=>{const errors=[];applicationErrors.set(page,errors);page.on('pageerror',error=>errors.push(error.message));});
async function get(request,path){const response=await request.get(path);expect(response.ok(),await response.text()).toBe(true);return response.json();}
async function post(request,path,data={}){const {csrf}=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const response=await request.post(path,{headers:{Origin:origin,'X-AeroScience-CSRF':csrf},data});expect(response.ok(),await response.text()).toBe(true);return response.json();}
async function host(request,locale){const saved=await get(request,'/api/settings/presentation');return post(request,'/api/settings/presentation',{expectedRevision:saved.preferences.revision,theme:'auto',locale});}
async function label(page,text){await page.evaluate(text=>{document.querySelector('[data-qa-capture]')?.remove();const badge=document.createElement('p');badge.dataset.qaCapture='true';badge.textContent=text;Object.assign(badge.style,{font:'12px sans-serif',padding:'6px',background:'#172b3d',color:'#fff',margin:'0'});document.body.prepend(badge);},text);}
async function evidence(page,surface){return page.evaluate(async surface=>{const{presentationCoverage,localeManifest}=await import('/i18n.js');return{surface,catalogVersion:localeManifest.catalogVersion,catalogIdentity:localeManifest.catalogIdentity,locale:document.documentElement.lang,boundMessageKeys:[...new Set(presentationCoverage(document))].sort(),explicitFallbackElements:document.querySelectorAll('[data-locale-fallback=true]').length};},surface);}
async function noOverflow(page){expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);}
async function accessible(page,include){const builder=new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']);if(include)builder.include(include);const report=await builder.analyze();expect(report.violations.map(v=>({id:v.id,nodes:v.nodes.map(n=>({target:n.target,summary:n.failureSummary}))}))).toEqual([]);}

test.afterEach(async({request,page})=>{const state=await get(request,'/api/state');if(state.activeMission)await post(request,`/api/missions/${state.activeMission.id}/end`);await post(request,'/api/instruments',[]);await host(request,'en-US');expect(applicationErrors.get(page)||[]).toEqual([]);});
test('real Chart.js receives primitive localized labels and refreshes without changing points or canvas identity',async({page})=>{
 await page.goto('/#home');await expect(page.getByRole('heading',{name:'Home',exact:true})).toBeVisible();
 await page.evaluate(async()=>{const{chart}=await import('/workstation-ui.js'),{message}=await import('/i18n.js');const points=[{x:1,y:123.25},{x:2,y:124.125}],figure=chart(document.body,message('ui.receivedEvidenceUi.messageReceivedIASSamplesNoInterpolation'),[{label:message('ui.receivedEvidenceUi.messageRecordedIAS'),data:points}],{type:'scatter',xLabel:message('ui.missionTimeline.messageOriginalSequence'),yLabel:message('ui.receivedEvidenceUi.messageRecordedIAS')});figure.id='p07-chart-boundary';figure.originalPoints=JSON.stringify(points);figure.originalCanvas=figure.querySelector('canvas');figure.originalChart=Chart.getChart(figure.originalCanvas);figure.originalCanvas.tabIndex=0;figure.originalCanvas.focus();});await expect(page.locator('#p07-chart-boundary canvas')).toBeVisible();await page.locator('#p07-chart-boundary canvas').focus();await expect(page.locator('#p07-chart-boundary canvas')).toBeFocused();
 for(const locale of ['en-US','es-ES','fr-FR','de-DE','pt-BR']){
  await page.evaluate(async locale=>{const{updatePresentation}=await import('/presentation-settings.js');updatePresentation({locale});},locale);
  const value=await page.locator('#p07-chart-boundary').evaluate(figure=>{const chart=Chart.getChart(figure.querySelector('canvas'));return{locale:chart.options.locale,x:chart.options.scales.x.title.text,y:chart.options.scales.y.title.text,label:chart.data.datasets[0].label,sameChart:chart===figure.originalChart,sameCanvas:figure.querySelector('canvas')===figure.originalCanvas,focused:document.activeElement===figure.originalCanvas,samePoints:JSON.stringify(chart.data.datasets[0].data)===figure.originalPoints};}),translate=createTranslator(bundle,locale).text;
  expect(value).toEqual({locale,x:translate('ui.missionTimeline.messageOriginalSequence'),y:translate('ui.receivedEvidenceUi.messageRecordedIAS'),label:translate('ui.receivedEvidenceUi.messageRecordedIAS'),sameChart:true,sameCanvas:true,focused:true,samePoints:true});
 }
 const canvas=await page.locator('#p07-chart-boundary canvas').elementHandle();await page.locator('#p07-chart-boundary').evaluate(figure=>figure.remove());await expect.poll(()=>canvas.evaluate(node=>Chart.getChart(node)===undefined)).toBe(true);
});
test('all five languages preserve exact original CSV, GeoJSON and provenance export bytes',async({page,request})=>{
 test.setTimeout(90000); // Five complete views; each download still has its own short response bound.
 const fixture=await post(request,'/api/testing/terrain-completion'),job=await get(request,`/api/terrain-elevation/jobs/${fixture.jobId}`);
 await post(request,`/api/terrain-elevation/jobs/${fixture.jobId}/control`,{requestId:crypto.randomUUID(),expectedRevision:job.revision,action:'start'});
 await expect.poll(async()=>(await get(request,`/api/terrain-elevation/jobs/${fixture.jobId}`)).state).toBe('PartiallyComplete');
 const original=await get(request,`/api/missions/${fixture.missionId}/science-summary`);
 const expected=new Map();
 for(const locale of ['en-US','es-ES','fr-FR','de-DE','pt-BR']){
  // Chromium limits repeated downloads from one document. Use the ordinary
  // fresh-view journey for each language, without changing browser policy.
  const view=locale==='en-US'?page:await page.context().newPage();
  await view.goto(`/#workspace/results?missions=${fixture.missionId}&view=explore`);await expect(view.locator('[data-displayed-cells]')).toHaveAttribute('data-displayed-cells',/^[1-9]/);
  await view.evaluate(async locale=>{const{updatePresentation}=await import('/presentation-settings.js');updatePresentation({locale});},locale);
  const translate=createTranslator(bundle,locale).text;
  for(const key of ['results.exportTable','results.exportGeo','results.exportProvenance']){
   const pending=view.waitForEvent('download',{timeout:5000});await view.locator('.survey-results').getByRole('button',{name:translate(key),exact:true}).click();const download=await pending,bytes=await readFile(await download.path());
   if(locale==='en-US')expected.set(key,bytes);else expect(bytes,`${locale} ${key}: original export bytes`).toEqual(expected.get(key));
   if(key==='results.exportTable')expect(bytes.toString()).toMatch(/^"Evidence class","Artifact ID","Artifact SHA256"/);
   if(key==='results.exportProvenance')expect(JSON.parse(bytes).evidenceClass).toBe('Derived viewer analysis subset, not an original LAS/LAZ or complete research package');
  }
  if(view!==page)await view.close();
 }
 expect(await get(request,`/api/missions/${fixture.missionId}/science-summary`)).toEqual(original);
});
test('settings and scientific overlay labels follow all five languages without replacing focused controls',async({page,request})=>{
 const fixture=await post(request,'/api/testing/terrain-completion'),original=await get(request,`/api/missions/${fixture.missionId}/science-summary`);
 await page.goto('/#settings/updates');
 for(const locale of ['en-US','es-ES','fr-FR','de-DE','pt-BR']){
  await page.evaluate(async locale=>{const{updatePresentation}=await import('/presentation-settings.js');updatePresentation({locale});},locale);
  const translate=createTranslator(bundle,locale).text;
  await expect(page.getByLabel(translate('updates.automaticNotifications'),{exact:true})).toBeVisible();
  await expect(page.getByLabel(translate('updates.includePrereleases'),{exact:true})).toBeVisible();
 }
 await page.goto(`/#workspace/map?missions=${fixture.missionId}`);
 await page.getByRole('button',{name:createTranslator(bundle,'pt-BR').text('ui.mapLayerManager.messageOverlaysLegend'),exact:true}).click();
 const originalControl=await page.locator('.map-overlays-dialog input').first().elementHandle();await originalControl.focus();
 for(const locale of ['en-US','es-ES','fr-FR','de-DE','pt-BR']){
  await page.evaluate(async locale=>{const{updatePresentation}=await import('/presentation-settings.js');updatePresentation({locale});},locale);
  const translate=createTranslator(bundle,locale).text,dialog=page.locator('.map-overlays-dialog');
  for(const key of ['map.savedObservations','map.verifiedRasters'])await expect(dialog.getByLabel(translate(key),{exact:true})).toBeVisible();
  expect(await originalControl.evaluate(n=>n===document.activeElement&&n.isConnected)).toBe(true);
 }
 expect(await get(request,`/api/missions/${fixture.missionId}/science-summary`)).toEqual(original);
});
for(const locale of bundle.manifest.locales)for(const theme of ['light','dark'])test(`${locale.id} ${theme}: real Results and native controls, expanded layouts and labeled browser captures`,async({page,request},info)=>{
 test.setTimeout(150000);const samples=[];
 await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});
 await page.addInitScript(locale=>localStorage.setItem('AeroScience.presentation.v1',JSON.stringify({version:1,theme:'auto',locale})),locale.id);
 const fixture=await post(request,'/api/testing/terrain-completion'),job=await get(request,`/api/terrain-elevation/jobs/${fixture.jobId}`);
 await post(request,`/api/terrain-elevation/jobs/${fixture.jobId}/control`,{requestId:crypto.randomUUID(),expectedRevision:job.revision,action:'start'});
 await expect.poll(async()=>(await get(request,`/api/terrain-elevation/jobs/${fixture.jobId}`)).state).toBe('PartiallyComplete');
 const original=await get(request,`/api/missions/${fixture.missionId}/science-summary`);
 await page.goto(`/#workspace/results?missions=${fixture.missionId}&view=explore&p07=preserved`);await expect(page.locator('html')).toHaveAttribute('lang',locale.id);await expect(page.locator('[data-displayed-cells]')).toHaveAttribute('data-displayed-cells',/^[1-9]/);
 const translate=createTranslator(bundle,locale.id).text;for(const key of ['terrain','relief','gaps','corridor','track','originals'])await expect(page.getByLabel(translate('results.'+key+'Layer'),{exact:true})).toBeVisible();
 await expect(page.locator('.survey-layers')).not.toContainText(bundle.catalogs['en-US'].messages['common.messageUnavailable']);
 for(const width of [1440,1024,768,390]){await page.setViewportSize({width,height:1000});await noOverflow(page);if(width===1440||width===390){await accessible(page,'.survey-results');await label(page,`P07 · ${locale.id} · ${theme} · ${width}px · SYNTHETIC BROWSER / NOT INSTALLED MSFS`);await page.screenshot({path:info.outputPath(`browser-results-${locale.id}-${theme}-${width}.png`),fullPage:true});}}
 await page.setViewportSize({width:1440,height:1000});await page.evaluate(()=>document.documentElement.style.zoom='2');await noOverflow(page);await expect(page.locator('.survey-results')).toBeVisible();await page.evaluate(()=>document.documentElement.style.zoom='');
 samples.push(await evidence(page,'scientificResults'));
 await page.locator('.survey-results a[href*=evidence]').click();await expect(page.locator('.survey-results table')).toBeVisible();await expect(page).toHaveURL(/p07=preserved/);samples.push(await evidence(page,'evidence'));
 expect(await get(request,`/api/missions/${fixture.missionId}/science-summary`)).toEqual(original);
 // Actual existing dashboard controllers; no operations are authorized by locale.
 for(const[surface,route]of [['dashboard','#home'],['missions','#missions'],['libraryDownloads','#library'],['organizations','#organizations'],['contextualHelp','#help']]){await page.goto('/'+route);await expect(page.locator('#workspace')).toBeVisible();await noOverflow(page);samples.push(await evidence(page,surface));}
 const active=await post(request,'/api/missions',{name:'P07 long mission Zoë 東京 <literal> µm — original identity',armScience:true,instruments:[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000}]});
 await page.goto('/#flight');await expect(page.locator('#science-journey')).toHaveAttribute('data-science','armed');samples.push(await evidence(page,'currentFlight'));await host(request,locale.id);await mountRealEngine(page);await expect(page.locator('ingamepanel-aeroscience')).toHaveAttribute('lang',locale.id);await page.locator('#as-payload-page').click();
 const expectedPhase=await page.evaluate(()=>AeroScienceI18n.text('native.phase.notStarted','',{status:AeroScienceI18n.text('native.phase.current','',{phase:document.querySelector('ingamepanel-aeroscience').nativeLabel('armed')})}));await expect(page.locator('#as-session-phase')).toHaveText(expectedPhase);
 for(const width of [640,320]){await page.locator('#aeroscience-frame').evaluate((frame,width)=>Object.assign(frame.style,{width:width+'px',height:width===320?'360px':'720px'}),width);await page.locator('ingame-ui-header').evaluate((header,label)=>{header.textContent=label;},`P07 · ${locale.id} · ${theme} HOST · ${width}px · SYNTHETIC / MSFS NOT TESTED`);expect(await page.locator('#as-scroll').evaluate(n=>n.scrollWidth<=n.clientWidth+1)).toBe(true);expect(await page.locator('#as-scroll').evaluate(n=>n.clientHeight)).toBeGreaterThanOrEqual(48);await accessible(page,'#as-root');await page.locator('#aeroscience-frame').screenshot({path:info.outputPath(`browser-toolbar-${locale.id}-${theme}-${width}.png`)});}
 await page.locator('#aeroscience-frame').evaluate(frame=>Object.assign(frame.style,{width:'320px',height:'360px'}));
 await page.evaluate(()=>{const sizes=Array.from(document.querySelectorAll('#as-root *')).map(n=>[n,parseFloat(getComputedStyle(n).fontSize)]);for(const[n,size]of sizes)n.style.fontSize=2*size+'px';});
 expect(await page.locator('#as-scroll').evaluate(n=>n.clientHeight)).toBeGreaterThanOrEqual(48);
 const action=page.locator('[data-science-action=startData]');await action.focus();await expect(action).toBeInViewport();
 await page.locator('#as-mission-page').focus();await page.keyboard.press('ArrowRight');await expect(page.locator('#as-payload-page')).toBeFocused();expect((await get(request,'/api/state')).activeMission.id).toBe(active.id);
 await info.attach('rendered-coverage.json',{body:JSON.stringify({locale:locale.id,theme,samples},null,2),contentType:'application/json'});
});

test('forced colors and reduced motion keep named controls and keyboard focus available',async({page})=>{
 await page.emulateMedia({forcedColors:'active',reducedMotion:'reduce',colorScheme:'dark'});await page.goto('/#settings');
 await page.locator('#presentation-language').focus();await expect(page.locator('#presentation-language')).toBeFocused();await accessible(page,'.presentation-settings');
 expect(await page.evaluate(()=>matchMedia('(prefers-reduced-motion: reduce)').matches)).toBe(true);
});

test('pseudolocale expands owned labels without replacing focused fields or translating original evidence',async({page},info)=>{
 await page.goto('/#settings');await page.locator('#presentation-language').focus();
 const result=await page.evaluate(async()=>{const{localeData}=await import('/i18n/catalog.generated.js'),{createTranslator,localizeTree}=await import('/i18n.js');const input=document.querySelector('#presentation-language'),data=JSON.parse(JSON.stringify(localeData));for(const[k,v]of Object.entries(data.catalogs['en-US'].messages))if(typeof v==='string')data.catalogs['en-US'].messages[k]='［'+v.replace(/[aeiou]/g,c=>({a:'á',e:'ë',i:'ï',o:'ö',u:'ü'})[c])+' — expanded stress label］';const raw=document.createElement('p');raw.id='original-evidence';raw.textContent='Close Zoë 東京 SHA-256 '+ 'a'.repeat(64)+' 2026-11-14T01:02:03Z 1.23456789 µm';document.querySelector('main').append(raw);const before=raw.textContent;localizeTree(document,createTranslator(data,'en-US').text);return{sameInput:input===document.querySelector('#presentation-language'),sameFocus:input===document.activeElement,rawUnchanged:raw.textContent===before,expanded:document.querySelector('.presentation-settings h2').textContent.includes('expanded stress label')};});
 expect(result).toEqual({sameInput:true,sameFocus:true,rawUnchanged:true,expanded:true});await page.setViewportSize({width:1440,height:1000});await page.evaluate(()=>document.documentElement.style.zoom='2');await noOverflow(page);await accessible(page,'.presentation-settings');await page.screenshot({path:info.outputPath('browser-pseudolocale-200-percent.png'),fullPage:true});
});
