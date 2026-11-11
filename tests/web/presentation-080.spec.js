import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

test.beforeEach(async({page})=>{await page.route('https://**',route=>route.abort());});

test('Auto follows system changes, explicit theme persists, and presentation choices do not write engine settings',async({page})=>{
  const writes=[];page.on('request',request=>{if(request.url().includes('/api/')&&request.method()!=='GET'&&!request.url().endsWith('/api/session'))writes.push(request.url());});
  await page.emulateMedia({colorScheme:'light'});await page.goto('/#settings');
  const theme=page.getByLabel('Theme',{exact:true});await expect(theme).toHaveValue('auto');
  await expect(page.locator('html')).toHaveAttribute('data-resolved-theme','light');
  await page.emulateMedia({colorScheme:'dark'});await expect(page.locator('html')).toHaveAttribute('data-resolved-theme','dark');
  await theme.selectOption('light');await page.reload();await expect(theme).toHaveValue('light');
  await expect(page.locator('html')).toHaveAttribute('data-resolved-theme','light');
  expect(await page.locator('html').evaluate(node=>getComputedStyle(node).colorScheme)).toBe('light');
  await theme.selectOption('dark');await page.emulateMedia({colorScheme:'light'});
  await expect(page.locator('html')).toHaveAttribute('data-resolved-theme','dark');
  await theme.selectOption('auto');await expect(page.locator('html')).toHaveAttribute('data-resolved-theme','light');
  expect(writes).toEqual([]);
});
test('five named language previews use English safely, survive reload and recover from invalid browser preferences',async({page})=>{
  await page.goto('/#settings');await page.evaluate(()=>localStorage.setItem('unrelated-fixture','retain me'));
  const language=page.getByLabel('Language',{exact:true});
  await expect(language.locator('option')).toHaveCount(5);
  for(const [locale,name] of [['es-ES','Español'],['fr-FR','Français'],['de-DE','Deutsch'],['pt-BR','Português (Brasil)']]){
    await language.selectOption(locale);await expect(page.locator('html')).toHaveAttribute('lang','en-US');
    await expect(page.locator('.presentation-settings [role=status]')).toContainText(name+' preview uses English');
    await expect(page.getByRole('link',{name:'Missions',exact:true})).toBeVisible();await expect(page.locator('#missions-workspace').getByRole('link',{name:'My Research',exact:true,includeHidden:true})).toBeAttached();
  }
  await page.reload();await expect(language).toHaveValue('pt-BR');
  expect(await page.evaluate(()=>localStorage.getItem('unrelated-fixture'))).toBe('retain me');
  await page.evaluate(()=>localStorage.setItem('AeroScience.presentation.v1',JSON.stringify({version:1,theme:'unsafe',locale:'unsupported'})));
  await page.reload();await expect(page.getByLabel('Theme',{exact:true})).toHaveValue('auto');await expect(language).toHaveValue('en-US');
  await page.evaluate(()=>localStorage.setItem('AeroScience.presentation.v1','{broken'));
  await page.reload();await expect(page.locator('.presentation-settings')).toContainText('could not save');
});
test('light and dark primary chrome is accessible at narrow width, high zoom and with long localized labels',async({page},testInfo)=>{
  test.setTimeout(60000);await page.setViewportSize({width:390,height:844});await page.goto('/#settings');
  for(const theme of ['light','dark']){
    await page.getByLabel('Theme',{exact:true}).selectOption(theme);
    const result=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze();
    expect(result.violations.map(v=>({id:v.id,nodes:v.nodes.map(n=>({target:n.target,summary:n.failureSummary}))})),theme).toEqual([]);
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
    await page.screenshot({path:testInfo.outputPath('synthetic-browser-settings-'+theme+'-390.png'),fullPage:true});
  }
  await page.setViewportSize({width:1440,height:1000});
  await page.evaluate(async()=>{
    const {localeData}=await import('/i18n/catalog.generated.js');
    const {localizeTree,createTranslator}=await import('/i18n.js');
    // Synthetic stress input, never a release locale or owner-data mutation.
    const data=JSON.parse(JSON.stringify(localeData));
    for(const [key,value]of Object.entries(data.catalogs['en-US'].messages))if(typeof value==='string'&&!value.includes('{'))data.catalogs['en-US'].messages[key]='［'+value+' — extended translation label］';
    localizeTree(document,createTranslator(data,'en-US').text);document.documentElement.style.zoom='2';
  });
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  await expect(page.getByLabel('Theme',{exact:false})).toBeVisible();
  await page.screenshot({path:testInfo.outputPath('synthetic-browser-settings-pseudo-200-percent.png'),fullPage:true});
});
test('shared components render text safely and managed dialogs retain cancel, dirty edits and return focus',async({page})=>{
  await page.goto('/#settings');
  await page.evaluate(async()=>{
    const ui=await import('/workstation-ui.js'),dialogs=await import('/dialog-ui.js');
    const fixture=ui.node('section');fixture.id='synthetic-components';fixture.setAttribute('aria-label','Synthetic component fixture');document.querySelector('main').append(fixture);
    ui.emptyState(fixture,{description:'Synthetic empty state — no scientific observations.'});
    ui.sourceBadge(fixture,'synthetic');ui.evidenceBadge(fixture,'unknown');
    ui.notification(fixture,'Synthetic notification');
    const field=ui.formField(fixture,'Synthetic field',ui.node('input'),{help:'A test field, not evidence.'});field.setError('Check this test entry.');
    const unsafe='Zoë 東京 <img src=x onerror=alert(1)> $& µm';ui.statusPill(fixture,unsafe,{live:true});
    window.syntheticClicks=0;ui.nextAction(fixture,'Synthetic next action',()=>{window.syntheticClicks++;});
    const opener=ui.button(fixture,'Open synthetic editor',()=>{
      const dialog=ui.node('dialog');dialog.setAttribute('aria-label','Synthetic editor');dialog.append(ui.node('h2','Synthetic editor'));
      ui.formField(dialog,'Synthetic draft');document.body.append(dialog);dialogs.dialogHeader(dialog,{opener,protectEdits:true});dialog.showModal();
    });
  });
  const fixture=page.locator('#synthetic-components');await expect(fixture.locator('img')).toHaveCount(0);
  await expect(fixture).toContainText('Zoë 東京 <img');await expect(fixture).toContainText('Unassessed');
  await expect(page.getByLabel('Synthetic field',{exact:true})).toHaveAttribute('aria-invalid','true');
  await page.getByRole('button',{name:'Synthetic next action',exact:true}).click();expect(await page.evaluate(()=>window.syntheticClicks)).toBe(1);
  const opener=page.getByRole('button',{name:'Open synthetic editor',exact:true});await opener.click();
  await page.getByLabel('Synthetic draft',{exact:true}).fill('unsaved');await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog',{name:'Unsaved changes',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Keep editing',exact:true}).click();await expect(page.getByRole('dialog',{name:'Unsaved changes',exact:true})).toHaveCount(0);
  await expect(page.getByLabel('Synthetic draft',{exact:true})).toBeFocused();
  await page.keyboard.press('Escape');await page.getByRole('button',{name:'Discard unsaved changes',exact:true}).click();await expect(opener).toBeFocused();
});

test('dirty dialog dismissal stays protected while an earlier confirmation close event is pending',async({page})=>{
  await page.goto('/#settings');
  await page.evaluate(async()=>{
    const {dialogHeader}=await import('/dialog-ui.js');
    const dialog=document.createElement('dialog');dialog.setAttribute('aria-label','Synthetic pending-close editor');
    const input=document.createElement('input');input.setAttribute('aria-label','Pending-close draft');dialog.append(input);document.body.append(dialog);
    dialogHeader(dialog,{protectEdits:true});dialog.showModal();
  });
  const editor=page.getByRole('dialog',{name:'Synthetic pending-close editor',exact:true});
  await page.getByLabel('Pending-close draft',{exact:true}).fill('Retain these unsaved edits');await page.keyboard.press('Escape');
  const warning=page.getByRole('dialog',{name:'Unsaved changes',exact:true});await expect(warning).toBeVisible();
  // close() changes open/focus synchronously; its close event is a later task.
  // Exercise another dismissal in that gap, without sleeps or discarding the draft.
  await warning.getByRole('button',{name:'Keep editing',exact:true}).evaluate(button=>{
    button.click();document.querySelector('dialog[aria-label="Synthetic pending-close editor"] .popup-close').click();
  });
  await expect(warning).toBeVisible();await expect(warning).toHaveCount(1);await expect(editor).toBeVisible();
  await page.keyboard.press('Escape');await expect(warning).toHaveCount(0);
  await expect(page.getByLabel('Pending-close draft',{exact:true})).toHaveValue('Retain these unsaved edits');
  await page.keyboard.press('Escape');await expect(warning).toBeVisible();
  await warning.getByRole('button',{name:'Discard unsaved changes',exact:true}).click();await expect(editor).toBeHidden();
});

test('both themes retain readable text across existing flight, scientific, history and utility workspaces',async({page,request})=>{
  test.setTimeout(120000);
  const origin='http://127.0.0.1:5080',session=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();
  const response=await request.post('/api/testing/workstation',{data:{acquisitions:12,observations:24},headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});
  expect(response.ok(),await response.text()).toBe(true);const fixture=await response.json();
  const routes=['#control','#workspace/map?missions='+fixture.missionId,'#workspace/analysis?missions='+fixture.missionId,
    '#workspace/compare?missions='+fixture.missionIds.slice(0,2).join(','),'#workspace/results?missions='+fixture.missionId,
    '#history','#downloads','#library','#help'];
  const failures=[];
  await page.setViewportSize({width:820,height:1180});
  for(const theme of ['light','dark']){
    await page.goto('/#settings');await page.getByLabel('Theme',{exact:true}).selectOption(theme);
    for(const route of routes){
      await page.goto('/'+route);await expect(page.locator('#workspace')).toBeVisible();
      await expect(page.locator('.page-heading h1:visible,.page-heading h2:visible').first()).toBeVisible();
      const result=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze();
      for(const violation of result.violations)failures.push({theme,route,id:violation.id,nodes:violation.nodes.map(node=>({target:node.target,summary:node.failureSummary}))});
    }
  }
  expect(failures).toEqual([]);
});
