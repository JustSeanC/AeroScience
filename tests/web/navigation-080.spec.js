import {test,expect} from '@playwright/test';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){
  const session=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();
  const result=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});
  expect(result.ok(),await result.text()).toBe(true);return result.json();
}
test.beforeEach(async({page})=>{await page.route('https://**',route=>route.abort());});
test('legacy and canonical entries mount existing views without unsafe commands or duplicate live nodes',async({page})=>{
  test.setTimeout(60000);const unsafe=[],streams=[];
  page.on('request',request=>{if(new URL(request.url()).pathname==='/api/events')streams.push(request.url());});
  page.on('request',request=>{if(request.method()==='POST'&&/\/api\/(missions(?:$|\?)|.*\/collection\/control|.*\/approve|.*\/execute|organizations\/live\/start|.*\/send)/.test(request.url()))unsafe.push(request.url());});
  await page.goto('/#my-research');await expect(page.locator('#workspace')).toBeVisible();
  const routes=[['#home','home'],['#organizations?future=x','my-research'],['#missions/templates?card=fixture','my-research'],['#planner','planner'],['#control','control'],['#science','instruments'],['#instruments','instruments'],
    ['#operations','operations'],['#results','history'],['#history','history'],['#workspace/map','science-workspace'],
    ['#workspace/compare','science-workspace'],['#workspace/analysis','science-workspace'],['#workspace/results','science-workspace'],['#workspace/replay','science-workspace'],
    ['#library','library'],['#library/download?science=hyperspectral','library'],['#downloads','downloads'],['#library/storage','library-storage'],
    ['#settings/units','settings'],['#settings/storage','settings'],['#help','help'],['#unknown','my-research'],
    ['#missions','missions-explorer'],['#missions/plans','planner'],['#flight','operations'],['#flight/details','control'],['#flight/instruments','instruments'],
    ['#results/explore/map','science-workspace'],['#results/overview','science-workspace']];
  for(const [hash,pageId]of routes){
    await page.evaluate(value=>{location.hash=value;},hash);await expect(page.locator('#'+pageId)).toBeVisible();
    await expect(page.locator('#science-journey')).toHaveCount(1);await expect(page.locator('#mission-intelligence')).toHaveCount(1);
    await expect(page.locator('#flight-workspace')).toHaveCount(1);await expect(page.locator('#main-content')).toHaveCount(1);
  }
  expect(unsafe).toEqual([]);expect(streams).toHaveLength(1);
});
test('recorded aliases preserve the exact old flight and all query parameters; Help preserves guide and anchor',async({page,request})=>{
  const fixture=await post(request,'/api/testing/workstation',{acquisitions:2,observations:4});
  await page.goto('/#workspace/timeline?missions='+fixture.missionId+'&card=keep&future=a%3Fb');
  await expect(page).toHaveURL(new RegExp('flight='+fixture.missionId));await expect(page.locator('#operations')).toContainText('Recorded Local Operations');
  let query=new URLSearchParams(new URL(page.url()).hash.split('?')[1]);expect(query.get('future')).toBe('a?b');expect(query.get('card')).toBe('keep');
  await page.goto('/#results/evidence?missions='+fixture.missionId+'&future=preserved');
  await expect(page).toHaveURL(new RegExp('flight='+fixture.missionId));await expect(page.locator('#operations')).toContainText('Recorded Local Operations');
  await page.goto('/#settings/support?guide=v0.4.2-pilot-guide.md&anchor=overview&future=preserved');
  await expect(page.locator('.help-guide')).toBeVisible();query=new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
  expect(query.get('guide')).toBe('v0.4.2-pilot-guide.md');expect(query.get('anchor')).toBe('overview');expect(query.get('future')).toBe('preserved');
});
test('keyboard drawer and Back/Forward restore focus, while repeated navigation keeps one recording handler',async({page,request})=>{
  await post(request,'/api/mission-selection',{planId:null});await post(request,'/api/instruments',[]);
  await page.setViewportSize({width:820,height:1000});await page.goto('/#control');
  const menu=page.getByRole('button',{name:'☰ Menu',exact:true});await menu.focus();await page.keyboard.press('Enter');
  await page.keyboard.press('Escape');await expect(menu).toBeFocused();
  await menu.click();await page.getByRole('navigation',{name:'Main navigation',exact:true}).getByRole('link',{name:'Results & History',exact:true}).click();
  await expect(page.locator('#results-workspace > header h1')).toBeFocused();await page.goBack();await expect(page.locator('#control')).toBeVisible();
  await page.goForward();await expect(page.locator('#history')).toBeVisible();
  for(let i=0;i<3;i++)for(const [hash,id]of [['#control','control'],['#operations','operations'],['#science','instruments'],['#settings','settings']]){
    await page.evaluate(value=>{location.hash=value;},hash);await expect(page.locator('#'+id)).toBeVisible();
  }
  await page.evaluate(()=>{location.hash='#control';});await expect(page.locator('#control')).toBeVisible();
  const starts=[];page.on('request',request=>{if(request.method()==='POST'&&new URL(request.url()).pathname==='/api/missions')starts.push(request.postDataJSON());});
  await page.getByLabel('MISSION NAME',{exact:true}).fill('Presentation navigation fixture');
  let flight;
  try{
    await page.getByRole('button',{name:'Start Recording',exact:true}).click();await expect(page.getByRole('button',{name:'End mission',exact:true})).toBeVisible();
    flight=(await(await request.get('/api/state')).json()).activeMission;expect(starts).toHaveLength(1);expect(flight.name).toBe('Presentation navigation fixture');
    await expect(page.locator('#science-journey')).toContainText('Retained original scientific observations: 0');
    await page.getByRole('button',{name:'End mission',exact:true}).click();flight=null;
  }finally{if(flight)await post(request,'/api/missions/'+flight.id+'/end');}
});
