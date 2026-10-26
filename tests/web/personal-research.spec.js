import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}) {
  const session=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();
  const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});
  expect(response.ok(),await response.text()).toBeTruthy(); return response.json();
}
test.afterEach(async({request})=>{
  const state=await(await request.get('/api/state')).json();
  if(state.activeMission)await post(request,`/api/missions/${state.activeMission.id}/end`);
  await post(request,'/api/mission-selection',{planId:null});
});
test('personal card uses the real planner, recorder, immutable evidence and offline local results',async({page,request})=>{
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto('/');await expect(page.locator('#altitude')).not.toHaveText('—');
  const state=await(await request.get('/api/state')).json();const frame=state.telemetry;
  await page.getByRole('link',{name:'My Research',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Personal Research',exact:true})).toBeVisible();
  const name='Browser flight regime '+Date.now();
  await page.getByLabel('Test card name',{exact:true}).fill(name);
  await page.getByLabel('Indicated airspeed target (knots)',{exact:true}).fill(String(frame.indicatedAirspeedKnots));
  await page.getByLabel('Airspeed tolerance (± knots)',{exact:true}).fill('30');
  await page.getByLabel('Altitude target (feet MSL)',{exact:true}).fill(String(frame.altitudeMslFeet));
  await page.getByLabel('Altitude tolerance (± feet)',{exact:true}).fill('1000');
  await page.getByLabel('Required continuous hold (seconds)',{exact:true}).fill('1');
  await page.getByRole('button',{name:'Save Test Card',exact:true}).click();
  const card=page.locator('#my-research article').filter({has:page.getByRole('heading',{name,exact:true})});
  await expect(card).toContainText('Revision 1');
  await card.getByRole('button',{name:'Open in Mission Planner',exact:true}).click();
  await expect(page.locator('#plan-name')).toHaveValue(name);await expect(page.locator('#plan-form')).toContainText('Personal Research test card');
  await page.getByRole('button',{name:'Save mission plan',exact:true}).click();
  await expect(page.getByText('Mission plan saved · Ready to start mission',{exact:false})).toBeVisible();
  const plans=await(await request.get('/api/plans')).json();const plan=plans.find(p=>p.name===name);expect(plan.researchCard.revision).toBe(1);
  await page.locator('.plan-row').filter({hasText:name}).getByRole('button',{name:'Select for Flight',exact:true}).click();
  await page.getByRole('button',{name:'Start mission',exact:true}).click();await expect(page.locator('#active-name')).toHaveText(name);
  const flight=(await(await request.get('/api/state')).json()).activeMission;
  await expect.poll(async()=>(await(await request.get(`/api/missions/${flight.id}`)).json()).mission.sampleCount).toBeGreaterThanOrEqual(9);
  await page.getByRole('button',{name:'End mission',exact:true}).click();
  await page.goto('/#my-research?flight='+flight.id);
  await expect(page.getByLabel('Recorded flight',{exact:true})).toHaveValue(flight.id);
  await page.getByRole('button',{name:'Evaluate Recorded Attempt',exact:true}).click();
  const review=page.locator('#personal-research-review');await expect(review.getByRole('heading',{name:name+' — Pass',exact:true})).toBeVisible();
  await expect(review).toContainText('Synthetic/demo telemetry');
  await page.getByRole('button',{name:'Evaluate Recorded Attempt',exact:true}).click();
  const results=await(await request.get(`/api/research/attempts?flightId=${flight.id}`)).json();expect(results).toHaveLength(1);
  // The millisecond time controls round inward; compare the exact selected interval,
  // rather than expecting the final submillisecond frame outside it to be included.
  const ticks=value=>{const [seconds,fraction='']=value.replace('+00:00','Z').replace(/Z$/,'').split('.');return BigInt(Date.parse(seconds+'Z'))*10000n+BigInt((fraction+'0000000').slice(0,7));};
  const samples=await(await request.get(`/api/missions/${flight.id}/telemetry?limit=1000`)).json();
  const selected=samples.filter(s=>ticks(s.timestampUtc)>=ticks(results[0].startUtc)&&ticks(s.timestampUtc)<=ticks(results[0].endUtc));
  expect(results[0].evidence.sampleCount).toBe(selected.length);expect(selected.length).toBeGreaterThanOrEqual(5);expect(results[0].evidence.sha256).toHaveLength(64);
  expect(results[0].evidence.planSha256).toHaveLength(64);expect(results[0].card.revision).toBe(1);
  // Local host remains reachable while internet requests fail; there is no external research service.
  await page.route('https://**',route=>route.abort());await page.reload();
  await expect(page.locator('#my-research')).toContainText(name+' — Pass');
  await page.getByRole('link',{name:'Mission History',exact:true}).click();await page.getByRole('button',{name:'View mission',exact:true}).first().click();
  await page.getByRole('link',{name:'Review Personal Research',exact:true}).click();
  await expect(page.getByLabel('Recorded flight',{exact:true})).toHaveValue(flight.id);
  expect(errors).toEqual([]);
});

test('card revisions preserve old flights and the personal editor remains accessible at narrow widths',async({page,request})=>{
  const card=await post(request,'/api/research/cards',{name:'Accessible research '+Date.now(),flightRegime:{airspeedKnots:120,airspeedToleranceKnots:5,altitudeMslFeet:3000,altitudeToleranceFeet:100,holdSeconds:30}});
  await page.setViewportSize({width:375,height:812});await page.goto('/#my-research');
  const row=page.locator('#my-research article').filter({has:page.getByRole('heading',{name:card.name,exact:true})});
  await row.getByRole('button',{name:'Edit Card',exact:true}).click();await expect(page.getByLabel('Test card name',{exact:true})).toBeFocused();
  await page.getByLabel('Required continuous hold (seconds)',{exact:true}).fill('45');
  await page.getByRole('button',{name:'Save New Revision',exact:true}).click();await expect(row).toContainText('Revision 2');
  const original=await(await request.get(`/api/research/cards/${card.id}?revision=1`)).json();expect(original.flightRegime.holdSeconds).toBe(30);
  expect((await(await request.get(`/api/research/cards/${card.id}`)).json()).flightRegime.holdSeconds).toBe(45);
  await expect(page.locator('#my-research')).toBeVisible();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
  const axe=await new AxeBuilder({page}).include('#my-research').analyze();expect(axe.violations).toEqual([]);
  await page.getByLabel('Test card name',{exact:true}).focus();await page.keyboard.press('Tab');await expect(page.getByLabel('Indicated airspeed target (knots)',{exact:true})).toBeFocused();
});
