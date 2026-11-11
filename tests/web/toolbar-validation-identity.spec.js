// Genuine staging code, controlled SDK host only: no installed-MSFS claim.
import {test,expect} from '@playwright/test';
import {execFileSync} from 'node:child_process';
import {cpSync,mkdtempSync,readFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {mountToolbar} from './helpers/toolbar.mjs';
import {compileStage} from '../../scripts/build/native-javascript.mjs';
test('generated validation panel uses matching isolated channels and exact source, and rejects a mismatched companion',async({page})=>{
 const temp=mkdtempSync(join(tmpdir(),'aeroscience-toolbar-identity-')),source='a'.repeat(40);let script,html;
 try{const stage=join(temp,'stage');cpSync('msfs-toolbar',stage,{recursive:true});execFileSync('python3',['scripts/release/private_validation.py','stage-toolbar','--folder',stage,'--commit',source]);compileStage(stage);const panel=join(stage,'PackageSources/html_ui/InGamePanels/AeroScienceValidationV070');script=readFileSync(join(panel,'AeroScience.js'),'utf8');html=readFileSync(join(panel,'AeroScience.html'),'utf8');}finally{rmSync(temp,{recursive:true,force:true});}
 const errors=[];page.on('pageerror',e=>errors.push(e.message));await mountToolbar(page,{script,htmlSource:html});await expect(page.locator('#as-compact')).toHaveCount(0);await expect(page.locator('#aeroscience-frame')).toHaveAttribute('title','AeroScience 0.7.0 Validation');await page.getByText('Connection & Build Details',{exact:true}).click();await expect(page.locator('#as-build-identity')).toContainText('validation toolbar | source '+source);await expect(page.locator('#as-channel-identity')).toContainText('AeroScience.Validation.0_7_0.Command.v1');await expect(page.locator('#as-channel-identity')).toContainText('AeroScience.Validation.0_7_0.Reply.v1');
 await page.locator('#aeroscience-frame').evaluate(frame=>Object.assign(frame.style,{width:'320px',height:'360px'}));expect(await page.locator('#as-scroll').evaluate(node=>node.scrollWidth<=node.clientWidth)).toBe(true);
 const first=await page.evaluate(()=>window.bus.sent.find(s=>s.command.op==='state'));expect(first.event).toBe('AeroScience.Validation.0_7_0.Command.v1');expect(first.command.validationSource).toBe(source);expect(await page.evaluate(()=>!!window.bus.handlers['AeroScience.Reply.v1'])).toBe(false);
 await page.evaluate(()=>{const req=window.bus.sent.find(s=>s.command.op==='state').command;window.bus.handlers['AeroScience.Validation.0_7_0.Reply.v1'](JSON.stringify({protocol:1,requestId:req.requestId,ok:true,state:window.bus.state}));});await expect(page.locator('#as-source')).toHaveText('SIMULATED TEST DATA');await expect(page.locator('#as-link-detail')).toContainText('Matching engine reply received');
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience-validation-v070');p.send('state');const req=window.bus.sent.at(-1).command;window.bus.handlers['AeroScience.Validation.0_7_0.Reply.v1'](JSON.stringify({protocol:1,requestId:req.requestId,ok:false,error:'Validation toolbar source mismatch: controlled fixture'}));});await expect(page.locator('#as-status')).toContainText('Validation toolbar source mismatch');await expect(page.locator('#as-start')).toHaveAttribute('disabled','');await expect(page.locator('[data-science-action=startData]')).toBeDisabled();expect(errors).toEqual([]);
});

test('validation dashboard retains its profile and exact source while navigating the new workspaces',async({page,request})=>{
 const build=await(await request.get('/api/build')).json();await page.route('**/api/build',r=>r.fulfill({json:{...build,profile:'validation',name:'AeroScience 0.7.0 Validation',sourceCommit:'a'.repeat(40),port:5086}}));await page.goto('/#my-research');await expect(page.locator('#validation-identity')).toContainText('source '+'a'.repeat(40));
 for(const name of ['Flight Planner','Flight Operations','Results & History','Scientific Library']){await page.getByRole('navigation',{name:'Main navigation',exact:true}).getByRole('link',{name,exact:true}).click();await expect(page).toHaveTitle('AeroScience 0.7.0 Validation · '+name);await expect(page.locator('#validation-identity')).toBeVisible();}
});

test('0.7.1-beta.2 generated toolbar uses its own channel, title and exact source and rejects a mismatch',async({page})=>{
 const temp=mkdtempSync(join(tmpdir(),'aeroscience-beta2-identity-')),source='b'.repeat(40);let script,html;
 try{const stage=join(temp,'stage');cpSync('msfs-toolbar',stage,{recursive:true});execFileSync('python3',['scripts/release/private_validation.py','stage-toolbar','--folder',stage,'--commit',source,'--beta-version','0.7.1-beta.2']);compileStage(stage);const panel=join(stage,'PackageSources/html_ui/InGamePanels/AeroScienceBetaV071Beta2');script=readFileSync(join(panel,'AeroScience.js'),'utf8');html=readFileSync(join(panel,'AeroScience.html'),'utf8');}finally{rmSync(temp,{recursive:true,force:true});}
 const errors=[];page.on('pageerror',e=>errors.push(e.message));await mountToolbar(page,{script,htmlSource:html});
 await expect(page.locator('#aeroscience-frame')).toHaveAttribute('title','AeroScience 0.7.1-beta.2');
 const first=await page.evaluate(()=>window.bus.sent.find(s=>s.command.op==='state'));expect(first.event).toBe('AeroScience.Beta.0_7_1_beta_2.Command.v1');expect(first.command.validationSource).toBe(source);
 expect(await page.evaluate(()=>!!window.bus.handlers['AeroScience.Validation.0_7_0.Reply.v1'])).toBe(false);
 await page.evaluate(()=>{const req=window.bus.sent.find(s=>s.command.op==='state').command;window.bus.handlers['AeroScience.Beta.0_7_1_beta_2.Reply.v1'](JSON.stringify({protocol:1,requestId:req.requestId,ok:false,error:'Validation toolbar source mismatch: Beta 2 fixture'}));});
 await expect(page.locator('#as-status')).toContainText('source mismatch');await expect(page.locator('#as-start')).toHaveAttribute('disabled','');await expect(page.locator('[data-science-action=startData]')).toBeDisabled();expect(errors).toEqual([]);
});

test('0.7.1-beta.2 dashboard metadata keeps the complete label and source through navigation',async({page,request})=>{
 const before=(await(await request.get('/api/state')).json()).activeMission;
 const build=await(await request.get('/api/build')).json();await page.route('**/api/build',r=>r.fulfill({json:{...build,profile:'validation',name:'AeroScience 0.7.1-beta.2',version:'0.7.1-beta.2',privateBetaVersion:'0.7.1-beta.2',sourceCommit:'b'.repeat(40),port:5087}}));
 await page.goto('/#my-research');await expect(page.locator('#application-version')).toHaveText('v0.7.1-beta.2');await expect(page.locator('#validation-identity')).toContainText('port 5087');await expect(page.locator('#validation-identity')).toContainText('source '+'b'.repeat(40));
 for(const name of ['Flight Planner','Results & History']){await page.getByRole('navigation',{name:'Main navigation',exact:true}).getByRole('link',{name,exact:true}).click();await expect(page).toHaveTitle('AeroScience 0.7.1-beta.2 · '+name);}
 expect((await(await request.get('/api/state')).json()).activeMission?.id??null).toBe(before?.id??null);
});
