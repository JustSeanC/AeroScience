import {test,expect} from '@playwright/test';
import {mountToolbar} from './helpers/toolbar.mjs';
const mission={id:'11111111-1111-4111-8111-111111111111',name:'Native operations fixture',mode:'universal',startedUtc:new Date().toISOString(),sampleCount:2};
async function emit(page,{phase='collecting',active=true,stale=false,collectionFault=false,research=null,connection='connected'}={}){
 await page.evaluate(({phase,active,stale,collectionFault,research,mission,connection})=>{const p=document.querySelector('ingamepanel-aeroscience');p.send('state');const req=window.bus.sent.filter(s=>s.command.op==='state').pop().command;window.bus.state={...window.bus.state,connection:{source:'simulator',state:connection,message:'Fixture telemetry'},activeMission:active?mission:null,research};p.receive(JSON.stringify({protocol:1,requestId:req.requestId,ok:true,state:window.bus.state,capabilities:['mission-intelligence-1','session-bound-controls-1'],intelligence:{version:1,generatedUtc:new Date(Date.now()-(stale?7000:0)).toISOString(),missionId:active?mission.id:null,sessionId:['collecting','paused'].includes(phase)?'22222222-2222-4222-8222-222222222222':null,phase,collectionFault,authority:'manual',gradeStatus:'notGraded',actions:[],metrics:[],warnings:[],triggerStatus:'Not assessed'}}));},{phase,active,stale,collectionFault,research,mission,connection});
}
const instrument=()=>({instrumentId:'ambient-temperature',enabled:true,intervalMs:1000,quality:'valid',measurements:[],acquisitionSets:0,observationCount:0,rejectedObservations:0,missingIntervals:0});
test('science header is authoritative and fixed across pages, scroll, stale states and disconnect',async({page})=>{
 await mountToolbar(page);for(const phase of ['armed','paused','betweenSessions','unknown','stopped']){await emit(page,{phase});await expect(page.locator('#as-collection-headline')).toHaveText('SCIENCE COLLECTION INACTIVE');}
 await emit(page);await expect(page.locator('#as-collection-headline')).toHaveText('SCIENCE COLLECTION ACTIVE');
 await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');p.intelligence.sessionId=null;p.renderCollectionBanner(true);});await expect(page.locator('#as-collection-summary')).toContainText('status unavailable');await expect(page.locator('#as-collection-headline')).toHaveText('SCIENCE COLLECTION INACTIVE');await emit(page);
 for(const tab of ['as-mission-page','as-payload-page','as-map-page']){await page.locator('#'+tab).click();await page.locator('#as-scroll').evaluate(n=>n.scrollTop=n.scrollHeight);await expect(page.locator('#as-collection-banner')).toBeInViewport();}
 await expect(page.locator('#as-research-page')).toBeHidden();
 for(const connection of ['disconnected','unavailable','initializing']){await emit(page,{connection});await expect(page.locator('#as-collection-headline')).toHaveText('SCIENCE COLLECTION INACTIVE');}
 await emit(page,{stale:true});await expect(page.locator('#as-collection-headline')).toHaveText('SCIENCE COLLECTION INACTIVE');
 await emit(page,{collectionFault:true});await expect(page.locator('#as-collection-summary')).toContainText('storage unavailable');
 await emit(page,{active:false});await expect(page.locator('#as-collection-summary')).toContainText('No active mission');
 await emit(page);await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');p.lastReply=Date.now()-7000;p.tick();});await expect(page.locator('#as-collection-headline')).toHaveText('SCIENCE COLLECTION INACTIVE');
 await page.locator('#aeroscience-frame').evaluate(n=>Object.assign(n.style,{width:'320px',height:'360px'}));expect(await page.locator('#as-root').evaluate(n=>n.scrollWidth<=n.clientWidth)).toBe(true);
 expect(await page.evaluate(()=>window.bus.sent.filter(s=>['start','science-control','research-control'].includes(s.command.op)))).toEqual([]);
});
test('interval draft survives unfocused polling, applies once, and displays authoritative confirmation or refusal',async({page})=>{
 await mountToolbar(page);const research={collectionStatus:'collecting',instruments:[instrument()]};await emit(page,{research});await page.locator('#as-payload-page').click();
 const select=page.getByLabel('Ambient temperature collection interval',{exact:true}),apply=page.getByRole('button',{name:'Apply Ambient temperature interval',exact:true});
 await select.focus();await select.press('ArrowDown');await expect(select).toHaveValue('2000');await select.evaluate(n=>n.blur());await emit(page,{research});await expect(select).toHaveValue('2000');
 await select.selectOption('5000');await select.evaluate(n=>n.blur());await emit(page,{research});await expect(select).toHaveValue('5000');
 await apply.click();const request=await page.evaluate(()=>window.bus.sent.find(s=>s.command.op==='instrument').command);expect(request.intervalMs).toBe(5000);expect(request.checkMission).toBe(true);expect(request.missionId).toBe(mission.id);
 await emit(page,{research});await expect(select).toHaveValue('5000');await expect(apply).toBeDisabled();
 research.instruments[0].intervalMs=5000;await page.evaluate(({id,research})=>{const p=document.querySelector('ingamepanel-aeroscience');window.bus.state.research=research;p.receive(JSON.stringify({protocol:1,requestId:id,ok:true,state:window.bus.state}));},{id:request.requestId,research});
 await expect(page.locator('#as-instruments')).toContainText('Confirmed: 5 s.');await emit(page,{research});await expect(select).toHaveValue('5000');
 await select.selectOption('60000');await apply.click();await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience'),req=window.bus.sent.filter(s=>s.command.op==='instrument').pop().command;p.receive(JSON.stringify({protocol:1,requestId:req.requestId,ok:false,error:'Interval refused by fixture',state:window.bus.state}));});
 await expect(page.locator('#as-instruments')).toContainText('Interval refused by fixture');await expect(select).toHaveValue('5000');
 expect(await page.evaluate(()=>window.bus.sent.filter(s=>s.command.op==='start'||s.command.op==='science-control'))).toEqual([]);
});
test('native generated notification punctuation changes without altering Unicode research names',async({page})=>{
 await mountToolbar(page);const result=await page.evaluate(()=>{const p=document.querySelector('ingamepanel-aeroscience');p.researchState={cards:[{card:{name:'Café · 研究'}}]};return p.nativeMessage('Begin Attempt · Café · 研究 · revision 1.');});expect(result).toBe('Begin Attempt | Café · 研究 | revision 1.');
});
test('failed interval delivery retains its draft and can return to the confirmed configuration',async({page})=>{
 await mountToolbar(page);const research={collectionStatus:'collecting',instruments:[instrument()]};await emit(page,{research});await page.locator('#as-payload-page').click();
 const interval=page.getByLabel('Ambient temperature collection interval',{exact:true});await interval.selectOption('5000');
 await page.evaluate(()=>{const send=window.bus.callSimConnect;window.bus.callSimConnect=function(event,payload){if(JSON.parse(payload).op==='instrument')return Promise.reject(Error('Controlled delivery failure'));return send.call(this,event,payload);};});
 await page.getByRole('button',{name:'Apply Ambient temperature interval',exact:true}).click();await expect(page.locator('#as-instruments')).toContainText('interval is not confirmed');await emit(page,{research});await expect(interval).toHaveValue('5000');
 await page.getByRole('button',{name:'Use confirmed Ambient temperature interval',exact:true}).click();await expect(interval).toHaveValue('1000');
});
