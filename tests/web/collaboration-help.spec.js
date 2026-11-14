import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdirSync} from 'node:fs';

test('Organizations contextual help is keyboard accessible, offline from external services and cannot start sharing or transmission',async({page})=>{
 const writes=[],external=[];page.on('request',r=>{const url=new URL(r.url());if(!['127.0.0.1','localhost'].includes(url.hostname))external.push(url.href);if(r.method()!=='GET'&&/organizations|submissions|downloads|science\/access/.test(url.pathname))writes.push(url.pathname);});
 await page.route(/^https:\/\//,r=>r.abort());await page.goto('/#organizations?keep=help-check');const root=page.locator('#research-organizations'),help=root.locator('[data-help-group="assignments"]');await expect(help.locator('summary')).toBeVisible();expect(await help.evaluate(e=>e.open)).toBe(false);const summary=help.locator('summary');await summary.focus();await page.keyboard.press('Enter');await expect(help).toHaveAttribute('open','');await expect(help).toContainText('A newer published revision does not replace your issued requirements');await expect(help).toContainText('It is not organization approval');await expect(summary).toBeFocused();
 const popupPromise=page.waitForEvent('popup');await help.getByRole('link',{name:'Read the bundled collaboration guide (opens a new tab)',exact:true}).click();const popup=await popupPromise;await expect(popup.getByRole('heading',{name:'Research collaboration and scientific evidence',exact:true})).toBeVisible();await expect(popup.locator('.help-guide')).toContainText('numerical profile arrays');await popup.close();await expect(help).toHaveAttribute('open','');expect(writes).toEqual([]);expect(external).toEqual([]);
});

test('bundled scientific guidance explains every evidence class and unknown reference without external downloads',async({page})=>{
 const external=[];page.on('request',r=>{if(!['127.0.0.1','localhost'].includes(new URL(r.url()).hostname))external.push(r.url());});await page.route(/^https:\/\//,r=>r.abort());
 await page.goto('/#help?guide=v0.8.0-research-collaboration.md');const guide=page.locator('.help-guide');await expect(guide.getByRole('heading',{name:'Four distinct scientific materials',exact:true})).toBeVisible();
 for(const text of ['Original AeroScience observations','Modeled scanner corridor','Source-backed DEM/DSM products','Historical LiDAR products','Unknown acquisition dates remain unknown','A vertical datum defines the reference for height','Saving a draft does not authorize transmission','no new shared track history or replay is stored','uncertain acknowledgments remain held'])await expect(guide).toContainText(text);
 const methodology=guide.getByRole('link',{name:'Instruments and scientific interpretation',exact:true});await expect(methodology).toHaveAttribute('href','#help?guide=remote-sensing%2Findex.md');await methodology.click();await expect(page.locator('.help-guide')).toContainText('Scientific');expect(external).toEqual([]);
});

test('Organizations help and navigation reflow in Auto light/dark at desktop, tablet, 390px and verified double text',async({page})=>{
 for(const theme of ['light','dark']){
  await page.emulateMedia({colorScheme:theme,reducedMotion:'reduce'});await page.goto('/#organizations');const root=page.locator('#research-organizations');await expect(root.getByRole('navigation',{name:'Organization tasks',exact:true})).toBeVisible();await expect(page.locator('html')).toHaveAttribute('data-resolved-theme',theme);
  for(const width of [1440,768,390]){await page.setViewportSize({width,height:1000});await root.evaluate(e=>e.style.fontSize='');
   if(process.env.AEROSCIENCE_P06_SCREENSHOTS==='1'&&width!==768){mkdirSync('docs/roadmap/images/v080-p06',{recursive:true});await page.evaluate(()=>{document.getElementById('p06-capture-label')?.remove();const p=document.createElement('p');p.id='p06-capture-label';p.textContent='P06 browser validation — isolated synthetic fixture; not installed Windows/MSFS acceptance.';document.getElementById('research-organizations').prepend(p);});await page.screenshot({path:`docs/roadmap/images/v080-p06/organizations-browser-synthetic-${theme}-${width}.png`,fullPage:true});}
   const original=await root.evaluate(e=>parseFloat(getComputedStyle(e).fontSize));await root.evaluate(e=>e.style.fontSize='200%');expect(await root.evaluate(e=>parseFloat(getComputedStyle(e).fontSize))).toBe(original*2);
   const help=root.locator('[data-help-group="assignments"]');await help.evaluate(e=>e.open=true);expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);expect((await new AxeBuilder({page}).include('#research-organizations').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze()).violations).toEqual([]);await help.evaluate(e=>e.open=false);
  }
 }
});
