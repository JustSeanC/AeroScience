// Compiled classic-script beta fixture. NOT genuine installed MSFS acceptance.
import {test,expect} from '@playwright/test';
import {execFileSync} from 'node:child_process';
import {mkdtempSync,readFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {mountToolbar} from './helpers/toolbar.mjs';
import {compileStage} from '../../scripts/build/native-javascript.mjs';

test('Beta 1 binds version/source and isolated channels, fits minimum panel and rejects foreign replies across reopen',async({page})=>{
 const temp=mkdtempSync(join(tmpdir(),'aeroscience-beta080-')),source='a'.repeat(40);let script,html,css;
 try{
  const stage=join(temp,'stage');execFileSync('python3',['scripts/release/beta_release_080.py','stage-toolbar','--folder',stage,'--source',source]);compileStage(stage);
  const panel=join(stage,'PackageSources/html_ui/InGamePanels/AeroScienceBetaV080');script=readFileSync(join(panel,'AeroScience.js'),'utf8');html=readFileSync(join(panel,'AeroScience.html'),'utf8');css=readFileSync(join(panel,'AeroScience.css'),'utf8');
 }finally{rmSync(temp,{recursive:true,force:true});}
 const errors=[];page.on('pageerror',e=>errors.push(e.message));await mountToolbar(page,{script,htmlSource:html});await page.addStyleTag({content:css});
 await expect(page.locator('#aeroscience-frame')).toHaveAttribute('title','AeroScience v0.8.0 Beta 1');
 await page.getByText('Connection & Build Details',{exact:true}).click();await expect(page.locator('#as-build-identity')).toContainText(source);await expect(page.locator('#as-build-identity')).toContainText('0.8.0-beta.1');
 await expect(page.locator('#as-channel-identity')).toContainText('AeroScience.Beta.0_8_0.Command.v1');
 await page.locator('#aeroscience-frame').evaluate(frame=>Object.assign(frame.style,{width:'320px',height:'360px'}));expect(await page.locator('#as-scroll').evaluate(e=>e.scrollWidth<=e.clientWidth)).toBe(true);
 const messages=await page.evaluate(()=>window.bus.sent);expect(messages.length).toBeGreaterThan(0);
 for(const {event,command}of messages){expect(event).toBe('AeroScience.Beta.0_8_0.Command.v1');expect(command.betaSource).toBe(source);expect(command.betaVersion).toBe('0.8.0-beta.1');expect(command.validationInstance).toBeUndefined();expect(Buffer.byteLength(JSON.stringify(command),'utf8')).toBeLessThanOrEqual(1024);}
 expect(await page.evaluate(()=>Object.keys(window.bus.handlers).some(k=>k==='AeroScience.Reply.v1'||k.startsWith('AeroScience.Validation.')))).toBe(false);
 await page.evaluate(()=>{const req=window.bus.sent.find(x=>x.command.op==='state').command;window.bus.handlers['AeroScience.Beta.0_8_0.Reply.v1'](JSON.stringify({protocol:1,requestId:req.requestId,ok:true,state:window.bus.state}));});
 await expect(page.locator('#as-link-detail')).toContainText('Matching engine reply received');
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience-beta-v080');p.send('state');const req=window.bus.sent.at(-1).command;window.bus.handlers['AeroScience.Beta.0_8_0.Reply.v1'](JSON.stringify({protocol:1,requestId:req.requestId,ok:false,error:'Beta toolbar source mismatch: controlled different source'}));});
 await expect(page.locator('#as-start')).toHaveAttribute('disabled','');await expect(page.locator('[data-science-action=startData]')).toBeDisabled();await expect(page.locator('#as-status')).toContainText('Beta toolbar source mismatch');
 const writes=await page.evaluate(()=>window.bus.sent.filter(x=>['start','science-control'].includes(x.command.op)).length);await page.locator('#as-start').evaluate(e=>e.click());
 expect(await page.evaluate(()=>window.bus.sent.filter(x=>['start','science-control'].includes(x.command.op)).length)).toBe(writes);
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience-beta-v080');p.remove();document.body.append(p);});
 await expect.poll(()=>page.evaluate(()=>window.bus.sent.filter(x=>x.command.op==='state').length)).toBeGreaterThan(2);
 const reopened=await page.evaluate(()=>window.bus.sent.at(-1).command);expect(reopened.betaSource).toBe(source);expect(reopened.betaVersion).toBe('0.8.0-beta.1');expect(errors).toEqual([]);
});
