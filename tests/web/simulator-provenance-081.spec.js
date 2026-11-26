import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

for(const theme of ['light','dark'])test(`P04 frozen simulator provenance: ${theme}, narrow/200% text, keyboard and safe evidence rendering`,async({page},info)=>{
  await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});await page.goto('/#settings/general');
  await expect(page.locator('.simulator-settings select')).toBeEnabled();
  await page.evaluate(async(theme)=>{
    const {simulatorOriginDetails}=await import('/simulator-origin.js');
    const section=document.createElement('section');section.id='p04-origin-fixture';document.querySelector('#settings').prepend(section);
    const label=document.createElement('h2');label.textContent=`P04 · ${theme.toUpperCase()} · SYNTHETIC BROWSER EVIDENCE FIXTURE · Not installed simulator acceptance`;section.append(label);
    const origin={version:1,simulatorId:'xplane12',reportedVersion:'12-fixture <img src=x onerror=alert(1)>',reportedBuild:null,
      adapterId:'aeroscience.xplane.web',adapterVersion:'p02-v1',apiVersion:'v1',mappingRevision:'xplane-web-v1-map1',
      connectionEpoch:'8d989cfe-357b-457d-8f0b-30b4c878d020',timestampBasis:'Host UTC at source receipt; simulator year unknown',
      limitations:['Synthetic fixture only; genuine installed X-Plane validation remains pending.']};
    window.p04OriginalOrigin=JSON.stringify(origin);window.p04Origin=origin;
    simulatorOriginDetails(section,origin,'simulator');simulatorOriginDetails(section,null,'simulator');
  },theme);
  const root=page.locator('#p04-origin-fixture'),source=root.locator('summary').filter({hasText:'Simulator source: X-Plane 12'});
  await source.focus();await expect(source).toBeFocused();await page.keyboard.press('Enter');
  await expect(root).toContainText('Reported simulator build');await expect(root).toContainText('Unknown');
  await expect(root.locator('img')).toHaveCount(0);await expect(root).toContainText('<img src=x onerror=alert(1)>');
  await root.locator('details').nth(1).locator('summary').click();await expect(root).toContainText('current simulator setting does not identify or relabel');
  for(const width of [1440,390]){
    await page.setViewportSize({width,height:1000});await page.evaluate(()=>{document.documentElement.style.fontSize='200%';});
    expect(await root.evaluate(el=>el.scrollWidth<=el.clientWidth+2)).toBe(true);
    expect((await new AxeBuilder({page}).include('#p04-origin-fixture').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze()).violations).toEqual([]);
    await page.screenshot({path:info.outputPath(`p04-${theme}-${width}-synthetic-browser.png`),fullPage:true});
  }
  expect(await page.evaluate(()=>JSON.stringify(window.p04Origin)===window.p04OriginalOrigin)).toBe(true);
});
