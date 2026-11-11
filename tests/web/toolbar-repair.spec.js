import {test,expect} from '@playwright/test';
import {mountToolbar} from './helpers/toolbar.mjs';
import {readFileSync,cpSync,mkdtempSync,rmSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {compileNative,compileStage} from '../../scripts/build/native-javascript.mjs';

const folder='msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience/';
const legacyScript=script=>'Element.prototype.replaceChildren=undefined;Element.prototype.append=undefined;'+(script||compileNative(readFileSync(folder+'AeroScience.js','utf8')));
async function stateReply(page,extra={}){
 await page.evaluate(extra=>{const p=document.querySelector('[data-initialization-state=ready]');p.send('state');const request=window.bus.sent.filter(s=>s.command.op==='state').pop().command;p.receive(JSON.stringify({protocol:1,requestId:request.requestId,ok:true,state:window.bus.state,...extra}));},extra);
}

test('compiled native panel renders telemetry and saved plans without modern DOM helpers',async({page})=>{
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await mountToolbar(page,{autoReply:true,script:legacyScript(),legacyConnectivity:true});
 await expect(page.locator('#as-altitude')).toHaveText('60,204');
 await expect(page.locator('#as-status')).not.toContainText('Invalid engine reply');
 await expect(page.locator('#as-map-plan option')).toHaveText(['Unplanned Mission']);
 await expect(page.locator('#as-plan-status')).toContainText('No saved mission plans');
 expect(errors).toEqual([]);
});

test('secondary display failure is diagnosed without losing usable telemetry or plan replies',async({page})=>{
 await mountToolbar(page);
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');p.renderResearchView=()=>{throw TypeError('private fixture text must never enter diagnostics');};});
 await stateReply(page);
 await expect(page.locator('#as-altitude')).toHaveText('60,204');
 await expect(page.locator('#as-start')).not.toHaveAttribute('disabled');
 await expect(page.locator('#as-status')).not.toContainText('Invalid engine reply');
 await page.getByText('Connection & Build Details',{exact:true}).click();
 await expect(page.locator('#as-ui-detail')).toContainText('payload / TypeError');
 await expect(page.locator('#as-ui-detail')).not.toContainText('private fixture');
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');const req=window.bus.sent.find(s=>s.command.op==='map-plans').command;p.receive(JSON.stringify({protocol:1,requestId:req.requestId,ok:true,map:[]}));window.bus.state.telemetry.altitudeMslFeet=60300;});
 await stateReply(page);await expect(page.locator('#as-altitude')).toHaveText('60,300');
 await expect(page.locator('#as-map-plan option')).toHaveText(['Unplanned Mission']);
});

test('malformed, unsupported, oversized and uncorrelated replies retain the last valid flight state',async({page})=>{
 await mountToolbar(page);await stateReply(page);await page.getByText('Connection & Build Details',{exact:true}).click();
 for(const [kind,expected]of [['json','malformed JSON'],['protocol','unsupported protocol'],['state','invalid flight state'],['bytes','8000 UTF-8 bytes']]){
  await page.evaluate(kind=>{const p=document.querySelector('ingamepanel-aeroscience');p.send('state');const id=window.bus.sent.filter(s=>s.command.op==='state').pop().command.requestId;p.receive(kind==='json'?'{broken':JSON.stringify({protocol:kind==='protocol'?2:1,requestId:id,ok:true,state:kind==='state'?{}:{...window.bus.state,extra:kind==='bytes'?'😀'.repeat(2200):''}}));},kind);
  await expect(page.locator('#as-reply-detail')).toContainText(expected);await expect(page.locator('#as-altitude')).toHaveText('60,204');
 }
 await page.evaluate(()=>document.querySelector('ingamepanel-aeroscience').receive(JSON.stringify({protocol:1,requestId:'unrelated',ok:true,state:{}})));
 await expect(page.locator('#as-reply-detail')).toContainText('count 4');
 await page.evaluate(()=>{window.bus.state.connection={source:'simulator',state:'connected',message:'Awaiting current aircraft telemetry'};window.bus.state.telemetry.timestampUtc=new Date(Date.now()-7000).toISOString();});await stateReply(page);
 await expect(page.locator('#as-altitude')).toHaveText(' - ');await expect(page.locator('#as-source')).toHaveText('AIRCRAFT TELEMETRY UNAVAILABLE');
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');p.lastReply=Date.now()-7000;p.tick();});
 await expect(page.locator('#as-status')).toContainText('engine unavailable');await expect(page.locator('#as-start')).toHaveAttribute('disabled');
});

test('generated validation assets preserve Unicode names and use readable native status labels',async({page})=>{
 const temp=mkdtempSync(join(tmpdir(),'as-toolbar-repair-')),source='b'.repeat(40);let script,htmlSource;
 try{const stage=join(temp,'stage');cpSync('msfs-toolbar',stage,{recursive:true});execFileSync('python3',['scripts/release/private_validation.py','stage-toolbar','--folder',stage,'--commit',source]);compileStage(stage);const base=join(stage,'PackageSources/html_ui/InGamePanels/AeroScienceValidationV070');script=readFileSync(join(base,'AeroScience.js'),'utf8');htmlSource=readFileSync(join(base,'AeroScience.html'),'utf8');}finally{rmSync(temp,{recursive:true,force:true});}
 await mountToolbar(page,{script:legacyScript(script),htmlSource});
 const intelligence={version:1,generatedUtc:new Date().toISOString(),missionId:null,revision:1,controlRevision:1,sessionId:null,phase:'unknown',authority:'unknown',actions:[],metrics:[],warnings:[],gradeStatus:'notGraded',triggerStatus:'Not assessed'};
 await stateReply(page,{capabilities:['mission-intelligence-1','session-bound-controls-1'],intelligence});
 await expect(page.locator('#as-session-phase')).toHaveText('Science status unavailable');await expect(page.locator('#as-grade')).toHaveText('Not graded | No score available.');
 await expect(page.locator('#as-session-phase')).not.toContainText('not started');
 await page.evaluate(()=>{const p=document.querySelector('[data-initialization-state=ready]');const command=window.bus.sent.find(s=>s.command.op==='map-plans').command;p.receive(JSON.stringify({protocol:1,requestId:command.requestId,ok:true,map:[{id:'unicode-plan',name:'Café 研究',mode:'universal',waypoints:2,patterns:0,instruments:'ambient-temperature'}]}));});
 await expect(page.locator('#as-map-plan option')).toHaveText(['Unplanned Mission','Café 研究']);
 await stateReply(page,{capabilities:['mission-intelligence-1','session-bound-controls-1'],intelligence:{...intelligence,phase:'armed'}});
 await expect(page.locator('#as-session-phase')).toHaveText('Science: Armed | Science not started');
 await page.getByText('Connection & Build Details',{exact:true}).click();await expect(page.locator('#as-channel-identity')).toContainText('AeroScience.Validation.0_7_0.Reply.v1');
 expect(await page.evaluate(()=>window.bus.sent.every(s=>s.command.validationSource==='b'.repeat(40)))).toBe(true);
 expect(await page.evaluate(()=>document.querySelector('[data-initialization-state=ready]').nativeUnit('°C kg/m³ slug/ft³ m/s²'))).toBe('degC kg/m^3 slug/ft^3 m/s^2');
});

test('prepared personal cards and empty states render with legacy DOM methods unavailable',async({page})=>{
 await mountToolbar(page,{script:legacyScript()});
 const flight={id:'11111111-1111-4111-8111-111111111111',name:'Card fixture flight',mode:'universal',startedUtc:new Date().toISOString(),sampleCount:0};
 await page.evaluate(flight=>window.bus.state.activeMission=flight,flight);await stateReply(page,{capabilities:['personal-research-controls-1']});
 await expect(page.locator('#as-research-page')).toBeHidden();
 await page.evaluate(flight=>{const p=document.querySelector('ingamepanel-aeroscience');p.researchState={flightId:flight.id,generatedUtc:new Date().toISOString(),revision:1,recording:true,canBeginAttempt:true,cards:[{card:{id:'card-one',version:1,name:'Café 研究 test card',revision:1,flightRegime:{airspeedKnots:100,altitudeMslFeet:5000}},active:false}],nextOffset:null};p.renderPersonalResearch(true);},flight);
 await page.locator('#as-research-page').click();await expect(page.locator('#as-research-list')).toContainText('Café 研究 test card');await expect(page.getByRole('button',{name:'Activate Card',exact:true})).toBeEnabled();
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');p.researchState={...p.researchState,revision:2,cards:[]};p.renderPersonalResearch(true);});
 await expect(page.locator('#as-research-status')).toHaveText('No personal cards selected for this flight.');await expect(page.locator('#as-research-list')).toBeEmpty();await expect(page.locator('#as-research-page')).toBeHidden();await expect(page.locator('#as-mission-view')).toBeVisible();
});

test('a missing saved plan cannot start an accidental unplanned flight and refresh never selects it',async({page})=>{
 await mountToolbar(page);await page.evaluate(()=>window.bus.state.selectedPlanId='deleted-plan');await stateReply(page);
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');const command=window.bus.sent.filter(s=>s.command.op==='map-plans').pop().command;p.receive(JSON.stringify({protocol:1,requestId:command.requestId,ok:true,map:[],selectedPlan:null}));});
 await expect(page.locator('#as-start-plan')).toContainText('Selected plan is no longer available');await expect(page.locator('#as-start')).toHaveAttribute('disabled');
 await page.locator('#as-map-plan').selectOption('');const selection=await page.evaluate(()=>window.bus.sent.filter(s=>s.command.op==='select-plan').map(s=>s.command));expect(selection).toHaveLength(1);expect(selection[0].planId).toBeNull();
 await expect(page.locator('#as-start')).toHaveAttribute('disabled');
 await page.evaluate(id=>{const p=document.querySelector('ingamepanel-aeroscience');window.bus.state.selectedPlanId=null;p.receive(JSON.stringify({protocol:1,requestId:id,ok:true,state:window.bus.state}));},selection[0].requestId);
 await expect(page.locator('#as-start-plan')).toContainText('Unplanned Mission');await expect(page.locator('#as-start')).not.toHaveAttribute('disabled');
 expect(await page.evaluate(()=>window.bus.sent.filter(s=>s.command.op==='start'))).toEqual([]);
});

test('unchanged state polling keeps dropdown option nodes and pilot-edited mission names',async({page})=>{
 await mountToolbar(page);await page.evaluate(()=>window.bus.state.selectedPlanId='stable-plan');await stateReply(page);
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');const command=window.bus.sent.filter(s=>s.command.op==='map-plans').pop().command;p.receive(JSON.stringify({protocol:1,requestId:command.requestId,ok:true,map:[{id:'stable-plan',name:'Saved route',mode:'universal',waypoints:2,patterns:0,instruments:''}]}));window.originalOption=document.getElementById('as-map-plan').options[1];});
 await page.locator('#as-name').fill('My next recording');await stateReply(page);
 expect(await page.evaluate(()=>window.originalOption===document.getElementById('as-map-plan').options[1])).toBe(true);await expect(page.locator('#as-name')).toHaveValue('My next recording');
 await page.locator('#as-map-plans').click();
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');const command=window.bus.sent.filter(s=>s.command.op==='map-plans').pop().command;p.receive(JSON.stringify({protocol:1,requestId:command.requestId,ok:true,map:[{id:'stable-plan',name:'Saved route',mode:'realistic',waypoints:2,patterns:0,instruments:''}]}));});
 await expect(page.locator('#as-mode')).toHaveValue('realistic');await expect(page.locator('#as-name')).toHaveValue('My next recording');
 expect(await page.evaluate(()=>window.bus.sent.filter(s=>s.command.op==='select-plan'))).toEqual([]);
});
