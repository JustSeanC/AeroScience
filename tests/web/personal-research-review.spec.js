import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const session=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const r=await request.post(path,{headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf},data});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
async function control(request,flight,card,action){const s=await(await request.get(`/api/missions/${flight.id}/research-operations`)).json();return post(request,'/api/research/control',{version:1,flightId:flight.id,requestId:crypto.randomUUID(),expectedRevision:s.revision,cardId:card.id,expectedAttemptId:s.cards[0].openAttempt?.id||null,action});}
async function start(request,card,name){const plan=await post(request,`/api/research/cards/${card.id}/plan`,{id:card.id,revision:card.revision});const flight=await post(request,'/api/missions',{name,planId:plan.id});await control(request,flight,card,'activate');return flight;}
async function seed(request){
 const frame=(await(await request.get('/api/state')).json()).telemetry;
 const card=await post(request,'/api/research/cards',{name:'A-P03 repeated experiment '+Date.now(),flightRegime:{airspeedKnots:frame.indicatedAirspeedKnots,airspeedToleranceKnots:30,altitudeMslFeet:frame.altitudeMslFeet,altitudeToleranceFeet:1000,holdSeconds:1}});
 const attempts=[];
 for(const [revision,name]of [[card,'P03 earlier flight'],[null,'P03 revised flight']]){
  const c=revision||await post(request,'/api/research/cards',{id:card.id,expectedRevision:1,name:card.name,flightRegime:{...card.flightRegime,airspeedKnots:frame.indicatedAirspeedKnots+200,holdSeconds:2}});
  const flight=await start(request,c,name),begin=await control(request,flight,c,'beginAttempt');
  await expect.poll(async()=>Date.parse((await(await request.get('/api/state')).json()).telemetry.timestampUtc)-Date.parse(begin.receipt.timestampUtc)).toBeGreaterThan(c.flightRegime.holdSeconds*1000+750);
  await control(request,flight,c,'endAttempt');await post(request,`/api/missions/${flight.id}/end`);
  const result=await post(request,`/api/research/recorded-attempts/${begin.receipt.attemptId}/evaluate`,{requestId:crypto.randomUUID()});attempts.push({flight,result,id:begin.receipt.attemptId});
 }
 await post(request,`/api/research/recorded-attempts/${attempts[0].id}/evaluate`,{requestId:crypto.randomUUID()});
 const current=await(await request.get(`/api/research/cards/${card.id}`)).json(),flight=await start(request,current,'P03 interrupted and pending');
 const interrupted=await control(request,flight,current,'beginAttempt');await control(request,flight,current,'deactivate');await control(request,flight,current,'activate');
 const pending=await control(request,flight,current,'beginAttempt');await control(request,flight,current,'endAttempt');await post(request,`/api/missions/${flight.id}/end`);
 await post(request,`/api/research/recorded-attempts/${interrupted.receipt.attemptId}/evaluate`,{requestId:crypto.randomUUID()});
 return {card,attempts,pending:pending.receipt.attemptId};
}
test.afterEach(async({request})=>{const s=await(await request.get('/api/state')).json();if(s.activeMission)await post(request,`/api/missions/${s.activeMission.id}/end`);await post(request,'/api/mission-selection',{planId:null});});

test('offline experiment overview compares frozen cross-flight attempts with real hashes, chart gaps and original evidence',async({page,request})=>{
 test.setTimeout(60000);await page.goto('/');await expect(page.locator('#altitude')).not.toHaveText('—');const {card,attempts}=await seed(request);
 if(attempts.some((a,i)=>a.result.outcome!==['pass','fail'][i])){
  const diagnostic=await Promise.all(attempts.map(async a=>({result:a.result,events:(await(await request.get(`/api/missions/${a.flight.id}/replay/events?after=0`)).json()).filter(e=>['connection','telemetry-gap','simulator-paused'].includes(e.kind)),samples:await(await request.get(`/api/missions/${a.flight.id}/telemetry?limit=1000`)).json()})));
  await test.info().attach('unexpected-recorded-research-outcome',{body:JSON.stringify(diagnostic),contentType:'application/json'});
 }
 expect(attempts.map(a=>a.result.outcome)).toEqual(['pass','fail']);
 await page.goto('/#my-research');await page.locator('#my-research article').filter({has:page.getByRole('heading',{name:card.name,exact:true})}).getByRole('button',{name:'Overview & Compare',exact:true}).click();
 const root=page.getByRole('region',{name:'Experiment overview and comparison',exact:true});await expect(root).toContainText('Current revision 2');await expect(root.getByText('1 / 1 / 1',{exact:true})).toBeVisible();await expect(root).toContainText('not yet demonstrated');
 expect((await(await request.get(`/api/research/cards/${card.id}/overview`)).json()).markedAttempts).toBe(4);
 for(const checkbox of await root.getByRole('checkbox').all())await checkbox.check();await root.getByRole('button',{name:'Compare Selected Attempts',exact:true}).click();
 const comparison=root.getByRole('region',{name:'Attempt comparison',exact:true});await expect(comparison).toContainText('Different card revisions');await expect(comparison).toContainText('Synthetic/demo telemetry');await expect(comparison).toContainText('not been evaluated');await expect(comparison).toContainText('Matches saved evidence');
 const chart=comparison.getByRole('img',{name:'Continuous Hold Compared With Each Requirement',exact:true});await expect(chart).toBeVisible();
 const plotted=await chart.evaluate(canvas=>window.Chart.getChart(canvas).data.datasets[1].data.map(p=>p.y));expect(plotted.filter(v=>v!==null)).toHaveLength(2);expect(plotted.filter(v=>v===null)).toHaveLength(2);
 await comparison.getByRole('button',{name:'Inspect Evidence',exact:true}).last().click();const evidence=root.getByRole('region',{name:'Selected attempt evidence',exact:true});await expect(evidence).toContainText('Retained sample and plan hashes match');await expect(evidence).toContainText('Synthetic/demo telemetry');await expect(evidence.getByRole('region',{name:'Test-card activity and evidence',exact:true})).toBeVisible();
 await expect(evidence.getByRole('link',{name:'Open Recorded Flight Replay',exact:true})).toHaveAttribute('href',`#workspace/replay?missions=${attempts[0].flight.id}`);
 await page.setViewportSize({width:375,height:812});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();expect((await new AxeBuilder({page}).include('#research-experiment-review').analyze()).violations).toEqual([]);
 await root.getByRole('button',{name:'Refresh Experiment',exact:true}).click();await expect(root.getByRole('button',{name:'Compare Selected Attempts',exact:true})).toBeDisabled();
 await page.route('https://**',r=>r.abort());await page.reload();await page.locator('#my-research article').filter({has:page.getByRole('heading',{name:card.name,exact:true})}).getByRole('button',{name:'Overview & Compare',exact:true}).click();await expect(root.getByText('1 / 1 / 1',{exact:true})).toBeVisible();
 const pending=root.getByRole('article').filter({has:page.getByRole('button',{name:'Evaluate Attempt',exact:true})});await pending.getByRole('button',{name:'Evaluate Attempt',exact:true}).click();await expect(root.getByText('1 / 1 / 2',{exact:true})).toBeVisible();expect((await(await request.get('/api/state')).json()).activeMission).toBeNull();
});

test('changed selection rejects a late comparison and a re-evaluated result requires fresh review',async({page,request})=>{
 test.setTimeout(60000);await page.goto('/');const {card,attempts}=await seed(request);
 await page.goto('/#my-research');await page.locator('#my-research article').filter({has:page.getByRole('heading',{name:card.name,exact:true})}).getByRole('button',{name:'Overview & Compare',exact:true}).click();
 const root=page.locator('#research-experiment-review');await expect(root.getByRole('checkbox')).toHaveCount(4);for(const c of await root.getByRole('checkbox').all())await c.check();
 await post(request,`/api/research/recorded-attempts/${attempts[0].id}/evaluate`,{requestId:crypto.randomUUID()});await root.getByRole('button',{name:'Compare Selected Attempts',exact:true}).click();await expect(root.getByRole('status').first()).toContainText('selected result changed');
 await root.getByRole('button',{name:'Refresh Experiment',exact:true}).click();await expect(root.getByRole('checkbox')).toHaveCount(4);await expect(root.getByRole('checkbox').first()).not.toBeChecked();for(const c of await root.getByRole('checkbox').all())await c.check();
 let release,finished,intercepted=false;const held=new Promise(r=>release=r),done=new Promise(r=>finished=r);
 await page.route(`**/api/research/cards/${card.id}/comparison`,async route=>{try{const response=await route.fetch();intercepted=true;await held;await route.fulfill({response});}finally{finished();}});
 try{await root.getByRole('button',{name:'Compare Selected Attempts',exact:true}).click();await expect.poll(()=>intercepted).toBeTruthy();await root.getByRole('checkbox').first().uncheck();release();await done;
  await expect(root.getByRole('status').first()).toContainText('Selection changed');await expect(root.getByRole('heading',{name:'What Changed Between Attempts?',exact:true})).toHaveCount(0);
 }finally{release();if(intercepted)await done;await page.unroute(`**/api/research/cards/${card.id}/comparison`);}
 await expect(root.getByRole('button',{name:'Compare Selected Attempts',exact:true})).not.toHaveAttribute('aria-busy','true');
 await root.getByRole('button',{name:'Compare Selected Attempts',exact:true}).click();await expect(root.getByRole('heading',{name:'What Changed Between Attempts?',exact:true})).toBeVisible();
});
